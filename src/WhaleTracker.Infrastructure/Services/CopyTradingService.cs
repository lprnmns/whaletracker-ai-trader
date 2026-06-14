using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.Infrastructure.Services;

public class CopyTradingService : ICopyTradingService
{
    private static readonly SemaphoreSlim ExecutionLock = new(1, 1);

    private readonly WhaleTrackerDbContext _db;
    private readonly IOkxService _okxService;
    private readonly ILogger<CopyTradingService> _logger;

    public CopyTradingService(
        WhaleTrackerDbContext db,
        IOkxService okxService,
        ILogger<CopyTradingService> logger)
    {
        _db = db;
        _okxService = okxService;
        _logger = logger;
    }

    public async Task<CopyLedgerSnapshot> GetLedgerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var virtualPositions = await _db.CopyTraderPositions
            .AsNoTracking()
            .OrderBy(x => x.Symbol)
            .ThenBy(x => x.Side)
            .ThenBy(x => x.TraderId)
            .ToListAsync(cancellationToken);

        var okxPositions = await _okxService.GetAllPositionsAsync();
        var aggregates = BuildAggregateSnapshot(virtualPositions, okxPositions);

        return new CopyLedgerSnapshot
        {
            CheckedAt = DateTime.UtcNow,
            TraderPositions = virtualPositions.Select(MapVirtualPosition).ToList(),
            Aggregates = aggregates
        };
    }

    public async Task<CopyLedgerEventsResponse> GetLedgerEventsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var events = await _db.CopyLedgerEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        return new CopyLedgerEventsResponse
        {
            CheckedAt = DateTime.UtcNow,
            Events = events.Select(MapLedgerEvent).ToList()
        };
    }

    public async Task<CopyPositionTargetResult> SetTraderPositionTargetAsync(
        CopyPositionTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);
        await ExecutionLock.WaitAsync(cancellationToken);
        try
        {
            return await SetTraderPositionTargetLockedAsync(normalized, cancellationToken);
        }
        finally
        {
            ExecutionLock.Release();
        }
    }

    private async Task<CopyPositionTargetResult> SetTraderPositionTargetLockedAsync(
        CopyPositionTargetRequest request,
        CancellationToken cancellationToken)
    {
        var calculation = request.TargetMarginUsdt > 0
            ? await _okxService.CalculateOrderAsync(
                request.Symbol,
                request.TargetMarginUsdt,
                request.Leverage,
                request.Side == "long" ? TradeAction.OPEN_LONG : TradeAction.OPEN_SHORT)
            : null;

        if (calculation is { IsValid: false })
        {
            return new CopyPositionTargetResult
            {
                Success = false,
                Executed = false,
                Message = calculation.ValidationMessage,
                TraderId = request.TraderId,
                Symbol = request.Symbol,
                Side = request.Side,
                Calculation = calculation,
                ErrorMessage = calculation.ValidationMessage
            };
        }

        var targetContracts = calculation?.Contracts ?? 0m;
        var instrument = calculation?.Instrument ?? await _okxService.GetInstrumentInfoAsync(request.Symbol);
        if (instrument == null)
        {
            return new CopyPositionTargetResult
            {
                Success = false,
                Message = $"Instrument not found: {request.Symbol}",
                TraderId = request.TraderId,
                Symbol = request.Symbol,
                Side = request.Side,
                Calculation = calculation,
                ErrorMessage = $"Instrument not found: {request.Symbol}"
            };
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var position = await _db.CopyTraderPositions
            .FirstOrDefaultAsync(
                x => x.TraderId == request.TraderId &&
                    x.Symbol == request.Symbol &&
                    x.Side == request.Side,
                cancellationToken);

        var previousTraderContracts = position?.TargetContracts ?? 0m;
        var aggregateBefore = await AggregateTargetAsync(request.Symbol, request.Side, cancellationToken);
        var okxBefore = await _okxService.GetAllPositionsAsync();
        var actualBefore = FindActualContracts(okxBefore, request.Symbol, request.Side);
        var aggregateAfter = aggregateBefore - previousTraderContracts + targetContracts;

        var result = new CopyPositionTargetResult
        {
            Success = true,
            Executed = false,
            Message = "Ledger target calculated.",
            TraderId = request.TraderId,
            Symbol = request.Symbol,
            Side = request.Side,
            RequestedTargetContracts = targetContracts,
            PreviousTraderContracts = previousTraderContracts,
            AggregateTargetBefore = aggregateBefore,
            AggregateTargetAfter = aggregateAfter,
            ActualContractsBefore = actualBefore,
            ActualContractsAfter = actualBefore,
            Calculation = calculation
        };

        if (request.Execute)
        {
            var execution = await AlignOkxPositionAsync(
                request,
                instrument,
                aggregateAfter,
                actualBefore,
                cancellationToken);

            result.Success = execution.Success;
            result.Executed = execution.Executed;
            result.Message = execution.Message;
            result.OrderAction = execution.OrderAction;
            result.OrderSize = execution.OrderSize;
            result.OkxOrderId = execution.OkxOrderId;
            result.ErrorMessage = execution.ErrorMessage;
            result.ActualContractsAfter = execution.ActualContractsAfter;
        }

        if (position == null)
        {
            position = new CopyTraderPositionEntity
            {
                TraderId = request.TraderId,
                Symbol = request.Symbol,
                Side = request.Side,
                CreatedAt = DateTime.UtcNow
            };
            _db.CopyTraderPositions.Add(position);
        }

        position.TargetContracts = targetContracts;
        position.TargetMarginUsdt = request.TargetMarginUsdt;
        position.Leverage = request.Leverage;
        position.SourceEventId = request.SourceEventId;
        position.IsActive = targetContracts > 0;
        position.UpdatedAt = DateTime.UtcNow;

        _db.CopyLedgerEvents.Add(new CopyLedgerEventEntity
        {
            TraderId = request.TraderId,
            Symbol = request.Symbol,
            Side = request.Side,
            SourceEventId = request.SourceEventId,
            RequestedTargetContracts = targetContracts,
            PreviousTraderContracts = previousTraderContracts,
            AggregateTargetBefore = aggregateBefore,
            AggregateTargetAfter = aggregateAfter,
            ActualContractsBefore = actualBefore,
            ActualContractsAfter = result.ActualContractsAfter,
            OrderAction = result.OrderAction,
            OrderSize = result.OrderSize,
            OkxOrderId = result.OkxOrderId,
            IsSuccess = result.Success,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
            RawPayload = JsonSerializer.Serialize(new
            {
                request,
                result = new
                {
                    result.Success,
                    result.Executed,
                    result.Message,
                    result.OrderAction,
                    result.OrderSize,
                    result.OkxOrderId,
                    result.ErrorMessage
                }
            }),
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CopyPositionTargetResult> AlignOkxPositionAsync(
        CopyPositionTargetRequest request,
        InstrumentInfo instrument,
        decimal aggregateTarget,
        decimal actualContracts,
        CancellationToken cancellationToken)
    {
        var diff = aggregateTarget - actualContracts;
        var absDiff = Math.Abs(diff);

        if (absDiff <= 0)
        {
            return new CopyPositionTargetResult
            {
                Success = true,
                Message = "OKX already matches ledger target.",
                ActualContractsAfter = actualContracts
            };
        }

        var lotSize = instrument.LotSz > 0 ? instrument.LotSz : instrument.MinSz;
        if (diff > 0)
        {
            var orderSize = RoundDown(absDiff, lotSize);
            if (orderSize < instrument.MinSz)
            {
                return new CopyPositionTargetResult
                {
                    Success = true,
                    Message = $"Increase diff below OKX minimum: {orderSize} < {instrument.MinSz}.",
                    ActualContractsAfter = actualContracts,
                    OrderAction = "SKIP_BELOW_MIN_INCREASE",
                    OrderSize = orderSize
                };
            }

            var leverageOk = await _okxService.SetLeverageAsync(request.Symbol, request.Leverage);
            if (!leverageOk)
            {
                return new CopyPositionTargetResult
                {
                    Success = false,
                    Message = "OKX leverage update failed.",
                    ActualContractsAfter = actualContracts,
                    OrderAction = "SET_LEVERAGE_FAILED",
                    ErrorMessage = "OKX leverage update failed."
                };
            }

            var side = request.Side == "long" ? "buy" : "sell";
            var trade = await _okxService.PlaceMarketOrderAsync(
                request.Symbol,
                side,
                request.Side,
                orderSize);

            var actualAfter = FindActualContracts(await _okxService.GetAllPositionsAsync(), request.Symbol, request.Side);
            return new CopyPositionTargetResult
            {
                Success = trade.Success,
                Executed = trade.Success,
                Message = trade.Success ? "OKX position increased to ledger target." : "OKX increase order failed.",
                ActualContractsAfter = actualAfter,
                OrderAction = request.Side == "long" ? "INCREASE_LONG" : "INCREASE_SHORT",
                OrderSize = orderSize,
                OkxOrderId = trade.OrderId,
                ErrorMessage = trade.ErrorMessage
            };
        }

        if (aggregateTarget <= 0)
        {
            if (actualContracts <= 0)
            {
                return new CopyPositionTargetResult
                {
                    Success = true,
                    Message = "Ledger flat and OKX already flat.",
                    ActualContractsAfter = 0,
                    OrderAction = "NOOP_FLAT"
                };
            }

            var close = await _okxService.ClosePositionAsync(request.Symbol, request.Side);
            var actualAfter = FindActualContracts(await _okxService.GetAllPositionsAsync(), request.Symbol, request.Side);
            return new CopyPositionTargetResult
            {
                Success = close.Success,
                Executed = close.Success,
                Message = close.Success ? "OKX aggregate position fully closed." : "OKX full close failed.",
                ActualContractsAfter = actualAfter,
                OrderAction = request.Side == "long" ? "CLOSE_LONG_FULL" : "CLOSE_SHORT_FULL",
                OrderSize = actualContracts,
                OkxOrderId = close.OrderId,
                ErrorMessage = close.ErrorMessage
            };
        }

        var reduceSize = RoundDown(Math.Min(absDiff, actualContracts), lotSize);
        if (reduceSize < instrument.MinSz)
        {
            return new CopyPositionTargetResult
            {
                Success = true,
                Message = $"Reduce diff below OKX minimum: {reduceSize} < {instrument.MinSz}.",
                ActualContractsAfter = actualContracts,
                OrderAction = "SKIP_BELOW_MIN_REDUCE",
                OrderSize = reduceSize
            };
        }

        var reduceSide = request.Side == "long" ? "sell" : "buy";
        var reduce = await _okxService.PlaceMarketOrderAsync(
            request.Symbol,
            reduceSide,
            request.Side,
            reduceSize,
            reduceOnly: true);

        var reducedActualAfter = FindActualContracts(await _okxService.GetAllPositionsAsync(), request.Symbol, request.Side);
        return new CopyPositionTargetResult
        {
            Success = reduce.Success,
            Executed = reduce.Success,
            Message = reduce.Success ? "OKX position reduced to ledger target." : "OKX reduce order failed.",
            ActualContractsAfter = reducedActualAfter,
            OrderAction = request.Side == "long" ? "REDUCE_LONG" : "REDUCE_SHORT",
            OrderSize = reduceSize,
            OkxOrderId = reduce.OrderId,
            ErrorMessage = reduce.ErrorMessage
        };
    }

    private async Task<decimal> AggregateTargetAsync(
        string symbol,
        string side,
        CancellationToken cancellationToken)
    {
        return await _db.CopyTraderPositions
            .Where(x => x.Symbol == symbol && x.Side == side && x.IsActive)
            .SumAsync(x => x.TargetContracts, cancellationToken);
    }

    private static CopyPositionTargetRequest Normalize(CopyPositionTargetRequest request)
    {
        var traderId = request.TraderId.Trim();
        if (string.IsNullOrWhiteSpace(traderId))
        {
            throw new ArgumentException("TraderId is required.");
        }

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.");
        }

        var side = request.Side.Trim().ToLowerInvariant();
        if (side is not ("long" or "short"))
        {
            throw new ArgumentException("Side must be long or short.");
        }

        return new CopyPositionTargetRequest
        {
            TraderId = traderId,
            Symbol = symbol,
            Side = side,
            TargetMarginUsdt = Math.Max(0, request.TargetMarginUsdt),
            Leverage = request.Leverage <= 0 ? 10 : request.Leverage,
            SourceEventId = request.SourceEventId.Trim(),
            Execute = request.Execute,
            Reason = request.Reason
        };
    }

    private static CopyTraderVirtualPosition MapVirtualPosition(CopyTraderPositionEntity entity)
    {
        return new CopyTraderVirtualPosition
        {
            TraderId = entity.TraderId,
            Symbol = entity.Symbol,
            Side = entity.Side,
            TargetContracts = entity.TargetContracts,
            TargetMarginUsdt = entity.TargetMarginUsdt,
            Leverage = entity.Leverage,
            IsActive = entity.IsActive,
            SourceEventId = entity.SourceEventId,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private static CopyLedgerEventView MapLedgerEvent(CopyLedgerEventEntity entity)
    {
        return new CopyLedgerEventView
        {
            Id = entity.Id,
            TraderId = entity.TraderId,
            Symbol = entity.Symbol,
            Side = entity.Side,
            SourceEventId = entity.SourceEventId,
            RequestedTargetContracts = entity.RequestedTargetContracts,
            PreviousTraderContracts = entity.PreviousTraderContracts,
            AggregateTargetBefore = entity.AggregateTargetBefore,
            AggregateTargetAfter = entity.AggregateTargetAfter,
            ActualContractsBefore = entity.ActualContractsBefore,
            ActualContractsAfter = entity.ActualContractsAfter,
            OrderAction = entity.OrderAction,
            OrderSize = entity.OrderSize,
            OkxOrderId = entity.OkxOrderId,
            IsSuccess = entity.IsSuccess,
            ErrorMessage = entity.ErrorMessage,
            CreatedAt = entity.CreatedAt
        };
    }

    private static List<CopyAggregatePosition> BuildAggregateSnapshot(
        IReadOnlyList<CopyTraderPositionEntity> virtualPositions,
        IReadOnlyList<Position> okxPositions)
    {
        var aggregates = new Dictionary<string, CopyAggregatePosition>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in virtualPositions
            .Where(x => x.IsActive && x.TargetContracts > 0)
            .GroupBy(x => new { x.Symbol, x.Side }))
        {
            var key = AggregateKey(group.Key.Symbol, group.Key.Side);
            aggregates[key] = new CopyAggregatePosition
            {
                Symbol = group.Key.Symbol,
                Side = group.Key.Side,
                TargetContracts = group.Sum(x => x.TargetContracts),
                TraderCount = group.Select(x => x.TraderId).Distinct().Count()
            };
        }

        foreach (var group in okxPositions
            .Select(position => new
            {
                Symbol = position.Symbol.ToUpperInvariant(),
                Side = ToLedgerSide(position.Direction),
                position.Size
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Side))
            .GroupBy(x => new { x.Symbol, x.Side }))
        {
            var key = AggregateKey(group.Key.Symbol, group.Key.Side);
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new CopyAggregatePosition
                {
                    Symbol = group.Key.Symbol,
                    Side = group.Key.Side
                };
                aggregates[key] = aggregate;
            }

            aggregate.ActualContracts = group.Sum(x => x.Size);
        }

        return aggregates.Values
            .Where(x => x.TargetContracts > 0 || x.ActualContracts > 0)
            .OrderBy(x => x.Symbol)
            .ThenBy(x => x.Side)
            .ToList();
    }

    private static string AggregateKey(string symbol, string side)
    {
        return $"{symbol.ToUpperInvariant()}:{side.ToLowerInvariant()}";
    }

    private static string ToLedgerSide(string direction)
    {
        return direction.Equals("Long", StringComparison.OrdinalIgnoreCase)
            ? "long"
            : direction.Equals("Short", StringComparison.OrdinalIgnoreCase)
                ? "short"
                : string.Empty;
    }

    private static decimal FindActualContracts(IEnumerable<Position> positions, string symbol, string side)
    {
        var direction = side == "long" ? "Long" : "Short";
        return positions
            .Where(x =>
                string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Direction, direction, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Size);
    }

    private static decimal RoundDown(decimal value, decimal step)
    {
        if (step <= 0)
        {
            return value;
        }

        return Math.Floor(value / step) * step;
    }
}

using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.Infrastructure.Services;

public sealed class HyperliquidCopyTradingService : IHyperliquidCopyTradingService
{
    private const string InfoUrl = "https://api.hyperliquid.xyz/info";
    private const int CurrentSizingVersion = 3;
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    private static readonly string[] DefaultWatchlist =
    {
        "0x9f6fed26339789725b0751f57aeac23367ceee57",
        "0x77998579f578c01030db65e75edc47bfe890c291",
        "0x0facac3bd128fe6d799898a059d3634b877c7a0a"
    };

    private readonly WhaleTrackerDbContext _db;
    private readonly HttpClient _httpClient;
    private readonly ICopyTradingService _copyTradingService;
    private readonly IOkxService _okxService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<HyperliquidCopyTradingService> _logger;

    public HyperliquidCopyTradingService(
        WhaleTrackerDbContext db,
        HttpClient httpClient,
        ICopyTradingService copyTradingService,
        IOkxService okxService,
        INotificationService notificationService,
        ILogger<HyperliquidCopyTradingService> logger)
    {
        _db = db;
        _httpClient = httpClient;
        _copyTradingService = copyTradingService;
        _okxService = okxService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<HyperliquidCopySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var traders = await _db.HyperliquidCopyTraders
            .AsNoTracking()
            .OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Label)
            .ThenBy(x => x.Address)
            .ToListAsync(cancellationToken);

        var positions = await _db.HyperliquidCopyPositions
            .AsNoTracking()
            .OrderBy(x => x.TraderAddress)
            .ThenBy(x => x.Symbol)
            .ThenBy(x => x.Side)
            .ToListAsync(cancellationToken);

        var events = await _db.HyperliquidCopyEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        return new HyperliquidCopySnapshot
        {
            CheckedAt = DateTime.UtcNow,
            Traders = traders.Select(MapTrader).ToList(),
            Positions = positions.Select(MapPosition).ToList(),
            RecentEvents = events.Select(MapEvent).ToList()
        };
    }

    public async Task<HyperliquidLiveLeaderboardResponse> GetLiveLeaderboardAsync(
        CancellationToken cancellationToken = default)
    {
        var traderRows = await _db.HyperliquidCopyTraders
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var traders = traderRows.ToDictionary(x => x.Address, StringComparer.OrdinalIgnoreCase);

        var latestScores = (await _db.HyperliquidLiveScoreSnapshots
                .AsNoTracking()
                .OrderByDescending(x => x.ScoredAt)
                .ThenByDescending(x => x.Id)
                .Take(1000)
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.TraderAddress, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => x.LiveScore)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => x.NetPnlUsd)
            .ToList();

        var active = await _db.HyperliquidLivePositions
            .AsNoTracking()
            .Where(x => x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN")
            .OrderByDescending(x => x.LastSeenAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var closed = await _db.HyperliquidLivePositions
            .AsNoTracking()
            .Where(x => x.Status == "CLOSED")
            .OrderByDescending(x => x.ClosedAt ?? x.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var fills = await _db.HyperliquidLiveFills
            .AsNoTracking()
            .OrderByDescending(x => x.ExchangeTime)
            .Take(200)
            .ToListAsync(cancellationToken);

        return new HyperliquidLiveLeaderboardResponse
        {
            CheckedAt = DateTime.UtcNow,
            Traders = latestScores.Select(score =>
            {
                traders.TryGetValue(score.TraderAddress, out var trader);
                return MapLiveScore(score, trader);
            }).ToList(),
            ActivePositions = active.Select(MapLivePosition).ToList(),
            ClosedPositions = closed.Select(MapLivePosition).ToList(),
            RecentFills = fills.Select(MapLiveFill).ToList()
        };
    }

    public async Task<HyperliquidCopyEnableResponse> EnableAsync(
        HyperliquidCopyEnableRequest request,
        CancellationToken cancellationToken = default)
    {
        var addresses = (request.TraderAddresses.Count > 0 ? request.TraderAddresses : DefaultWatchlist)
            .Select(NormalizeAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0)
        {
            throw new ArgumentException("At least one trader address is required.");
        }

        var now = DateTime.UtcNow;
        foreach (var address in addresses)
        {
            var trader = await _db.HyperliquidCopyTraders
                .FirstOrDefaultAsync(x => x.Address == address, cancellationToken);

            if (trader == null)
            {
                trader = new HyperliquidCopyTraderEntity
                {
                    Address = address,
                    CreatedAt = now
                };
                _db.HyperliquidCopyTraders.Add(trader);
            }

            trader.Label = string.IsNullOrWhiteSpace(request.LabelPrefix)
                ? ShortAddress(address)
                : $"{request.LabelPrefix} {ShortAddress(address)}";
            trader.IsEnabled = true;
            trader.ExecuteOrders = request.ExecuteOrders;
            trader.MarginPerTraderUsdt = Math.Max(0, request.MarginPerTraderUsdt);
            trader.Leverage = request.Leverage <= 0 ? 10 : request.Leverage;
            trader.CopyActiveOnEnable = request.CopyActiveOnEnable;
            trader.AdoptActiveOnlyWhenNegative = request.AdoptActiveOnlyWhenNegative;
            trader.LastError = string.Empty;
            trader.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var syncResults = new List<HyperliquidCopyTraderSyncResult>();
        if (request.SyncImmediately)
        {
            foreach (var address in addresses)
            {
                syncResults.Add(await SyncTraderAsync(address, request.ExecuteOrders, cancellationToken));
            }
        }

        return new HyperliquidCopyEnableResponse
        {
            Success = true,
            Message = request.ExecuteOrders
                ? "Hyperliquid copy trading enabled with live OKX execution."
                : "Hyperliquid copy trading enabled in paper mode.",
            Snapshot = await GetSnapshotAsync(cancellationToken),
            SyncResults = syncResults
        };
    }

    public async Task<IReadOnlyList<HyperliquidCopyTraderSyncResult>> SyncEnabledTradersAsync(
        CancellationToken cancellationToken = default)
    {
        var addresses = await _db.HyperliquidCopyTraders
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.LastSyncAt ?? DateTime.MinValue)
            .Select(x => x.Address)
            .ToListAsync(cancellationToken);

        var results = new List<HyperliquidCopyTraderSyncResult>();
        foreach (var address in addresses)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            results.Add(await SyncTraderAsync(address, null, cancellationToken));
        }

        return results;
    }

    public async Task<HyperliquidCopyTraderSyncResult> SyncTraderAsync(
        string traderAddress,
        bool? executeOverride = null,
        CancellationToken cancellationToken = default)
    {
        var address = NormalizeAddress(traderAddress);
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            return await SyncTraderLockedAsync(address, executeOverride, cancellationToken);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public async Task<bool> DisableAsync(string traderAddress, CancellationToken cancellationToken = default)
    {
        var address = NormalizeAddress(traderAddress);
        var trader = await _db.HyperliquidCopyTraders
            .FirstOrDefaultAsync(x => x.Address == address, cancellationToken);
        if (trader == null)
        {
            return false;
        }

        trader.IsEnabled = false;
        trader.UpdatedAt = DateTime.UtcNow;
        await AddEventAsync(address, string.Empty, string.Empty, "DISABLED", string.Empty, "Trader disabled.", 0, true, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<HyperliquidCopyTraderSyncResult> SyncTraderLockedAsync(
        string address,
        bool? executeOverride,
        CancellationToken cancellationToken)
    {
        var trader = await _db.HyperliquidCopyTraders
            .FirstOrDefaultAsync(x => x.Address == address, cancellationToken);
        if (trader == null)
        {
            throw new ArgumentException($"Trader not enabled: {address}");
        }

        try
        {
            var state = await FetchAsync<HyperliquidStateRequest, HyperliquidClearinghouseState>(
                new HyperliquidStateRequest("clearinghouseState", address),
                cancellationToken);
            var sourceAccountValue = state.MarginSummary.AccountValueUsd;
            if (sourceAccountValue <= 0)
            {
                throw new InvalidOperationException(
                    $"Hyperliquid account value is not positive for {address}.");
            }

            var isInitialSync = trader.LastSyncAt == null;
            var now = DateTime.UtcNow;
            var newFills = new List<HyperliquidFill>();
            if (trader.LastFillPollAt == null ||
                now - trader.LastFillPollAt >= TimeSpan.FromSeconds(30))
            {
                trader.LastFillPollAt = now;
                try
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    newFills = await FetchNewFillsAsync(
                        address,
                        trader.LastSeenFillTimeMs,
                        nowMs,
                        cancellationToken);
                    if (newFills.Count > 0)
                    {
                        trader.LastSeenFillTimeMs = Math.Max(
                            trader.LastSeenFillTimeMs,
                            newFills.Max(x => x.Time));
                        await AddEventAsync(
                            address,
                            string.Empty,
                            string.Empty,
                            "FILLS_DETECTED",
                            $"hl-fills:{address}:{trader.LastSeenFillTimeMs}",
                            $"{newFills.Count} new Hyperliquid fills detected.",
                            0,
                            true,
                            new { fills = newFills.TakeLast(20) },
                            cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Hyperliquid fill polling failed for {Address}; state sync will continue.",
                        address);
                }
            }

            var active = state.AssetPositions
                .Select(x => x.Position)
                .Where(x => x != null && Math.Abs(x.Size) > 0)
                .Select(x => x!)
                .ToList();

            var supportedSymbols = (await _okxService.GetSupportedSymbolsAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (supportedSymbols.Count == 0)
            {
                throw new InvalidOperationException(
                    "OKX live USDT perpetual symbol universe is empty; copy sync stopped safely.");
            }

            foreach (var position in active)
            {
                position.OkxSymbol = HyperliquidSymbolMapper.ToOkxSymbol(position.Coin);
            }

            var copyableActive = active
                .Where(x => supportedSymbols.Contains(x.OkxSymbol))
                .ToList();
            var currentKeys = copyableActive
                .Select(x => PositionKey(address, x.OkxSymbol, SideFromSize(x.Size)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await RecordLiveFillsAsync(address, newFills, cancellationToken);
            foreach (var position in copyableActive)
            {
                var hasNewFill = newFills.Any(fill =>
                    string.Equals(
                        HyperliquidSymbolMapper.ToOkxSymbol(fill.Coin),
                        position.OkxSymbol,
                        StringComparison.OrdinalIgnoreCase));
                await UpsertLivePositionAsync(
                    address,
                    position,
                    sourceAccountValue,
                    supportedSymbols.Contains(position.OkxSymbol),
                    isInitialSync,
                    hasNewFill,
                    cancellationToken);
            }

            var existingPositions = await _db.HyperliquidCopyPositions
                .Where(x => x.TraderAddress == address)
                .ToListAsync(cancellationToken);

            var decisions = new List<HyperliquidCopyPositionDecision>();

            var candidates = new List<HyperliquidPosition>();
            foreach (var position in copyableActive)
            {
                var side = SideFromSize(position.Size);
                var existing = existingPositions.FirstOrDefault(x =>
                    (x.Symbol.Equals(position.OkxSymbol, StringComparison.OrdinalIgnoreCase) ||
                     x.Symbol.Equals(position.Coin, StringComparison.OrdinalIgnoreCase)) &&
                    x.Side.Equals(side, StringComparison.OrdinalIgnoreCase));

                var alreadyCopied = existing?.Status == "COPIED";
                var alreadyBelowMinimum = existing?.Status == "SKIPPED_BELOW_MIN";
                var hasNewFill = newFills.Any(fill =>
                    string.Equals(
                        HyperliquidSymbolMapper.ToOkxSymbol(fill.Coin),
                        position.OkxSymbol,
                        StringComparison.OrdinalIgnoreCase));
                var canAdopt = HyperliquidCopyAdoptionPolicy.ShouldAdopt(
                    alreadyCopied,
                    alreadyBelowMinimum,
                    existing == null,
                    existing?.Status == "CLOSED",
                    isInitialSync,
                    trader.CopyActiveOnEnable,
                    trader.AdoptActiveOnlyWhenNegative,
                    position.UnrealizedPnlUsd,
                    hasNewFill);

                if (canAdopt)
                {
                    candidates.Add(position);
                }
            }

            var copied = 0;
            var skipped = 0;
            var closed = 0;

            foreach (var position in copyableActive)
            {
                var side = SideFromSize(position.Size);
                var key = PositionKey(address, position.OkxSymbol, side);
                var existing = existingPositions.FirstOrDefault(x =>
                    (x.Symbol.Equals(position.OkxSymbol, StringComparison.OrdinalIgnoreCase) ||
                     x.Symbol.Equals(position.Coin, StringComparison.OrdinalIgnoreCase)) &&
                    x.Side.Equals(side, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    existing = new HyperliquidCopyPositionEntity
                    {
                        TraderAddress = address,
                        Symbol = position.OkxSymbol,
                        Side = side,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.HyperliquidCopyPositions.Add(existing);
                    existingPositions.Add(existing);
                }

                var previousStatus = existing.Status;
                var previousSize = existing.SourceSize;
                var previousTargetMargin = existing.TargetMarginUsdt;
                var previousSizingBudget = existing.SizingBudgetUsdt;
                var previousSizingLeverage = existing.SizingLeverage;
                var previousSizingVersion = existing.SizingVersion;
                UpdateSourcePosition(existing, position, sourceAccountValue);

                var alreadyCopied = string.Equals(previousStatus, "COPIED", StringComparison.OrdinalIgnoreCase);
                var alreadyBelowMinimum = string.Equals(
                    previousStatus,
                    "SKIPPED_BELOW_MIN",
                    StringComparison.OrdinalIgnoreCase);
                var alreadySized = alreadyCopied || alreadyBelowMinimum;
                var sourceSizeChanged = Math.Abs(previousSize - position.Size) > 0.00000001m;
                var shouldCopy = candidates.Contains(position);
                if (!shouldCopy)
                {
                    skipped++;
                    var opportunityAlreadyMissed = string.Equals(
                        previousStatus,
                        "SKIPPED_PROFIT",
                        StringComparison.OrdinalIgnoreCase);
                    existing.Status = opportunityAlreadyMissed || position.UnrealizedPnlUsd > 0
                        ? "SKIPPED_PROFIT"
                        : "SKIPPED";
                    existing.TargetMarginUsdt = 0;
                    existing.LastMessage = existing.Status == "SKIPPED_PROFIT"
                        ? "Active position was already in profit; opportunity remains missed until it closes."
                        : "Position skipped by copy rules.";
                    existing.UpdatedAt = DateTime.UtcNow;
                    decisions.Add(ToDecision(existing));
                    if (!string.Equals(previousStatus, existing.Status, StringComparison.OrdinalIgnoreCase))
                    {
                        await AddEventAsync(address, position.OkxSymbol, side, existing.Status, key, existing.LastMessage, 0, true, position, cancellationToken);
                    }
                    continue;
                }

                var desiredTargetMargin = HyperliquidCopySizingMath.TargetMarginUsdt(
                    trader.MarginPerTraderUsdt,
                    position.PositionValueUsd,
                    sourceAccountValue,
                    trader.Leverage);
                var sizingConfigurationChanged =
                    previousSizingVersion != CurrentSizingVersion ||
                    previousSizingBudget != trader.MarginPerTraderUsdt ||
                    previousSizingLeverage != trader.Leverage;
                var executeRequested = executeOverride ?? trader.ExecuteOrders;
                var previousErrored = string.Equals(previousStatus, "ERROR", StringComparison.OrdinalIgnoreCase);
                var shouldAlignTarget = (!alreadySized && !previousErrored) ||
                    sourceSizeChanged ||
                    sizingConfigurationChanged ||
                    (executeRequested && existing.LastCopiedAt == null && !previousErrored);
                var targetMargin = shouldAlignTarget ? desiredTargetMargin : previousTargetMargin;

                existing.Status = shouldAlignTarget ? "COPIED" : previousStatus;
                existing.TargetMarginUsdt = targetMargin;
                existing.SizingBudgetUsdt = trader.MarginPerTraderUsdt;
                existing.SizingLeverage = trader.Leverage;
                existing.SizingVersion = CurrentSizingVersion;
                existing.LastMessage = shouldAlignTarget
                    ? alreadySized
                        ? "Normalized source exposure target refreshed."
                        : "Active position adopted with normalized source exposure sizing."
                    : alreadyBelowMinimum
                        ? "Source position unchanged; normalized target remains below OKX minimum."
                        : "Source position unchanged; copied target left as-is.";
                existing.UpdatedAt = DateTime.UtcNow;

                CopyPositionTargetResult? okxResult = null;
                if (shouldAlignTarget)
                {
                    okxResult = await _copyTradingService.SetTraderPositionTargetAsync(
                        new CopyPositionTargetRequest
                        {
                            TraderId = LedgerTraderId(address),
                            Symbol = position.OkxSymbol,
                            Side = side,
                            TargetMarginUsdt = targetMargin,
                            Leverage = trader.Leverage,
                            Execute = executeRequested,
                            CloseIfBelowMinimum = true,
                            MaximumUpwardMarginDeviationPercent = 10m,
                            SourceEventId = key,
                            Reason = $"Hyperliquid copy {ShortAddress(address)} {position.Coin}->{position.OkxSymbol} {side}"
                        },
                        cancellationToken);

                    if (okxResult.Success)
                    {
                        if (desiredTargetMargin > 0 &&
                            okxResult.RequestedTargetContracts <= 0)
                        {
                            existing.Status = "SKIPPED_BELOW_MIN";
                            existing.TargetMarginUsdt = 0;
                            existing.LastMessage =
                                "Normalized target is below OKX minimum; position left flat.";
                            skipped++;
                        }
                        else
                        {
                            if (executeRequested)
                            {
                                existing.LastCopiedAt = DateTime.UtcNow;
                            }

                            copied++;
                        }
                    }
                    else
                    {
                        existing.Status = "ERROR";
                        existing.LastMessage = okxResult.ErrorMessage ?? okxResult.Message;
                    }
                }
                else
                {
                    if (alreadyCopied)
                    {
                        copied++;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                decisions.Add(ToDecision(existing, okxResult));
                if (shouldAlignTarget ||
                    !string.Equals(previousStatus, existing.Status, StringComparison.OrdinalIgnoreCase))
                {
                    await AddEventAsync(
                        address,
                        position.OkxSymbol,
                        side,
                        existing.Status,
                        key,
                        existing.LastMessage,
                        targetMargin,
                        okxResult?.Success ?? true,
                        new { source = position, okx = okxResult },
                        cancellationToken);
                }
            }

            foreach (var existing in existingPositions.Where(x =>
                x.Status == "COPIED" &&
                !currentKeys.Contains(PositionKey(address, x.Symbol, x.Side))).ToList())
            {
                var closeEventId = $"hl-close:{address}:{existing.Symbol}:{existing.Side}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                var okxResult = await _copyTradingService.SetTraderPositionTargetAsync(
                    new CopyPositionTargetRequest
                    {
                        TraderId = LedgerTraderId(address),
                        Symbol = existing.Symbol,
                        Side = existing.Side,
                        TargetMarginUsdt = 0,
                        Leverage = trader.Leverage,
                        Execute = executeOverride ?? trader.ExecuteOrders,
                        SourceEventId = closeEventId,
                        Reason = $"Hyperliquid source position closed {ShortAddress(address)} {existing.Symbol} {existing.Side}"
                    },
                    cancellationToken);

                existing.Status = "CLOSED";
                existing.TargetMarginUsdt = 0;
                existing.LastMessage = okxResult.Success
                    ? "Source position is gone; copied OKX target closed."
                    : okxResult.ErrorMessage ?? okxResult.Message;
                existing.UpdatedAt = DateTime.UtcNow;
                closed++;
                decisions.Add(ToDecision(existing, okxResult));
                await AddEventAsync(address, existing.Symbol, existing.Side, "CLOSED", closeEventId, existing.LastMessage, 0, okxResult.Success, okxResult, cancellationToken);
            }

            foreach (var existing in existingPositions.Where(x =>
                x.Status != "COPIED" &&
                x.Status != "CLOSED" &&
                !currentKeys.Contains(PositionKey(address, x.Symbol, x.Side))).ToList())
            {
                existing.Status = "CLOSED";
                existing.TargetMarginUsdt = 0;
                existing.LastMessage =
                    "Source position closed; a future reopen will be treated as a new live position.";
                existing.UpdatedAt = DateTime.UtcNow;
                await AddEventAsync(
                    address,
                    existing.Symbol,
                    existing.Side,
                    "CLOSED",
                    $"hl-observed-close:{address}:{existing.Symbol}:{existing.Side}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    existing.LastMessage,
                    0,
                    true,
                    null,
                    cancellationToken);
            }

            await CloseMissingLivePositionsAsync(address, currentKeys, cancellationToken);
            await RefreshLiveScoreAsync(address, cancellationToken);

            trader.LastSyncAt = DateTime.UtcNow;
            trader.LastError = string.Empty;
            trader.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return new HyperliquidCopyTraderSyncResult
            {
                TraderAddress = address,
                Success = true,
                Message = "Synced.",
                ActiveSourcePositions = active.Count,
                CopiedPositions = copied,
                SkippedPositions = skipped,
                ClosedTargets = closed,
                NewFills = newFills.Count,
                Decisions = decisions
            };
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            var failedTrader = await _db.HyperliquidCopyTraders
                .FirstOrDefaultAsync(x => x.Address == address, cancellationToken);
            if (failedTrader != null)
            {
                failedTrader.LastError = ex.Message;
                failedTrader.LastSyncAt = DateTime.UtcNow;
                failedTrader.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogWarning(ex, "Hyperliquid copy sync failed for {Address}", address);
            return new HyperliquidCopyTraderSyncResult
            {
                TraderAddress = address,
                Success = false,
                Message = ex.Message
            };
        }
    }

    private async Task<List<HyperliquidFill>> FetchNewFillsAsync(
        string address,
        long lastSeenFillTimeMs,
        long nowMs,
        CancellationToken cancellationToken)
    {
        var start = lastSeenFillTimeMs > 0
            ? lastSeenFillTimeMs + 1
            : nowMs - 24 * 60 * 60 * 1000;
        var fills = await FetchAsync<HyperliquidFillsRequest, List<HyperliquidFill>>(
            new HyperliquidFillsRequest("userFillsByTime", address, start, nowMs),
            cancellationToken);
        return fills.OrderBy(x => x.Time).ToList();
    }

    private async Task RecordLiveFillsAsync(
        string address,
        IReadOnlyList<HyperliquidFill> fills,
        CancellationToken cancellationToken)
    {
        if (fills.Count == 0)
        {
            return;
        }

        var keys = fills
            .Select(fill => LiveFillDedupeKey(address, fill))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var existingKeys = await _db.HyperliquidLiveFills
            .AsNoTracking()
            .Where(x => keys.Contains(x.DedupeKey))
            .Select(x => x.DedupeKey)
            .ToListAsync(cancellationToken);
        var existingSet = existingKeys.ToHashSet(StringComparer.Ordinal);

        foreach (var fill in fills)
        {
            var key = LiveFillDedupeKey(address, fill);
            if (existingSet.Contains(key))
            {
                continue;
            }

            existingSet.Add(key);
            var exchangeTime = DateTimeOffset
                .FromUnixTimeMilliseconds(fill.Time)
                .UtcDateTime;
            _db.HyperliquidLiveFills.Add(new HyperliquidLiveFillEntity
            {
                TraderAddress = address,
                Coin = fill.Coin.ToUpperInvariant(),
                OkxSymbol = HyperliquidSymbolMapper.ToOkxSymbol(fill.Coin),
                Side = FillPositionSide(fill),
                Direction = fill.Dir,
                Price = ParseDecimal(fill.Px),
                Size = Math.Abs(ParseDecimal(fill.Sz)),
                ClosedPnlUsd = ParseDecimal(fill.ClosedPnl),
                FeeUsd = Math.Abs(ParseDecimal(fill.Fee)),
                ExchangeTimeMs = fill.Time,
                ExchangeTime = exchangeTime,
                DedupeKey = key,
                RawPayload = JsonSerializer.Serialize(fill),
                CreatedAt = DateTime.UtcNow
            });
        }

        var closeFills = fills
            .Where(fill => FillIsClose(fill))
            .Take(5)
            .ToList();
        foreach (var fill in closeFills)
        {
            await _notificationService.SendAsync(
                "Hyperliquid close fill",
                $"{ShortAddress(address)} {fill.Coin} {fill.Dir} @ {fill.Px} pnl ${ParseDecimal(fill.ClosedPnl):0.##} fee ${Math.Abs(ParseDecimal(fill.Fee)):0.####}",
                cancellationToken);
        }
    }

    private async Task UpsertLivePositionAsync(
        string address,
        HyperliquidPosition position,
        decimal sourceAccountValue,
        bool isOkxTradable,
        bool isInitialSync,
        bool hasNewFill,
        CancellationToken cancellationToken)
    {
        var side = SideFromSize(position.Size);
        var now = DateTime.UtcNow;
        var existing = await _db.HyperliquidLivePositions
            .Where(x =>
                x.TraderAddress == address &&
                x.OkxSymbol == position.OkxSymbol &&
                x.Side == side &&
                (x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN"))
            .OrderByDescending(x => x.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var openedFromTracking = !isInitialSync;
        if (existing == null)
        {
            existing = new HyperliquidLivePositionEntity
            {
                TraderAddress = address,
                Coin = position.Coin.ToUpperInvariant(),
                OkxSymbol = position.OkxSymbol,
                Side = side,
                Status = openedFromTracking ? "LIVE_OPEN" : "BASELINE_OPEN",
                OpenedAt = now,
                FirstSeenAt = now,
                SourceAccountValueAtOpen = sourceAccountValue,
                EntryPrice = position.EntryPrice,
                OpenedFromTracking = openedFromTracking,
                CreatedAt = now
            };
            _db.HyperliquidLivePositions.Add(existing);

            if (openedFromTracking)
            {
                await _notificationService.SendAsync(
                    "Hyperliquid live position opened",
                    $"{ShortAddress(address)} {position.Coin}->{position.OkxSymbol} {side.ToUpperInvariant()} notional ${Math.Abs(position.PositionValueUsd):0.##} entry {position.EntryPrice:0.####} account ${sourceAccountValue:0.##}",
                    cancellationToken);
            }
        }

        existing.Coin = position.Coin.ToUpperInvariant();
        existing.OkxSymbol = position.OkxSymbol;
        existing.LastSeenAt = now;
        existing.EntryPrice = existing.EntryPrice <= 0 ? position.EntryPrice : existing.EntryPrice;
        existing.CurrentSize = Math.Abs(position.Size);
        existing.MaxSize = Math.Max(existing.MaxSize, Math.Abs(position.Size));
        existing.CurrentNotionalUsd = Math.Abs(position.PositionValueUsd);
        existing.MaxNotionalUsd = Math.Max(existing.MaxNotionalUsd, Math.Abs(position.PositionValueUsd));
        existing.LatestSourceAccountValueUsd = sourceAccountValue;
        existing.PositionPctOfAccount = sourceAccountValue > 0
            ? Math.Abs(position.PositionValueUsd) / sourceAccountValue * 100m
            : 0m;
        existing.UnrealizedPnlUsd = position.UnrealizedPnlUsd;
        existing.IsOkxTradable = isOkxTradable;
        existing.CopyStatus = isOkxTradable ? "OKX_TRADABLE" : "NOT_OKX_TRADABLE";
        existing.UpdatedAt = now;
    }

    private async Task CloseMissingLivePositionsAsync(
        string address,
        HashSet<string> currentKeys,
        CancellationToken cancellationToken)
    {
        var openPositions = await _db.HyperliquidLivePositions
            .Where(x =>
                x.TraderAddress == address &&
                (x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN"))
            .ToListAsync(cancellationToken);

        foreach (var position in openPositions)
        {
            if (currentKeys.Contains(PositionKey(address, position.OkxSymbol, position.Side)))
            {
                continue;
            }

            var fills = await _db.HyperliquidLiveFills
                .Where(x =>
                    x.TraderAddress == address &&
                    x.OkxSymbol == position.OkxSymbol &&
                    x.ExchangeTime >= position.OpenedAt)
                .OrderBy(x => x.ExchangeTime)
                .ToListAsync(cancellationToken);
            var closeFills = fills.Where(fill =>
                fill.Direction.Contains("Close", StringComparison.OrdinalIgnoreCase) &&
                fill.Side.Equals(position.Side, StringComparison.OrdinalIgnoreCase)).ToList();
            var lastClose = closeFills.LastOrDefault();
            var now = DateTime.UtcNow;

            position.Status = "CLOSED";
            position.ClosedAt = lastClose?.ExchangeTime ?? now;
            position.ExitPrice = lastClose?.Price ?? 0;
            position.RealizedPnlUsd = closeFills.Sum(x => x.ClosedPnlUsd);
            position.FeeUsd = fills.Sum(x => x.FeeUsd);
            position.NetPnlUsd = position.RealizedPnlUsd - position.FeeUsd;
            position.CurrentSize = 0;
            position.CurrentNotionalUsd = 0;
            position.UnrealizedPnlUsd = 0;
            position.UpdatedAt = now;

            await _notificationService.SendAsync(
                "Hyperliquid position closed",
                $"{ShortAddress(address)} {position.OkxSymbol} {position.Side.ToUpperInvariant()} duration {FormatDuration(position.ClosedAt.Value - position.OpenedAt)} net ${position.NetPnlUsd:0.##} source {(position.OpenedFromTracking ? "live" : "baseline")}",
                cancellationToken);
        }
    }

    private async Task RefreshLiveScoreAsync(string address, CancellationToken cancellationToken)
    {
        var positions = await _db.HyperliquidLivePositions
            .Where(x => x.TraderAddress == address)
            .ToListAsync(cancellationToken);
        var closed = positions.Where(x => x.Status == "CLOSED" && x.OpenedFromTracking).ToList();
        var active = positions.Where(x => x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN").ToList();
        var liveActive = active.Where(x => x.OpenedFromTracking).ToList();
        var wins = closed.Count(x => x.NetPnlUsd > 0);
        var losses = closed.Count(x => x.NetPnlUsd < 0);
        var closedCount = closed.Count;
        var winRate = closedCount > 0 ? wins / (decimal)closedCount * 100m : 0m;
        var realized = closed.Sum(x => x.NetPnlUsd);
        var unrealized = liveActive.Sum(x => x.UnrealizedPnlUsd);
        var averageAccount = closed
            .Where(x => x.SourceAccountValueAtOpen > 0)
            .Select(x => x.SourceAccountValueAtOpen)
            .DefaultIfEmpty(liveActive.Select(x => x.LatestSourceAccountValueUsd).FirstOrDefault(x => x > 0))
            .Average();
        var pnlPctAccount = averageAccount > 0 ? realized / averageAccount * 100m : 0m;
        var copyable = positions.Count(x => x.IsOkxTradable);
        var copied = await _db.HyperliquidCopyPositions
            .CountAsync(x => x.TraderAddress == address && x.Status == "COPIED", cancellationToken);
        var skipped = await _db.HyperliquidCopyPositions
            .CountAsync(x => x.TraderAddress == address && x.Status.StartsWith("SKIPPED"), cancellationToken);
        var avgHoldSeconds = closed
            .Where(x => x.ClosedAt != null)
            .Select(x => (decimal)(x.ClosedAt!.Value - x.OpenedAt).TotalSeconds)
            .DefaultIfEmpty(0)
            .Average();
        var best = closed.Select(x => x.NetPnlUsd).DefaultIfEmpty(0).Max();
        var worst = closed.Select(x => x.NetPnlUsd).DefaultIfEmpty(0).Min();
        var liveSampleCount = closedCount + liveActive.Count;
        var sampleScore = Math.Min(25m, closedCount * 3m + liveActive.Count);
        var pnlScore = liveSampleCount > 0 ? Math.Clamp(17.5m + pnlPctAccount * 5m, 0m, 35m) : 0m;
        var winScore = closedCount > 0 ? Math.Clamp(winRate / 100m * 20m, 0m, 20m) : 0m;
        var copyScore = liveSampleCount > 0 && positions.Count > 0
            ? Math.Clamp(copyable / (decimal)positions.Count * 15m, 0m, 15m)
            : 0m;
        var liveScore = liveSampleCount > 0
            ? Math.Clamp(sampleScore + pnlScore + winScore + copyScore, 0m, 100m)
            : 0m;
        var confidence = liveSampleCount > 0
            ? Math.Clamp(closedCount * 5m + liveActive.Count * 3m + copyable, 0m, 100m)
            : 0m;

        _db.HyperliquidLiveScoreSnapshots.Add(new HyperliquidLiveScoreSnapshotEntity
        {
            TraderAddress = address,
            ScoredAt = DateTime.UtcNow,
            LiveScore = liveScore,
            Confidence = confidence,
            RealizedPnlUsd = realized,
            UnrealizedPnlUsd = unrealized,
            NetPnlUsd = realized + unrealized,
            PnlPctAccount = pnlPctAccount,
            ClosedPositions = closedCount,
            ActivePositions = liveActive.Count,
            Wins = wins,
            Losses = losses,
            WinRate = winRate,
            OkxCopyablePositions = copyable,
            CopiedPositions = copied,
            SkippedPositions = skipped,
            AvgHoldSeconds = avgHoldSeconds,
            BestTradeUsd = best,
            WorstTradeUsd = worst,
            ScoreComponentsJson = JsonSerializer.Serialize(new
            {
                sampleScore,
                pnlScore,
                winScore,
                copyScore,
                averageAccount
            })
        });
    }

    private async Task<TResponse> FetchAsync<TRequest, TResponse>(
        TRequest payload,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(InfoUrl, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Hyperliquid HTTP {(int)response.StatusCode}: {body}");
        }

        return JsonSerializer.Deserialize<TResponse>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("Hyperliquid returned empty response.");
    }

    private async Task AddEventAsync(
        string address,
        string symbol,
        string side,
        string eventType,
        string sourceEventId,
        string message,
        decimal targetMargin,
        bool isSuccess,
        object? rawPayload,
        CancellationToken cancellationToken)
    {
        _db.HyperliquidCopyEvents.Add(new HyperliquidCopyEventEntity
        {
            TraderAddress = address,
            Symbol = symbol.ToUpperInvariant(),
            Side = side,
            EventType = eventType,
            SourceEventId = sourceEventId,
            Message = message,
            TargetMarginUsdt = targetMargin,
            IsSuccess = isSuccess,
            RawPayload = rawPayload == null ? string.Empty : JsonSerializer.Serialize(rawPayload),
            CreatedAt = DateTime.UtcNow
        });
        await Task.CompletedTask;
    }

    private static void UpdateSourcePosition(
        HyperliquidCopyPositionEntity entity,
        HyperliquidPosition position,
        decimal sourceAccountValue)
    {
        entity.Symbol = position.OkxSymbol;
        entity.Side = SideFromSize(position.Size);
        entity.SourceSize = position.Size;
        entity.SourceEntryPrice = position.EntryPrice;
        entity.SourcePositionValueUsd = Math.Abs(position.PositionValueUsd);
        entity.SourceMarginUsedUsd = position.MarginUsedUsd;
        entity.SourceUnrealizedPnlUsd = position.UnrealizedPnlUsd;
        entity.SourceAccountValueUsd = sourceAccountValue;
        entity.SourceExposurePercent = HyperliquidCopySizingMath.ExposurePercent(
            position.PositionValueUsd,
            sourceAccountValue);
        entity.SourceMarginPercent = HyperliquidCopySizingMath.MarginPercent(
            position.MarginUsedUsd,
            sourceAccountValue);
        entity.LastSourceSeenAt = DateTime.UtcNow;
    }

    private static HyperliquidCopyPositionDecision ToDecision(
        HyperliquidCopyPositionEntity entity,
        CopyPositionTargetResult? okxResult = null)
    {
        return new HyperliquidCopyPositionDecision
        {
            Symbol = entity.Symbol,
            Side = entity.Side,
            Status = entity.Status,
            Message = entity.LastMessage,
            SourceUnrealizedPnlUsd = entity.SourceUnrealizedPnlUsd,
            SourcePositionValueUsd = entity.SourcePositionValueUsd,
            SourceAccountValueUsd = entity.SourceAccountValueUsd,
            SourceExposurePercent = entity.SourceExposurePercent,
            SourceMarginPercent = entity.SourceMarginPercent,
            TargetMarginUsdt = entity.TargetMarginUsdt,
            OkxResult = okxResult
        };
    }

    private static HyperliquidCopyTraderView MapTrader(HyperliquidCopyTraderEntity entity) => new()
    {
        Address = entity.Address,
        Label = entity.Label,
        IsEnabled = entity.IsEnabled,
        ExecuteOrders = entity.ExecuteOrders,
        MarginPerTraderUsdt = entity.MarginPerTraderUsdt,
        Leverage = entity.Leverage,
        AdoptActiveOnlyWhenNegative = entity.AdoptActiveOnlyWhenNegative,
        CopyActiveOnEnable = entity.CopyActiveOnEnable,
        LastSeenFillTimeMs = entity.LastSeenFillTimeMs,
        LastFillPollAt = entity.LastFillPollAt,
        LastSyncAt = entity.LastSyncAt,
        LastError = entity.LastError
    };

    private static HyperliquidCopyPositionView MapPosition(HyperliquidCopyPositionEntity entity) => new()
    {
        TraderAddress = entity.TraderAddress,
        Symbol = entity.Symbol,
        Side = entity.Side,
        Status = entity.Status,
        SourceSize = entity.SourceSize,
        SourceEntryPrice = entity.SourceEntryPrice,
        SourcePositionValueUsd = entity.SourcePositionValueUsd,
        SourceMarginUsedUsd = entity.SourceMarginUsedUsd,
        SourceUnrealizedPnlUsd = entity.SourceUnrealizedPnlUsd,
        SourceAccountValueUsd = entity.SourceAccountValueUsd,
        SourceExposurePercent = entity.SourceExposurePercent,
        SourceMarginPercent = entity.SourceMarginPercent,
        TargetMarginUsdt = entity.TargetMarginUsdt,
        SizingBudgetUsdt = entity.SizingBudgetUsdt,
        SizingLeverage = entity.SizingLeverage,
        SizingVersion = entity.SizingVersion,
        LastSourceSeenAt = entity.LastSourceSeenAt,
        LastCopiedAt = entity.LastCopiedAt,
        LastMessage = entity.LastMessage
    };

    private static HyperliquidCopyEventView MapEvent(HyperliquidCopyEventEntity entity) => new()
    {
        Id = entity.Id,
        TraderAddress = entity.TraderAddress,
        Symbol = entity.Symbol,
        Side = entity.Side,
        EventType = entity.EventType,
        SourceEventId = entity.SourceEventId,
        Message = entity.Message,
        TargetMarginUsdt = entity.TargetMarginUsdt,
        IsSuccess = entity.IsSuccess,
        CreatedAt = entity.CreatedAt
    };

    private static HyperliquidLiveTraderScoreView MapLiveScore(
        HyperliquidLiveScoreSnapshotEntity score,
        HyperliquidCopyTraderEntity? trader) => new()
    {
        TraderAddress = score.TraderAddress,
        Label = trader?.Label ?? ShortAddress(score.TraderAddress),
        IsEnabled = trader?.IsEnabled ?? false,
        ExecuteOrders = trader?.ExecuteOrders ?? false,
        LiveScore = score.LiveScore,
        Confidence = score.Confidence,
        RealizedPnlUsd = score.RealizedPnlUsd,
        UnrealizedPnlUsd = score.UnrealizedPnlUsd,
        NetPnlUsd = score.NetPnlUsd,
        PnlPctAccount = score.PnlPctAccount,
        ClosedPositions = score.ClosedPositions,
        ActivePositions = score.ActivePositions,
        Wins = score.Wins,
        Losses = score.Losses,
        WinRate = score.WinRate,
        OkxCopyablePositions = score.OkxCopyablePositions,
        CopiedPositions = score.CopiedPositions,
        SkippedPositions = score.SkippedPositions,
        AvgHoldSeconds = score.AvgHoldSeconds,
        BestTradeUsd = score.BestTradeUsd,
        WorstTradeUsd = score.WorstTradeUsd,
        ScoredAt = score.ScoredAt
    };

    private static HyperliquidLivePositionView MapLivePosition(HyperliquidLivePositionEntity entity)
    {
        var end = entity.ClosedAt ?? DateTime.UtcNow;
        var durationSeconds = Math.Max(0, (decimal)(end - entity.OpenedAt).TotalSeconds);
        return new HyperliquidLivePositionView
        {
            Id = entity.Id,
            TraderAddress = entity.TraderAddress,
            Coin = entity.Coin,
            OkxSymbol = entity.OkxSymbol,
            Side = entity.Side,
            Status = entity.Status,
            OpenedAt = entity.OpenedAt,
            LastSeenAt = entity.LastSeenAt,
            ClosedAt = entity.ClosedAt,
            EntryPrice = entity.EntryPrice,
            ExitPrice = entity.ExitPrice,
            CurrentSize = entity.CurrentSize,
            MaxSize = entity.MaxSize,
            CurrentNotionalUsd = entity.CurrentNotionalUsd,
            MaxNotionalUsd = entity.MaxNotionalUsd,
            PositionPctOfAccount = entity.PositionPctOfAccount,
            UnrealizedPnlUsd = entity.UnrealizedPnlUsd,
            RealizedPnlUsd = entity.RealizedPnlUsd,
            FeeUsd = entity.FeeUsd,
            NetPnlUsd = entity.NetPnlUsd,
            OpenedFromTracking = entity.OpenedFromTracking,
            IsOkxTradable = entity.IsOkxTradable,
            CopyStatus = entity.CopyStatus,
            SkipReason = entity.SkipReason,
            DurationSeconds = durationSeconds,
            PnlPctAccount = entity.SourceAccountValueAtOpen > 0
                ? entity.NetPnlUsd / entity.SourceAccountValueAtOpen * 100m
                : 0m,
            PnlPctNotional = entity.MaxNotionalUsd > 0
                ? entity.NetPnlUsd / entity.MaxNotionalUsd * 100m
                : 0m
        };
    }

    private static HyperliquidLiveFillView MapLiveFill(HyperliquidLiveFillEntity entity) => new()
    {
        TraderAddress = entity.TraderAddress,
        Coin = entity.Coin,
        OkxSymbol = entity.OkxSymbol,
        Direction = entity.Direction,
        Side = entity.Side,
        Price = entity.Price,
        Size = entity.Size,
        ClosedPnlUsd = entity.ClosedPnlUsd,
        FeeUsd = entity.FeeUsd,
        ExchangeTime = entity.ExchangeTime
    };

    private static string NormalizeAddress(string value)
    {
        var address = value.Trim().ToLowerInvariant();
        if (!address.StartsWith("0x", StringComparison.Ordinal) ||
            address.Length != 42 ||
            address.Skip(2).Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException($"Invalid Hyperliquid address: {value}");
        }

        return address;
    }

    private static string LedgerTraderId(string address) => $"hl:{address}";

    private static string ShortAddress(string address) =>
        address.Length <= 12 ? address : $"{address[..6]}...{address[^4..]}";

    private static string PositionKey(string address, string symbol, string side) =>
        $"hl-pos:{address}:{symbol.ToUpperInvariant()}:{side.ToLowerInvariant()}";

    private static string SideFromSize(decimal size) => size >= 0 ? "long" : "short";

    private static string LiveFillDedupeKey(string address, HyperliquidFill fill)
    {
        var raw = JsonSerializer.Serialize(fill);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
        return $"hl-fill:{address}:{fill.Time}:{fill.Coin}:{hash}";
    }

    private static bool FillIsClose(HyperliquidFill fill) =>
        fill.Dir.Contains("Close", StringComparison.OrdinalIgnoreCase) ||
        ParseDecimal(fill.ClosedPnl) != 0;

    private static string FillPositionSide(HyperliquidFill fill)
    {
        if (fill.Dir.Contains("Short", StringComparison.OrdinalIgnoreCase))
        {
            return "short";
        }

        if (fill.Dir.Contains("Long", StringComparison.OrdinalIgnoreCase))
        {
            return "long";
        }

        return fill.Side.Equals("A", StringComparison.OrdinalIgnoreCase) ? "short" : "long";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{value.TotalDays:0.0}d";
        }

        if (value.TotalHours >= 1)
        {
            return $"{value.TotalHours:0.0}h";
        }

        return $"{Math.Max(0, value.TotalMinutes):0}m";
    }

    private sealed record HyperliquidStateRequest(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("user")] string User);

    private sealed record HyperliquidFillsRequest(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("user")] string User,
        [property: JsonPropertyName("startTime")] long StartTime,
        [property: JsonPropertyName("endTime")] long EndTime);

    private sealed class HyperliquidClearinghouseState
    {
        public HyperliquidMarginSummary MarginSummary { get; set; } = new();
        public List<HyperliquidAssetPosition> AssetPositions { get; set; } = new();
    }

    private sealed class HyperliquidMarginSummary
    {
        public string AccountValue { get; set; } = "0";
        public decimal AccountValueUsd => ParseDecimal(AccountValue);
    }

    private sealed class HyperliquidAssetPosition
    {
        public HyperliquidPosition? Position { get; set; }
    }

    private sealed class HyperliquidPosition
    {
        public string Coin { get; set; } = string.Empty;
        [JsonIgnore]
        public string OkxSymbol { get; set; } = string.Empty;

        [JsonPropertyName("szi")]
        public string Szi { get; set; } = "0";

        [JsonPropertyName("entryPx")]
        public string EntryPx { get; set; } = "0";

        [JsonPropertyName("positionValue")]
        public string PositionValue { get; set; } = "0";

        [JsonPropertyName("unrealizedPnl")]
        public string UnrealizedPnl { get; set; } = "0";

        [JsonPropertyName("marginUsed")]
        public string MarginUsed { get; set; } = "0";

        public decimal Size => ParseDecimal(Szi);
        public decimal EntryPrice => ParseDecimal(EntryPx);
        public decimal PositionValueUsd => ParseDecimal(PositionValue);
        public decimal UnrealizedPnlUsd => ParseDecimal(UnrealizedPnl);
        public decimal MarginUsedUsd => ParseDecimal(MarginUsed);
    }

    private sealed class HyperliquidFill
    {
        public long Time { get; set; }
        public string Coin { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public string Dir { get; set; } = string.Empty;
        public string Px { get; set; } = string.Empty;
        public string Sz { get; set; } = string.Empty;
        public string ClosedPnl { get; set; } = string.Empty;
        public string Fee { get; set; } = string.Empty;
    }

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
}

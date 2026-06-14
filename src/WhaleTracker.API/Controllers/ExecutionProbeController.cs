using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/execution-probe")]
public class ExecutionProbeController : ControllerBase
{
    private readonly IOkxService _okxService;
    private readonly ILiveEventPublisher _liveEvents;

    public ExecutionProbeController(
        IOkxService okxService,
        ILiveEventPublisher liveEvents)
    {
        _okxService = okxService;
        _liveEvents = liveEvents;
    }

    [HttpGet("okx-account-config")]
    public async Task<IActionResult> OkxAccountConfig()
    {
        var config = await _okxService.GetAccountConfigurationAsync();
        var account = await _okxService.GetAccountInfoAsync();

        return Ok(new
        {
            config,
            account = new
            {
                account.TotalUsd,
                account.Leverage,
                positions = account.ActivePositions.Select(position => new
                {
                    position.Symbol,
                    position.Direction,
                    position.MarginUsd,
                    position.EntryPrice,
                    position.Size,
                    position.UnrealizedPnl
                })
            }
        });
    }

    [HttpPost("okx-trade-signal")]
    public async Task<IActionResult> ExecuteOkxTradeSignal(
        [FromBody] OkxTradeSignalProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.Symbol) ||
            string.IsNullOrWhiteSpace(request.Action))
        {
            return BadRequest(new { error = "Symbol and action are required." });
        }

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var action = request.Action.Trim().ToUpperInvariant();
        var leverage = request.Leverage <= 0 ? 10 : request.Leverage;
        var marginUsdt = request.MarginUsdt < 0 ? 0 : request.MarginUsdt;

        if (!IsTradeAction(action))
        {
            return BadRequest(new { error = "Action must be OPEN_LONG, OPEN_SHORT, CLOSE_LONG, or CLOSE_SHORT." });
        }

        var calculation = action.StartsWith("OPEN_", StringComparison.Ordinal)
            ? await _okxService.CalculateOrderAsync(symbol, marginUsdt, leverage, action)
            : null;

        if (!request.Execute)
        {
            return Ok(new
            {
                mode = "dry-run",
                request = new
                {
                    symbol,
                    action,
                    marginUsdt,
                    leverage,
                    request.Execute
                },
                calculation
            });
        }

        var signal = new TradeSignal
        {
            Decision = "TRADE",
            Reason = request.Reason,
            Symbol = symbol,
            Action = action,
            MarginAmountUSDT = marginUsdt,
            Leverage = leverage,
            TradeConfidence = 100,
            SourceTxHash = request.SourceTxHash
        };

        var result = await _okxService.ExecuteTradeAsync(signal);
        await _liveEvents.PublishAsync(
            result.Success ? LiveEventTypes.TradeSubmitted : LiveEventTypes.TradeRejected,
            result.Success
                ? $"OKX probe trade accepted: {action} {symbol}"
                : $"OKX probe trade rejected: {result.ErrorMessage}",
            request.WalletAddress,
            request.SourceTxHash,
            symbol,
            null,
            new
            {
                request = new
                {
                    symbol,
                    action,
                    marginUsdt,
                    leverage,
                    request.Execute
                },
                calculation,
                response = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.Size,
                    result.ErrorMessage
                },
                mode = "live-trade-signal-probe"
            },
            result.Success ? "success" : "danger",
            cancellationToken);

        return Ok(new
        {
            request = new
            {
                symbol,
                action,
                marginUsdt,
                leverage,
                request.Execute
            },
            calculation,
            result = new
            {
                result.Success,
                result.OrderId,
                result.Symbol,
                result.Side,
                result.Size,
                result.ErrorMessage
            }
        });
    }

    [HttpPost("okx-market-order")]
    public async Task<IActionResult> PlaceOkxMarketOrder(
        [FromBody] OkxExecutionProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.Symbol) ||
            request.Size <= 0)
        {
            return BadRequest(new { error = "Symbol and positive size are required." });
        }

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var side = string.IsNullOrWhiteSpace(request.Side) ? "buy" : request.Side.Trim().ToLowerInvariant();
        var posSide = string.IsNullOrWhiteSpace(request.PosSide) ? "long" : request.PosSide.Trim().ToLowerInvariant();

        var result = await _okxService.PlaceMarketOrderAsync(
            symbol,
            side,
            posSide,
            request.Size,
            request.ReduceOnly);

        await _liveEvents.PublishAsync(
            result.Success ? LiveEventTypes.TradeSubmitted : LiveEventTypes.TradeRejected,
            result.Success
                ? $"OKX execution probe accepted: {symbol} {side} {posSide} {request.Size}"
                : $"OKX execution probe rejected: {result.ErrorMessage}",
            request.WalletAddress,
            request.SourceTxHash,
            symbol,
            null,
            new
            {
                request = new
                {
                    symbol,
                    side,
                    posSide,
                    request.Size,
                    request.ReduceOnly
                },
                response = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.Size,
                    result.ErrorMessage
                },
                mode = "live-execution-probe"
            },
            result.Success ? "success" : "danger",
            cancellationToken);

        return Ok(new
        {
            request = new
            {
                symbol,
                side,
                posSide,
                request.Size,
                request.ReduceOnly,
                request.WalletAddress,
                request.SourceTxHash
            },
            result = new
            {
                result.Success,
                result.OrderId,
                result.Symbol,
                result.Side,
                result.Size,
                result.ErrorMessage
            }
        });
    }

    private static bool IsTradeAction(string action)
    {
        return action is TradeAction.OPEN_LONG or
            TradeAction.OPEN_SHORT or
            TradeAction.CLOSE_LONG or
            TradeAction.CLOSE_SHORT;
    }
}

public sealed class OkxExecutionProbeRequest
{
    public string Symbol { get; set; } = "ETH";
    public string Side { get; set; } = "buy";
    public string PosSide { get; set; } = "long";
    public decimal Size { get; set; } = 0.01m;
    public bool ReduceOnly { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string SourceTxHash { get; set; } = string.Empty;
}

public sealed class OkxTradeSignalProbeRequest
{
    public string Symbol { get; set; } = "DOGE";
    public string Action { get; set; } = TradeAction.OPEN_LONG;
    public decimal MarginUsdt { get; set; } = 1m;
    public int Leverage { get; set; } = 10;
    public bool Execute { get; set; }
    public string Reason { get; set; } = "Execution probe";
    public string WalletAddress { get; set; } = string.Empty;
    public string SourceTxHash { get; set; } = string.Empty;
}

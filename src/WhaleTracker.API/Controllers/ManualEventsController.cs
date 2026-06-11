using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/manual-events")]
public class ManualEventsController : ControllerBase
{
    private readonly IWhaleTrackerService _whaleTrackerService;

    public ManualEventsController(IWhaleTrackerService whaleTrackerService)
    {
        _whaleTrackerService = whaleTrackerService;
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process([FromBody] ManualEventRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return BadRequest(new { error = "Symbol is required." });
        }

        if (request.UsdValue <= 0)
        {
            return BadRequest(new { error = "UsdValue must be greater than zero." });
        }

        var txHash = string.IsNullOrWhiteSpace(request.TxHash)
            ? $"manual-{Guid.NewGuid():N}"
            : request.TxHash.Trim();

        var symbol = NormalizeSymbol(request.Symbol);
        var transaction = new TransactionEvent
        {
            TxHash = txHash,
            Chain = string.IsNullOrWhiteSpace(request.Chain) ? "ethereum" : request.Chain.Trim(),
            Direction = NormalizeDirection(request.Direction, request.Type),
            TokenSymbol = symbol,
            NormalizedSymbol = symbol,
            Amount = request.Amount,
            UsdValue = request.UsdValue,
            BlockTimestamp = request.TimestampUtc?.ToUniversalTime() ?? DateTime.UtcNow,
            TransactionType = string.IsNullOrWhiteSpace(request.Type) ? "manual" : request.Type.Trim()
        };

        var signal = await _whaleTrackerService.ProcessTransactionAsync(transaction);

        return Ok(new
        {
            processedAt = DateTime.UtcNow,
            transaction,
            signal
        });
    }

    private static string NormalizeDirection(string? direction, string? type)
    {
        if (!string.IsNullOrWhiteSpace(direction))
        {
            return direction.Trim();
        }

        return (type ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "BUY" or "RECEIVE" or "DEPOSIT" => "Incoming",
            "SELL" or "SEND" or "WITHDRAW" or "WITHDRAWAL" => "Outgoing",
            _ => "Unknown"
        };
    }

    private static string NormalizeSymbol(string symbol)
    {
        return symbol.Trim().ToUpperInvariant() switch
        {
            "WETH" => "ETH",
            "WBTC" => "BTC",
            "USDC" => "USDT",
            var normalized => normalized
        };
    }
}

public class ManualEventRequest
{
    public string? TxHash { get; set; }
    public string? Chain { get; set; }
    public string? Type { get; set; }
    public string? Direction { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal UsdValue { get; set; }
    public DateTime? TimestampUtc { get; set; }
}

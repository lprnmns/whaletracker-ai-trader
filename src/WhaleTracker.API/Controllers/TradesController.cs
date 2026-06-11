using Microsoft.AspNetCore.Mvc;
using System.Text;
using WhaleTracker.Data.Repositories;

namespace WhaleTracker.API.Controllers;

/// <summary>
/// Trade Logs Controller
/// Geçmiş işlem kayıtları
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TradesController : ControllerBase
{
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<TradesController> _logger;

    public TradesController(
        ITradeRepository tradeRepository,
        ILogger<TradesController> logger)
    {
        _tradeRepository = tradeRepository;
        _logger = logger;
    }

    /// <summary>
    /// Son işlemleri getir
    /// GET /api/trades?count=50
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetTrades([FromQuery] int count = 50)
    {
        var trades = await _tradeRepository.GetRecentTradeLogsAsync(count);
        return Ok(trades);
    }

    /// <summary>
    /// Belirli bir coin için işlemler
    /// GET /api/trades/symbol/ETH
    /// </summary>
    [HttpGet("symbol/{symbol}")]
    public async Task<IActionResult> GetTradesBySymbol(string symbol, [FromQuery] int count = 20)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest(new { error = "Symbol is required." });
        }

        var trades = await _tradeRepository.GetTradeLogsBySymbolAsync(symbol, count);
        return Ok(trades);
    }

    /// <summary>
    /// Tarih aralığına göre işlemler
    /// GET /api/trades/range?from=2024-01-01&to=2024-12-31
    /// </summary>
    [HttpGet("range")]
    public async Task<IActionResult> GetTradesByDateRange(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to)
    {
        if (from == default || to == default)
        {
            return BadRequest(new { error = "Both from and to query parameters are required." });
        }

        var trades = await _tradeRepository.GetTradeLogsByDateRangeAsync(from, to);
        return Ok(trades);
    }

    /// <summary>
    /// İşlem istatistikleri
    /// GET /api/trades/stats
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetTradeStats()
    {
        var trades = await _tradeRepository.GetRecentTradeLogsAsync(1000);
        var total = trades.Count;
        var successful = trades.Count(x => x.IsSuccess);
        var failed = total - successful;

        return Ok(new
        {
            total,
            successful,
            failed,
            successRate = total == 0 ? 0 : Math.Round(successful * 100m / total, 2),
            symbols = trades
                .GroupBy(x => x.Symbol)
                .Select(g => new
                {
                    symbol = g.Key,
                    count = g.Count(),
                    successful = g.Count(x => x.IsSuccess)
                })
                .OrderByDescending(x => x.count)
        });
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv([FromQuery] int count = 1000)
    {
        var trades = await _tradeRepository.GetRecentTradeLogsAsync(count);
        var csv = new StringBuilder();
        csv.AppendLine("created_at,whale_tx_hash,okx_order_id,symbol,action,margin_usdt,leverage,executed_price,is_success,error_message,confidence,ai_reason");

        foreach (var trade in trades)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(trade.CreatedAt.ToString("O")),
                Csv(trade.WhaleTxHash),
                Csv(trade.OkxOrderId ?? string.Empty),
                Csv(trade.Symbol),
                Csv(trade.Action),
                trade.MarginUsdt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                trade.Leverage.ToString(System.Globalization.CultureInfo.InvariantCulture),
                trade.ExecutedPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                trade.IsSuccess ? "true" : "false",
                Csv(trade.ErrorMessage ?? string.Empty),
                trade.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(trade.AiReason ?? string.Empty)
            }));
        }

        return File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv",
            $"trade-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

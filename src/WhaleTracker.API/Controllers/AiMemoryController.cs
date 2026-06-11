using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using WhaleTracker.Data;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/ai-memory")]
public class AiMemoryController : ControllerBase
{
    private readonly WhaleTrackerDbContext _db;

    public AiMemoryController(WhaleTrackerDbContext db)
    {
        _db = db;
    }

    [HttpGet("state")]
    public async Task<IActionResult> GetState(CancellationToken cancellationToken = default)
    {
        var state = await _db.AiBiasStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == "global", cancellationToken);

        if (state == null)
        {
            return Ok(new
            {
                id = "global",
                biasScore = 0,
                direction = "NEUTRAL",
                symbolWeightsJson = "{}",
                summary = "No AI memory events recorded yet.",
                eventCount = 0
            });
        }

        return Ok(state);
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int count = 50,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 200);

        var events = await _db.AiDecisionEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .Select(x => new
            {
                x.Id,
                x.TxHash,
                x.WalletAddress,
                x.MovementType,
                x.Symbol,
                x.MovementUsd,
                x.WalletBalanceUsd,
                x.Action,
                x.ShouldTrade,
                x.Confidence,
                x.BiasDelta,
                x.BiasScoreAfter,
                x.IgnoredReason,
                x.Reasoning,
                x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(events);
    }

    [HttpGet("events.csv")]
    public async Task<IActionResult> ExportEventsCsv(
        [FromQuery] int count = 1000,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 10000);

        var events = await _db.AiDecisionEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("created_at,tx_hash,wallet_address,movement_type,symbol,movement_usd,wallet_balance_usd,action,should_trade,confidence,bias_delta,bias_score_after,ignored_reason,reasoning");

        foreach (var item in events)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(item.CreatedAt.ToString("O")),
                Csv(item.TxHash),
                Csv(item.WalletAddress),
                Csv(item.MovementType),
                Csv(item.Symbol),
                item.MovementUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.WalletBalanceUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(item.Action),
                item.ShouldTrade ? "true" : "false",
                item.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.BiasDelta.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.BiasScoreAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(item.IgnoredReason),
                Csv(item.Reasoning)
            }));
        }

        return File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv",
            $"ai-memory-events-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

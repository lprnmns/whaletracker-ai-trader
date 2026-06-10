using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
}

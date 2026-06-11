using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/live-events")]
public class LiveEventsController : ControllerBase
{
    private readonly WhaleTrackerDbContext _db;

    public LiveEventsController(WhaleTrackerDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int count = 100,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 500);

        var events = await _db.LiveEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .Select(x => new LiveEventEnvelope
            {
                Id = x.Id,
                Type = x.Type,
                Severity = x.Severity,
                WalletAddress = x.WalletAddress,
                TxHash = x.TxHash,
                Symbol = x.Symbol,
                UsdValue = x.UsdValue,
                Summary = x.Summary,
                PayloadJson = x.PayloadJson,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(events);
    }
}

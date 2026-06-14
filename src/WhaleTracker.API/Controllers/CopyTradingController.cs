using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/copy-trading")]
public class CopyTradingController : ControllerBase
{
    private readonly ICopyTradingService _copyTradingService;

    public CopyTradingController(ICopyTradingService copyTradingService)
    {
        _copyTradingService = copyTradingService;
    }

    [HttpGet("ledger")]
    public async Task<IActionResult> Ledger(CancellationToken cancellationToken)
    {
        var snapshot = await _copyTradingService.GetLedgerSnapshotAsync(cancellationToken);
        return Ok(snapshot);
    }

    [HttpGet("events")]
    public async Task<IActionResult> Events(
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var events = await _copyTradingService.GetLedgerEventsAsync(limit <= 0 ? 100 : limit, cancellationToken);
        return Ok(events);
    }

    [HttpPost("target")]
    public async Task<IActionResult> SetTarget(
        [FromBody] CopyPositionTargetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _copyTradingService.SetTraderPositionTargetAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

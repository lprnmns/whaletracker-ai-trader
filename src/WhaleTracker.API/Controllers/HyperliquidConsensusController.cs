using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/hyperliquid-consensus")]
public sealed class HyperliquidConsensusController : ControllerBase
{
    private readonly IHyperliquidConsensusService _service;

    public HyperliquidConsensusController(IHyperliquidConsensusService service)
    {
        _service = service;
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetSnapshotAsync(cancellationToken));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        return Ok(await _service.RefreshConsensusAsync(cancellationToken));
    }

    [HttpPost("import-watchlist")]
    public async Task<IActionResult> ImportWatchlist(
        [FromBody] HyperliquidConsensusImportRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ImportWatchlistRunAsync(request, cancellationToken));
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

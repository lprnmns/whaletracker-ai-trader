using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/hyperliquid-copy")]
public class HyperliquidCopyController : ControllerBase
{
    private readonly IHyperliquidCopyTradingService _copyService;

    public HyperliquidCopyController(IHyperliquidCopyTradingService copyService)
    {
        _copyService = copyService;
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(CancellationToken cancellationToken)
    {
        return Ok(await _copyService.GetSnapshotAsync(cancellationToken));
    }

    [HttpPost("enable")]
    public async Task<IActionResult> Enable(
        [FromBody] HyperliquidCopyEnableRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _copyService.EnableAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        [FromBody] HyperliquidCopySyncRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<HyperliquidCopyTraderSyncResult>();
        if (request.TraderAddresses.Count == 0)
        {
            results.AddRange(await _copyService.SyncEnabledTradersAsync(cancellationToken));
        }
        else
        {
            foreach (var address in request.TraderAddresses)
            {
                results.Add(await _copyService.SyncTraderAsync(
                    address,
                    request.OverrideExecuteOrders ? request.ExecuteOrders : null,
                    cancellationToken));
            }
        }

        return Ok(results);
    }

    [HttpPost("traders/{address}/sync")]
    public async Task<IActionResult> SyncTrader(
        string address,
        [FromBody] HyperliquidCopySyncRequest? request,
        CancellationToken cancellationToken)
    {
        var executeOverride = request?.OverrideExecuteOrders == true
            ? request.ExecuteOrders
            : (bool?)null;
        return Ok(await _copyService.SyncTraderAsync(address, executeOverride, cancellationToken));
    }

    [HttpPost("traders/{address}/disable")]
    public async Task<IActionResult> DisableTrader(string address, CancellationToken cancellationToken)
    {
        return await _copyService.DisableAsync(address, cancellationToken)
            ? Ok(new { success = true })
            : NotFound(new { error = "Trader not found." });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/runtime-control")]
public class RuntimeControlController : ControllerBase
{
    private const string GlobalControlId = "global";
    private readonly WhaleTrackerDbContext _db;
    private readonly IWhaleTrackerService _whaleTrackerService;

    public RuntimeControlController(
        WhaleTrackerDbContext db,
        IWhaleTrackerService whaleTrackerService)
    {
        _db = db;
        _whaleTrackerService = whaleTrackerService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        var control = await GetOrCreateAsync(cancellationToken);
        return Ok(control);
    }

    [HttpPatch]
    public async Task<IActionResult> Update(
        [FromBody] UpdateRuntimeControlRequest request,
        CancellationToken cancellationToken = default)
    {
        var control = await GetOrCreateAsync(cancellationToken);

        if (request.AutoTradingEnabled.HasValue)
        {
            control.AutoTradingEnabled = request.AutoTradingEnabled.Value;
        }

        if (request.PollingIntervalSeconds.HasValue)
        {
            control.PollingIntervalSeconds = Math.Clamp(request.PollingIntervalSeconds.Value, 5, 3600);
        }

        control.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(control);
    }

    [HttpPost("scan-now")]
    public async Task<IActionResult> ScanNow(CancellationToken cancellationToken = default)
    {
        var control = await GetOrCreateAsync(cancellationToken);
        control.LastScanStartedAt = DateTime.UtcNow;
        control.LastError = string.Empty;
        control.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await _whaleTrackerService.ScanAndProcessAsync();

            control.LastScanCompletedAt = DateTime.UtcNow;
            control.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                control.LastScanStartedAt,
                control.LastScanCompletedAt
            });
        }
        catch (Exception ex)
        {
            control.LastError = ex.Message;
            control.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return StatusCode(500, new
            {
                success = false,
                error = ex.Message,
                control.LastScanStartedAt
            });
        }
    }

    private async Task<RuntimeControlEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var control = await _db.RuntimeControls
            .FirstOrDefaultAsync(x => x.Id == GlobalControlId, cancellationToken);

        if (control != null)
        {
            return control;
        }

        control = new RuntimeControlEntity
        {
            Id = GlobalControlId,
            AutoTradingEnabled = false,
            PollingIntervalSeconds = 30
        };
        _db.RuntimeControls.Add(control);
        await _db.SaveChangesAsync(cancellationToken);
        return control;
    }
}

public class UpdateRuntimeControlRequest
{
    public bool? AutoTradingEnabled { get; set; }
    public int? PollingIntervalSeconds { get; set; }
}

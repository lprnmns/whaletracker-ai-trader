using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/insider-detection")]
public class InsiderDetectionController : ControllerBase
{
    private readonly IInsiderDetectionService _detector;

    public InsiderDetectionController(IInsiderDetectionService detector)
    {
        _detector = detector;
    }

    [HttpPost("analyze")]
    public IActionResult Analyze([FromBody] InsiderDetectionRequest request)
    {
        if (request == null || request.Swaps.Count == 0)
        {
            return BadRequest(new { error = "At least one historical swap is required." });
        }

        if (request.PreCrashStartUtc == default ||
            request.PreCrashEndUtc == default ||
            request.DipBuyStartUtc == default ||
            request.DipBuyEndUtc == default)
        {
            return BadRequest(new { error = "All scan windows are required." });
        }

        return Ok(_detector.Analyze(request));
    }
}

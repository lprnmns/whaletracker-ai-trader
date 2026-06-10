using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Route("api/historical-scans")]
public class HistoricalScansController : ControllerBase
{
    private readonly IHistoricalSwapScanner _scanner;
    private readonly WhaleTrackerDbContext _db;

    public HistoricalScansController(IHistoricalSwapScanner scanner, WhaleTrackerDbContext db)
    {
        _scanner = scanner;
        _db = db;
    }

    [HttpPost("uniswap-v3")]
    public async Task<IActionResult> ScanUniswapV3(
        [FromBody] InsiderDetectionRequest request,
        [FromQuery] bool persist = true,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        if (request.PreCrashStartUtc == default ||
            request.PreCrashEndUtc == default ||
            request.DipBuyStartUtc == default ||
            request.DipBuyEndUtc == default)
        {
            return BadRequest(new { error = "All scan windows are required." });
        }

        var result = await _scanner.ScanUniswapV3Async(request, cancellationToken);
        if (!persist)
        {
            return Ok(result);
        }

        var scan = await SaveScanAsync(request, result, cancellationToken);
        return Ok(new
        {
            scanId = scan.Id,
            result.ScannedSwapCount,
            result.CandidateCount,
            result.Candidates
        });
    }

    [HttpGet]
    public async Task<IActionResult> ListScans([FromQuery] int count = 20, CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 100);
        var scans = await _db.HistoricalScans
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .Select(x => new
            {
                x.Id,
                x.Provider,
                x.PreCrashStartUtc,
                x.PreCrashEndUtc,
                x.DipBuyStartUtc,
                x.DipBuyEndUtc,
                x.ScannedSwapCount,
                x.CandidateCount,
                x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(scans);
    }

    [HttpGet("{scanId:long}/candidates")]
    public async Task<IActionResult> ListCandidates(long scanId, CancellationToken cancellationToken = default)
    {
        var candidates = await _db.InsiderCandidates
            .AsNoTracking()
            .Where(x => x.HistoricalScanId == scanId)
            .OrderByDescending(x => x.InsiderScore)
            .ThenByDescending(x => x.EstimatedProfitUsd)
            .ToListAsync(cancellationToken);

        return Ok(candidates.Select(x => new
        {
            x.Id,
            x.WalletAddress,
            x.AssetSymbol,
            x.EstimatedProfitUsd,
            x.MatchedAssetAmount,
            x.AverageSellPriceUsd,
            x.AverageBuyPriceUsd,
            x.InsiderScore,
            x.TimingScore,
            x.SizeScore,
            x.ProfitScore,
            evidenceTxHashes = JsonSerializer.Deserialize<List<string>>(x.EvidenceTxHashesJson) ?? new List<string>(),
            x.CreatedAt
        }));
    }

    private async Task<HistoricalScanEntity> SaveScanAsync(
        InsiderDetectionRequest request,
        InsiderDetectionResult result,
        CancellationToken cancellationToken)
    {
        var scan = new HistoricalScanEntity
        {
            Provider = "etherscan_uniswap_v3",
            PreCrashStartUtc = request.PreCrashStartUtc.ToUniversalTime(),
            PreCrashEndUtc = request.PreCrashEndUtc.ToUniversalTime(),
            DipBuyStartUtc = request.DipBuyStartUtc.ToUniversalTime(),
            DipBuyEndUtc = request.DipBuyEndUtc.ToUniversalTime(),
            MinimumProfitUsd = request.MinimumProfitUsd,
            ScannedSwapCount = result.ScannedSwapCount,
            CandidateCount = result.CandidateCount,
            Candidates = result.Candidates.Select(x => new InsiderCandidateEntity
            {
                WalletAddress = x.WalletAddress,
                AssetSymbol = x.AssetSymbol,
                EstimatedProfitUsd = x.EstimatedProfitUsd,
                MatchedAssetAmount = x.MatchedAssetAmount,
                AverageSellPriceUsd = x.AverageSellPriceUsd,
                AverageBuyPriceUsd = x.AverageBuyPriceUsd,
                InsiderScore = x.InsiderScore,
                TimingScore = x.TimingScore,
                SizeScore = x.SizeScore,
                ProfitScore = x.ProfitScore,
                EvidenceTxHashesJson = JsonSerializer.Serialize(x.EvidenceTxHashes)
            }).ToList()
        };

        _db.HistoricalScans.Add(scan);
        await _db.SaveChangesAsync(cancellationToken);
        return scan;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/trader-finder")]
public sealed class TraderFinderController : ControllerBase
{
    private readonly ITraderPerformanceService _performanceService;
    private readonly WhaleTrackerDbContext _db;

    public TraderFinderController(
        ITraderPerformanceService performanceService,
        WhaleTrackerDbContext db)
    {
        _performanceService = performanceService;
        _db = db;
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan(
        [FromBody] TraderFinderRequest request,
        CancellationToken cancellationToken)
    {
        if (request.StartUtc == default || request.EndUtc == default || request.StartUtc >= request.EndUtc)
        {
            return BadRequest(new { error = "A valid start and end UTC range is required." });
        }

        request.Top = Math.Clamp(request.Top, 1, 100);
        request.MinimumStartingValueUsd = Math.Max(0, request.MinimumStartingValueUsd);

        var walletSet = request.CandidateWallets
            .Where(IsValidEvmAddress)
            .Select(NormalizeAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (request.IncludeTrackedWallets)
        {
            var tracked = await _db.TrackedWallets
                .AsNoTracking()
                .Where(x => x.IsActive)
                .Select(x => x.WalletAddress)
                .ToListAsync(cancellationToken);
            walletSet.UnionWith(tracked.Where(IsValidEvmAddress).Select(NormalizeAddress));
        }

        if (walletSet.Count == 0)
        {
            return BadRequest(new
            {
                error = "No candidate wallets. Add addresses manually or keep IncludeTrackedWallets enabled."
            });
        }

        var results = new List<TraderPerformance>();
        foreach (var wallet in walletSet.Take(250))
        {
            results.Add(await _performanceService.AnalyzeAsync(
                wallet,
                request.StartUtc,
                request.EndUtc,
                cancellationToken));
        }

        var qualified = results
            .Where(x => x.Status == "completed")
            .Where(x => x.StartingValueUsd >= request.MinimumStartingValueUsd)
            .Where(x => x.AdjustedProfitUsd > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.AdjustedProfitUsd)
            .Take(request.Top)
            .ToList();

        var scan = new TraderScanEntity
        {
            StartUtc = request.StartUtc.ToUniversalTime(),
            EndUtc = request.EndUtc.ToUniversalTime(),
            MinimumStartingValueUsd = request.MinimumStartingValueUsd,
            RequestedTop = request.Top,
            EvaluatedWalletCount = results.Count,
            QualifiedWalletCount = qualified.Count,
            Candidates = qualified.Select(ToEntity).ToList()
        };
        _db.TraderScans.Add(scan);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            scanId = scan.Id,
            evaluatedWalletCount = results.Count,
            qualifiedWalletCount = qualified.Count,
            candidates = scan.Candidates.Select(ToResponse),
            failures = results
                .Where(x => x.Status == "failed")
                .Select(x => new { x.WalletAddress, x.Error })
        });
    }

    [HttpGet("scans")]
    public async Task<IActionResult> ListScans(
        [FromQuery] int count = 20,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 100);
        return Ok(await _db.TraderScans
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .Select(x => new
            {
                x.Id,
                x.StartUtc,
                x.EndUtc,
                x.MinimumStartingValueUsd,
                x.RequestedTop,
                x.EvaluatedWalletCount,
                x.QualifiedWalletCount,
                x.CreatedAt
            })
            .ToListAsync(cancellationToken));
    }

    [HttpGet("scans/{scanId:long}/candidates")]
    public async Task<IActionResult> ListCandidates(
        long scanId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _db.TraderCandidates
            .AsNoTracking()
            .Where(x => x.TraderScanId == scanId)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.AdjustedProfitUsd)
            .ToListAsync(cancellationToken);
        return Ok(candidates.Select(ToResponse));
    }

    [HttpPost("candidates/{candidateId:long}/track")]
    public async Task<IActionResult> Track(
        long candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _db.TraderCandidates
            .FirstOrDefaultAsync(x => x.Id == candidateId, cancellationToken);
        if (candidate == null)
        {
            return NotFound(new { error = "Trader candidate not found." });
        }

        var existing = await _db.TrackedWallets
            .FirstOrDefaultAsync(x => x.WalletAddress == candidate.WalletAddress, cancellationToken);
        if (existing == null)
        {
            existing = new TrackedWalletEntity
            {
                WalletAddress = candidate.WalletAddress,
                Label = $"top-trader-{candidate.Id}",
                Source = "trader_finder",
                Chain = "multi-chain",
                IsActive = true,
                ConfidenceScore = candidate.Score,
                EstimatedProfitUsd = candidate.AdjustedProfitUsd,
                Notes = $"Adjusted return {candidate.AdjustedReturnPercent:F2}%."
            };
            _db.TrackedWallets.Add(existing);
        }
        else
        {
            existing.IsActive = true;
            existing.ConfidenceScore = Math.Max(existing.ConfidenceScore, candidate.Score);
            existing.EstimatedProfitUsd = Math.Max(existing.EstimatedProfitUsd, candidate.AdjustedProfitUsd);
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(existing);
    }

    [HttpPost("scans/{scanId:long}/track-top")]
    public async Task<IActionResult> TrackTop(
        long scanId,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var candidates = await _db.TraderCandidates
            .Where(x => x.TraderScanId == scanId)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.AdjustedProfitUsd)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var candidate in candidates)
        {
            var exists = await _db.TrackedWallets
                .AnyAsync(x => x.WalletAddress == candidate.WalletAddress, cancellationToken);
            if (exists)
            {
                continue;
            }

            _db.TrackedWallets.Add(new TrackedWalletEntity
            {
                WalletAddress = candidate.WalletAddress,
                Label = $"top-trader-{candidate.Id}",
                Source = "trader_finder",
                Chain = "multi-chain",
                IsActive = true,
                ConfidenceScore = candidate.Score,
                EstimatedProfitUsd = candidate.AdjustedProfitUsd,
                Notes = $"Adjusted return {candidate.AdjustedReturnPercent:F2}%."
            });
            added++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { scanId, matched = candidates.Count, added });
    }

    private static TraderCandidateEntity ToEntity(TraderPerformance item) => new()
    {
        WalletAddress = item.WalletAddress,
        StartingValueUsd = item.StartingValueUsd,
        EndingValueUsd = item.EndingValueUsd,
        ReceivedExternalUsd = item.ReceivedExternalUsd,
        SentExternalUsd = item.SentExternalUsd,
        TotalFeesUsd = item.TotalFeesUsd,
        AdjustedProfitUsd = item.AdjustedProfitUsd,
        AdjustedReturnPercent = item.AdjustedReturnPercent,
        RealizedGainUsd = item.RealizedGainUsd,
        Score = item.Score,
        StartPointUtc = item.StartPointUtc,
        EndPointUtc = item.EndPointUtc,
        ChartPeriod = item.ChartPeriod
    };

    private static object ToResponse(TraderCandidateEntity item) => new
    {
        item.Id,
        item.TraderScanId,
        item.WalletAddress,
        item.StartingValueUsd,
        item.EndingValueUsd,
        item.ReceivedExternalUsd,
        item.SentExternalUsd,
        item.TotalFeesUsd,
        item.AdjustedProfitUsd,
        item.AdjustedReturnPercent,
        item.RealizedGainUsd,
        item.Score,
        item.StartPointUtc,
        item.EndPointUtc,
        item.ChartPeriod,
        item.CreatedAt
    };

    private static bool IsValidEvmAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var value = address.Trim();
        return value.Length == 42 &&
               value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               value.Skip(2).All(Uri.IsHexDigit);
    }

    private static string NormalizeAddress(string address) => address.Trim().ToLowerInvariant();
}

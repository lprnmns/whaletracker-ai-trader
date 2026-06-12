using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WhaleTracker.API.Services;
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
    private readonly ITraderDiscoveryJobQueue _discoveryQueue;
    private readonly ITraderPerformanceJobQueue _performanceQueue;
    private readonly WhaleTrackerDbContext _db;

    public TraderFinderController(
        ITraderDiscoveryJobQueue discoveryQueue,
        ITraderPerformanceJobQueue performanceQueue,
        WhaleTrackerDbContext db)
    {
        _discoveryQueue = discoveryQueue;
        _performanceQueue = performanceQueue;
        _db = db;
    }

    [HttpPost("discover")]
    public async Task<IActionResult> Discover(
        [FromBody] TraderDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var run = new TraderDiscoveryRunEntity
        {
            Provider = "dune",
            State = "QUEUED",
            LookbackDays = request.LookbackDays,
            MinimumActiveWeeks = request.MinimumActiveWeeks,
            MinimumMeaningfulSwaps = request.MinimumMeaningfulSwaps,
            MinimumSwapUsd = request.MinimumSwapUsd,
            CandidateLimit = request.CandidateLimit,
            CandidateCount = 0,
            ProgressPercent = 0,
            CurrentStage = "queued",
            StatusMessage = "Waiting for the discovery worker.",
            ProgressLogJson = JsonSerializer.Serialize(new[]
            {
                new TraderDiscoveryProgress
                {
                    Percent = 0,
                    Stage = "queued",
                    State = "QUEUED",
                    Message = "Discovery job queued.",
                    TimestampUtc = DateTime.UtcNow
                }
            }),
            StartedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow
        };

        _db.TraderDiscoveryRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        if (!_discoveryQueue.Enqueue(run.Id))
        {
            run.State = "FAILED";
            run.CurrentStage = "queue_failed";
            run.ErrorMessage = "Discovery job could not be queued.";
            await _db.SaveChangesAsync(cancellationToken);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ToDiscoveryRunResponse(run));
        }

        return Accepted(ToDiscoveryRunResponse(run));
    }

    [HttpGet("discovery-runs")]
    public async Task<IActionResult> ListDiscoveryRuns(
        [FromQuery] int count = 20,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 100);
        var runs = await _db.TraderDiscoveryRuns
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);
        return Ok(runs.Select(ToDiscoveryRunResponse));
    }

    [HttpGet("discovery-runs/{runId:long}")]
    public async Task<IActionResult> GetDiscoveryRun(
        long runId,
        CancellationToken cancellationToken = default)
    {
        var run = await _db.TraderDiscoveryRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);
        return run == null ? NotFound() : Ok(ToDiscoveryRunResponse(run));
    }

    [HttpGet("discovery-runs/{runId:long}/candidates")]
    public async Task<IActionResult> ListDiscoveryCandidates(
        long runId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _db.TraderDiscoveryCandidates
            .AsNoTracking()
            .Where(x => x.TraderDiscoveryRunId == runId)
            .OrderByDescending(x => x.ApprovedNotionalUsd)
            .ToListAsync(cancellationToken);
        return Ok(candidates.Select(ToDiscoveryResponse));
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

        var wallets = walletSet.Take(250).ToList();

        var scan = new TraderScanEntity
        {
            StartUtc = request.StartUtc.ToUniversalTime(),
            EndUtc = request.EndUtc.ToUniversalTime(),
            MinimumStartingValueUsd = request.MinimumStartingValueUsd,
            RequestedTop = request.Top,
            EvaluatedWalletCount = 0,
            QualifiedWalletCount = 0,
            State = "QUEUED",
            ProgressPercent = 0,
            CurrentStage = "queued",
            StatusMessage = $"Waiting to analyze {wallets.Count} wallets.",
            CandidateWalletsJson = JsonSerializer.Serialize(wallets),
            ProgressLogJson = JsonSerializer.Serialize(new[]
            {
                new TraderDiscoveryProgress
                {
                    Percent = 0,
                    Stage = "queued",
                    State = "QUEUED",
                    Message = $"Performance verification queued for {wallets.Count} wallets.",
                    TimestampUtc = DateTime.UtcNow
                }
            })
        };
        _db.TraderScans.Add(scan);
        await _db.SaveChangesAsync(cancellationToken);
        if (!_performanceQueue.Enqueue(scan.Id))
        {
            scan.State = "FAILED";
            scan.CurrentStage = "queue_failed";
            scan.ErrorMessage = "Performance job could not be queued.";
            await _db.SaveChangesAsync(cancellationToken);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, TraderPerformanceWorker.ToPayload(scan));
        }

        return Accepted(TraderPerformanceWorker.ToPayload(scan));
    }

    [HttpGet("scans")]
    public async Task<IActionResult> ListScans(
        [FromQuery] int count = 20,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 100);
        var scans = await _db.TraderScans
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);
        return Ok(scans.Select(TraderPerformanceWorker.ToPayload));
    }

    [HttpGet("scans/{scanId:long}")]
    public async Task<IActionResult> GetScan(
        long scanId,
        CancellationToken cancellationToken = default)
    {
        var scan = await _db.TraderScans
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == scanId, cancellationToken);
        return scan == null ? NotFound() : Ok(TraderPerformanceWorker.ToPayload(scan));
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

    private static object ToDiscoveryResponse(TraderDiscoveryCandidateEntity item) => new
    {
        item.Id,
        item.TraderDiscoveryRunId,
        item.WalletAddress,
        item.MeaningfulSwapCount,
        item.ActiveWeekCount,
        item.ApprovedNotionalUsd,
        item.ActiveChainCount,
        activeChains = JsonSerializer.Deserialize<List<string>>(item.ActiveChainsJson) ?? new(),
        item.FirstTradeUtc,
        item.LastTradeUtc,
        item.CreatedAt
    };

    private static object ToDiscoveryRunResponse(TraderDiscoveryRunEntity run) => new
    {
        run.Id,
        run.Provider,
        run.ExecutionId,
        run.State,
        run.LookbackDays,
        run.MinimumActiveWeeks,
        run.MinimumMeaningfulSwaps,
        run.MinimumSwapUsd,
        run.CandidateLimit,
        run.CandidateCount,
        run.ProgressPercent,
        run.CurrentStage,
        run.StatusMessage,
        run.ErrorMessage,
        progressLog = DeserializeProgressLog(run.ProgressLogJson),
        run.StartedAtUtc,
        run.CompletedAtUtc,
        run.CreatedAt
    };

    private static List<TraderDiscoveryProgress> DeserializeProgressLog(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TraderDiscoveryProgress>>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

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

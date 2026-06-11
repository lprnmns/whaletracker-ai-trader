using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
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

    [HttpGet("{scanId:long}/candidates.csv")]
    public async Task<IActionResult> ExportCandidatesCsv(long scanId, CancellationToken cancellationToken = default)
    {
        var candidates = await _db.InsiderCandidates
            .AsNoTracking()
            .Where(x => x.HistoricalScanId == scanId)
            .OrderByDescending(x => x.InsiderScore)
            .ThenByDescending(x => x.EstimatedProfitUsd)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("wallet_address,asset_symbol,estimated_profit_usd,matched_asset_amount,average_sell_price_usd,average_buy_price_usd,insider_score,timing_score,size_score,profit_score,evidence_tx_hashes,created_at");

        foreach (var item in candidates)
        {
            var evidence = JsonSerializer.Deserialize<List<string>>(item.EvidenceTxHashesJson) ?? new List<string>();
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(item.WalletAddress),
                Csv(item.AssetSymbol),
                item.EstimatedProfitUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.MatchedAssetAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.AverageSellPriceUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.AverageBuyPriceUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.InsiderScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.TimingScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.SizeScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ProfitScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(string.Join(" ", evidence)),
                Csv(item.CreatedAt.ToString("O"))
            }));
        }

        return File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv",
            $"historical-scan-{scanId}-candidates.csv");
    }

    [HttpPost("{scanId:long}/promote-candidates")]
    public async Task<IActionResult> PromoteCandidates(
        long scanId,
        [FromQuery] decimal minScore = 25m,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var candidates = await _db.InsiderCandidates
            .Where(x => x.HistoricalScanId == scanId && x.InsiderScore >= minScore)
            .OrderByDescending(x => x.InsiderScore)
            .ThenByDescending(x => x.EstimatedProfitUsd)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var promoted = 0;
        foreach (var candidate in candidates)
        {
            var walletAddress = candidate.WalletAddress.Trim().ToLowerInvariant();
            var existing = await _db.TrackedWallets
                .FirstOrDefaultAsync(x => x.WalletAddress == walletAddress, cancellationToken);

            if (existing == null)
            {
                _db.TrackedWallets.Add(new TrackedWalletEntity
                {
                    WalletAddress = walletAddress,
                    Label = $"insider-{candidate.AssetSymbol}-{candidate.Id}",
                    Source = "historical_scan_auto",
                    Chain = "ethereum",
                    IsActive = true,
                    ConfidenceScore = candidate.InsiderScore,
                    EstimatedProfitUsd = candidate.EstimatedProfitUsd,
                    AssetSymbol = candidate.AssetSymbol,
                    HistoricalScanId = candidate.HistoricalScanId,
                    InsiderCandidateId = candidate.Id,
                    Notes = $"Auto-promoted with minScore {minScore}."
                });
                promoted++;
                continue;
            }

            existing.IsActive = true;
            existing.ConfidenceScore = Math.Max(existing.ConfidenceScore, candidate.InsiderScore);
            existing.EstimatedProfitUsd = Math.Max(existing.EstimatedProfitUsd, candidate.EstimatedProfitUsd);
            existing.AssetSymbol = string.IsNullOrWhiteSpace(existing.AssetSymbol) ? candidate.AssetSymbol : existing.AssetSymbol;
            existing.HistoricalScanId ??= candidate.HistoricalScanId;
            existing.InsiderCandidateId ??= candidate.Id;
            existing.UpdatedAt = DateTime.UtcNow;
            promoted++;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            scanId,
            minScore,
            limit,
            matched = candidates.Count,
            promoted
        });
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

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

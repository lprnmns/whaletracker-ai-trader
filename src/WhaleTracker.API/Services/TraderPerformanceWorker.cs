using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhaleTracker.API.Hubs;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Services;

public sealed class TraderPerformanceWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITraderPerformanceJobQueue _queue;
    private readonly IHubContext<MissionControlHub> _hub;
    private readonly ILogger<TraderPerformanceWorker> _logger;

    public TraderPerformanceWorker(
        IServiceScopeFactory scopeFactory,
        ITraderPerformanceJobQueue queue,
        IHubContext<MissionControlHub> hub,
        ILogger<TraderPerformanceWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedScansAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var scanId = await _queue.DequeueAsync(stoppingToken);
            try
            {
                await ProcessAsync(scanId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trader performance scan {ScanId} failed.", scanId);
                await MarkFailedAsync(scanId, ex, stoppingToken);
            }
            finally
            {
                _queue.Complete(scanId);
            }
        }
    }

    private async Task ProcessAsync(long scanId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        var performanceService = scope.ServiceProvider.GetRequiredService<ITraderPerformanceService>();
        var scan = await db.TraderScans
            .Include(x => x.Candidates)
            .SingleAsync(x => x.Id == scanId, cancellationToken);
        var wallets = DeserializeWallets(scan.CandidateWalletsJson);
        var results = new List<TraderPerformance>();

        await UpdateAsync(scan, db, 2, "starting", "RUNNING",
            $"Performance worker started for {wallets.Count} wallets.", cancellationToken);

        for (var index = 0; index < wallets.Count; index++)
        {
            var wallet = wallets[index];
            var percent = 5 + (int)Math.Floor(index / (decimal)Math.Max(1, wallets.Count) * 85m);
            await UpdateAsync(
                scan,
                db,
                percent,
                "analyzing_wallets",
                "RUNNING",
                $"Analyzing wallet {index + 1}/{wallets.Count}: {ShortAddress(wallet)}",
                cancellationToken);

            var result = await performanceService.AnalyzeAsync(
                wallet,
                scan.StartUtc,
                scan.EndUtc,
                cancellationToken);
            results.Add(result);
            scan.EvaluatedWalletCount = index + 1;

            if (result.Status == "failed")
            {
                AppendLog(scan, new TraderDiscoveryProgress
                {
                    Percent = percent,
                    Stage = "wallet_failed",
                    State = "RUNNING",
                    Message = $"{ShortAddress(wallet)}: {result.Error}"
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            await BroadcastAsync(scan, cancellationToken);
        }

        await UpdateAsync(scan, db, 92, "ranking", "RUNNING",
            "Wallet metrics completed. Applying portfolio, profit and score filters.", cancellationToken);

        var qualified = results
            .Where(x => x.Status == "completed")
            .Where(x => x.StartingValueUsd >= scan.MinimumStartingValueUsd)
            .Where(x => x.AdjustedProfitUsd > 0)
            .Where(x => x.AdjustedReturnPercent >= 3m)
            .Where(x => x.PositivePeriodPercent >= 55m)
            .Where(x => x.MaximumDrawdownPercent <= 35m)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.AdjustedProfitUsd)
            .Take(scan.RequestedTop)
            .ToList();

        scan.Candidates.Clear();
        scan.Candidates.AddRange(qualified.Select(ToEntity));
        scan.QualifiedWalletCount = qualified.Count;
        scan.ProgressPercent = 100;
        scan.CurrentStage = "completed";
        scan.State = "COMPLETED";
        scan.StatusMessage =
            $"Analyzed {results.Count} wallets; {qualified.Count} qualified, {results.Count(x => x.Status == "failed")} failed.";
        AppendLog(scan, new TraderDiscoveryProgress
        {
            Percent = 100,
            Stage = scan.CurrentStage,
            State = scan.State,
            Message = scan.StatusMessage
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(scan, cancellationToken);
    }

    private async Task UpdateAsync(
        TraderScanEntity scan,
        WhaleTrackerDbContext db,
        int percent,
        string stage,
        string state,
        string message,
        CancellationToken cancellationToken)
    {
        scan.ProgressPercent = Math.Max(scan.ProgressPercent, percent);
        scan.CurrentStage = stage;
        scan.State = state;
        scan.StatusMessage = message;
        AppendLog(scan, new TraderDiscoveryProgress
        {
            Percent = scan.ProgressPercent,
            Stage = stage,
            State = state,
            Message = message
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(scan, cancellationToken);
    }

    private async Task RecoverInterruptedScansAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        var ids = await db.TraderScans
            .Where(x => x.State == "QUEUED" || x.State == "RUNNING")
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            _queue.Enqueue(id);
        }
    }

    private async Task MarkFailedAsync(long scanId, Exception exception, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        var scan = await db.TraderScans.SingleOrDefaultAsync(x => x.Id == scanId, cancellationToken);
        if (scan == null) return;

        scan.State = "FAILED";
        scan.CurrentStage = "failed";
        scan.ErrorMessage = exception.Message;
        scan.StatusMessage = "Performance verification failed.";
        AppendLog(scan, new TraderDiscoveryProgress
        {
            Percent = scan.ProgressPercent,
            Stage = "failed",
            State = "FAILED",
            Message = exception.Message
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(scan, cancellationToken);
    }

    private Task BroadcastAsync(TraderScanEntity scan, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("traderPerformanceProgress", ToPayload(scan), cancellationToken);

    public static object ToPayload(TraderScanEntity scan) => new
    {
        scan.Id,
        scan.StartUtc,
        scan.EndUtc,
        scan.MinimumStartingValueUsd,
        scan.RequestedTop,
        scan.EvaluatedWalletCount,
        scan.QualifiedWalletCount,
        scan.State,
        scan.ProgressPercent,
        scan.CurrentStage,
        scan.StatusMessage,
        scan.ErrorMessage,
        progressLog = ParseLog(scan.ProgressLogJson),
        scan.CreatedAt
    };

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
        PositivePeriodPercent = item.PositivePeriodPercent,
        MaximumDrawdownPercent = item.MaximumDrawdownPercent,
        Score = item.Score,
        StartPointUtc = item.StartPointUtc,
        EndPointUtc = item.EndPointUtc,
        ChartPeriod = item.ChartPeriod
    };

    private static void AppendLog(TraderScanEntity scan, TraderDiscoveryProgress progress)
    {
        var log = ParseLog(scan.ProgressLogJson);
        progress.TimestampUtc = progress.TimestampUtc == default ? DateTime.UtcNow : progress.TimestampUtc;
        log.Add(progress);
        scan.ProgressLogJson = JsonSerializer.Serialize(log.TakeLast(200), JsonOptions);
    }

    private static List<TraderDiscoveryProgress> ParseLog(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TraderDiscoveryProgress>>(json, JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static List<string> DeserializeWallets(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string ShortAddress(string address) =>
        address.Length > 12 ? $"{address[..6]}...{address[^4..]}" : address;
}

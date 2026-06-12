using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhaleTracker.API.Hubs;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Services;

public sealed class TraderDiscoveryWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITraderDiscoveryJobQueue _queue;
    private readonly IHubContext<MissionControlHub> _hub;
    private readonly ILogger<TraderDiscoveryWorker> _logger;

    public TraderDiscoveryWorker(
        IServiceScopeFactory scopeFactory,
        ITraderDiscoveryJobQueue queue,
        IHubContext<MissionControlHub> hub,
        ILogger<TraderDiscoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedRunsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var runId = await _queue.DequeueAsync(stoppingToken);
            try
            {
                await ProcessAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trader discovery run {RunId} failed.", runId);
                await MarkFailedAsync(runId, ex, stoppingToken);
            }
            finally
            {
                _queue.Complete(runId);
            }
        }
    }

    private async Task ProcessAsync(long runId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        var discovery = scope.ServiceProvider.GetRequiredService<ITraderDiscoveryService>();
        var run = await db.TraderDiscoveryRuns
            .Include(x => x.Candidates)
            .SingleAsync(x => x.Id == runId, cancellationToken);

        run.State = "RUNNING";
        run.CurrentStage = "starting";
        run.ProgressPercent = 2;
        run.StatusMessage = "Background discovery worker started.";
        run.ErrorMessage = string.Empty;
        run.StartedAtUtc = DateTime.UtcNow;
        AppendLog(run, new TraderDiscoveryProgress
        {
            Percent = 2,
            Stage = "starting",
            State = "RUNNING",
            Message = run.StatusMessage
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(run, cancellationToken);

        var request = new TraderDiscoveryRequest
        {
            LookbackDays = run.LookbackDays,
            MinimumActiveWeeks = run.MinimumActiveWeeks,
            MinimumMeaningfulSwaps = run.MinimumMeaningfulSwaps,
            MinimumSwapUsd = run.MinimumSwapUsd,
            CandidateLimit = run.CandidateLimit
        };

        var result = await discovery.DiscoverAsync(
            request,
            async (progress, token) =>
            {
                run.ProgressPercent = progress.Percent;
                run.CurrentStage = progress.Stage;
                run.State = progress.State;
                run.StatusMessage = progress.Message;
                if (!string.IsNullOrWhiteSpace(progress.ExecutionId))
                {
                    run.ExecutionId = progress.ExecutionId;
                }

                AppendLog(run, progress);
                await db.SaveChangesAsync(token);
                await BroadcastAsync(run, token);
            },
            cancellationToken);

        run.CurrentStage = "saving_candidates";
        run.State = "SAVING";
        run.ProgressPercent = 92;
        run.StatusMessage = $"{result.Candidates.Count} candidates found. Saving to PostgreSQL.";
        AppendLog(run, new TraderDiscoveryProgress
        {
            Percent = 92,
            Stage = run.CurrentStage,
            State = run.State,
            Message = run.StatusMessage,
            ExecutionId = result.ExecutionId
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(run, cancellationToken);

        run.Candidates.Clear();
        run.Candidates.AddRange(result.Candidates.Select(candidate => new TraderDiscoveryCandidateEntity
        {
            WalletAddress = candidate.WalletAddress,
            MeaningfulSwapCount = candidate.MeaningfulSwapCount,
            ActiveWeekCount = candidate.ActiveWeekCount,
            ApprovedNotionalUsd = candidate.ApprovedNotionalUsd,
            AverageSwapUsd = candidate.AverageSwapUsd,
            MaximumDailySwaps = candidate.MaximumDailySwaps,
            DistinctMajorAssets = candidate.DistinctMajorAssets,
            CopyabilityScore = candidate.CopyabilityScore,
            CurrentCopyableValueUsd = candidate.CurrentCopyableValueUsd,
            ActiveChainCount = candidate.ActiveChainCount,
            ActiveChainsJson = JsonSerializer.Serialize(candidate.ActiveChains),
            FirstTradeUtc = candidate.FirstTradeUtc,
            LastTradeUtc = candidate.LastTradeUtc
        }));
        run.ExecutionId = result.ExecutionId;
        run.CandidateCount = result.Candidates.Count;
        run.CompletedAtUtc = DateTime.UtcNow;
        run.ProgressPercent = 100;
        run.CurrentStage = "completed";
        run.State = "COMPLETED";
        var diagnostics = result.Diagnostics;
        run.StatusMessage =
            $"Funnel: {diagnostics.RawSwapCount:N0} major swap legs -> " +
            $"{diagnostics.ApprovedPairSwapCount:N0} approved-pair legs -> " +
            $"{diagnostics.EligibleTransactionCount:N0} eligible transactions -> " +
            $"{diagnostics.WalletCount:N0} wallets -> " +
            $"{diagnostics.ActiveWalletCount:N0} activity-qualified -> " +
            $"{run.CandidateCount:N0} candidates.";
        AppendLog(run, new TraderDiscoveryProgress
        {
            Percent = 100,
            Stage = run.CurrentStage,
            State = run.State,
            Message = run.StatusMessage,
            ExecutionId = run.ExecutionId
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(run, cancellationToken);
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        var pendingIds = await db.TraderDiscoveryRuns
            .Where(x => x.State == "QUEUED" || x.State == "RUNNING" ||
                        x.State == "QUERY_STATE_PENDING" ||
                        x.State == "QUERY_STATE_EXECUTING" ||
                        x.State == "SAVING")
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var runId in pendingIds)
        {
            _queue.Enqueue(runId);
        }
    }

    private async Task MarkFailedAsync(long runId, Exception exception, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        var run = await db.TraderDiscoveryRuns.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run == null)
        {
            return;
        }

        run.State = "FAILED";
        run.CurrentStage = "failed";
        run.ErrorMessage = exception.Message;
        run.StatusMessage = "Discovery failed. Review the error and progress log.";
        run.CompletedAtUtc = DateTime.UtcNow;
        AppendLog(run, new TraderDiscoveryProgress
        {
            Percent = run.ProgressPercent,
            Stage = "failed",
            State = "FAILED",
            Message = exception.Message
        });
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(run, cancellationToken);
    }

    private async Task BroadcastAsync(TraderDiscoveryRunEntity run, CancellationToken cancellationToken)
    {
        await _hub.Clients.All.SendAsync("traderDiscoveryProgress", ToPayload(run), cancellationToken);
    }

    private static object ToPayload(TraderDiscoveryRunEntity run) => new
    {
        run.Id,
        run.State,
        run.ProgressPercent,
        run.CurrentStage,
        run.StatusMessage,
        run.ErrorMessage,
        run.ExecutionId,
        run.CandidateCount,
        run.StartedAtUtc,
        run.CompletedAtUtc,
        progressLog = ParseLog(run.ProgressLogJson)
    };

    private static void AppendLog(TraderDiscoveryRunEntity run, TraderDiscoveryProgress progress)
    {
        var log = ParseLog(run.ProgressLogJson);
        progress.TimestampUtc = progress.TimestampUtc == default ? DateTime.UtcNow : progress.TimestampUtc;
        log.Add(progress);
        run.ProgressLogJson = JsonSerializer.Serialize(log.TakeLast(100), JsonOptions);
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
}

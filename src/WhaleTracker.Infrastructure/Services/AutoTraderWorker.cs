using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.Infrastructure.Services;

public class AutoTraderWorker : BackgroundService
{
    private const string GlobalControlId = "global";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoTraderWorker> _logger;

    public AutoTraderWorker(IServiceScopeFactory scopeFactory, ILogger<AutoTraderWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoTrader worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = 10;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
                var control = await GetOrCreateControlAsync(db, stoppingToken);

                control.LastWorkerHeartbeatAt = DateTime.UtcNow;
                control.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);

                delaySeconds = Math.Clamp(control.PollingIntervalSeconds, 5, 3600);
                control.LastScanStartedAt = DateTime.UtcNow;
                control.LastError = string.Empty;
                await db.SaveChangesAsync(stoppingToken);

                var tracker = scope.ServiceProvider.GetRequiredService<IWhaleTrackerService>();
                await tracker.ScanAndProcessAsync();

                control.LastScanCompletedAt = DateTime.UtcNow;
                control.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutoTrader worker loop failed.");
                await TryPersistErrorAsync(ex.Message, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private static async Task<RuntimeControlEntity> GetOrCreateControlAsync(
        WhaleTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        var control = await db.RuntimeControls
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
        db.RuntimeControls.Add(control);
        await db.SaveChangesAsync(cancellationToken);
        return control;
    }

    private async Task TryPersistErrorAsync(string error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
            var control = await GetOrCreateControlAsync(db, cancellationToken);
            control.LastError = error;
            control.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTrader worker error state could not be persisted.");
        }
    }
}

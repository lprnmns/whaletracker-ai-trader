using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface ITraderPerformanceService
{
    Task<TraderPerformance> AnalyzeAsync(
        string walletAddress,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default);
}

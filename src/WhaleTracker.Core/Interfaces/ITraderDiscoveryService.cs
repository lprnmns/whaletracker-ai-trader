using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface ITraderDiscoveryService
{
    Task<TraderDiscoveryResult> DiscoverAsync(
        TraderDiscoveryRequest request,
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}

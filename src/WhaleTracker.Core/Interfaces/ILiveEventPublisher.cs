using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface ILiveEventPublisher
{
    Task<LiveEventEnvelope> PublishAsync(
        string type,
        string summary,
        string walletAddress = "",
        string txHash = "",
        string symbol = "",
        decimal? usdValue = null,
        object? payload = null,
        string severity = "info",
        CancellationToken cancellationToken = default);
}

using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface IHyperliquidConsensusService
{
    Task<HyperliquidConsensusImportResponse> ImportWatchlistRunAsync(
        HyperliquidConsensusImportRequest request,
        CancellationToken cancellationToken = default);

    Task<HyperliquidConsensusSnapshotResponse> RefreshConsensusAsync(
        CancellationToken cancellationToken = default);

    Task<HyperliquidConsensusSnapshotResponse> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
}

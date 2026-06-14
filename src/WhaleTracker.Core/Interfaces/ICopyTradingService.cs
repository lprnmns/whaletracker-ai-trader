using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface ICopyTradingService
{
    Task<CopyLedgerSnapshot> GetLedgerSnapshotAsync(CancellationToken cancellationToken = default);

    Task<CopyLedgerEventsResponse> GetLedgerEventsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<CopyPositionTargetResult> SetTraderPositionTargetAsync(
        CopyPositionTargetRequest request,
        CancellationToken cancellationToken = default);
}

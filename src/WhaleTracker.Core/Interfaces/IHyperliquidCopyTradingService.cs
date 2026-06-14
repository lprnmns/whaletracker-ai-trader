using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface IHyperliquidCopyTradingService
{
    Task<HyperliquidCopySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<HyperliquidCopyEnableResponse> EnableAsync(
        HyperliquidCopyEnableRequest request,
        CancellationToken cancellationToken = default);

    Task<HyperliquidCopyTraderSyncResult> SyncTraderAsync(
        string traderAddress,
        bool? executeOverride = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HyperliquidCopyTraderSyncResult>> SyncEnabledTradersAsync(
        CancellationToken cancellationToken = default);

    Task<bool> DisableAsync(string traderAddress, CancellationToken cancellationToken = default);
}

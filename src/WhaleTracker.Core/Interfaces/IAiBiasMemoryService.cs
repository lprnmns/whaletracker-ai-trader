using WhaleTracker.Core.Models;

namespace WhaleTracker.Core.Interfaces;

public interface IAiBiasMemoryService
{
    Task<string> BuildPromptMemoryAsync(CancellationToken cancellationToken = default);

    Task RecordDecisionAsync(
        TransactionEvent transaction,
        AIDecision decision,
        string walletAddress,
        decimal walletBalanceUsd,
        CancellationToken cancellationToken = default);
}

namespace WhaleTracker.Core.Models;

public static class LiveEventTypes
{
    public const string WalletActivityDetected = "WalletActivityDetected";
    public const string AiAwakened = "AiAwakened";
    public const string AiDecisionCompleted = "AiDecisionCompleted";
    public const string TradeSubmitted = "TradeSubmitted";
    public const string TradeRejected = "TradeRejected";
    public const string BiasUpdated = "BiasUpdated";
    public const string ProviderHealthChanged = "ProviderHealthChanged";
}

public sealed class LiveEventEnvelope
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public string WalletAddress { get; set; } = string.Empty;
    public string TxHash { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal? UsdValue { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

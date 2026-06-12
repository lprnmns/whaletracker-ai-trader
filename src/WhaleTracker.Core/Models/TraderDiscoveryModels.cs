namespace WhaleTracker.Core.Models;

public sealed class TraderDiscoveryRequest
{
    public int LookbackDays { get; set; } = 28;
    public int MinimumActiveWeeks { get; set; } = 3;
    public int MinimumMeaningfulSwaps { get; set; } = 4;
    public decimal MinimumSwapUsd { get; set; } = 1_500m;
    public int CandidateLimit { get; set; } = 200;
}

public sealed class TraderDiscoveryCandidate
{
    public string WalletAddress { get; set; } = string.Empty;
    public int MeaningfulSwapCount { get; set; }
    public int ActiveWeekCount { get; set; }
    public decimal ApprovedNotionalUsd { get; set; }
    public int ActiveChainCount { get; set; }
    public List<string> ActiveChains { get; set; } = new();
    public DateTime FirstTradeUtc { get; set; }
    public DateTime LastTradeUtc { get; set; }
}

public sealed class TraderDiscoveryResult
{
    public string Provider { get; set; } = "dune";
    public string ExecutionId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public List<TraderDiscoveryCandidate> Candidates { get; set; } = new();
    public TraderDiscoveryDiagnostics Diagnostics { get; set; } = new();
}

public sealed class TraderDiscoveryDiagnostics
{
    public long RawSwapCount { get; set; }
    public long ApprovedPairSwapCount { get; set; }
    public long EligibleTransactionCount { get; set; }
    public long WalletCount { get; set; }
    public long ActiveWalletCount { get; set; }
}

public sealed class TraderDiscoveryProgress
{
    public int Percent { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

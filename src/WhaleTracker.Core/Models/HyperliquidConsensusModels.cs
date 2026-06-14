namespace WhaleTracker.Core.Models;

public class HyperliquidConsensusImportRequest
{
    public string RunId { get; set; } = "hl_full_001_011_30d";
    public int Take { get; set; } = 40;
    public bool SyncTraders { get; set; } = true;
    public bool RebuildProfiles { get; set; } = true;
    public bool RefreshConsensus { get; set; } = true;
    public bool PreserveRealExecution { get; set; } = true;
}

public class HyperliquidConsensusImportResponse
{
    public string RunId { get; set; } = string.Empty;
    public int WatchlistCount { get; set; }
    public int SyncedTraders { get; set; }
    public int ProfilesWritten { get; set; }
    public int ExposuresWritten { get; set; }
    public int ConsensusCoins { get; set; }
    public IReadOnlyList<string> WatchlistAddresses { get; set; } = Array.Empty<string>();
}

public class HyperliquidConsensusSnapshotResponse
{
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<CoinConsensusView> Coins { get; set; } = Array.Empty<CoinConsensusView>();
    public IReadOnlyList<TraderCoinExposureView> Exposures { get; set; } = Array.Empty<TraderCoinExposureView>();
    public IReadOnlyList<TraderCoinProfileView> TopProfiles { get; set; } = Array.Empty<TraderCoinProfileView>();
}

public class CoinConsensusView
{
    public long Id { get; set; }
    public string Coin { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public decimal LongPower { get; set; }
    public decimal ShortPower { get; set; }
    public decimal NetSignal { get; set; }
    public decimal Participation { get; set; }
    public decimal ConflictRatio { get; set; }
    public decimal DirectionScore { get; set; }
    public decimal QualityScore { get; set; }
    public string TargetSide { get; set; } = string.Empty;
    public decimal TargetNotionalUsd { get; set; }
    public string Action { get; set; } = string.Empty;
    public string SkipReason { get; set; } = string.Empty;
    public int ContributorCount { get; set; }
    public string TopContributorsJson { get; set; } = string.Empty;
}

public class TraderCoinExposureView
{
    public string TraderAddress { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Coin { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal CurrentNotionalUsd { get; set; }
    public decimal CurrentAccountValueUsd { get; set; }
    public decimal CurrentAllocPct { get; set; }
    public decimal UnrealizedPnlUsd { get; set; }
    public decimal EntryPrice { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public decimal NormalizedExposure { get; set; }
    public decimal AllocationConviction { get; set; }
    public decimal CoinSkillScore { get; set; }
    public decimal SampleConfidence { get; set; }
    public decimal FreshnessScore { get; set; }
    public decimal RiskAdjustment { get; set; }
    public decimal WeightedSignal { get; set; }
    public bool IsBaseline { get; set; }
}

public class TraderCoinProfileView
{
    public string TraderAddress { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Coin { get; set; } = string.Empty;
    public int WindowDays { get; set; }
    public int ClosedPositions { get; set; }
    public decimal WinRate { get; set; }
    public decimal NetPnlUsd { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AvgAllocPct { get; set; }
    public decimal MedianAllocPct { get; set; }
    public decimal P90AllocPct { get; set; }
    public decimal CoinSkillScore { get; set; }
    public decimal SampleConfidence { get; set; }
    public decimal HistoricalQualityScore { get; set; }
    public decimal HistoricalConfidenceScore { get; set; }
}

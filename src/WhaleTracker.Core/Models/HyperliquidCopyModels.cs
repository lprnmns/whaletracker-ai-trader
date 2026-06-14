namespace WhaleTracker.Core.Models;

public class HyperliquidCopyEnableRequest
{
    public IReadOnlyList<string> TraderAddresses { get; set; } = Array.Empty<string>();
    public decimal MarginPerTraderUsdt { get; set; } = 10m;
    public int Leverage { get; set; } = 10;
    public bool ExecuteOrders { get; set; }
    public bool CopyActiveOnEnable { get; set; } = true;
    public bool AdoptActiveOnlyWhenNegative { get; set; } = true;
    public bool SyncImmediately { get; set; } = true;
    public string LabelPrefix { get; set; } = "Hyperliquid";
}

public class HyperliquidCopySyncRequest
{
    public IReadOnlyList<string> TraderAddresses { get; set; } = Array.Empty<string>();
    public bool ExecuteOrders { get; set; }
    public bool OverrideExecuteOrders { get; set; }
}

public class HyperliquidCopyTraderView
{
    public string Address { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool ExecuteOrders { get; set; }
    public decimal MarginPerTraderUsdt { get; set; }
    public int Leverage { get; set; }
    public bool AdoptActiveOnlyWhenNegative { get; set; }
    public bool CopyActiveOnEnable { get; set; }
    public long LastSeenFillTimeMs { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string LastError { get; set; } = string.Empty;
}

public class HyperliquidCopyPositionView
{
    public string TraderAddress { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal SourceSize { get; set; }
    public decimal SourceEntryPrice { get; set; }
    public decimal SourcePositionValueUsd { get; set; }
    public decimal SourceMarginUsedUsd { get; set; }
    public decimal SourceUnrealizedPnlUsd { get; set; }
    public decimal TargetMarginUsdt { get; set; }
    public DateTime LastSourceSeenAt { get; set; }
    public DateTime? LastCopiedAt { get; set; }
    public string LastMessage { get; set; } = string.Empty;
}

public class HyperliquidCopyEventView
{
    public long Id { get; set; }
    public string TraderAddress { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal TargetMarginUsdt { get; set; }
    public bool IsSuccess { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HyperliquidCopySnapshot
{
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<HyperliquidCopyTraderView> Traders { get; set; } =
        Array.Empty<HyperliquidCopyTraderView>();
    public IReadOnlyList<HyperliquidCopyPositionView> Positions { get; set; } =
        Array.Empty<HyperliquidCopyPositionView>();
    public IReadOnlyList<HyperliquidCopyEventView> RecentEvents { get; set; } =
        Array.Empty<HyperliquidCopyEventView>();
}

public class HyperliquidCopyEnableResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public HyperliquidCopySnapshot Snapshot { get; set; } = new();
    public IReadOnlyList<HyperliquidCopyTraderSyncResult> SyncResults { get; set; } =
        Array.Empty<HyperliquidCopyTraderSyncResult>();
}

public class HyperliquidCopyTraderSyncResult
{
    public string TraderAddress { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ActiveSourcePositions { get; set; }
    public int CopiedPositions { get; set; }
    public int SkippedPositions { get; set; }
    public int ClosedTargets { get; set; }
    public int NewFills { get; set; }
    public IReadOnlyList<HyperliquidCopyPositionDecision> Decisions { get; set; } =
        Array.Empty<HyperliquidCopyPositionDecision>();
}

public class HyperliquidCopyPositionDecision
{
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal SourceUnrealizedPnlUsd { get; set; }
    public decimal SourcePositionValueUsd { get; set; }
    public decimal TargetMarginUsdt { get; set; }
    public CopyPositionTargetResult? OkxResult { get; set; }
}

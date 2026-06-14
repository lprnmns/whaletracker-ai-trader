namespace WhaleTracker.Core.Models;

public class CopyPositionTargetRequest
{
    public string TraderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = "long";
    public decimal TargetMarginUsdt { get; set; }
    public int Leverage { get; set; } = 10;
    public string SourceEventId { get; set; } = string.Empty;
    public bool Execute { get; set; }
    public bool CloseIfBelowMinimum { get; set; }
    public decimal MaximumUpwardMarginDeviationPercent { get; set; } = 10m;
    public string Reason { get; set; } = string.Empty;
}

public class CopyPositionTargetResult
{
    public bool Success { get; set; }
    public bool Executed { get; set; }
    public string Message { get; set; } = string.Empty;
    public string TraderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal RequestedTargetContracts { get; set; }
    public decimal PreviousTraderContracts { get; set; }
    public decimal AggregateTargetBefore { get; set; }
    public decimal AggregateTargetAfter { get; set; }
    public decimal ActualContractsBefore { get; set; }
    public decimal ActualContractsAfter { get; set; }
    public string OrderAction { get; set; } = string.Empty;
    public decimal OrderSize { get; set; }
    public string OkxOrderId { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public OrderCalculation? Calculation { get; set; }
}

public class CopyTraderVirtualPosition
{
    public string TraderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal TargetContracts { get; set; }
    public decimal TargetMarginUsdt { get; set; }
    public int Leverage { get; set; }
    public bool IsActive { get; set; }
    public string SourceEventId { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public class CopyAggregatePosition
{
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal TargetContracts { get; set; }
    public decimal ActualContracts { get; set; }
    public decimal DifferenceContracts => TargetContracts - ActualContracts;
    public bool IsInSync => DifferenceContracts == 0;
    public int TraderCount { get; set; }
}

public class CopyLedgerSnapshot
{
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<CopyTraderVirtualPosition> TraderPositions { get; set; } =
        Array.Empty<CopyTraderVirtualPosition>();
    public IReadOnlyList<CopyAggregatePosition> Aggregates { get; set; } =
        Array.Empty<CopyAggregatePosition>();
}

public class CopyLedgerEventView
{
    public long Id { get; set; }
    public string TraderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public decimal RequestedTargetContracts { get; set; }
    public decimal PreviousTraderContracts { get; set; }
    public decimal AggregateTargetBefore { get; set; }
    public decimal AggregateTargetAfter { get; set; }
    public decimal ActualContractsBefore { get; set; }
    public decimal ActualContractsAfter { get; set; }
    public string OrderAction { get; set; } = string.Empty;
    public decimal OrderSize { get; set; }
    public string OkxOrderId { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class CopyLedgerEventsResponse
{
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<CopyLedgerEventView> Events { get; set; } =
        Array.Empty<CopyLedgerEventView>();
}

public static class CopyTradingTargetMath
{
    public static bool ShouldFlattenForMinimum(
        OrderCalculation? calculation,
        bool closeIfBelowMinimum,
        decimal maximumUpwardMarginDeviationPercent)
    {
        if (!closeIfBelowMinimum || calculation == null)
        {
            return false;
        }

        if (calculation.ValidationStatus == OrderValidationStatus.InsufficientMargin)
        {
            return true;
        }

        return calculation.IsValid &&
            calculation.MarginDeviationPercent > Math.Max(0, maximumUpwardMarginDeviationPercent);
    }
}

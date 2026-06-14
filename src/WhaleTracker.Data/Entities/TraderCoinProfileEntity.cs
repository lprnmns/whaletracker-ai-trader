using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("trader_coin_profiles")]
public class TraderCoinProfileEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("trader_address")]
    [MaxLength(100)]
    public string TraderAddress { get; set; } = string.Empty;

    [Column("coin")]
    [MaxLength(30)]
    public string Coin { get; set; } = string.Empty;

    [Column("window_days")]
    public int WindowDays { get; set; }

    [Column("computed_at")]
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    [Column("closed_positions")]
    public int ClosedPositions { get; set; }

    [Column("winning_positions")]
    public int WinningPositions { get; set; }

    [Column("losing_positions")]
    public int LosingPositions { get; set; }

    [Column("win_rate")]
    public decimal WinRate { get; set; }

    [Column("net_pnl_usd")]
    public decimal NetPnlUsd { get; set; }

    [Column("gross_profit_usd")]
    public decimal GrossProfitUsd { get; set; }

    [Column("gross_loss_usd")]
    public decimal GrossLossUsd { get; set; }

    [Column("profit_factor")]
    public decimal ProfitFactor { get; set; }

    [Column("total_entry_notional_usd")]
    public decimal TotalEntryNotionalUsd { get; set; }

    [Column("avg_entry_notional_usd")]
    public decimal AvgEntryNotionalUsd { get; set; }

    [Column("median_entry_notional_usd")]
    public decimal MedianEntryNotionalUsd { get; set; }

    [Column("avg_alloc_pct")]
    public decimal AvgAllocPct { get; set; }

    [Column("median_alloc_pct")]
    public decimal MedianAllocPct { get; set; }

    [Column("p75_alloc_pct")]
    public decimal P75AllocPct { get; set; }

    [Column("p90_alloc_pct")]
    public decimal P90AllocPct { get; set; }

    [Column("max_alloc_pct")]
    public decimal MaxAllocPct { get; set; }

    [Column("avg_hold_seconds")]
    public decimal AvgHoldSeconds { get; set; }

    [Column("median_hold_seconds")]
    public decimal MedianHoldSeconds { get; set; }

    [Column("best_trade_pnl_usd")]
    public decimal BestTradePnlUsd { get; set; }

    [Column("worst_trade_pnl_usd")]
    public decimal WorstTradePnlUsd { get; set; }

    [Column("one_trade_pnl_concentration")]
    public decimal OneTradePnlConcentration { get; set; }

    [Column("coin_skill_score")]
    public decimal CoinSkillScore { get; set; }

    [Column("sample_confidence")]
    public decimal SampleConfidence { get; set; }

    [Column("historical_quality_score")]
    public decimal HistoricalQualityScore { get; set; }

    [Column("historical_confidence_score")]
    public decimal HistoricalConfidenceScore { get; set; }

    [Column("approximation_quality")]
    [MaxLength(80)]
    public string ApproximationQuality { get; set; } = string.Empty;
}

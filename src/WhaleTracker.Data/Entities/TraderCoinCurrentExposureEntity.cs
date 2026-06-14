using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("trader_coin_current_exposures")]
public class TraderCoinCurrentExposureEntity
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

    [Column("side")]
    [MaxLength(10)]
    public string Side { get; set; } = string.Empty;

    [Column("current_notional_usd")]
    public decimal CurrentNotionalUsd { get; set; }

    [Column("current_account_value_usd")]
    public decimal CurrentAccountValueUsd { get; set; }

    [Column("current_alloc_pct")]
    public decimal CurrentAllocPct { get; set; }

    [Column("unrealized_pnl_usd")]
    public decimal UnrealizedPnlUsd { get; set; }

    [Column("entry_price")]
    public decimal EntryPrice { get; set; }

    [Column("opened_at")]
    public DateTime OpenedAt { get; set; }

    [Column("last_seen_at")]
    public DateTime LastSeenAt { get; set; }

    [Column("normalized_exposure")]
    public decimal NormalizedExposure { get; set; }

    [Column("allocation_conviction")]
    public decimal AllocationConviction { get; set; }

    [Column("coin_skill_score")]
    public decimal CoinSkillScore { get; set; }

    [Column("sample_confidence")]
    public decimal SampleConfidence { get; set; }

    [Column("freshness_score")]
    public decimal FreshnessScore { get; set; }

    [Column("risk_adjustment")]
    public decimal RiskAdjustment { get; set; }

    [Column("weighted_signal")]
    public decimal WeightedSignal { get; set; }

    [Column("is_baseline")]
    public bool IsBaseline { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

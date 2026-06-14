using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("aggregate_signal_contributions")]
public class AggregateSignalContributionEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("coin_consensus_snapshot_id")]
    public long CoinConsensusSnapshotId { get; set; }

    [Column("trader_address")]
    [MaxLength(100)]
    public string TraderAddress { get; set; } = string.Empty;

    [Column("coin")]
    [MaxLength(30)]
    public string Coin { get; set; } = string.Empty;

    [Column("source_side")]
    [MaxLength(10)]
    public string SourceSide { get; set; } = string.Empty;

    [Column("source_position_notional_usd")]
    public decimal SourcePositionNotionalUsd { get; set; }

    [Column("source_account_value_usd")]
    public decimal SourceAccountValueUsd { get; set; }

    [Column("exposure_unit")]
    public decimal ExposureUnit { get; set; }

    [Column("trader_weight")]
    public decimal TraderWeight { get; set; }

    [Column("coin_skill_score")]
    public decimal CoinSkillScore { get; set; }

    [Column("allocation_conviction")]
    public decimal AllocationConviction { get; set; }

    [Column("weighted_signal")]
    public decimal WeightedSignal { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

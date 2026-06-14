using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("coin_consensus_snapshots")]
public class CoinConsensusSnapshotEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("coin")]
    [MaxLength(30)]
    public string Coin { get; set; } = string.Empty;

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Column("long_power")]
    public decimal LongPower { get; set; }

    [Column("short_power")]
    public decimal ShortPower { get; set; }

    [Column("net_signal")]
    public decimal NetSignal { get; set; }

    [Column("participation")]
    public decimal Participation { get; set; }

    [Column("conflict_ratio")]
    public decimal ConflictRatio { get; set; }

    [Column("direction_score")]
    public decimal DirectionScore { get; set; }

    [Column("quality_score")]
    public decimal QualityScore { get; set; }

    [Column("target_side")]
    [MaxLength(10)]
    public string TargetSide { get; set; } = string.Empty;

    [Column("target_notional_usd")]
    public decimal TargetNotionalUsd { get; set; }

    [Column("action")]
    [MaxLength(40)]
    public string Action { get; set; } = string.Empty;

    [Column("skip_reason")]
    public string SkipReason { get; set; } = string.Empty;

    [Column("contributor_count")]
    public int ContributorCount { get; set; }

    [Column("top_contributors_json")]
    public string TopContributorsJson { get; set; } = string.Empty;
}

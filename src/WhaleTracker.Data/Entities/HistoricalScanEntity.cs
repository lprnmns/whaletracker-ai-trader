using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("historical_scans")]
public class HistoricalScanEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("provider")]
    [MaxLength(50)]
    public string Provider { get; set; } = "etherscan_uniswap_v3";

    [Column("pre_crash_start_utc")]
    public DateTime PreCrashStartUtc { get; set; }

    [Column("pre_crash_end_utc")]
    public DateTime PreCrashEndUtc { get; set; }

    [Column("dip_buy_start_utc")]
    public DateTime DipBuyStartUtc { get; set; }

    [Column("dip_buy_end_utc")]
    public DateTime DipBuyEndUtc { get; set; }

    [Column("minimum_profit_usd")]
    public decimal MinimumProfitUsd { get; set; }

    [Column("scanned_swap_count")]
    public int ScannedSwapCount { get; set; }

    [Column("candidate_count")]
    public int CandidateCount { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<InsiderCandidateEntity> Candidates { get; set; } = new();
}

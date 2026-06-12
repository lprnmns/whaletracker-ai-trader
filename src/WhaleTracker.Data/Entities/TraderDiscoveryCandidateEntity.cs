using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("trader_discovery_candidates")]
public class TraderDiscoveryCandidateEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("trader_discovery_run_id")]
    public long TraderDiscoveryRunId { get; set; }

    public TraderDiscoveryRunEntity? TraderDiscoveryRun { get; set; }

    [Column("wallet_address")]
    [MaxLength(100)]
    public string WalletAddress { get; set; } = string.Empty;

    [Column("meaningful_swap_count")]
    public int MeaningfulSwapCount { get; set; }

    [Column("active_week_count")]
    public int ActiveWeekCount { get; set; }

    [Column("approved_notional_usd")]
    public decimal ApprovedNotionalUsd { get; set; }

    [Column("average_swap_usd")]
    public decimal AverageSwapUsd { get; set; }

    [Column("maximum_daily_swaps")]
    public int MaximumDailySwaps { get; set; }

    [Column("distinct_major_assets")]
    public int DistinctMajorAssets { get; set; }

    [Column("copyability_score")]
    public decimal CopyabilityScore { get; set; }

    [Column("current_copyable_value_usd")]
    public decimal CurrentCopyableValueUsd { get; set; }

    [Column("active_chain_count")]
    public int ActiveChainCount { get; set; }

    [Column("active_chains_json")]
    public string ActiveChainsJson { get; set; } = "[]";

    [Column("first_trade_utc")]
    public DateTime FirstTradeUtc { get; set; }

    [Column("last_trade_utc")]
    public DateTime LastTradeUtc { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

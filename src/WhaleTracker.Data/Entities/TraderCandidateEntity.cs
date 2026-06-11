using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("trader_candidates")]
public class TraderCandidateEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("trader_scan_id")]
    public long TraderScanId { get; set; }

    public TraderScanEntity? TraderScan { get; set; }

    [Column("wallet_address")]
    [MaxLength(100)]
    public string WalletAddress { get; set; } = string.Empty;

    [Column("starting_value_usd")]
    public decimal StartingValueUsd { get; set; }

    [Column("ending_value_usd")]
    public decimal EndingValueUsd { get; set; }

    [Column("received_external_usd")]
    public decimal ReceivedExternalUsd { get; set; }

    [Column("sent_external_usd")]
    public decimal SentExternalUsd { get; set; }

    [Column("total_fees_usd")]
    public decimal TotalFeesUsd { get; set; }

    [Column("adjusted_profit_usd")]
    public decimal AdjustedProfitUsd { get; set; }

    [Column("adjusted_return_percent")]
    public decimal AdjustedReturnPercent { get; set; }

    [Column("realized_gain_usd")]
    public decimal RealizedGainUsd { get; set; }

    [Column("score")]
    public decimal Score { get; set; }

    [Column("start_point_utc")]
    public DateTime StartPointUtc { get; set; }

    [Column("end_point_utc")]
    public DateTime EndPointUtc { get; set; }

    [Column("chart_period")]
    [MaxLength(20)]
    public string ChartPeriod { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

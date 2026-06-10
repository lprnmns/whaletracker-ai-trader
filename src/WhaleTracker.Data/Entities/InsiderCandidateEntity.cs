using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("insider_candidates")]
public class InsiderCandidateEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("historical_scan_id")]
    public long HistoricalScanId { get; set; }

    public HistoricalScanEntity? HistoricalScan { get; set; }

    [Column("wallet_address")]
    [MaxLength(100)]
    public string WalletAddress { get; set; } = string.Empty;

    [Column("asset_symbol")]
    [MaxLength(20)]
    public string AssetSymbol { get; set; } = string.Empty;

    [Column("estimated_profit_usd")]
    public decimal EstimatedProfitUsd { get; set; }

    [Column("matched_asset_amount")]
    public decimal MatchedAssetAmount { get; set; }

    [Column("average_sell_price_usd")]
    public decimal AverageSellPriceUsd { get; set; }

    [Column("average_buy_price_usd")]
    public decimal AverageBuyPriceUsd { get; set; }

    [Column("insider_score")]
    public decimal InsiderScore { get; set; }

    [Column("timing_score")]
    public decimal TimingScore { get; set; }

    [Column("size_score")]
    public decimal SizeScore { get; set; }

    [Column("profit_score")]
    public decimal ProfitScore { get; set; }

    [Column("evidence_tx_hashes_json")]
    public string EvidenceTxHashesJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

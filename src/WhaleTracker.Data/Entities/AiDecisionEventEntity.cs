using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("ai_decision_events")]
public class AiDecisionEventEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("tx_hash")]
    [MaxLength(100)]
    public string TxHash { get; set; } = string.Empty;

    [Column("wallet_address")]
    [MaxLength(100)]
    public string WalletAddress { get; set; } = string.Empty;

    [Column("movement_type")]
    [MaxLength(40)]
    public string MovementType { get; set; } = string.Empty;

    [Column("symbol")]
    [MaxLength(20)]
    public string Symbol { get; set; } = string.Empty;

    [Column("movement_usd")]
    public decimal MovementUsd { get; set; }

    [Column("wallet_balance_usd")]
    public decimal WalletBalanceUsd { get; set; }

    [Column("action")]
    [MaxLength(30)]
    public string Action { get; set; } = "IGNORE";

    [Column("should_trade")]
    public bool ShouldTrade { get; set; }

    [Column("confidence")]
    public int Confidence { get; set; }

    [Column("bias_delta")]
    public decimal BiasDelta { get; set; }

    [Column("bias_score_after")]
    public decimal BiasScoreAfter { get; set; }

    [Column("ignored_reason")]
    public string IgnoredReason { get; set; } = string.Empty;

    [Column("reasoning")]
    public string Reasoning { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

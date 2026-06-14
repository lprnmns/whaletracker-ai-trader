using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("hyperliquid_copy_positions")]
public class HyperliquidCopyPositionEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("trader_address")]
    [MaxLength(100)]
    public string TraderAddress { get; set; } = string.Empty;

    [Column("symbol")]
    [MaxLength(20)]
    public string Symbol { get; set; } = string.Empty;

    [Column("side")]
    [MaxLength(10)]
    public string Side { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(40)]
    public string Status { get; set; } = string.Empty;

    [Column("source_size")]
    public decimal SourceSize { get; set; }

    [Column("source_entry_price")]
    public decimal SourceEntryPrice { get; set; }

    [Column("source_position_value_usd")]
    public decimal SourcePositionValueUsd { get; set; }

    [Column("source_margin_used_usd")]
    public decimal SourceMarginUsedUsd { get; set; }

    [Column("source_unrealized_pnl_usd")]
    public decimal SourceUnrealizedPnlUsd { get; set; }

    [Column("target_margin_usdt")]
    public decimal TargetMarginUsdt { get; set; }

    [Column("last_source_seen_at")]
    public DateTime LastSourceSeenAt { get; set; } = DateTime.UtcNow;

    [Column("last_copied_at")]
    public DateTime? LastCopiedAt { get; set; }

    [Column("last_message")]
    public string LastMessage { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

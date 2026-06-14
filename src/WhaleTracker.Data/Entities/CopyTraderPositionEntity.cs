using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("copy_trader_positions")]
public class CopyTraderPositionEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("trader_id")]
    [MaxLength(120)]
    public string TraderId { get; set; } = string.Empty;

    [Column("symbol")]
    [MaxLength(20)]
    public string Symbol { get; set; } = string.Empty;

    [Column("side")]
    [MaxLength(10)]
    public string Side { get; set; } = string.Empty;

    [Column("target_contracts")]
    public decimal TargetContracts { get; set; }

    [Column("target_margin_usdt")]
    public decimal TargetMarginUsdt { get; set; }

    [Column("leverage")]
    public int Leverage { get; set; } = 10;

    [Column("source_event_id")]
    [MaxLength(160)]
    public string SourceEventId { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

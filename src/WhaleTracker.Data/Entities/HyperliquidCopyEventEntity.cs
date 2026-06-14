using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("hyperliquid_copy_events")]
public class HyperliquidCopyEventEntity
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

    [Column("event_type")]
    [MaxLength(60)]
    public string EventType { get; set; } = string.Empty;

    [Column("source_event_id")]
    [MaxLength(180)]
    public string SourceEventId { get; set; } = string.Empty;

    [Column("message")]
    public string Message { get; set; } = string.Empty;

    [Column("target_margin_usdt")]
    public decimal TargetMarginUsdt { get; set; }

    [Column("is_success")]
    public bool IsSuccess { get; set; }

    [Column("raw_payload")]
    public string RawPayload { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

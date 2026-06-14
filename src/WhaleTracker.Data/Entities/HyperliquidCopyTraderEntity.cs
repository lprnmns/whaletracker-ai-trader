using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("hyperliquid_copy_traders")]
public class HyperliquidCopyTraderEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("address")]
    [MaxLength(100)]
    public string Address { get; set; } = string.Empty;

    [Column("label")]
    [MaxLength(120)]
    public string Label { get; set; } = string.Empty;

    [Column("is_enabled")]
    public bool IsEnabled { get; set; }

    [Column("execute_orders")]
    public bool ExecuteOrders { get; set; }

    [Column("margin_per_trader_usdt")]
    public decimal MarginPerTraderUsdt { get; set; } = 10m;

    [Column("leverage")]
    public int Leverage { get; set; } = 10;

    [Column("adopt_active_only_when_negative")]
    public bool AdoptActiveOnlyWhenNegative { get; set; } = true;

    [Column("copy_active_on_enable")]
    public bool CopyActiveOnEnable { get; set; } = true;

    [Column("last_seen_fill_time_ms")]
    public long LastSeenFillTimeMs { get; set; }

    [Column("last_fill_poll_at")]
    public DateTime? LastFillPollAt { get; set; }

    [Column("last_sync_at")]
    public DateTime? LastSyncAt { get; set; }

    [Column("last_error")]
    public string LastError { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

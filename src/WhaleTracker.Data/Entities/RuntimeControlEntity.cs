using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("runtime_control")]
public class RuntimeControlEntity
{
    [Key]
    [Column("id")]
    [MaxLength(60)]
    public string Id { get; set; } = "global";

    [Column("auto_trading_enabled")]
    public bool AutoTradingEnabled { get; set; }

    [Column("polling_interval_seconds")]
    public int PollingIntervalSeconds { get; set; } = 30;

    [Column("last_worker_heartbeat_at")]
    public DateTime? LastWorkerHeartbeatAt { get; set; }

    [Column("last_scan_started_at")]
    public DateTime? LastScanStartedAt { get; set; }

    [Column("last_scan_completed_at")]
    public DateTime? LastScanCompletedAt { get; set; }

    [Column("last_error")]
    public string LastError { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("trader_scans")]
public class TraderScanEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("start_utc")]
    public DateTime StartUtc { get; set; }

    [Column("end_utc")]
    public DateTime EndUtc { get; set; }

    [Column("minimum_starting_value_usd")]
    public decimal MinimumStartingValueUsd { get; set; }

    [Column("requested_top")]
    public int RequestedTop { get; set; }

    [Column("evaluated_wallet_count")]
    public int EvaluatedWalletCount { get; set; }

    [Column("qualified_wallet_count")]
    public int QualifiedWalletCount { get; set; }

    [Column("state")]
    [MaxLength(40)]
    public string State { get; set; } = "QUEUED";

    [Column("progress_percent")]
    public int ProgressPercent { get; set; }

    [Column("current_stage")]
    [MaxLength(80)]
    public string CurrentStage { get; set; } = "queued";

    [Column("status_message")]
    public string StatusMessage { get; set; } = string.Empty;

    [Column("error_message")]
    public string ErrorMessage { get; set; } = string.Empty;

    [Column("candidate_wallets_json")]
    public string CandidateWalletsJson { get; set; } = "[]";

    [Column("progress_log_json")]
    public string ProgressLogJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TraderCandidateEntity> Candidates { get; set; } = new();
}

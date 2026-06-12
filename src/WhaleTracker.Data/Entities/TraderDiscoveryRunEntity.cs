using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("trader_discovery_runs")]
public class TraderDiscoveryRunEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("provider")]
    [MaxLength(30)]
    public string Provider { get; set; } = "dune";

    [Column("execution_id")]
    [MaxLength(80)]
    public string ExecutionId { get; set; } = string.Empty;

    [Column("state")]
    [MaxLength(40)]
    public string State { get; set; } = string.Empty;

    [Column("lookback_days")]
    public int LookbackDays { get; set; }

    [Column("minimum_active_weeks")]
    public int MinimumActiveWeeks { get; set; }

    [Column("minimum_meaningful_swaps")]
    public int MinimumMeaningfulSwaps { get; set; }

    [Column("minimum_swap_usd")]
    public decimal MinimumSwapUsd { get; set; }

    [Column("candidate_limit")]
    public int CandidateLimit { get; set; }

    [Column("candidate_count")]
    public int CandidateCount { get; set; }

    [Column("progress_percent")]
    public int ProgressPercent { get; set; }

    [Column("current_stage")]
    [MaxLength(80)]
    public string CurrentStage { get; set; } = "queued";

    [Column("status_message")]
    public string StatusMessage { get; set; } = string.Empty;

    [Column("error_message")]
    public string ErrorMessage { get; set; } = string.Empty;

    [Column("progress_log_json")]
    public string ProgressLogJson { get; set; } = "[]";

    [Column("started_at_utc")]
    public DateTime StartedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTime CompletedAtUtc { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TraderDiscoveryCandidateEntity> Candidates { get; set; } = new();
}

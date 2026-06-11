using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("live_events")]
public class LiveEventEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("type")]
    [MaxLength(80)]
    public string Type { get; set; } = string.Empty;

    [Column("severity")]
    [MaxLength(20)]
    public string Severity { get; set; } = "info";

    [Column("wallet_address")]
    [MaxLength(100)]
    public string WalletAddress { get; set; } = string.Empty;

    [Column("tx_hash")]
    [MaxLength(100)]
    public string TxHash { get; set; } = string.Empty;

    [Column("symbol")]
    [MaxLength(20)]
    public string Symbol { get; set; } = string.Empty;

    [Column("usd_value")]
    public decimal? UsdValue { get; set; }

    [Column("summary")]
    public string Summary { get; set; } = string.Empty;

    [Column("payload_json")]
    public string PayloadJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

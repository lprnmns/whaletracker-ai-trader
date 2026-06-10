using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("ai_bias_state")]
public class AiBiasStateEntity
{
    [Key]
    [Column("id")]
    [MaxLength(60)]
    public string Id { get; set; } = "global";

    [Column("bias_score")]
    public decimal BiasScore { get; set; }

    [Column("direction")]
    [MaxLength(20)]
    public string Direction { get; set; } = "NEUTRAL";

    [Column("symbol_weights_json")]
    public string SymbolWeightsJson { get; set; } = "{}";

    [Column("summary")]
    public string Summary { get; set; } = string.Empty;

    [Column("event_count")]
    public int EventCount { get; set; }

    [Column("last_event_at")]
    public DateTime? LastEventAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

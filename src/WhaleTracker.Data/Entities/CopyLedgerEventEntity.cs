using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WhaleTracker.Data.Entities;

[Table("copy_ledger_events")]
public class CopyLedgerEventEntity
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

    [Column("source_event_id")]
    [MaxLength(160)]
    public string SourceEventId { get; set; } = string.Empty;

    [Column("requested_target_contracts")]
    public decimal RequestedTargetContracts { get; set; }

    [Column("previous_trader_contracts")]
    public decimal PreviousTraderContracts { get; set; }

    [Column("aggregate_target_before")]
    public decimal AggregateTargetBefore { get; set; }

    [Column("aggregate_target_after")]
    public decimal AggregateTargetAfter { get; set; }

    [Column("actual_contracts_before")]
    public decimal ActualContractsBefore { get; set; }

    [Column("actual_contracts_after")]
    public decimal ActualContractsAfter { get; set; }

    [Column("order_action")]
    [MaxLength(40)]
    public string OrderAction { get; set; } = string.Empty;

    [Column("order_size")]
    public decimal OrderSize { get; set; }

    [Column("okx_order_id")]
    [MaxLength(80)]
    public string OkxOrderId { get; set; } = string.Empty;

    [Column("is_success")]
    public bool IsSuccess { get; set; }

    [Column("error_message")]
    public string ErrorMessage { get; set; } = string.Empty;

    [Column("raw_payload")]
    public string RawPayload { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// The lifecycle state of a stock reservation.
/// </summary>
public enum StockReservationStatus
{
    /// <summary>Stock is held for an order and reduces available-to-promise.</summary>
    Active = 0,

    /// <summary>The hold was released (order cancelled/unallocated); no longer reduces availability.</summary>
    Released = 1,

    /// <summary>The reserved stock was physically issued (delivered) and on-hand was decremented.</summary>
    Consumed = 2,
}

/// <summary>
/// An append-only-ish hold against a single <see cref="Models.Inventory"/> row. Availability is
/// computed as on-hand minus the sum of ACTIVE reservations, so two orders can never promise the
/// same physical stock. A reservation is created when an order is confirmed, released if the order
/// is cancelled, and consumed (with an on-hand decrement) when goods are issued/delivered.
///
/// Rows are never hard-deleted and the quantity is never mutated after creation; state changes move
/// the row through <see cref="StockReservationStatus"/> so the stock ledger stays auditable.
/// </summary>
public sealed class StockReservation
{
    public long Id { get; set; }

    /// <summary>Owning tenant. Fail-closed query filter is applied on this column.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>The inventory row whose stock is held.</summary>
    public long InventoryId { get; set; }

    /// <summary>The order this hold belongs to (nullable to allow non-order holds later).</summary>
    public long? OrderId { get; set; }

    /// <summary>The specific order line, when known.</summary>
    public long? OrderItemId { get; set; }

    /// <summary>Held quantity. Immutable after creation.</summary>
    public decimal Quantity { get; set; }

    public StockReservationStatus Status { get; set; } = StockReservationStatus.Active;

    /// <summary>
    /// Caller-supplied key that makes reservation idempotent: re-confirming the same order line
    /// returns the existing reservation instead of creating a duplicate hold. Unique per tenant.
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;

    public string CreatedBy { get; set; } = "system";
    public DateTime CreatedOn { get; set; }
    public DateTime? ReleasedOn { get; set; }
    public DateTime? ConsumedOn { get; set; }

    /// <summary>Optimistic-concurrency token so two racing state changes cannot both win.</summary>
    public uint Version { get; set; }
}

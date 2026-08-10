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

    /// <summary>
    /// FR-INV-01. The material lot this hold names — the physical units a picker is authorised to
    /// take, not merely the product they may take them from.
    ///
    /// <para><b>Why the hold has to name the lot.</b> Quarantining one of two lots correctly removes
    /// its quantity from available-to-promise, so no NEW order can be promised it. It does nothing
    /// about an existing hold, because a hold that names only an inventory row is satisfied by any
    /// unit on that row — including the recalled ones. The system-side door was shut at declaration
    /// and the physical one was not. A hold that names its lot lets
    /// <c>ConsumeAsync</c> refuse the recalled units outright, lets quarantine release exactly the
    /// orders that were holding THAT lot rather than displacing whoever happened to be newest, and
    /// lets a recall answer "which orders are affected" instead of "which orders hold the product".</para>
    ///
    /// <para><b>Why it is nullable, and what that costs.</b> Lots are created by goods receipt and
    /// only by goods receipt (<c>IMaterialLotRecorder</c>). Opening balances, cycle-count increases,
    /// adjustments and inter-warehouse transfers all raise on-hand with no lot behind them, so an
    /// inventory row's physical stock legitimately exceeds the sum of its lots. Requiring a lot here
    /// would mean either fabricating lots with no supplier purchase order behind them — the exact
    /// untraceable stock the traceability module exists to prevent — or refusing to reserve stock the
    /// business really holds. So the un-lotted balance is reservable, and every read reports it as a
    /// <b>named gap</b> (<see cref="OrderLineIssue.IssuedWithoutLot"/>,
    /// <c>LotCommitmentView</c>) rather than as a blank that reads like completeness.</para>
    /// </summary>
    public long? MaterialLotId { get; set; }

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

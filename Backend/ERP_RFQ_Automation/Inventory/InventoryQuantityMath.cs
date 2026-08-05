using ERP_RFQ_Automation.Inventory.Commercial;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// The single authoritative definition of the inventory arithmetic that the whole product
/// depends on. Available-to-promise used to be re-derived by hand in six places
/// (<c>InventoryAvailabilityService</c>, <c>InventoryIntelligenceController</c>,
/// <c>ProductRepository</c>, <c>CommercialInventoryEntities</c>, and twice in
/// <c>ProcurementApplicationService</c>). Five sites happened to agree; that is luck, not a
/// guarantee, and a sales rep quoting a customer needs a number that cannot drift because one
/// call site forgot a bucket. Every ATP computation must route through
/// <see cref="AvailableToPromise"/>.
/// </summary>
public static class InventoryQuantityMath
{
    /// <summary>
    /// Available-to-promise: physical on-hand minus every bucket that is either already
    /// committed (reserved/allocated) or not sellable (quarantine/damaged/expired) minus the
    /// protected safety stock. Clamped at zero — a negative ATP is never a promise a
    /// salesperson can make, and the clamp keeps every downstream sum monotonic.
    ///
    /// Quarantined, damaged and expired units are still physically <i>on hand</i>; they are
    /// subtracted here because they are not sellable, which is exactly why the movement ledger
    /// treats those reclassifications as on-hand neutral (see <see cref="OnHandDelta"/>).
    /// </summary>
    public static decimal AvailableToPromise(
        decimal onHand,
        decimal reserved,
        decimal allocated,
        decimal quarantine,
        decimal damaged,
        decimal expired,
        decimal safetyStock)
        => Math.Max(0m, onHand - reserved - allocated - quarantine - damaged - expired - safetyStock);

    /// <summary>
    /// The signed effect a movement has on <see cref="Models.Inventory.QtyOnHand"/>.
    ///
    /// Receipts, transfers in, upward adjustments and customer returns add physical units.
    /// Issues, transfers out and downward adjustments remove them. Quarantine, quarantine
    /// release, damage and expiration are <b>bucket reclassifications</b>: the units are still
    /// in the building, they just stop being sellable, so they must not move on-hand or the
    /// ledger would double-count them against the ATP formula above.
    /// </summary>
    public static int OnHandDelta(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Receipt => 1,
        InventoryMovementType.TransferIn => 1,
        InventoryMovementType.AdjustmentIncrease => 1,
        InventoryMovementType.ReturnReceipt => 1,

        InventoryMovementType.Issue => -1,
        InventoryMovementType.TransferOut => -1,
        InventoryMovementType.AdjustmentDecrease => -1,

        InventoryMovementType.Quarantine => 0,
        InventoryMovementType.QuarantineRelease => 0,
        InventoryMovementType.Damage => 0,
        InventoryMovementType.Expiration => 0,

        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            "Unmapped inventory movement type: its effect on on-hand stock is undefined."),
    };

    /// <summary>The on-hand quantity implied by an ordered sequence of movements.</summary>
    public static decimal OnHandFromMovements(IEnumerable<InventoryMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        return movements.Sum(movement => OnHandDelta(movement.Type) * movement.Quantity);
    }
}

/// <summary>
/// One inventory row's reconciliation between the persisted on-hand balance and the append-only
/// movement ledger. <see cref="Drift"/> is non-zero whenever some code path mutated stock
/// without posting a movement (or posted a movement without moving stock) — the exact failure
/// mode that makes an ERP's stock figures untrustworthy.
/// </summary>
public sealed record InventoryLedgerReconciliation(
    long BusinessUnitId,
    long InventoryId,
    long? ProductId,
    long? WarehouseId,
    decimal RecordedOnHand,
    decimal LedgerOnHand,
    int MovementCount)
{
    /// <summary>Recorded minus ledger-derived. Zero means the ledger and the balance agree.</summary>
    public decimal Drift => RecordedOnHand - LedgerOnHand;

    public bool IsBalanced => Drift == 0m;
}

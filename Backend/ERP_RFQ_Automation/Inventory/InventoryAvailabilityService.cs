using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>On-hand / reserved / available snapshot for one inventory item.</summary>
public readonly record struct StockAvailability(
    long InventoryId,
    decimal OnHand,
    decimal Reserved,
    decimal Allocated,
    decimal Quarantine,
    decimal Damaged,
    decimal Expired,
    decimal SafetyStock)
{
    /// <summary>
    /// Available-to-promise. Delegates to <see cref="InventoryQuantityMath.AvailableToPromise"/>
    /// so this number is defined in exactly one place for the whole product.
    /// </summary>
    public decimal Available => InventoryQuantityMath.AvailableToPromise(
        OnHand, Reserved, Allocated, Quarantine, Damaged, Expired, SafetyStock);
}

/// <summary>Raised when a reservation cannot be honoured because insufficient stock is available.</summary>
public sealed class InsufficientStockException(long inventoryId, decimal requested, decimal available)
    : InvalidOperationException(
        $"Cannot reserve {requested} of inventory {inventoryId}: only {available} available.")
{
    public long InventoryId { get; } = inventoryId;
    public decimal Requested { get; } = requested;
    public decimal Available { get; } = available;
}

/// <summary>
/// FR-INV-01. One lot's reservable position on an inventory row: what the lot still physically
/// holds, less the part of it that other orders have already named.
/// </summary>
/// <param name="MaterialLotId">The lot.</param>
/// <param name="LotNumber">The supplier batch, the serial, or the receipt-derived identifier.</param>
/// <param name="Remaining">Received minus declared-consumed — what the lot still contains.</param>
/// <param name="Held">The sum of ACTIVE holds naming this lot.</param>
/// <param name="ExpiryDate">Shelf life of the material, null when it does not expire.</param>
/// <param name="ReceivedOn">When it entered the building. The FIFO tie-break.</param>
public readonly record struct ReservableLot(
    long MaterialLotId,
    string LotNumber,
    decimal Remaining,
    decimal Held,
    DateOnly? ExpiryDate,
    DateTime ReceivedOn)
{
    /// <summary>How much of this lot a new hold may still name. Never negative.</summary>
    public decimal Reservable => Math.Max(0m, Remaining - Held);
}

/// <summary>
/// FR-INV-01. What a recall actually needs: the orders that are holding or have consumed a named
/// lot, as opposed to the orders that merely hold the product the lot happens to be an instance of.
/// </summary>
/// <param name="Held">Active holds naming the lot — stock committed but not yet issued.</param>
/// <param name="Consumed">Holds already issued against the lot.</param>
public sealed record LotCommitmentView(
    long MaterialLotId,
    IReadOnlyList<LotCommitment> Held,
    IReadOnlyList<LotCommitment> Consumed)
{
    public decimal HeldQuantity => Held.Sum(x => x.Quantity);
    public decimal ConsumedQuantity => Consumed.Sum(x => x.Quantity);

    /// <summary>The distinct orders a recall of this lot has to reach.</summary>
    public IReadOnlyList<long> AffectedOrderIds =>
        Held.Concat(Consumed).Where(x => x.OrderId.HasValue).Select(x => x.OrderId!.Value)
            .Distinct().OrderBy(x => x).ToList();
}

/// <summary>One order's commitment against a lot.</summary>
public sealed record LotCommitment(
    long ReservationId, long? OrderId, long? OrderItemId, decimal Quantity,
    StockReservationStatus Status, DateTime CreatedOn);

/// <summary>
/// Raised when a goods issue would move stock out of a lot that is under quality hold.
///
/// <para>This is the <b>physical</b> half of FR-MTR-05. Available-to-promise already refuses to
/// promise quarantined stock and the fulfilment declaration already refuses to name a quarantined
/// lot — but a hold taken before the recall names units a picker can still walk to the rack and
/// take. Since the hold now names its lot, the issue can refuse it.</para>
/// </summary>
public sealed class QuarantinedLotIssueException(long reservationId, long materialLotId, string lotNumber)
    : InvalidOperationException(
        $"Reservation {reservationId} holds lot {lotNumber}, which is quarantined. The stock cannot be "
        + "issued until the lot is released by an authorized user, or the order is re-allocated to "
        + "another lot.")
{
    public long ReservationId { get; } = reservationId;
    public long MaterialLotId { get; } = materialLotId;
    public string LotNumber { get; } = lotNumber;
}

public interface IInventoryAvailabilityService
{
    /// <summary>Computes on-hand, reserved and available-to-promise for one inventory item.</summary>
    Task<StockAvailability> GetAvailabilityAsync(long businessUnitId, long inventoryId, CancellationToken ct = default);

    /// <summary>
    /// Atomically holds <paramref name="quantity"/> of an inventory item for an order. Idempotent on
    /// <paramref name="idempotencyKey"/> — a replay returns the existing hold. Throws
    /// <see cref="InsufficientStockException"/> if available-to-promise is below the request.
    ///
    /// <para><paramref name="materialLotId"/> names the physical lot the hold reserves (FR-INV-01).
    /// A named lot is proved to belong to this tenant, to sit on this inventory row, to be
    /// AVAILABLE rather than quarantined, and to have that much of itself unheld — otherwise the
    /// lot column would be decoration rather than a commitment. Null holds the un-lotted balance:
    /// opening stock, adjustments and transfers have no receipt behind them and therefore no lot.</para>
    /// </summary>
    Task<StockReservation> ReserveAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId = null, long? orderItemId = null, string? actor = null,
        long? materialLotId = null, CancellationToken ct = default);

    /// <summary>
    /// FR-INV-01. The lots on one inventory row that a new hold may still name, ordered the way a
    /// warehouse actually picks: <b>first-expired-first-out</b>, then oldest receipt, then id.
    ///
    /// <para>FEFO rather than plain FIFO because a trading business holds sealant, adhesives and
    /// batteries alongside steel — picking the oldest receipt when a newer one expires sooner
    /// manufactures write-offs. Where nothing carries a shelf life the ordering degenerates
    /// exactly to FIFO, which is what the rack does anyway.</para>
    ///
    /// <para>Quarantined lots are excluded, so this and the availability bucket agree by
    /// construction rather than by two code paths remembering the same rule.</para>
    /// </summary>
    Task<IReadOnlyList<ReservableLot>> GetReservableLotsAsync(
        long businessUnitId, long inventoryId, CancellationToken ct = default);

    /// <summary>
    /// FR-INV-01 / FR-MTR-05. Releases every ACTIVE hold that names <paramref name="materialLotId"/>.
    ///
    /// <para>This is what lot-level reservation buys over
    /// <see cref="ReleaseForQuarantineAsync"/>. The quantity-based sweep frees the newest holds on
    /// the inventory row until enough stock is uncommitted — which is the best a hold that names no
    /// lot allows, but it displaces whichever orders happen to be newest rather than the orders that
    /// were actually holding the recalled material. This releases precisely the affected ones and
    /// leaves every order holding a sound lot untouched.</para>
    /// </summary>
    Task<IReadOnlyList<StockReservation>> ReleaseHoldsOnLotAsync(
        long businessUnitId, long materialLotId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// FR-INV-01. Who is committed to a lot: the orders holding it now and the orders it has
    /// already been issued to. The population a recall notice has to reach.
    /// </summary>
    Task<LotCommitmentView> GetLotCommitmentsAsync(
        long businessUnitId, long materialLotId, CancellationToken ct = default);

    /// <summary>Releases every ACTIVE hold for an order (e.g. on cancellation); availability is restored.</summary>
    Task<int> ReleaseForOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// FR-MTR-05. Releases ACTIVE holds on one inventory row until at least
    /// <paramref name="quantityToFree"/> of committed stock has been given back, newest hold first.
    /// Joins the caller's ambient transaction.
    ///
    /// <para><b>Why quarantine has to do this at all.</b> Reducing available-to-promise stops the
    /// NEXT order reserving quarantined stock, but it does nothing about stock that is already held:
    /// <see cref="ConsumeAsync"/> checks only that physical on-hand covers the hold — it never
    /// re-reads availability — so a lot reserved before it was recalled would still ship. Leaving
    /// the hold in place would make the quarantine look enforced on every screen while the goods
    /// went out of the door.</para>
    ///
    /// <para>Newest-first is deliberate: the oldest commitment is the one the customer was promised
    /// first, and first-come-first-served is a rule a salesperson can explain. Every release is
    /// audited as <c>STOCK_RESERVATION_RELEASED_ON_QUARANTINE</c> — a distinct event type, because
    /// "we took your stock back because the material was recalled" and "the order was cancelled" are
    /// different facts and must not be indistinguishable in the ledger.</para>
    /// </summary>
    Task<IReadOnlyList<StockReservation>> ReleaseForQuarantineAsync(
        long businessUnitId, long inventoryId, decimal quantityToFree, string? actor = null,
        CancellationToken ct = default);

    /// <summary>Releases one active hold with optimistic concurrency and an auditable transition.</summary>
    Task ReleaseAsync(long businessUnitId, long reservationId, uint expectedVersion,
        string idempotencyKey, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Consumes a hold on goods issue/delivery: the reservation becomes Consumed and the physical
    /// on-hand quantity is decremented in the same transaction. Idempotent — a consumed hold is a no-op.
    /// </summary>
    Task ConsumeAsync(long businessUnitId, long reservationId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Splits an ACTIVE hold in two: the returned child holds exactly <paramref name="quantity"/>
    /// and the original keeps the balance. The sum of ACTIVE quantities is unchanged, so
    /// available-to-promise does not move and no stock is briefly re-promisable mid-split.
    ///
    /// This exists so a PARTIAL goods issue can consume exactly the quantity that physically left
    /// the building — <see cref="ConsumeAsync"/> is all-or-nothing per hold, so shipping 10 of a
    /// 100-unit hold used to decrement on-hand by the full 100 and flip the whole line to Consumed,
    /// after which nothing could recover the 90.
    /// </summary>
    Task<StockReservation> SplitAsync(long businessUnitId, long reservationId, decimal quantity,
        string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Releases every ACTIVE hold created strictly before <paramref name="createdBefore"/>. An
    /// abandoned reservation otherwise suppresses available-to-promise forever, which quietly
    /// starves real demand; this is the sweep that gives that stock back. Auditable — every
    /// expiry posts a STOCK_RESERVATION_EXPIRED event — and idempotent, because a hold that is
    /// already Released or Consumed is skipped.
    /// </summary>
    Task<int> ExpireStaleAsync(long businessUnitId, DateTime createdBefore, string? actor = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reconciles persisted on-hand balances against the append-only movement ledger. Any row
    /// with a non-zero <see cref="InventoryLedgerReconciliation.Drift"/> was mutated by a code
    /// path that did not post a movement, which means the stock figures cannot be trusted.
    /// </summary>
    Task<IReadOnlyList<InventoryLedgerReconciliation>> ReconcileLedgerAsync(
        long businessUnitId, bool driftOnly = true, CancellationToken ct = default);
}

/// <summary>
/// Turns the single <see cref="Models.Inventory.QtyOnHand"/> column into a proper
/// on-hand / reserved / available model backed by an append-only reservation ledger, so two orders
/// can never promise the same physical stock. On-hand is only ever decremented by an authorised
/// consume (delivery) — never by a quote or a supplier response.
/// </summary>
public sealed class InventoryAvailabilityService(ErpRfqAutomationContext db) : IInventoryAvailabilityService
{
    private readonly ErpRfqAutomationContext _db = db;

    public async Task<StockAvailability> GetAvailabilityAsync(long businessUnitId, long inventoryId, CancellationToken ct = default)
    {
        var inventory = await _db.Set<Models.Inventory>()
            .Where(i => i.Id == inventoryId && i.Buid == businessUnitId)
            .Select(i => new
            {
                i.QtyOnHand,
                i.AllocatedQuantity,
                i.QuarantineQuantity,
                i.DamagedQuantity,
                i.ExpiredQuantity,
                i.SafetyStockQuantity
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Inventory {inventoryId} was not found in this tenant.");

        var reserved = await ActiveReservedAsync(businessUnitId, inventoryId, ct);
        return new StockAvailability(inventoryId, inventory.QtyOnHand, reserved,
            inventory.AllocatedQuantity, inventory.QuarantineQuantity, inventory.DamagedQuantity,
            inventory.ExpiredQuantity, inventory.SafetyStockQuantity);
    }

    public async Task<StockReservation> ReserveAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId = null, long? orderItemId = null, string? actor = null,
        long? materialLotId = null, CancellationToken ct = default)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Reservation quantity must be positive.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        if (_db.Database.CurrentTransaction is not null)
            return await ReserveWithinTransactionAsync(businessUnitId, inventoryId, quantity,
                idempotencyKey, orderId, orderItemId, actor, materialLotId, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // PostgreSQL's transaction-scoped advisory lock is the serialization boundary.
            // READ COMMITTED refreshes the snapshot after a waiter acquires that lock.
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var reserved = await ReserveWithinTransactionAsync(businessUnitId, inventoryId, quantity,
                idempotencyKey, orderId, orderItemId, actor, materialLotId, ct);
            await transaction.CommitAsync(ct);
            return reserved;
        });
    }

    private async Task<StockReservation> ReserveWithinTransactionAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId, long? orderItemId, string? actor, long? materialLotId, CancellationToken ct)
    {
        if (orderId.HasValue)
            await LockAsync(OrderLock(businessUnitId, orderId.Value), ct);
        // BUGFIX: this used to lock the identity "{bu}:{inventoryId}" while ConsumeAsync locked
        // "inventory:{bu}:{inventoryId}". Different strings hash to different advisory-lock keys,
        // so a reserve and a concurrent goods issue against the SAME inventory row did not
        // exclude each other: the consume could decrement on-hand after the reserve had read
        // availability but before it wrote its hold, over-promising physical stock. Both paths
        // now derive the identity from InventoryLock so they can never drift apart again.
        await LockAsync(InventoryLock(businessUnitId, inventoryId), ct);

        // The lock and replay check share one transaction, fencing concurrent holds.
        var existing = await _db.Set<StockReservation>()
            .FirstOrDefaultAsync(r => r.BusinessUnitId == businessUnitId && r.IdempotencyKey == idempotencyKey, ct);
        if (existing != null)
        {
            if (existing.InventoryId != inventoryId || existing.Quantity != quantity ||
                existing.OrderId != orderId || existing.OrderItemId != orderItemId ||
                existing.MaterialLotId != materialLotId)
                throw new InvalidOperationException(
                    "The reservation idempotency key was already used for a different request.");
            return existing;
        }

        var availability = await GetAvailabilityAsync(businessUnitId, inventoryId, ct);
        if (availability.Available < quantity)
            throw new InsufficientStockException(inventoryId, quantity, availability.Available);

        // FR-INV-01. A lot column nothing validates is decoration. The named lot has to be this
        // tenant's, sit on this inventory row, be AVAILABLE, and still have this much of itself
        // unheld — otherwise two orders could both "name" the same physical units while the
        // inventory-level total looked healthy, which is precisely the over-promise the
        // inventory-level check exists to prevent, one level down.
        if (materialLotId is { } lotId)
        {
            var lot = await _db.Set<Traceability.MaterialLot>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == lotId)
                .Select(x => new { x.Id, x.LotNumber, x.InventoryId, x.Status, x.QuantityReceived, x.QuantityConsumed })
                .SingleOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    $"Material lot {lotId} was not found in this tenant.");
            if (lot.InventoryId != inventoryId)
                throw new InvalidOperationException(
                    $"Material lot {lot.LotNumber} does not sit on inventory {inventoryId}.");
            if (lot.Status == Traceability.MaterialLotStatuses.Quarantined)
                throw new InvalidOperationException(
                    $"Material lot {lot.LotNumber} is quarantined and cannot be reserved.");

            var heldOnLot = await _db.Set<StockReservation>()
                .Where(r => r.BusinessUnitId == businessUnitId && r.MaterialLotId == lotId
                            && r.Status == StockReservationStatus.Active)
                .SumAsync(r => (decimal?)r.Quantity, ct) ?? 0m;
            var reservableOnLot = lot.QuantityReceived - lot.QuantityConsumed - heldOnLot;
            if (reservableOnLot < quantity)
                throw new InsufficientStockException(inventoryId, quantity, Math.Max(0m, reservableOnLot));
        }

        var reservation = new StockReservation
        {
            BusinessUnitId = businessUnitId,
            InventoryId = inventoryId,
            OrderId = orderId,
            OrderItemId = orderItemId,
            MaterialLotId = materialLotId,
            Quantity = quantity,
            Status = StockReservationStatus.Active,
            IdempotencyKey = idempotencyKey,
            CreatedBy = actor ?? "system",
            CreatedOn = DateTime.UtcNow,
            Version = 1,
        };
        _db.Set<StockReservation>().Add(reservation);
        await _db.SaveChangesAsync(ct);
        return reservation;
    }

    public async Task<int> ReleaseForOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null)
            return await ReleaseWithinTransactionAsync(businessUnitId, orderId, actor, ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var count = await ReleaseWithinTransactionAsync(businessUnitId, orderId, actor, ct);
            await transaction.CommitAsync(ct);
            return count;
        });
    }

    public async Task ReleaseAsync(long businessUnitId, long reservationId, uint expectedVersion,
        string idempotencyKey, string? actor = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            await LockAsync(ReservationLock(businessUnitId, reservationId), ct);
            var reservation = await _db.Set<StockReservation>()
                .SingleOrDefaultAsync(row => row.BusinessUnitId == businessUnitId && row.Id == reservationId, ct)
                ?? throw new KeyNotFoundException("Reservation was not found in this tenant.");

            if (reservation.Status == StockReservationStatus.Released)
            {
                var replay = await _db.ProcurementEvents.AsNoTracking().AnyAsync(row =>
                    row.BusinessUnitId == businessUnitId
                    && row.AggregateType == "StockReservation"
                    && row.AggregateId == reservationId
                    && row.EventType == "STOCK_RESERVATION_RELEASED"
                    && row.IdempotencyKey == idempotencyKey.Trim(), ct);
                if (!replay)
                    throw new InvalidOperationException("Reservation was already released by another request.");
                await transaction.CommitAsync(ct);
                return;
            }
            if (reservation.Status != StockReservationStatus.Active)
                throw new InvalidOperationException("Only an active reservation can be released.");
            if (reservation.Version != expectedVersion)
                throw new DbUpdateConcurrencyException("Reservation changed. Refresh and retry.");

            reservation.Status = StockReservationStatus.Released;
            reservation.ReleasedOn = DateTime.UtcNow;
            reservation.Version++;
            AddReservationEvent(reservation, "STOCK_RESERVATION_RELEASED", actor, reservation.ReleasedOn.Value,
                idempotencyKey.Trim());
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    private async Task<int> ReleaseWithinTransactionAsync(long businessUnitId, long orderId, string? actor,
        CancellationToken ct)
    {
        await LockAsync(OrderLock(businessUnitId, orderId), ct);
        var reservationIds = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId)
            .OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync(ct);
        foreach (var reservationId in reservationIds)
            await LockAsync(ReservationLock(businessUnitId, reservationId), ct);
        var active = await _db.Set<StockReservation>()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.Status == StockReservationStatus.Active)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var r in active)
        {
            r.Status = StockReservationStatus.Released;
            r.ReleasedOn = now;
            r.Version++;
            AddReservationEvent(r, "STOCK_RESERVATION_RELEASED", actor, now);
        }
        if (active.Count > 0)
            await _db.SaveChangesAsync(ct);
        return active.Count;
    }

    public async Task<IReadOnlyList<StockReservation>> ReleaseForQuarantineAsync(
        long businessUnitId, long inventoryId, decimal quantityToFree, string? actor = null,
        CancellationToken ct = default)
    {
        if (quantityToFree <= 0m) return [];

        async Task<IReadOnlyList<StockReservation>> ReleaseAsync()
        {
            await LockAsync(InventoryLock(businessUnitId, inventoryId), ct);
            var active = await _db.Set<StockReservation>()
                .Where(r => r.BusinessUnitId == businessUnitId && r.InventoryId == inventoryId
                            && r.Status == StockReservationStatus.Active)
                // Newest first: the earliest promise survives, which is the rule a customer
                // conversation can be built on.
                .OrderByDescending(r => r.CreatedOn).ThenByDescending(r => r.Id)
                .ToListAsync(ct);

            var released = new List<StockReservation>();
            var freed = 0m;
            var now = DateTime.UtcNow;
            foreach (var reservation in active)
            {
                if (freed >= quantityToFree) break;
                reservation.Status = StockReservationStatus.Released;
                reservation.ReleasedOn = now;
                reservation.Version++;
                AddReservationEvent(reservation, "STOCK_RESERVATION_RELEASED_ON_QUARANTINE", actor, now,
                    $"stock-reservation-quarantine:{reservation.Id}:{reservation.Version}");
                freed += reservation.Quantity;
                released.Add(reservation);
            }

            if (released.Count > 0) await _db.SaveChangesAsync(ct);
            return released;
        }

        if (_db.Database.CurrentTransaction is not null) return await ReleaseAsync();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var released = await ReleaseAsync();
            await transaction.CommitAsync(ct);
            return released;
        });
    }

    public async Task<IReadOnlyList<ReservableLot>> GetReservableLotsAsync(
        long businessUnitId, long inventoryId, CancellationToken ct = default)
    {
        var lots = await _db.Set<Traceability.MaterialLot>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.InventoryId == inventoryId
                        && x.Status == Traceability.MaterialLotStatuses.Available)
            .Select(x => new
            {
                x.Id, x.LotNumber, x.QuantityReceived, x.QuantityConsumed, x.ExpiryDate, x.ReceivedOn
            })
            .ToListAsync(ct);
        if (lots.Count == 0) return [];

        var lotIds = lots.Select(x => x.Id).ToArray();
        var held = (await _db.Set<StockReservation>().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && r.MaterialLotId != null
                            && lotIds.Contains(r.MaterialLotId!.Value)
                            && r.Status == StockReservationStatus.Active)
                .GroupBy(r => r.MaterialLotId!.Value)
                .Select(g => new { MaterialLotId = g.Key, Quantity = g.Sum(x => x.Quantity) })
                .ToListAsync(ct))
            .ToDictionary(x => x.MaterialLotId, x => x.Quantity);

        // FEFO, then FIFO, then id. Nulls last on expiry: material that never expires must not
        // outrank material that does, and DateOnly? sorts nulls FIRST by default — the sort key
        // is therefore explicit rather than relying on the framework's default, which is exactly
        // the kind of silent ordering assumption that produces write-offs nobody can explain.
        return lots
            .Select(x => new ReservableLot(x.Id, x.LotNumber,
                x.QuantityReceived - x.QuantityConsumed, held.GetValueOrDefault(x.Id, 0m),
                x.ExpiryDate, x.ReceivedOn))
            .Where(x => x.Reservable > 0m)
            .OrderBy(x => x.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(x => x.ExpiryDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.ReceivedOn)
            .ThenBy(x => x.MaterialLotId)
            .ToList();
    }

    public async Task<IReadOnlyList<StockReservation>> ReleaseHoldsOnLotAsync(
        long businessUnitId, long materialLotId, string? actor = null, CancellationToken ct = default)
    {
        async Task<IReadOnlyList<StockReservation>> ReleaseAsync()
        {
            var active = await _db.Set<StockReservation>()
                .Where(r => r.BusinessUnitId == businessUnitId && r.MaterialLotId == materialLotId
                            && r.Status == StockReservationStatus.Active)
                .OrderBy(r => r.Id)
                .ToListAsync(ct);
            if (active.Count == 0) return [];

            foreach (var inventoryId in active.Select(r => r.InventoryId).Distinct().OrderBy(x => x))
                await LockAsync(InventoryLock(businessUnitId, inventoryId), ct);

            var now = DateTime.UtcNow;
            foreach (var reservation in active)
            {
                reservation.Status = StockReservationStatus.Released;
                reservation.ReleasedOn = now;
                reservation.Version++;
                AddReservationEvent(reservation, "STOCK_RESERVATION_RELEASED_ON_QUARANTINE", actor, now,
                    $"stock-reservation-lot-quarantine:{reservation.Id}:{reservation.Version}");
            }
            await _db.SaveChangesAsync(ct);
            return active;
        }

        if (_db.Database.CurrentTransaction is not null) return await ReleaseAsync();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var released = await ReleaseAsync();
            await transaction.CommitAsync(ct);
            return released;
        });
    }

    public async Task<LotCommitmentView> GetLotCommitmentsAsync(
        long businessUnitId, long materialLotId, CancellationToken ct = default)
    {
        var rows = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.MaterialLotId == materialLotId
                        && r.Status != StockReservationStatus.Released)
            .OrderBy(r => r.OrderId).ThenBy(r => r.Id)
            .Select(r => new LotCommitment(r.Id, r.OrderId, r.OrderItemId, r.Quantity, r.Status, r.CreatedOn))
            .ToListAsync(ct);

        return new LotCommitmentView(materialLotId,
            rows.Where(x => x.Status == StockReservationStatus.Active).ToList(),
            rows.Where(x => x.Status == StockReservationStatus.Consumed).ToList());
    }

    public async Task ConsumeAsync(long businessUnitId, long reservationId, string? actor = null, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            await ConsumeWithinTransactionAsync(businessUnitId, reservationId, actor, ct);
            return;
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            await ConsumeWithinTransactionAsync(businessUnitId, reservationId, actor, ct);
            await transaction.CommitAsync(ct);
        });
    }

    public async Task<StockReservation> SplitAsync(long businessUnitId, long reservationId, decimal quantity,
        string? actor = null, CancellationToken ct = default)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Split quantity must be positive.");

        if (_db.Database.CurrentTransaction is not null)
            return await SplitWithinTransactionAsync(businessUnitId, reservationId, quantity, actor, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var child = await SplitWithinTransactionAsync(businessUnitId, reservationId, quantity, actor, ct);
            await transaction.CommitAsync(ct);
            return child;
        });
    }

    private async Task<StockReservation> SplitWithinTransactionAsync(long businessUnitId, long reservationId,
        decimal quantity, string? actor, CancellationToken ct)
    {
        await LockAsync(ReservationLock(businessUnitId, reservationId), ct);
        var reservation = await _db.Set<StockReservation>()
            .FirstOrDefaultAsync(r => r.BusinessUnitId == businessUnitId && r.Id == reservationId, ct)
            ?? throw new InvalidOperationException($"Reservation {reservationId} was not found.");

        if (reservation.Status != StockReservationStatus.Active)
            throw new InvalidOperationException($"Reservation {reservationId} is not active and cannot be split.");
        if (quantity >= reservation.Quantity)
            throw new ArgumentOutOfRangeException(nameof(quantity),
                "A split must leave a positive balance on the original hold; consume it outright instead.");

        // The same inventory row is touched (the child holds part of the parent's stock), so the
        // inventory identity is locked too — otherwise a concurrent reserve could read
        // availability between the parent's decrement and the child's insert.
        await LockAsync(InventoryLock(businessUnitId, reservation.InventoryId), ct);

        var now = DateTime.UtcNow;
        reservation.Quantity -= quantity;
        reservation.Version++;

        var child = new StockReservation
        {
            BusinessUnitId = businessUnitId,
            InventoryId = reservation.InventoryId,
            OrderId = reservation.OrderId,
            OrderItemId = reservation.OrderItemId,
            // The child holds part of the parent's PHYSICAL stock, so it holds the parent's lot.
            // Dropping it here would silently un-name the units on every partial shipment — the
            // exact gap this gate exists to close, reintroduced by the split path.
            MaterialLotId = reservation.MaterialLotId,
            Quantity = quantity,
            Status = StockReservationStatus.Active,
            // Deterministic and unique per split: the parent's version advances on every split,
            // so a replay reuses the same key and trips UX_stock_reservations_idempotency rather
            // than silently creating a second hold.
            IdempotencyKey = $"{reservation.IdempotencyKey}:split:{reservation.Version}",
            CreatedBy = actor ?? "system",
            CreatedOn = now,
            Version = 1,
        };
        _db.Set<StockReservation>().Add(child);

        // The split is a real ledger transition and is audited as one. It is NOT a release:
        // no stock goes back to available-to-promise, and calling it a release would read as
        // exactly the opposite of what happened.
        AddReservationEvent(reservation, "STOCK_RESERVATION_SPLIT", actor, now);

        await _db.SaveChangesAsync(ct);
        return child;
    }

    public async Task<int> ExpireStaleAsync(long businessUnitId, DateTime createdBefore, string? actor = null,
        CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);

            var staleIds = await _db.Set<StockReservation>().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId
                            && r.Status == StockReservationStatus.Active
                            && r.CreatedOn < createdBefore)
                .OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync(ct);
            foreach (var id in staleIds)
                await LockAsync(ReservationLock(businessUnitId, id), ct);

            // Re-read under the locks: a hold that was released or consumed in the meantime is
            // no longer stale and must not be touched.
            var stale = await _db.Set<StockReservation>()
                .Where(r => r.BusinessUnitId == businessUnitId && staleIds.Contains(r.Id)
                            && r.Status == StockReservationStatus.Active)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            foreach (var reservation in stale)
            {
                reservation.Status = StockReservationStatus.Released;
                reservation.ReleasedOn = now;
                reservation.Version++;
                AddReservationEvent(reservation, "STOCK_RESERVATION_EXPIRED", actor, now,
                    $"stock-reservation-expired:{reservation.Id}:{reservation.Version}");
            }
            if (stale.Count > 0) await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return stale.Count;
        });
    }

    public async Task<IReadOnlyList<InventoryLedgerReconciliation>> ReconcileLedgerAsync(
        long businessUnitId, bool driftOnly = true, CancellationToken ct = default)
    {
        var rows = await _db.Set<Models.Inventory>().AsNoTracking()
            .Where(i => i.Buid == businessUnitId)
            .Select(i => new { i.Id, i.ProductId, i.WarehouseId, i.QtyOnHand })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var inventoryIds = rows.Select(x => x.Id).ToArray();
        // Sum per (inventory, type) in SQL, then apply the signed on-hand semantics in memory:
        // the sign table lives in InventoryQuantityMath so the ledger and the ATP formula can
        // never disagree about whether a quarantine moves physical stock.
        var byType = await _db.InventoryMovements.AsNoTracking()
            .Where(m => m.BusinessUnitId == businessUnitId && inventoryIds.Contains(m.InventoryId))
            .GroupBy(m => new { m.InventoryId, m.Type })
            .Select(g => new { g.Key.InventoryId, g.Key.Type, Quantity = g.Sum(m => m.Quantity), Count = g.Count() })
            .ToListAsync(ct);

        var ledger = byType.GroupBy(x => x.InventoryId).ToDictionary(
            g => g.Key,
            g => (OnHand: g.Sum(x => InventoryQuantityMath.OnHandDelta(x.Type) * x.Quantity),
                  Count: g.Sum(x => x.Count)));

        var results = rows.Select(row =>
        {
            var (ledgerOnHand, count) = ledger.TryGetValue(row.Id, out var value) ? value : (0m, 0);
            return new InventoryLedgerReconciliation(businessUnitId, row.Id, row.ProductId, row.WarehouseId,
                row.QtyOnHand, ledgerOnHand, count);
        });
        return (driftOnly ? results.Where(x => !x.IsBalanced) : results)
            .OrderBy(x => x.InventoryId).ToList();
    }

    private async Task ConsumeWithinTransactionAsync(long businessUnitId, long reservationId, string? actor,
        CancellationToken ct)
    {
        await LockAsync(ReservationLock(businessUnitId, reservationId), ct);
        var reservation = await _db.Set<StockReservation>()
            .FirstOrDefaultAsync(r => r.BusinessUnitId == businessUnitId && r.Id == reservationId, ct)
            ?? throw new InvalidOperationException($"Reservation {reservationId} was not found.");

        if (reservation.Status == StockReservationStatus.Consumed)
            return; // idempotent
        if (reservation.Status == StockReservationStatus.Released)
            throw new InvalidOperationException($"Reservation {reservationId} was released and cannot be consumed.");

        // FR-MTR-05 / FR-INV-01 — THE PHYSICAL DOOR. Quarantine removes the lot's quantity from
        // available-to-promise, which stops the next order being promised it, and the fulfilment
        // declaration refuses to name a quarantined lot. Neither stops a hold taken BEFORE the
        // recall from being issued, because the hold used to name only an inventory row and any
        // unit on that row satisfied it. Now that the hold names its lot, the issue can refuse.
        //
        // This is checked here rather than in the caller because ConsumeAsync is the single point
        // every issue path funnels through: the shipment controller, the order consume endpoint and
        // the partial-shipment splitter all end up here. A check in any one of them would be a
        // guard three callers have to remember (wiring-contract failure #9).
        if (reservation.MaterialLotId is { } consumedLotId)
        {
            var lot = await _db.Set<Traceability.MaterialLot>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == consumedLotId)
                .Select(x => new { x.Id, x.LotNumber, x.Status })
                .SingleOrDefaultAsync(ct);
            if (lot is not null && lot.Status == Traceability.MaterialLotStatuses.Quarantined)
                throw new QuarantinedLotIssueException(reservation.Id, lot.Id, lot.LotNumber);
        }

        await LockAsync(InventoryLock(businessUnitId, reservation.InventoryId), ct);

        var inventory = await _db.Set<Models.Inventory>()
            .FirstOrDefaultAsync(i => i.Id == reservation.InventoryId && i.Buid == businessUnitId, ct)
            ?? throw new InvalidOperationException($"Inventory {reservation.InventoryId} was not found.");
        if (!inventory.ProductId.HasValue || !inventory.WarehouseId.HasValue)
            throw new InvalidOperationException("A goods issue requires product and warehouse inventory identity.");
        if (inventory.QtyOnHand < reservation.Quantity)
            throw new InvalidOperationException("Physical on-hand stock is below the reserved issue quantity.");

        // Physical stock leaves the building only here, on an authorised goods issue.
        var now = DateTime.UtcNow;
        inventory.QtyOnHand -= reservation.Quantity;
        inventory.ModifiedBy = actor ?? "system";
        inventory.ModifiedOn = now;

        reservation.Status = StockReservationStatus.Consumed;
        reservation.ConsumedOn = now;
        reservation.Version++;
        _db.InventoryMovements.Add(new Commercial.InventoryMovement
        {
            BusinessUnitId = businessUnitId,
            ProductId = inventory.ProductId.Value,
            InventoryId = inventory.Id,
            WarehouseId = inventory.WarehouseId.Value,
            Type = Commercial.InventoryMovementType.Issue,
            Quantity = reservation.Quantity,
            OccurredOn = now,
            IdempotencyKey = $"reservation-consume:{reservation.Id}",
            SourceType = "StockReservation",
            SourceId = reservation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Reason = "Authorised reserved stock issue",
            CreatedBy = actor ?? "system",
            CreatedOn = now
        });
        AddReservationEvent(reservation, "STOCK_RESERVATION_CONSUMED", actor, now);

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Advisory-lock identities. Every caller MUST derive its identity from these helpers: a
    /// hand-written string that differs by one character silently produces a different lock and
    /// therefore no mutual exclusion at all, which is exactly the defect these replaced.
    /// </summary>
    internal static string InventoryLock(long businessUnitId, long inventoryId)
        => $"inventory:{businessUnitId}:{inventoryId}";
    private static string ReservationLock(long businessUnitId, long reservationId)
        => $"reservation:{businessUnitId}:{reservationId}";
    private static string OrderLock(long businessUnitId, long orderId)
        => $"reservation-order:{businessUnitId}:{orderId}";

    private async Task LockAsync(string identity, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", ct);
    }

    private void AddReservationEvent(StockReservation reservation, string eventType, string? actor, DateTime occurredOn,
        string? idempotencyKey = null)
    {
        _db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = reservation.BusinessUnitId,
            AggregateType = "StockReservation",
            AggregateId = reservation.Id,
            AggregateVersion = reservation.Version,
            EventType = eventType,
            Actor = actor ?? "system",
            CorrelationId = $"stock-reservation:{reservation.Id}",
            IdempotencyKey = idempotencyKey ?? $"{eventType.ToLowerInvariant()}:{reservation.Id}:{reservation.Version}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                reservation.InventoryId,
                reservation.OrderId,
                reservation.OrderItemId,
                reservation.MaterialLotId,
                reservation.Quantity
            }),
            OccurredOn = occurredOn
        });
    }

    private async Task<decimal> ActiveReservedAsync(long businessUnitId, long inventoryId, CancellationToken ct)
    {
        var sum = await _db.Set<StockReservation>()
            .Where(r => r.BusinessUnitId == businessUnitId && r.InventoryId == inventoryId
                        && r.Status == StockReservationStatus.Active)
            .SumAsync(r => (decimal?)r.Quantity, ct);
        return sum ?? 0m;
    }
}

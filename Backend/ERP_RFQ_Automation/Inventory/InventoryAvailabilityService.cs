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

public interface IInventoryAvailabilityService
{
    /// <summary>Computes on-hand, reserved and available-to-promise for one inventory item.</summary>
    Task<StockAvailability> GetAvailabilityAsync(long businessUnitId, long inventoryId, CancellationToken ct = default);

    /// <summary>
    /// Atomically holds <paramref name="quantity"/> of an inventory item for an order. Idempotent on
    /// <paramref name="idempotencyKey"/> — a replay returns the existing hold. Throws
    /// <see cref="InsufficientStockException"/> if available-to-promise is below the request.
    /// </summary>
    Task<StockReservation> ReserveAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId = null, long? orderItemId = null, string? actor = null, CancellationToken ct = default);

    /// <summary>Releases every ACTIVE hold for an order (e.g. on cancellation); availability is restored.</summary>
    Task<int> ReleaseForOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default);

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
        long? orderId = null, long? orderItemId = null, string? actor = null, CancellationToken ct = default)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Reservation quantity must be positive.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        if (_db.Database.CurrentTransaction is not null)
            return await ReserveWithinTransactionAsync(businessUnitId, inventoryId, quantity,
                idempotencyKey, orderId, orderItemId, actor, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // PostgreSQL's transaction-scoped advisory lock is the serialization boundary.
            // READ COMMITTED refreshes the snapshot after a waiter acquires that lock.
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var reserved = await ReserveWithinTransactionAsync(businessUnitId, inventoryId, quantity,
                idempotencyKey, orderId, orderItemId, actor, ct);
            await transaction.CommitAsync(ct);
            return reserved;
        });
    }

    private async Task<StockReservation> ReserveWithinTransactionAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId, long? orderItemId, string? actor, CancellationToken ct)
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
                existing.OrderId != orderId || existing.OrderItemId != orderItemId)
                throw new InvalidOperationException(
                    "The reservation idempotency key was already used for a different request.");
            return existing;
        }

        var availability = await GetAvailabilityAsync(businessUnitId, inventoryId, ct);
        if (availability.Available < quantity)
            throw new InsufficientStockException(inventoryId, quantity, availability.Available);

        var reservation = new StockReservation
        {
            BusinessUnitId = businessUnitId,
            InventoryId = inventoryId,
            OrderId = orderId,
            OrderItemId = orderItemId,
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

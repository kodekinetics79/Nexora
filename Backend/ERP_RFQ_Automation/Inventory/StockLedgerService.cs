using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>Raised when a stock write would break an inventory invariant.</summary>
public sealed class StockLedgerException(string message) : InvalidOperationException(message);

/// <summary>The state of one inventory row after a ledger write.</summary>
public sealed record StockLedgerResult(
    long InventoryId,
    long ProductId,
    long WarehouseId,
    decimal OnHand,
    decimal Quarantine,
    decimal Damaged,
    decimal Expired,
    decimal SafetyStock,
    long? MovementId);

public interface IStockLedgerService
{
    /// <summary>
    /// Sets the counted physical quantity for one product in one warehouse and posts the
    /// balancing adjustment movement. This is how opening stock and cycle counts enter the
    /// ledger — previously the ONLY way an inventory row could be created was issuing a
    /// supplier purchase order, so a client migrating their existing stock had no route in
    /// and every warehouse view rendered empty.
    /// </summary>
    Task<StockLedgerResult> RecordCountAsync(long businessUnitId, long productId, long warehouseId,
        decimal countedQuantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default);

    /// <summary>Applies a signed delta to on-hand and posts the matching adjustment movement.</summary>
    Task<StockLedgerResult> AdjustAsync(long businessUnitId, long productId, long warehouseId,
        decimal delta, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reclassifies sellable stock into (or out of) a non-sellable bucket. On-hand does not
    /// move — the units are still in the building — but available-to-promise does.
    /// </summary>
    Task<StockLedgerResult> ReclassifyAsync(long businessUnitId, long productId, long warehouseId,
        StockBucket bucket, decimal quantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default);

    /// <summary>Sets the protected safety-stock buffer. A policy value, so it posts no movement.</summary>
    Task<StockLedgerResult> SetSafetyStockAsync(long businessUnitId, long productId, long warehouseId,
        decimal safetyStock, string actor, CancellationToken ct = default);

    /// <summary>
    /// Moves physical stock between two warehouses of the same tenant as one atomic pair of
    /// TransferOut/TransferIn movements. Without this, warehouses are decorative: stock could
    /// only ever enter the warehouse its purchase order named.
    /// </summary>
    Task<(StockLedgerResult From, StockLedgerResult To)> TransferAsync(long businessUnitId, long productId,
        long fromWarehouseId, long toWarehouseId, decimal quantity, string idempotencyKey, string actor,
        string? reason = null, CancellationToken ct = default);
}

/// <summary>The non-sellable buckets that reclassification can move stock into.</summary>
public enum StockBucket
{
    /// <summary>Held pending inspection. Positive quantity quarantines, negative releases.</summary>
    Quarantine,
    /// <summary>Written down as damaged.</summary>
    Damaged,
    /// <summary>Past its expiry date.</summary>
    Expired,
}

/// <summary>
/// The authoritative write path for physical stock. Every mutation is tenant-scoped, taken under
/// the same advisory lock the reservation engine uses, idempotent on a caller-supplied key, and
/// posts an <see cref="InventoryMovement"/> in the same transaction as the balance change — so
/// <c>Inventory.QtyOnHand</c> can never drift from the sum of its movements.
///
/// <para><b>Ledger authority.</b> <c>Inventory.QtyOnHand</c> (per product per warehouse) is the
/// authoritative physical balance. <c>Product.QtyOnHand</c> is a legacy denormalised column that
/// several older paths still write; it is NOT authoritative and must not drive availability. See
/// the module report for the remaining callers that need migrating.</para>
/// </summary>
public sealed class StockLedgerService(ErpRfqAutomationContext db) : IStockLedgerService
{
    private readonly ErpRfqAutomationContext _db = db;

    public Task<StockLedgerResult> RecordCountAsync(long businessUnitId, long productId, long warehouseId,
        decimal countedQuantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default)
    {
        if (countedQuantity < 0m)
            throw new ArgumentOutOfRangeException(nameof(countedQuantity), "A counted quantity cannot be negative.");
        return WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor, ct,
            inventory => ApplyOnHandDelta(inventory, countedQuantity - inventory.QtyOnHand,
                reason ?? "Physical count", actor));
    }

    public Task<StockLedgerResult> AdjustAsync(long businessUnitId, long productId, long warehouseId,
        decimal delta, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default)
    {
        if (delta == 0m)
            throw new ArgumentOutOfRangeException(nameof(delta), "A stock adjustment must be non-zero.");
        return WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor, ct,
            inventory => ApplyOnHandDelta(inventory, delta, reason ?? "Stock adjustment", actor));
    }

    public Task<StockLedgerResult> ReclassifyAsync(long businessUnitId, long productId, long warehouseId,
        StockBucket bucket, decimal quantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default)
    {
        if (quantity == 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A reclassification must be non-zero.");
        if (bucket is not (StockBucket.Quarantine or StockBucket.Damaged or StockBucket.Expired))
            throw new ArgumentOutOfRangeException(nameof(bucket));
        return WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor, ct,
            inventory =>
            {
                var current = bucket switch
                {
                    StockBucket.Quarantine => inventory.QuarantineQuantity,
                    StockBucket.Damaged => inventory.DamagedQuantity,
                    _ => inventory.ExpiredQuantity,
                };
                var updated = current + quantity;
                if (updated < 0m)
                    throw new StockLedgerException(
                        $"Cannot release {-quantity} from the {bucket} bucket: only {current} is held there.");

                switch (bucket)
                {
                    case StockBucket.Quarantine: inventory.QuarantineQuantity = updated; break;
                    case StockBucket.Damaged: inventory.DamagedQuantity = updated; break;
                    default: inventory.ExpiredQuantity = updated; break;
                }

                // Reclassification never moves physical units, so the movement is on-hand neutral
                // (InventoryQuantityMath.OnHandDelta returns 0 for all four types).
                var type = bucket switch
                {
                    StockBucket.Quarantine => quantity > 0m
                        ? InventoryMovementType.Quarantine
                        : InventoryMovementType.QuarantineRelease,
                    StockBucket.Damaged => InventoryMovementType.Damage,
                    _ => InventoryMovementType.Expiration,
                };
                return (type, Math.Abs(quantity), reason ?? $"{bucket} reclassification");
            });
    }

    public async Task<StockLedgerResult> SetSafetyStockAsync(long businessUnitId, long productId,
        long warehouseId, decimal safetyStock, string actor, CancellationToken ct = default)
    {
        if (safetyStock < 0m)
            throw new ArgumentOutOfRangeException(nameof(safetyStock), "Safety stock cannot be negative.");
        RequireActor(actor);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(Isolation(), ct);
            var inventory = await ResolveInventoryAsync(businessUnitId, productId, warehouseId, actor, ct);
            await LockAsync(InventoryAvailabilityService.InventoryLock(businessUnitId, inventory.Id), ct);
            inventory.SafetyStockQuantity = safetyStock;
            Touch(inventory, actor);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Snapshot(inventory, null);
        });
    }

    public async Task<(StockLedgerResult From, StockLedgerResult To)> TransferAsync(long businessUnitId,
        long productId, long fromWarehouseId, long toWarehouseId, decimal quantity, string idempotencyKey,
        string actor, string? reason = null, CancellationToken ct = default)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A transfer quantity must be positive.");
        if (fromWarehouseId == toWarehouseId)
            throw new StockLedgerException("A transfer must move stock between two different warehouses.");
        RequireActor(actor);
        RequireKey(idempotencyKey);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(Isolation(), ct);

            var source = await ResolveInventoryAsync(businessUnitId, productId, fromWarehouseId, actor, ct);
            var destination = await ResolveInventoryAsync(businessUnitId, productId, toWarehouseId, actor, ct);
            // Deterministic lock ordering: two transfers moving stock in opposite directions
            // between the same warehouses would otherwise deadlock against each other.
            foreach (var id in new[] { source.Id, destination.Id }.OrderBy(x => x))
                await LockAsync(InventoryAvailabilityService.InventoryLock(businessUnitId, id), ct);

            var outKey = $"{idempotencyKey.Trim()}:out";
            var replay = await FindMovementAsync(businessUnitId, outKey, ct);
            if (replay is not null)
            {
                await tx.CommitAsync(ct);
                return (Snapshot(source, replay.Id), Snapshot(destination, null));
            }

            var reserved = await ActiveReservedAsync(businessUnitId, source.Id, ct);
            source.QtyOnHand -= quantity;
            EnsureInvariants(source, reserved);
            destination.QtyOnHand += quantity;
            Touch(source, actor);
            Touch(destination, actor);

            var now = DateTime.UtcNow;
            var text = reason ?? $"Warehouse transfer {fromWarehouseId} to {toWarehouseId}";
            var outbound = NewMovement(source, InventoryMovementType.TransferOut, quantity, outKey,
                "StockTransfer", idempotencyKey.Trim(), text, actor, now);
            var inbound = NewMovement(destination, InventoryMovementType.TransferIn, quantity,
                $"{idempotencyKey.Trim()}:in", "StockTransfer", idempotencyKey.Trim(), text, actor, now);
            _db.InventoryMovements.Add(outbound);
            _db.InventoryMovements.Add(inbound);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (Snapshot(source, outbound.Id), Snapshot(destination, inbound.Id));
        });
    }

    /// <summary>
    /// Shared write pipeline: transaction, advisory lock, idempotency replay check, mutation,
    /// invariant enforcement and movement posting — in that order, all in one transaction.
    /// </summary>
    private async Task<StockLedgerResult> WriteAsync(long businessUnitId, long productId, long warehouseId,
        string idempotencyKey, string actor, CancellationToken ct,
        Func<Models.Inventory, (InventoryMovementType Type, decimal Quantity, string Reason)?> mutate)
    {
        RequireActor(actor);
        RequireKey(idempotencyKey);
        var key = idempotencyKey.Trim();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(Isolation(), ct);
            var inventory = await ResolveInventoryAsync(businessUnitId, productId, warehouseId, actor, ct);
            await LockAsync(InventoryAvailabilityService.InventoryLock(businessUnitId, inventory.Id), ct);

            var replay = await FindMovementAsync(businessUnitId, key, ct);
            if (replay is not null)
            {
                await tx.CommitAsync(ct);
                return Snapshot(inventory, replay.Id);
            }

            var posting = mutate(inventory);
            if (posting is null)
            {
                await tx.CommitAsync(ct);
                return Snapshot(inventory, null);
            }

            var reserved = await ActiveReservedAsync(businessUnitId, inventory.Id, ct);
            EnsureInvariants(inventory, reserved);
            Touch(inventory, actor);

            var movement = NewMovement(inventory, posting.Value.Type, posting.Value.Quantity, key,
                "StockLedger", key, posting.Value.Reason, actor, DateTime.UtcNow);
            _db.InventoryMovements.Add(movement);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Snapshot(inventory, movement.Id);
        });
    }

    private static (InventoryMovementType, decimal, string)? ApplyOnHandDelta(
        Models.Inventory inventory, decimal delta, string reason, string actor)
    {
        _ = actor;
        if (delta == 0m) return null; // a count that matches the book value posts nothing
        inventory.QtyOnHand += delta;
        return (delta > 0m ? InventoryMovementType.AdjustmentIncrease : InventoryMovementType.AdjustmentDecrease,
            Math.Abs(delta), reason);
    }

    /// <summary>
    /// The invariants that make stock figures trustworthy: physical stock is never negative, and
    /// the non-sellable plus committed buckets can never exceed what is physically present.
    /// Safety stock is deliberately excluded — it is a policy buffer that may legitimately be set
    /// above the current balance to signal a shortage.
    /// </summary>
    private static void EnsureInvariants(Models.Inventory inventory, decimal reserved)
    {
        if (inventory.QtyOnHand < 0m)
            throw new StockLedgerException(
                $"The change would drive on-hand stock for inventory {inventory.Id} negative ({inventory.QtyOnHand}).");
        var committed = reserved + inventory.AllocatedQuantity + inventory.QuarantineQuantity
            + inventory.DamagedQuantity + inventory.ExpiredQuantity;
        if (committed > inventory.QtyOnHand)
            throw new StockLedgerException(
                $"The change would leave {committed} committed or non-sellable units against only "
                + $"{inventory.QtyOnHand} on hand for inventory {inventory.Id}.");
    }

    /// <summary>
    /// Finds the tenant's inventory row for a product/warehouse pair, creating it on first use.
    /// Both the product and the warehouse are re-read under the caller's tenant, so a caller can
    /// never attach stock to another tenant's product or warehouse by passing its id.
    /// </summary>
    private async Task<Models.Inventory> ResolveInventoryAsync(long businessUnitId, long productId,
        long warehouseId, string actor, CancellationToken ct)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var existing = await _db.Set<Models.Inventory>().SingleOrDefaultAsync(
            x => x.Buid == businessUnitId && x.ProductId == productId && x.WarehouseId == warehouseId, ct);
        if (existing is not null) return existing;

        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId && x.Buid == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Product {productId} was not found in this tenant.");
        var warehouseExists = await _db.Warehouses.AsNoTracking()
            .AnyAsync(x => x.Id == warehouseId && x.BusinessUnitId == businessUnitId, ct);
        if (!warehouseExists)
            throw new KeyNotFoundException($"Warehouse {warehouseId} was not found in this tenant.");

        var created = new Models.Inventory
        {
            Buid = businessUnitId,
            ProductId = product.Id,
            WarehouseId = warehouseId,
            PartNo = product.PartNo,
            ProductName = product.ProductName,
            Description = product.Description,
            QtyOnHand = 0m,
            ReorderPoint = product.ReorderPoint,
            UnitCost = product.UnitCost,
            SellingPrice = product.SellingPrice,
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Set<Models.Inventory>().Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    private Task<InventoryMovement?> FindMovementAsync(long businessUnitId, string key, CancellationToken ct)
        => _db.InventoryMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct)!;

    private async Task<decimal> ActiveReservedAsync(long businessUnitId, long inventoryId, CancellationToken ct)
        => await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.InventoryId == inventoryId
                        && r.Status == StockReservationStatus.Active)
            .SumAsync(r => (decimal?)r.Quantity, ct) ?? 0m;

    private static InventoryMovement NewMovement(Models.Inventory inventory, InventoryMovementType type,
        decimal quantity, string idempotencyKey, string sourceType, string sourceId, string reason,
        string actor, DateTime now) => new()
        {
            BusinessUnitId = inventory.Buid!.Value,
            ProductId = inventory.ProductId!.Value,
            InventoryId = inventory.Id,
            WarehouseId = inventory.WarehouseId!.Value,
            Type = type,
            Quantity = quantity,
            OccurredOn = now,
            IdempotencyKey = idempotencyKey,
            SourceType = sourceType,
            SourceId = sourceId,
            Reason = reason,
            CreatedBy = actor,
            CreatedOn = now,
        };

    private static StockLedgerResult Snapshot(Models.Inventory inventory, long? movementId)
        => new(inventory.Id, inventory.ProductId ?? 0, inventory.WarehouseId ?? 0, inventory.QtyOnHand,
            inventory.QuarantineQuantity, inventory.DamagedQuantity, inventory.ExpiredQuantity,
            inventory.SafetyStockQuantity, movementId);

    private static void Touch(Models.Inventory inventory, string actor)
    {
        inventory.ModifiedBy = actor;
        inventory.ModifiedOn = DateTime.UtcNow;
    }

    private IsolationLevel Isolation()
        => _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;

    private async Task LockAsync(string identity, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", ct);
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required for a stock write.", nameof(actor));
    }

    private static void RequireKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
    }
}

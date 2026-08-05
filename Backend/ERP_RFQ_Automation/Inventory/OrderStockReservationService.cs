using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>Per-line outcome of allocating stock for an order.</summary>
public sealed record OrderLineAllocation(
    long OrderItemId,
    long ProductId,
    string PartNo,
    decimal Requested,
    decimal Reserved,
    decimal Shortage,
    long? InventoryId,
    string Outcome); // Reserved | PartiallyReserved | Shortage | NoInventoryMatch

/// <summary>Aggregate result of an order allocation pass.</summary>
public sealed record OrderAllocationResult(long OrderId, IReadOnlyList<OrderLineAllocation> Lines)
{
    public bool FullyAllocated => Lines.Count > 0 && Lines.All(l => l.Shortage == 0m && l.Outcome == "Reserved");
    public bool HasShortages => Lines.Any(l => l.Shortage > 0m || l.Outcome is "Shortage" or "NoInventoryMatch" or "PartiallyReserved");
    public decimal TotalShortage => Lines.Sum(l => l.Shortage);
}

/// <summary>
/// Per-line outcome of a goods issue. <c>Issued</c> is what THIS issue moved (it is below
/// <c>Declared</c> when the line no longer holds that much stock, e.g. a replayed shipment);
/// <c>StillReserved</c> is the balance the line holds afterwards — the quantity a later shipment
/// can consume, or that <c>ReleaseOrderAsync</c> can give back if the order is cancelled.
/// </summary>
public sealed record OrderLineIssue(long OrderItemId, decimal Declared, decimal Issued, decimal StillReserved);

/// <summary>Aggregate result of one goods issue against an order.</summary>
public sealed record OrderIssueResult(long OrderId, IReadOnlyList<OrderLineIssue> Lines)
{
    public decimal TotalIssued => Lines.Sum(l => l.Issued);
    public decimal TotalStillReserved => Lines.Sum(l => l.StillReserved);

    /// <summary>True when at least one line still holds stock the issue did not consume.</summary>
    public bool HasUnshippedBalance => Lines.Any(l => l.StillReserved > 0m);
}

public interface IOrderStockReservationService
{
    /// <summary>
    /// Reserves available stock for every line of an order (called at order confirmation). Each line
    /// resolves its product to inventory by part number; available stock is held and any shortfall is
    /// reported so procurement demand can be raised. Idempotent per order line — re-running returns the
    /// existing holds rather than double-reserving.
    /// </summary>
    Task<OrderAllocationResult> ReserveOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default);

    /// <summary>Releases all active holds for an order (order cancelled / unallocated).</summary>
    Task<int> ReleaseOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default);

    /// <summary>Consumes all active holds for an order on goods issue/delivery, decrementing on-hand.</summary>
    Task<int> ConsumeOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Consumes EXACTLY the quantities a goods issue declares, per order line, and leaves the
    /// unshipped balance reserved for that same line.
    ///
    /// This is the entry point a partial shipment must use. <see cref="ConsumeOrderAsync"/> is
    /// order-scoped and all-or-nothing: it reads no quantity at all, so shipping 10 units of a
    /// 100-unit line decremented on-hand by 100, posted an Issue movement for 100 and flipped the
    /// hold to Consumed — after which <see cref="ReleaseOrderAsync"/> could never recover the 90,
    /// and <c>ReconcileLedgerAsync</c> reported zero drift because the movement and the decrement
    /// agreed with each other. Silent, irreversible ledger corruption with no signal.
    ///
    /// A line consumes at most the stock it actually holds: a declared quantity above the active
    /// holds (a replayed shipment, or a line that could only be partially allocated) issues what
    /// is there and reports the difference through <see cref="OrderLineIssue"/>, exactly as the
    /// allocation path already reports a shortage rather than failing.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The order is not in this tenant.</exception>
    /// <exception cref="InvalidOperationException">A declared line does not belong to the order.</exception>
    Task<OrderIssueResult> ConsumeOrderLinesAsync(
        long businessUnitId, long orderId, IReadOnlyDictionary<long, decimal> quantityByOrderItemId,
        string? actor = null, CancellationToken ct = default);

    /// <summary>Releases active holds whose owning order no longer exists (recovers leaked stock).</summary>
    Task<int> ReleaseOrphanedAsync(long businessUnitId, string? actor = null, CancellationToken ct = default);
}

/// <summary>
/// Bridges the order spine to the stock ledger: turns "confirm this order" into concrete reservations
/// against real inventory rows, and "deliver this order" into stock consumption. Partial allocation is
/// first-class — an order with insufficient stock reserves what it can and reports the shortage instead
/// of failing, so a supplier PO can be raised for the balance.
/// </summary>
public sealed class OrderStockReservationService(
    ErpRfqAutomationContext db,
    IInventoryAvailabilityService availability) : IOrderStockReservationService
{
    private readonly ErpRfqAutomationContext _db = db;
    private readonly IInventoryAvailabilityService _availability = availability;

    public async Task<OrderAllocationResult> ReserveOrderAsync(
        long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
    {
        // TENANT ISOLATION: OrderItem carries no BusinessUnitId (isolation is parent-derived), so
        // the previous "WHERE OrderId = @id" read another tenant's order lines verbatim — the same
        // class of defect found in SupplierPurchaseHistory. Every line is now reached only through
        // an Order proven to belong to the caller's tenant.
        var orderExists = await _db.Set<Order>().AsNoTracking()
            .AnyAsync(o => o.Id == orderId && o.BusinessUnitId == businessUnitId, ct);
        if (!orderExists)
            throw new KeyNotFoundException($"Order {orderId} was not found in this tenant.");

        var lines = await (from item in _db.Set<OrderItem>().AsNoTracking()
                           join order in _db.Set<Order>().AsNoTracking() on item.OrderId equals order.Id
                           where order.Id == orderId && order.BusinessUnitId == businessUnitId && item.IsActive
                           orderby item.Id
                           select new { item.Id, item.ProductId, item.Quantity, item.WarehouseId })
            .ToListAsync(ct);

        // ATOMICITY: allocation used to run one self-committing transaction per line, so a failure
        // on line 3 left lines 1-2 holding stock for an order that was never allocated. One
        // transaction spans the whole order; ReserveAsync joins an ambient transaction when it
        // finds one, so the per-line advisory locks still apply.
        async Task<OrderAllocationResult> AllocateAllAsync()
        {
            var results = new List<OrderLineAllocation>(lines.Count);
            foreach (var line in lines)
                results.Add(await AllocateLineAsync(businessUnitId, orderId, line.Id, line.ProductId,
                    line.Quantity, line.WarehouseId, actor, ct));
            return new OrderAllocationResult(orderId, results);
        }

        if (_db.Database.CurrentTransaction is not null) return await AllocateAllAsync();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var result = await AllocateAllAsync();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// Releases ACTIVE holds whose order no longer exists. Deleting an order
    /// (<c>OrderRepository.DeleteAsync</c>) removes the row without touching stock — there is no
    /// FK from <see cref="StockReservation"/> to <c>Orders</c> — so the hold survives with a
    /// dangling OrderId, nothing can ever call <see cref="ReleaseOrderAsync"/> for it, and the
    /// stock is suppressed from availability forever. This sweep is the recovery path.
    /// </summary>
    public async Task<int> ReleaseOrphanedAsync(long businessUnitId, string? actor = null,
        CancellationToken ct = default)
    {
        var orphanOrderIds = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.Status == StockReservationStatus.Active
                        && r.OrderId != null
                        && !_db.Set<Order>().Any(o => o.Id == r.OrderId && o.BusinessUnitId == businessUnitId))
            .Select(r => r.OrderId!.Value).Distinct().ToArrayAsync(ct);

        var released = 0;
        foreach (var orderId in orphanOrderIds)
            released += await _availability.ReleaseForOrderAsync(businessUnitId, orderId, actor, ct);
        return released;
    }

    private async Task<OrderLineAllocation> AllocateLineAsync(long businessUnitId, long orderId, long orderItemId,
        long productId, decimal requested, long? lineWarehouseId, string? actor, CancellationToken ct)
    {
        var partNo = await _db.Set<Product>().AsNoTracking()
            .Where(p => p.Id == productId && p.Buid == businessUnitId)
            .Select(p => p.PartNo)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(partNo))
            return new OrderLineAllocation(orderItemId, productId, "", requested, 0m, requested, null, "NoInventoryMatch");

        // Idempotency is keyed on the ORDER LINE, not on a single key string, because one line can
        // now legitimately hold stock in several warehouses. Consumed holds count as already
        // fulfilled; released holds do not, so an unallocated order can be re-allocated.
        var lineReservations = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId && r.OrderItemId == orderItemId)
            .Select(r => new { r.InventoryId, r.Quantity, r.Status })
            .ToListAsync(ct);
        var priorHolds = lineReservations
            .Where(r => r.Status is StockReservationStatus.Active or StockReservationStatus.Consumed)
            .ToList();
        var alreadyHeld = priorHolds.Sum(x => x.Quantity);
        // Attempt counter per (line, inventory row). A line may legitimately need a second hold on
        // the same warehouse row when stock arrives after a partial allocation, so the key has to
        // vary between passes while staying deterministic within one.
        var attempts = lineReservations.GroupBy(r => r.InventoryId).ToDictionary(g => g.Key, g => g.Count());

        // BUGFIX: the candidate set used to be "ProductId matches OR PartNo matches", so a second
        // product that happened to share a part number could have its stock reserved for this line.
        // Product identity is the only safe join; PartNo is a display attribute, not a key.
        var candidates = await _db.Set<Models.Inventory>().AsNoTracking()
            .Where(i => i.ProductId == productId && i.Buid == businessUnitId
                        && (lineWarehouseId == null || i.WarehouseId == lineWarehouseId))
            .Select(i => new { i.Id, i.WarehouseId })
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return new OrderLineAllocation(orderItemId, productId, partNo, requested, 0m, requested, null, "NoInventoryMatch");

        var outstanding = Math.Max(0m, requested - alreadyHeld);
        if (outstanding == 0m)
            return new OrderLineAllocation(orderItemId, productId, partNo, requested, alreadyHeld, 0m,
                priorHolds.Count == 1 ? priorHolds[0].InventoryId : null, "Reserved");

        // BUGFIX: candidates used to be ranked by QtyOnHand and only the single best row was used.
        // A warehouse holding 1000 units that were all reserved outranked one with 500 free, and
        // stock split 60/50 across two warehouses reported a false shortage on an order for 100 —
        // while the quote-stage router (FulfilmentRouteService) promised all 110. Rank by
        // available-to-promise and spill across warehouses so quote and order agree.
        var ranked = new List<(long InventoryId, decimal Available)>();
        foreach (var candidate in candidates)
        {
            var availability = await _availability.GetAvailabilityAsync(businessUnitId, candidate.Id, ct);
            if (availability.Available > 0m) ranked.Add((candidate.Id, availability.Available));
        }

        var remaining = outstanding;
        long? lastInventoryId = priorHolds.Count > 0 ? priorHolds[0].InventoryId : null;
        foreach (var (inventoryId, available) in ranked.OrderByDescending(x => x.Available).ThenBy(x => x.InventoryId))
        {
            if (remaining <= 0m) break;
            var take = Math.Min(remaining, available);
            if (take <= 0m) continue;
            try
            {
                await _availability.ReserveAsync(businessUnitId, inventoryId, take,
                    ReservationKey(orderId, orderItemId, inventoryId, attempts.GetValueOrDefault(inventoryId)),
                    orderId, orderItemId, actor, ct);
                remaining -= take;
                lastInventoryId = inventoryId;
            }
            catch (InsufficientStockException)
            {
                // Another order took the stock between the availability read and the hold. The
                // reserve is the authority; treat this row as exhausted and try the next warehouse
                // instead of failing the whole allocation with a 500.
            }
        }

        // Re-read rather than accumulate: the persisted holds are the only trustworthy count of
        // what this line owns, and a replayed key returns an existing hold rather than a new one.
        var totalReserved = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId && r.OrderItemId == orderItemId
                        && (r.Status == StockReservationStatus.Active || r.Status == StockReservationStatus.Consumed))
            .SumAsync(r => (decimal?)r.Quantity, ct) ?? 0m;
        var shortage = Math.Max(0m, requested - totalReserved);
        var outcome = shortage == 0m ? "Reserved" : totalReserved > 0m ? "PartiallyReserved" : "Shortage";
        return new OrderLineAllocation(orderItemId, productId, partNo, requested, totalReserved, shortage,
            lastInventoryId ?? candidates[0].Id, outcome);
    }

    /// <summary>
    /// Deterministic per (order line, inventory row) so a replayed confirmation reuses the same
    /// hold, and a line spilling across warehouses gets one distinct key per warehouse.
    /// </summary>
    private static string ReservationKey(long orderId, long orderItemId, long inventoryId, int attempt)
        => $"order:{orderId}:item:{orderItemId}:inventory:{inventoryId}:seq:{attempt}";

    public Task<int> ReleaseOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
        => _availability.ReleaseForOrderAsync(businessUnitId, orderId, actor, ct);

    public async Task<int> ConsumeOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
    {
        var reservationIds = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.Status == StockReservationStatus.Active)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (reservationIds.Count == 0) return 0;

        // ATOMICITY: a goods issue for a multi-line order is one physical event. Consuming each
        // hold in its own transaction meant a failure part-way left some lines' on-hand
        // decremented and others not, with no record of which.
        async Task ConsumeAllAsync()
        {
            foreach (var id in reservationIds)
                await _availability.ConsumeAsync(businessUnitId, id, actor, ct);
        }

        if (_db.Database.CurrentTransaction is not null)
        {
            await ConsumeAllAsync();
            return reservationIds.Count;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            await ConsumeAllAsync();
            await transaction.CommitAsync(ct);
        });
        return reservationIds.Count;
    }

    public async Task<OrderIssueResult> ConsumeOrderLinesAsync(
        long businessUnitId, long orderId, IReadOnlyDictionary<long, decimal> quantityByOrderItemId,
        string? actor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quantityByOrderItemId);

        // TENANT ISOLATION: identical to ReserveOrderAsync. OrderItem carries no BusinessUnitId,
        // so every line is reached only through an Order proven to belong to the caller.
        var orderExists = await _db.Set<Order>().AsNoTracking()
            .AnyAsync(o => o.Id == orderId && o.BusinessUnitId == businessUnitId, ct);
        if (!orderExists)
            throw new KeyNotFoundException($"Order {orderId} was not found in this tenant.");

        var declaredIds = quantityByOrderItemId.Keys.ToArray();
        if (declaredIds.Length == 0) return new OrderIssueResult(orderId, []);

        // A declared line that belongs to some OTHER order (possibly another tenant's) must not
        // reach the ledger: OrderItem ids are global, and a caller naming a foreign line would
        // otherwise have it silently ignored while the shipment claimed to have issued it.
        var ownLineIds = await (from item in _db.Set<OrderItem>().AsNoTracking()
                                join order in _db.Set<Order>().AsNoTracking() on item.OrderId equals order.Id
                                where order.Id == orderId && order.BusinessUnitId == businessUnitId
                                      && declaredIds.Contains(item.Id)
                                select item.Id).ToListAsync(ct);
        var foreign = declaredIds.Except(ownLineIds).OrderBy(id => id).ToArray();
        if (foreign.Length > 0)
            throw new InvalidOperationException(
                $"Order line(s) {string.Join(", ", foreign)} do not belong to order {orderId}.");

        async Task<OrderIssueResult> IssueAllAsync()
        {
            var lines = new List<OrderLineIssue>(declaredIds.Length);
            foreach (var orderItemId in declaredIds.OrderBy(id => id))
                lines.Add(await IssueLineAsync(businessUnitId, orderId, orderItemId,
                    quantityByOrderItemId[orderItemId], actor, ct));
            return new OrderIssueResult(orderId, lines);
        }

        // ATOMICITY: a goods issue for a multi-line order is one physical event (same reasoning
        // as ConsumeOrderAsync). Joins the caller's ambient transaction when there is one.
        if (_db.Database.CurrentTransaction is not null) return await IssueAllAsync();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var result = await IssueAllAsync();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task<OrderLineIssue> IssueLineAsync(long businessUnitId, long orderId, long orderItemId,
        decimal declared, string? actor, CancellationToken ct)
    {
        var issued = 0m;
        if (declared > 0m)
        {
            var holds = await _db.Set<StockReservation>().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                            && r.OrderItemId == orderItemId && r.Status == StockReservationStatus.Active)
                .OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.Quantity })
                .ToListAsync(ct);

            var remaining = declared;
            foreach (var hold in holds)
            {
                if (remaining <= 0m) break;
                if (hold.Quantity <= remaining)
                {
                    await _availability.ConsumeAsync(businessUnitId, hold.Id, actor, ct);
                    remaining -= hold.Quantity;
                    issued += hold.Quantity;
                }
                else
                {
                    // Partial: split off exactly what is shipping and consume the child, so the
                    // remainder stays a live ACTIVE hold on the same line rather than being
                    // swallowed by an all-or-nothing consume.
                    var shipped = await _availability.SplitAsync(businessUnitId, hold.Id, remaining, actor, ct);
                    await _availability.ConsumeAsync(businessUnitId, shipped.Id, actor, ct);
                    issued += remaining;
                    remaining = 0m;
                }
            }
        }

        // Re-read rather than accumulate: the persisted holds are the only trustworthy account of
        // what this line still owns after the issue.
        var stillReserved = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.OrderItemId == orderItemId && r.Status == StockReservationStatus.Active)
            .SumAsync(r => (decimal?)r.Quantity, ct) ?? 0m;

        return new OrderLineIssue(orderItemId, declared, issued, stillReserved);
    }
}

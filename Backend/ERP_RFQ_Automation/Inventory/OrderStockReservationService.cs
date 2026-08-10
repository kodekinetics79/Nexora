using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>Per-line outcome of allocating stock for an order.</summary>
/// <param name="ReservedFromLots">
/// FR-INV-01. The part of <paramref name="Reserved"/> that names a material lot, and is therefore
/// traceable, recall-addressable and refused at goods issue if the lot is quarantined.
/// </param>
/// <param name="ReservedWithoutLot">
/// The balance held against stock that has no lot behind it — opening balances, cycle-count
/// increases, adjustments and inter-warehouse transfers, none of which pass through a goods
/// receipt. Reported as its own number rather than folded into the total, because a hold nobody
/// can trace is a fact somebody has to be able to see. Zero is the healthy state.
/// </param>
public sealed record OrderLineAllocation(
    long OrderItemId,
    long ProductId,
    string PartNo,
    decimal Requested,
    decimal Reserved,
    decimal Shortage,
    long? InventoryId,
    string Outcome, // Reserved | PartiallyReserved | Shortage | NoInventoryMatch
    decimal ReservedFromLots = 0m,
    decimal ReservedWithoutLot = 0m);

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
/// <param name="IssuedFromLots">
/// FR-INV-01/FR-INV-03. The part of <paramref name="Issued"/> that named a material lot and was
/// therefore declared against the lot's where-used trace by this issue.
/// </param>
/// <param name="IssuedWithoutLot">
/// The part that came from stock with no lot behind it. It is real material and it really left,
/// so the trace reports it as an <c>UndeclaredFulfilment</c> gap. Naming it here is what stops
/// that gap being discovered only on the trace screen.
/// </param>
public sealed record OrderLineIssue(
    long OrderItemId, decimal Declared, decimal Issued, decimal StillReserved,
    decimal IssuedFromLots = 0m, decimal IssuedWithoutLot = 0m)
{
    /// <summary>
    /// True when the issue moved less than the shipment declared. The caller MUST act on this:
    /// a shipment that reports success while issuing nothing is a lorry that left with paperwork
    /// and no goods.
    /// </summary>
    public bool IsShort => Issued < Declared;
}

/// <summary>Aggregate result of one goods issue against an order.</summary>
public sealed record OrderIssueResult(long OrderId, IReadOnlyList<OrderLineIssue> Lines)
{
    public decimal TotalIssued => Lines.Sum(l => l.Issued);
    public decimal TotalDeclared => Lines.Sum(l => l.Declared);
    public decimal TotalStillReserved => Lines.Sum(l => l.StillReserved);
    public decimal TotalIssuedFromLots => Lines.Sum(l => l.IssuedFromLots);
    public decimal TotalIssuedWithoutLot => Lines.Sum(l => l.IssuedWithoutLot);

    /// <summary>True when at least one line still holds stock the issue did not consume.</summary>
    public bool HasUnshippedBalance => Lines.Any(l => l.StillReserved > 0m);

    /// <summary>Lines where less physically moved than the despatch said would move.</summary>
    public IReadOnlyList<OrderLineIssue> ShortLines => Lines.Where(l => l.IsShort).ToList();

    public bool IsShort => ShortLines.Count > 0;
}

/// <summary>
/// Raised when a goods issue moved less stock than the caller declared.
///
/// <para><b>Why this is an exception and not a return value the caller may ignore.</b> It was a
/// return value, and <c>ShipmentController</c> discarded it. Partial allocation does not throw —
/// deliberately, so a short order can still raise a supplier purchase order for the balance — so an
/// order whose stock had been quarantined shipped on paper with nothing issued, and the order was
/// then marked SHIPPED by counting shipment <i>lines</i> rather than issued <i>quantity</i>. Two
/// silences compounding into a delivery note for goods that never moved.</para>
/// </summary>
public sealed class IncompleteGoodsIssueException(long orderId, IReadOnlyList<OrderLineIssue> shortLines)
    : InvalidOperationException(
        $"The goods issue for order {orderId} could not move the declared quantity. "
        + string.Join(" ", shortLines.Select(l =>
            $"Line {l.OrderItemId}: declared {l.Declared}, issued {l.Issued}.")))
{
    public long OrderId { get; } = orderId;
    public IReadOnlyList<OrderLineIssue> ShortLines { get; } = shortLines;
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

    /// <summary>
    /// Consumes all active holds for an order on goods issue/delivery, decrementing on-hand and
    /// declaring the lot movement for every hold that names a lot.
    ///
    /// <para>Implemented by delegating to <see cref="ConsumeOrderLinesAsync"/> with each line's own
    /// held quantity, so the traceability declaration cannot be skipped. It used to consume holds
    /// directly, which left <c>MaterialLot.QuantityConsumed</c> untouched and made the issued units
    /// reservable a second time.</para>
    /// </summary>
    /// <returns>The number of holds consumed.</returns>
    /// <exception cref="KeyNotFoundException">The order is not in this tenant.</exception>
    /// <exception cref="InvalidOperationException">
    /// A hold on the order names no order line, so its movement could not be declared.
    /// </exception>
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
    /// <param name="shipmentId">
    /// The delivery note driving the issue. Stamped onto the lot consumption declarations so
    /// where-used trace can answer "which despatch note did this lot go out on" without a join
    /// through quantities that only happen to add up.
    /// </param>
    /// <param name="complianceOverrideReason">
    /// The despatch supervisor's written acceptance of a lapsed certificate, applied only to the
    /// lots that need it. See <see cref="ILotFulfilmentDeclarer"/>.
    /// </param>
    /// <exception cref="KeyNotFoundException">The order is not in this tenant.</exception>
    /// <exception cref="InvalidOperationException">A declared line does not belong to the order.</exception>
    Task<OrderIssueResult> ConsumeOrderLinesAsync(
        long businessUnitId, long orderId, IReadOnlyDictionary<long, decimal> quantityByOrderItemId,
        string? actor = null, long? shipmentId = null, string? complianceOverrideReason = null,
        CancellationToken ct = default);

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
    IInventoryAvailabilityService availability,
    ILotFulfilmentDeclarer lotDeclarer) : IOrderStockReservationService
{
    private readonly ErpRfqAutomationContext _db = db;
    private readonly IInventoryAvailabilityService _availability = availability;
    private readonly ILotFulfilmentDeclarer _lotDeclarer = lotDeclarer;

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
            _db.ChangeTracker.Clear();
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
            .Select(r => new { r.InventoryId, r.MaterialLotId, r.Quantity, r.Status })
            .ToListAsync(ct);
        var priorHolds = lineReservations
            .Where(r => r.Status is StockReservationStatus.Active or StockReservationStatus.Consumed)
            .ToList();
        var alreadyHeld = priorHolds.Sum(x => x.Quantity);
        // Attempt counter per (line, inventory row, lot). A line may legitimately need a second
        // hold on the same warehouse row — and now on the same LOT — when stock arrives after a
        // partial allocation, so the key has to vary between passes while staying deterministic
        // within one. Keying on the inventory row alone would collide the moment one row's stock
        // spilled across two lots.
        var attempts = lineReservations
            .GroupBy(r => (r.InventoryId, r.MaterialLotId))
            .ToDictionary(g => g.Key, g => g.Count());

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
                priorHolds.Count == 1 ? priorHolds[0].InventoryId : null, "Reserved",
                priorHolds.Where(x => x.MaterialLotId.HasValue).Sum(x => x.Quantity),
                priorHolds.Where(x => !x.MaterialLotId.HasValue).Sum(x => x.Quantity));

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
            var rowBudget = Math.Min(remaining, available);
            if (rowBudget <= 0m) continue;

            // FR-INV-01. Within the chosen warehouse row, the hold is taken lot by lot in
            // first-expired-first-out order, so the reservation names the physical units it holds
            // rather than a product and a warehouse. Anything left over after the lots are
            // exhausted is the un-lotted balance — stock that entered by count, adjustment or
            // transfer and therefore has no receipt and no lot. It is still real stock and is
            // still held; it is simply held without a name, and reported as such.
            var lots = await _availability.GetReservableLotsAsync(businessUnitId, inventoryId, ct);
            foreach (var lot in lots)
            {
                if (rowBudget <= 0m) break;
                var take = Math.Min(rowBudget, lot.Reservable);
                if (take <= 0m) continue;
                if (await TryHoldAsync(businessUnitId, orderId, orderItemId, inventoryId, lot.MaterialLotId,
                        take, attempts, actor, ct))
                {
                    rowBudget -= take;
                    remaining -= take;
                    lastInventoryId = inventoryId;
                }
            }

            if (rowBudget > 0m
                && await TryHoldAsync(businessUnitId, orderId, orderItemId, inventoryId, null, rowBudget,
                    attempts, actor, ct))
            {
                remaining -= rowBudget;
                lastInventoryId = inventoryId;
            }
        }

        // Re-read rather than accumulate: the persisted holds are the only trustworthy count of
        // what this line owns, and a replayed key returns an existing hold rather than a new one.
        var held = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId && r.OrderItemId == orderItemId
                        && (r.Status == StockReservationStatus.Active || r.Status == StockReservationStatus.Consumed))
            .Select(r => new { r.Quantity, r.MaterialLotId })
            .ToListAsync(ct);
        var totalReserved = held.Sum(x => x.Quantity);
        var shortage = Math.Max(0m, requested - totalReserved);
        var outcome = shortage == 0m ? "Reserved" : totalReserved > 0m ? "PartiallyReserved" : "Shortage";
        return new OrderLineAllocation(orderItemId, productId, partNo, requested, totalReserved, shortage,
            lastInventoryId ?? candidates[0].Id, outcome,
            held.Where(x => x.MaterialLotId.HasValue).Sum(x => x.Quantity),
            held.Where(x => !x.MaterialLotId.HasValue).Sum(x => x.Quantity));
    }

    /// <summary>
    /// Places one hold and reports whether it stuck. An <see cref="InsufficientStockException"/>
    /// means another order took the stock between the availability read and the write — the
    /// reserve is the authority, so the candidate is treated as exhausted and the next lot (or
    /// warehouse) is tried, rather than failing the whole allocation with a 500.
    /// </summary>
    private async Task<bool> TryHoldAsync(long businessUnitId, long orderId, long orderItemId,
        long inventoryId, long? materialLotId, decimal quantity,
        Dictionary<(long InventoryId, long? MaterialLotId), int> attempts, string? actor, CancellationToken ct)
    {
        try
        {
            await _availability.ReserveAsync(businessUnitId, inventoryId, quantity,
                ReservationKey(orderId, orderItemId, inventoryId, materialLotId,
                    attempts.GetValueOrDefault((inventoryId, materialLotId))),
                orderId, orderItemId, actor, materialLotId, ct);
            return true;
        }
        catch (InsufficientStockException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deterministic per (order line, inventory row, lot) so a replayed confirmation reuses the
    /// same hold, a line spilling across warehouses gets one distinct key per warehouse, and a
    /// warehouse spilling across lots gets one per lot.
    /// </summary>
    private static string ReservationKey(long orderId, long orderItemId, long inventoryId,
        long? materialLotId, int attempt)
        => $"order:{orderId}:item:{orderItemId}:inventory:{inventoryId}"
           + $":lot:{materialLotId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}"
           + $":seq:{attempt}";

    public Task<int> ReleaseOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
        => _availability.ReleaseForOrderAsync(businessUnitId, orderId, actor, ct);

    public async Task<int> ConsumeOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
    {
        // LOT DECLARATION: this used to read reservation IDs and call _availability.ConsumeAsync in
        // a loop, never touching the lot declarer. On-hand fell and the hold flipped to Consumed,
        // but MaterialLot.QuantityConsumed never moved — and GetReservableLotsAsync computes a lot's
        // reservable balance as (received - consumed) minus ACTIVE holds only. The consumed hold
        // drops out of the held side while the consumed side never moves, so the lot offered its
        // full original quantity again, after the goods had physically left. The same units could
        // then be held and issued a second time, with certificates and origin attached to material
        // that is no longer in the building.
        //
        // The declaration lives on the line path, so this order-scoped entry point now delegates to
        // it, declaring for every line exactly the quantity that line currently holds. That keeps
        // the endpoint's meaning ("issue everything reserved for this order") while making the
        // traceability ledger tell the truth.
        var holds = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.Status == StockReservationStatus.Active)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.OrderItemId, r.Quantity })
            .ToListAsync(ct);
        if (holds.Count == 0) return 0;

        // A hold that names no order line cannot be declared: a lot consumption is recorded against
        // (lot, order, order line), so there is nowhere to attribute it. Refusing is the only honest
        // answer — issuing it silently is exactly the double-reservable defect described above, and
        // issuing it without a declaration would reopen it for un-lotted stock's neighbours too.
        var unattributable = holds.Where(x => x.OrderItemId is null).Select(x => x.Id).ToArray();
        if (unattributable.Length > 0)
            throw new InvalidOperationException(
                $"Order {orderId} holds stock on reservation(s) {string.Join(", ", unattributable)} that name no "
                + "order line, so a goods issue cannot declare what it moved. Release those holds, or issue "
                + "through the shipment path, which declares per line.");

        var declared = holds
            .GroupBy(x => x.OrderItemId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        _ = await ConsumeOrderLinesAsync(businessUnitId, orderId, declared, actor, ct: ct);

        // The contract is a count of holds consumed. The declared quantity IS what the lines held,
        // so this is normally every hold read above; counting the rows that actually moved keeps the
        // number honest if stock changed hands underneath this call.
        var holdIds = holds.Select(x => x.Id).ToArray();
        return await _db.Set<StockReservation>().AsNoTracking()
            .CountAsync(r => r.BusinessUnitId == businessUnitId && holdIds.Contains(r.Id)
                             && r.Status == StockReservationStatus.Consumed, ct);
    }

    public async Task<OrderIssueResult> ConsumeOrderLinesAsync(
        long businessUnitId, long orderId, IReadOnlyDictionary<long, decimal> quantityByOrderItemId,
        string? actor = null, long? shipmentId = null, string? complianceOverrideReason = null,
        CancellationToken ct = default)
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
            var lotLines = new List<LotIssueLine>();
            foreach (var orderItemId in declaredIds.OrderBy(id => id))
                lines.Add(await IssueLineAsync(businessUnitId, orderId, orderItemId,
                    quantityByOrderItemId[orderItemId], actor, shipmentId, lotLines, ct));

            // FR-INV-03 / FR-MTR-03. The declaration is made from what the issue ACTUALLY moved,
            // in the same transaction, so where-used trace can never disagree with the stock
            // ledger about which lots left the building. Gate 5 left this to a person to remember;
            // an unremembered declaration is a recall the business cannot execute.
            //
            // It runs after every line so one despatch is one compliance decision: the override
            // reason is weighed against all the lots on the despatch at once, not line by line.
            await _lotDeclarer.DeclareIssueAsync(businessUnitId, lotLines, actor ?? "system",
                IssueCorrelation(orderId, shipmentId), complianceOverrideReason, ct);

            return new OrderIssueResult(orderId, lines);
        }

        // ATOMICITY: a goods issue for a multi-line order is one physical event (same reasoning
        // as ConsumeOrderAsync). Joins the caller's ambient transaction when there is one.
        if (_db.Database.CurrentTransaction is not null) return await IssueAllAsync();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var result = await IssueAllAsync();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task<OrderLineIssue> IssueLineAsync(long businessUnitId, long orderId, long orderItemId,
        decimal declared, string? actor, long? shipmentId, List<LotIssueLine> lotLines, CancellationToken ct)
    {
        var issued = 0m;
        var fromLots = 0m;
        if (declared > 0m)
        {
            var holds = await _db.Set<StockReservation>().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                            && r.OrderItemId == orderItemId && r.Status == StockReservationStatus.Active)
                .Select(r => new { r.Id, r.Quantity, r.MaterialLotId })
                .ToListAsync(ct);

            // Consume in the order the warehouse should physically pick: first-expired-first-out,
            // then oldest hold. Ordering by reservation id alone was right only by accident —
            // holds are created in FEFO order, so it agreed as long as no second allocation pass
            // ever added a hold on an earlier-expiring lot that arrived later. It would then have
            // issued the longer-dated material first and quietly aged out the stock that expires
            // soonest, with nothing anywhere reporting it.
            var lotIds = holds.Where(x => x.MaterialLotId.HasValue)
                .Select(x => x.MaterialLotId!.Value).Distinct().ToArray();
            var lotOrder = lotIds.Length == 0
                ? new Dictionary<long, (DateOnly? Expiry, DateTime Received)>()
                : (await _db.Set<Traceability.MaterialLot>().AsNoTracking()
                        .Where(x => x.BusinessUnitId == businessUnitId && lotIds.Contains(x.Id))
                        .Select(x => new { x.Id, x.ExpiryDate, x.ReceivedOn })
                        .ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => ((DateOnly?)x.ExpiryDate, x.ReceivedOn));

            var picking = holds
                .Select(h => new
                {
                    h.Id, h.Quantity, h.MaterialLotId,
                    Key = h.MaterialLotId.HasValue && lotOrder.TryGetValue(h.MaterialLotId.Value, out var l)
                        ? l
                        : ((DateOnly?)null, DateTime.MaxValue),
                })
                .OrderBy(x => x.Key.Item1.HasValue ? 0 : 1)
                .ThenBy(x => x.Key.Item1 ?? DateOnly.MaxValue)
                .ThenBy(x => x.Key.Item2)
                .ThenBy(x => x.Id)
                .ToList();

            var remaining = declared;
            foreach (var hold in picking)
            {
                if (remaining <= 0m) break;
                decimal moved;
                if (hold.Quantity <= remaining)
                {
                    await _availability.ConsumeAsync(businessUnitId, hold.Id, actor, ct);
                    moved = hold.Quantity;
                }
                else
                {
                    // Partial: split off exactly what is shipping and consume the child, so the
                    // remainder stays a live ACTIVE hold on the same line rather than being
                    // swallowed by an all-or-nothing consume. The child carries the parent's lot.
                    var shipped = await _availability.SplitAsync(businessUnitId, hold.Id, remaining, actor, ct);
                    await _availability.ConsumeAsync(businessUnitId, shipped.Id, actor, ct);
                    moved = remaining;
                }

                remaining -= moved;
                issued += moved;
                if (hold.MaterialLotId is { } lotId)
                {
                    fromLots += moved;
                    lotLines.Add(new LotIssueLine(lotId, orderId, orderItemId, shipmentId, moved));
                }
            }
        }

        // Re-read rather than accumulate: the persisted holds are the only trustworthy account of
        // what this line still owns after the issue.
        var stillReserved = await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.OrderItemId == orderItemId && r.Status == StockReservationStatus.Active)
            .SumAsync(r => (decimal?)r.Quantity, ct) ?? 0m;

        return new OrderLineIssue(orderItemId, declared, issued, stillReserved, fromLots, issued - fromLots);
    }

    /// <summary>
    /// One correlation id per physical issue. The despatch note identifies it where there is one;
    /// the order-scoped consume endpoint has no despatch note, so it correlates on the order and
    /// stays distinguishable from a shipment-driven issue of the same order.
    /// </summary>
    private static string IssueCorrelation(long orderId, long? shipmentId)
        => shipmentId.HasValue ? $"goods-issue:shipment:{shipmentId.Value}" : $"goods-issue:order:{orderId}";
}

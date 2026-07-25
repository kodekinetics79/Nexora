using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

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
        var lines = await _db.Set<OrderItem>()
            .Where(oi => oi.OrderId == orderId && oi.IsActive)
            .Select(oi => new { oi.Id, oi.ProductId, oi.Quantity, oi.WarehouseId })
            .ToListAsync(ct);

        var results = new List<OrderLineAllocation>(lines.Count);

        foreach (var line in lines)
        {
            var partNo = await _db.Set<Product>()
                .Where(p => p.Id == line.ProductId && p.Buid == businessUnitId)
                .Select(p => p.PartNo)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(partNo))
            {
                results.Add(new OrderLineAllocation(line.Id, line.ProductId, "", line.Quantity, 0m, line.Quantity, null, "NoInventoryMatch"));
                continue;
            }

            // Resolve the inventory row: honour the line's warehouse if set, otherwise pick the row
            // with the most on-hand. Part number is the tenant-scoped join between product and stock.
            // Ranking is done client-side because the row count per part is tiny (one per warehouse)
            // and decimal ORDER BY is not portable to the SQLite test provider.
            var matches = await _db.Set<Models.Inventory>()
                .Where(i => (i.ProductId == line.ProductId || i.PartNo == partNo) && i.Buid == businessUnitId
                            && (line.WarehouseId == null || i.WarehouseId == line.WarehouseId))
                .Select(i => new { i.Id, i.QtyOnHand })
                .ToListAsync(ct);
            var candidate = matches.OrderByDescending(i => i.QtyOnHand).FirstOrDefault();

            if (candidate is null)
            {
                results.Add(new OrderLineAllocation(line.Id, line.ProductId, partNo, line.Quantity, 0m, line.Quantity, null, "NoInventoryMatch"));
                continue;
            }

            var key = $"order:{orderId}:item:{line.Id}";

            // Already reserved on a prior pass? Report the existing hold (idempotent).
            var existing = await _db.Set<StockReservation>()
                .Where(r => r.BusinessUnitId == businessUnitId && r.IdempotencyKey == key)
                .Select(r => new { r.Quantity, r.Status })
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                var shortage0 = Math.Max(0m, line.Quantity - existing.Quantity);
                results.Add(new OrderLineAllocation(line.Id, line.ProductId, partNo, line.Quantity,
                    existing.Quantity, shortage0, candidate.Id,
                    shortage0 == 0m ? "Reserved" : "PartiallyReserved"));
                continue;
            }

            var avail = await _availability.GetAvailabilityAsync(businessUnitId, candidate.Id, ct);
            var toReserve = Math.Min(line.Quantity, avail.Available);

            if (toReserve <= 0m)
            {
                results.Add(new OrderLineAllocation(line.Id, line.ProductId, partNo, line.Quantity, 0m, line.Quantity, candidate.Id, "Shortage"));
                continue;
            }

            await _availability.ReserveAsync(businessUnitId, candidate.Id, toReserve, key, orderId, line.Id, actor, ct);
            var shortage = Math.Max(0m, line.Quantity - toReserve);
            results.Add(new OrderLineAllocation(line.Id, line.ProductId, partNo, line.Quantity, toReserve, shortage,
                candidate.Id, shortage == 0m ? "Reserved" : "PartiallyReserved"));
        }

        return new OrderAllocationResult(orderId, results);
    }

    public Task<int> ReleaseOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
        => _availability.ReleaseForOrderAsync(businessUnitId, orderId, actor, ct);

    public async Task<int> ConsumeOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
    {
        var reservationIds = await _db.Set<StockReservation>()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.Status == StockReservationStatus.Active)
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var id in reservationIds)
            await _availability.ConsumeAsync(businessUnitId, id, actor, ct);

        return reservationIds.Count;
    }
}

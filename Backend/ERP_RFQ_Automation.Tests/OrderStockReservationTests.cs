using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies order-to-stock allocation: order confirmation reserves available stock per line
/// (matched product-to-inventory by part number), partial allocation reports shortages for
/// procurement instead of failing, allocation is idempotent, and delivery consumes on-hand.
/// </summary>
public class OrderStockReservationTests
{
    private const long Bu = 1;

    private sealed record Seeded(long OrderId);

    private static Seeded SeedOrder(TestDb db, decimal onHand, decimal orderQty, bool withInventory = true, string partNo = "WIDGET-1")
    {
        using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Bu);
        var customer = Seed.Customer(ctx, id: 1, buid: Bu, name: "Acme");
        var status = Seed.LeadStatus(ctx, setupId: 900, businessUnitId: Bu, value: "Confirmed");
        ctx.Set<Warehouse>().Add(new Warehouse
        {
            Id = 1, WarehouseCode = "MAIN", WarehouseName = "Main warehouse", BusinessUnitId = Bu,
            IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        ctx.SaveChanges();

        ctx.Set<Product>().Add(new Product
        {
            Id = 1, PartNo = partNo, ProductName = "Widget", Buid = Bu, WarehouseId = 1,
            CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        if (withInventory)
        {
            ctx.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
            {
                Id = 1, PartNo = partNo, ProductName = "Widget", QtyOnHand = onHand, ReorderPoint = 0m,
                Buid = Bu, ProductId = 1, WarehouseId = 1, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
        }
        ctx.Set<Order>().Add(new Order
        {
            Id = 1, OrderNo = "SO-1", CustomerId = customer.Id, BusinessUnitId = Bu, StatusId = status.SetupId,
            TotalAmount = 0m, CreatedBy = "test", CreatedOn = DateTime.UtcNow, OrderDate = DateTime.UtcNow, IsActive = true
        });
        ctx.Set<OrderItem>().Add(new OrderItem
        {
            Id = 1, OrderId = 1, ProductId = 1, WarehouseId = 1, Quantity = orderQty, UnitPrice = 10m, Discount = 0m,
            TaxAmount = 0m, TotalAmount = orderQty * 10m, CreatedBy = "test", CreatedDate = DateTime.UtcNow, IsActive = true
        });
        ctx.SaveChanges();
        return new Seeded(1);
    }

    private static OrderStockReservationService Service(TestDb db)
    {
        var ctx = db.ContextFor(Bu);
        return new OrderStockReservationService(ctx, new InventoryAvailabilityService(ctx));
    }

    [Fact]
    public async Task Confirming_order_reserves_available_stock_per_line()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);

        var result = await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        Assert.True(result.FullyAllocated);
        var line = Assert.Single(result.Lines);
        Assert.Equal(40m, line.Reserved);
        Assert.Equal(0m, line.Shortage);
        Assert.Equal("Reserved", line.Outcome);

        var avail = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(60m, avail.Available);
    }

    [Fact]
    public async Task Insufficient_stock_gives_partial_allocation_and_reports_shortage()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 30m, orderQty: 50m);

        var result = await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        Assert.False(result.FullyAllocated);
        Assert.True(result.HasShortages);
        var line = Assert.Single(result.Lines);
        Assert.Equal(30m, line.Reserved);          // reserved what was available...
        Assert.Equal(20m, line.Shortage);          // ...and flagged the balance for procurement
        Assert.Equal("PartiallyReserved", line.Outcome);
        Assert.Equal(20m, result.TotalShortage);
    }

    [Fact]
    public async Task No_inventory_row_reports_no_match_and_full_shortage()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 0m, orderQty: 10m, withInventory: false);

        var result = await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        var line = Assert.Single(result.Lines);
        Assert.Equal("NoInventoryMatch", line.Outcome);
        Assert.Equal(0m, line.Reserved);
        Assert.Equal(10m, line.Shortage);
    }

    [Fact]
    public async Task Allocation_is_idempotent()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);

        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        var again = await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        Assert.Equal(40m, again.Lines.Single().Reserved);
        // Re-confirming did not double-reserve: still exactly 60 available.
        Assert.Equal(60m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).Available);
    }

    [Fact]
    public async Task Delivery_consumes_reserved_stock_and_decrements_onhand()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);

        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        var consumed = await Service(db).ConsumeOrderAsync(Bu, 1, "rep@acme");

        Assert.Equal(1, consumed);
        var avail = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(60m, avail.OnHand);     // stock physically left on delivery
        Assert.Equal(0m, avail.Reserved);
    }

    [Fact]
    public async Task Releasing_order_frees_the_hold()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);

        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        var released = await Service(db).ReleaseOrderAsync(Bu, 1, "rep@acme");

        Assert.Equal(1, released);
        Assert.Equal(100m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).Available);
    }

    // ------------------------------------------------------------------ partial goods issue

    [Fact]
    public async Task A_partial_goods_issue_consumes_only_the_declared_quantity()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        // THE DEFECT VERBATIM: the order-scoped ConsumeOrderAsync reads no quantity at all, so
        // issuing 10 units consumed the whole 40-unit hold — on-hand fell by 40, an Issue movement
        // for 40 was posted and the reservation flipped to Consumed, so nothing could ever recover
        // the 30 that never left the warehouse. The reconciler reported no drift, because the
        // movement and the decrement agreed with each other.
        var issue = await Service(db).ConsumeOrderLinesAsync(
            Bu, 1, new Dictionary<long, decimal> { [1] = 10m }, "rep@acme");

        var line = Assert.Single(issue.Lines);
        Assert.Equal(10m, line.Issued);
        Assert.Equal(30m, line.StillReserved);
        Assert.True(issue.HasUnshippedBalance);

        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(90m, availability.OnHand);     // exactly the 10 that shipped
        Assert.Equal(30m, availability.Reserved);   // the balance is still held for this order
        Assert.Equal(60m, availability.Available);  // and is still NOT promisable to anyone else

        await using var verify = db.ContextFor(Bu);
        var movement = Assert.Single(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(InventoryMovementType.Issue, movement.Type);
        Assert.Equal(10m, movement.Quantity);       // the ledger records the physical truth
    }

    [Fact]
    public async Task The_balance_left_by_a_partial_goods_issue_is_still_recoverable()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        await Service(db).ConsumeOrderLinesAsync(Bu, 1, new Dictionary<long, decimal> { [1] = 10m }, "rep@acme");

        // The whole point of not over-consuming: cancelling the rest of the order gives the
        // unshipped units back. Against the old path this returned 0 — the hold was Consumed and
        // ReleaseOrderAsync has nothing to release.
        var released = await Service(db).ReleaseOrderAsync(Bu, 1, "rep@acme");

        Assert.Equal(1, released);
        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(90m, availability.OnHand);
        Assert.Equal(0m, availability.Reserved);
        Assert.Equal(90m, availability.Available);  // 30 units back on the shelf
    }

    [Fact]
    public async Task Issuing_the_remainder_of_a_partly_shipped_line_consumes_exactly_the_balance()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        await Service(db).ConsumeOrderLinesAsync(Bu, 1, new Dictionary<long, decimal> { [1] = 10m }, "rep@acme");

        // A second allocation pass runs first in the shipment path; the 10 already issued must not
        // be re-reserved, or the line would end up holding more than was ever ordered.
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        var issue = await Service(db).ConsumeOrderLinesAsync(
            Bu, 1, new Dictionary<long, decimal> { [1] = 30m }, "rep@acme");

        Assert.Equal(30m, Assert.Single(issue.Lines).Issued);
        Assert.False(issue.HasUnshippedBalance);

        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(60m, availability.OnHand);     // 40 ordered units, delivered across two issues
        Assert.Equal(0m, availability.Reserved);

        await using var verify = db.ContextFor(Bu);
        var movements = await verify.InventoryMovements.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(new[] { 10m, 30m }, movements.Select(x => x.Quantity).ToArray());
    }

    [Fact]
    public async Task A_declared_quantity_above_what_the_line_holds_issues_only_what_is_held()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        // A replayed shipment, or an operator typing past the ordered quantity. The ledger can
        // only ever issue stock the line actually holds, so on-hand cannot be driven below what
        // the order legitimately reserved.
        var issue = await Service(db).ConsumeOrderLinesAsync(
            Bu, 1, new Dictionary<long, decimal> { [1] = 500m }, "rep@acme");

        var line = Assert.Single(issue.Lines);
        Assert.Equal(500m, line.Declared);
        Assert.Equal(40m, line.Issued);
        Assert.Equal(0m, line.StillReserved);
        Assert.Equal(60m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).OnHand);
    }

    [Fact]
    public async Task A_goods_issue_naming_a_line_of_another_order_is_refused()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");

        // OrderItem carries no BusinessUnitId — isolation is parent-derived — so an id from
        // somebody else's order must be rejected outright rather than quietly issuing nothing.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).ConsumeOrderLinesAsync(Bu, 1, new Dictionary<long, decimal> { [999] = 1m }, "rep@acme"));

        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
        Assert.Equal(100m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).OnHand);
    }
}

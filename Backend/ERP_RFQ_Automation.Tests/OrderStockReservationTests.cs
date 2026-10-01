using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies confirmation without reservations and direct, quantity-driven shipment stock issues.
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
        return InventoryServices.OrderStock(ctx);
    }

    private static async Task<OrderIssueResult> IssueAsync(TestDb db, long shipmentId, decimal quantity, long orderItemId = 1)
    {
        using (var context = db.ContextFor(Bu))
        {
            context.Shipments.Add(new Shipment
            {
                Id = shipmentId, ShipmentNo = $"DN-{shipmentId}", OrderId = 1, BusinessUnitId = Bu,
                StatusId = 900, ShipmentDate = DateTime.UtcNow, CreatedBy = "test",
                CreatedOn = DateTime.UtcNow, IsActive = true,
            });
            await context.SaveChangesAsync();
        }
        return await Service(db).ConsumeOrderLinesAsync(Bu, 1,
            new Dictionary<long, decimal> { [orderItemId] = quantity }, "test", shipmentId);
    }

    [Fact]
    public async Task Confirming_order_does_not_hold_stock()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        var result = await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        Assert.Empty(result.Lines);
        using var verify = db.ContextFor(Bu);
        Assert.Empty(verify.StockReservations);
        Assert.Equal(100m, (await new InventoryAvailabilityService(verify).GetAvailabilityAsync(Bu, 1)).Available);
    }

    [Fact]
    public async Task Confirmation_does_not_create_partial_reservations_when_stock_is_short()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 30m, orderQty: 50m);
        var result = await Service(db).ReserveOrderAsync(Bu, 1);
        Assert.Empty(result.Lines);
        using var verify = db.ContextFor(Bu);
        Assert.Empty(verify.StockReservations);
        Assert.Equal(30m, (await new InventoryAvailabilityService(verify).GetAvailabilityAsync(Bu, 1)).Available);
    }

    [Fact]
    public async Task Confirmation_without_inventory_does_not_create_stock_or_holds()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 0m, orderQty: 10m, withInventory: false);
        Assert.Empty((await Service(db).ReserveOrderAsync(Bu, 1)).Lines);
        using var verify = db.ContextFor(Bu);
        Assert.Empty(verify.StockReservations);
        Assert.Empty(verify.Set<Models.Inventory>());
    }

    [Fact]
    public async Task Repeated_confirmation_keeps_all_stock_available()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1);
        Assert.Empty((await Service(db).ReserveOrderAsync(Bu, 1)).Lines);
        Assert.Equal(100m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).Available);
        using var verify = db.ContextFor(Bu);
        Assert.Empty(verify.StockReservations);
    }

    [Fact]
    public async Task Shipment_issues_stock_without_a_reservation()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        var result = await IssueAsync(db, 1, 40m);
        Assert.Equal(40m, Assert.Single(result.Lines).Issued);
        var avail = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(60m, avail.OnHand);
        Assert.Equal(0m, avail.Reserved);
    }

    [Fact]
    public async Task Cancelling_an_order_preserves_historical_holds()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        using var history = db.ContextFor(Bu);
        var hold = await HistoricalReservations.SeedAsync(history, Bu, 1, 40m, "old-order", orderId: 1, orderItemId: 1);
        Assert.Equal(0, await Service(db).ReleaseOrderAsync(Bu, 1));
        await history.Entry(hold).ReloadAsync();
        Assert.Equal(StockReservationStatus.Active, hold.Status);
        Assert.Equal(100m, (await new InventoryAvailabilityService(history).GetAvailabilityAsync(Bu, 1)).Available);
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
        var issue = await IssueAsync(db, 1, 10m);

        var line = Assert.Single(issue.Lines);
        Assert.Equal(10m, line.Issued);
        Assert.Equal(0m, line.StillReserved);
        Assert.False(issue.HasUnshippedBalance);

        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1);
        Assert.Equal(90m, availability.OnHand);     // exactly the 10 that shipped
        Assert.Equal(0m, availability.Reserved);   // no hold is created for the unshipped balance
        Assert.Equal(90m, availability.Available);  // only actual shipment reduces availability

        await using var verify = db.ContextFor(Bu);
        var movement = Assert.Single(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(InventoryMovementType.Issue, movement.Type);
        Assert.Equal(10m, movement.Quantity);       // the ledger records the physical truth
    }

    [Fact]
    public async Task Unshipped_stock_remains_available_before_and_after_cancellation()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await IssueAsync(db, 1, 10m);
        Assert.Equal(90m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).Available);
        Assert.Equal(0, await Service(db).ReleaseOrderAsync(Bu, 1));
        Assert.Equal(90m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).Available);
    }

    [Fact]
    public async Task Issuing_the_remainder_of_a_partly_shipped_line_consumes_exactly_the_balance()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        await IssueAsync(db, 1, 10m);

        // A second allocation pass runs first in the shipment path; the 10 already issued must not
        // be re-reserved, or the line would end up holding more than was ever ordered.
        await Service(db).ReserveOrderAsync(Bu, 1, "rep@acme");
        var issue = await IssueAsync(db, 2, 30m);

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
    public async Task A_declared_quantity_above_order_balance_is_refused_without_issuing_stock()
    {
        using var db = new TestDb();
        SeedOrder(db, onHand: 100m, orderQty: 40m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(db, 1, 500m));
        Assert.Equal(100m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).OnHand);
        using var verify = db.ContextFor(Bu);
        Assert.Empty(verify.InventoryMovements);
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
            IssueAsync(db, 1, 1m, orderItemId: 999));

        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
        Assert.Equal(100m, (await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, 1)).OnHand);
    }
}

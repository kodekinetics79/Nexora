using ERP_RFQ_Automation.Inventory;
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
        ctx.SaveChanges();

        ctx.Set<Product>().Add(new Product { Id = 1, PartNo = partNo, ProductName = "Widget", Buid = Bu, CreatedBy = "test", CreatedOn = DateTime.UtcNow });
        if (withInventory)
        {
            ctx.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
            {
                Id = 1, PartNo = partNo, ProductName = "Widget", QtyOnHand = onHand, ReorderPoint = 0m,
                Buid = Bu, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
        }
        ctx.Set<Order>().Add(new Order
        {
            Id = 1, OrderNo = "SO-1", CustomerId = customer.Id, BusinessUnitId = Bu, StatusId = status.SetupId,
            TotalAmount = 0m, CreatedBy = "test", CreatedOn = DateTime.UtcNow, OrderDate = DateTime.UtcNow, IsActive = true
        });
        ctx.Set<OrderItem>().Add(new OrderItem
        {
            Id = 1, OrderId = 1, ProductId = 1, Quantity = orderQty, UnitPrice = 10m, Discount = 0m,
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
}

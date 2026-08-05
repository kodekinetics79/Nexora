using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the order-to-stock allocation contract: it is tenant-isolated (order lines carry no
/// BusinessUnitId of their own), it spills across warehouses so an order does not report a false
/// shortage when the quote engine already promised the stock, it ranks warehouses by what is
/// actually available rather than by raw on-hand, it will not steal another product's stock on a
/// part-number collision, and abandoned holds can be recovered.
/// </summary>
public class OrderStockAllocationIntegrityTests
{
    private const long Bu = 1;
    private const long OtherBu = 2;

    private static void SeedTenant(TestDb db, long businessUnitId)
    {
        using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        Seed.Customer(ctx, id: businessUnitId, buid: businessUnitId, name: $"Customer {businessUnitId}");
        Seed.LeadStatus(ctx, setupId: 900 + (int)businessUnitId, businessUnitId: businessUnitId, value: "Confirmed");
        ctx.SaveChanges();
    }

    private static long SeedProduct(TestDb db, long businessUnitId, long productId, string partNo)
    {
        using var ctx = db.ContextFor(null);
        ctx.Products.Add(new Product
        {
            Id = productId, Buid = businessUnitId, PartNo = partNo, ProductName = partNo,
            ReorderPoint = 0m, CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true
        });
        ctx.SaveChanges();
        return productId;
    }

    private static long SeedWarehouse(TestDb db, long businessUnitId, long warehouseId, string code)
    {
        using var ctx = db.ContextFor(null);
        ctx.Warehouses.Add(new Warehouse
        {
            Id = warehouseId, BusinessUnitId = businessUnitId, WarehouseCode = code, WarehouseName = code,
            IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        ctx.SaveChanges();
        return warehouseId;
    }

    private static long SeedStock(TestDb db, long businessUnitId, long inventoryId, long productId,
        long warehouseId, decimal onHand)
    {
        using var ctx = db.ContextFor(null);
        ctx.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
        {
            Id = inventoryId, Buid = businessUnitId, ProductId = productId, WarehouseId = warehouseId,
            PartNo = $"INV-{inventoryId}", QtyOnHand = onHand, ReorderPoint = 0m,
            CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        ctx.SaveChanges();
        return inventoryId;
    }

    private static void SeedOrder(TestDb db, long businessUnitId, long orderId, long productId,
        decimal quantity, long? warehouseId = null)
    {
        using var ctx = db.ContextFor(null);
        ctx.Set<Order>().Add(new Order
        {
            Id = orderId, OrderNo = $"SO-{orderId}", CustomerId = businessUnitId, BusinessUnitId = businessUnitId,
            StatusId = 900 + (int)businessUnitId, TotalAmount = 0m, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            OrderDate = DateTime.UtcNow, IsActive = true
        });
        ctx.Set<OrderItem>().Add(new OrderItem
        {
            Id = orderId, OrderId = orderId, ProductId = productId, WarehouseId = warehouseId, Quantity = quantity,
            UnitPrice = 10m, Discount = 0m, TaxAmount = 0m, TotalAmount = quantity * 10m,
            CreatedBy = "test", CreatedDate = DateTime.UtcNow, IsActive = true
        });
        ctx.SaveChanges();
    }

    private static OrderStockReservationService Service(TestDb db, long tenant = Bu)
    {
        var ctx = db.ContextFor(tenant);
        return new OrderStockReservationService(ctx, new InventoryAvailabilityService(ctx));
    }

    [Fact]
    public async Task Allocation_spills_across_warehouses_instead_of_reporting_a_false_shortage()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var productId = SeedProduct(db, Bu, 10, "SPILL-1");
        var warehouseA = SeedWarehouse(db, Bu, 20, "WH-A");
        var warehouseB = SeedWarehouse(db, Bu, 21, "WH-B");
        SeedStock(db, Bu, 30, productId, warehouseA, 60m);
        SeedStock(db, Bu, 31, productId, warehouseB, 50m);
        SeedOrder(db, Bu, orderId: 40, productId: productId, quantity: 100m);

        var result = await Service(db).ReserveOrderAsync(Bu, 40, "rep@acme");

        // Previously this reserved only the 60 in the single "best" warehouse and reported a
        // 40-unit shortage, while the quote engine had already promised all 110.
        var line = Assert.Single(result.Lines);
        Assert.Equal(100m, line.Reserved);
        Assert.Equal(0m, line.Shortage);
        Assert.Equal("Reserved", line.Outcome);
        Assert.True(result.FullyAllocated);

        using var verify = db.ContextFor(Bu);
        var holds = verify.StockReservations.Where(x => x.OrderId == 40).ToList();
        Assert.Equal(2, holds.Count);
        Assert.Equal(100m, holds.Sum(x => x.Quantity));
    }

    [Fact]
    public async Task Allocation_ranks_warehouses_by_available_not_by_raw_on_hand()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var productId = SeedProduct(db, Bu, 11, "RANK-1");
        var big = SeedWarehouse(db, Bu, 22, "WH-BIG");
        var small = SeedWarehouse(db, Bu, 23, "WH-SMALL");
        var bigStock = SeedStock(db, Bu, 32, productId, big, 1000m);
        SeedStock(db, Bu, 33, productId, small, 50m);

        // Everything in the big warehouse is already committed to another order.
        await new InventoryAvailabilityService(db.ContextFor(Bu))
            .ReserveAsync(Bu, bigStock, 1000m, "other-order", orderId: 999);

        SeedOrder(db, Bu, orderId: 41, productId: productId, quantity: 50m);
        var result = await Service(db).ReserveOrderAsync(Bu, 41, "rep@acme");

        var line = Assert.Single(result.Lines);
        Assert.Equal(50m, line.Reserved);
        Assert.Equal("Reserved", line.Outcome);
    }

    [Fact]
    public async Task Allocation_refuses_an_order_belonging_to_another_tenant()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        SeedTenant(db, OtherBu);
        var victimProduct = SeedProduct(db, OtherBu, 12, "VICTIM-1");
        var victimWarehouse = SeedWarehouse(db, OtherBu, 24, "WH-VICTIM");
        SeedStock(db, OtherBu, 34, victimProduct, victimWarehouse, 500m);
        SeedOrder(db, OtherBu, orderId: 42, productId: victimProduct, quantity: 100m);

        // Tenant 1 asks to allocate tenant 2's order. OrderItem has no BusinessUnitId, so the old
        // "WHERE OrderId = @id" read the victim's lines outright.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(db, Bu).ReserveOrderAsync(Bu, 42, "attacker@evil"));

        using var verify = db.ContextFor(null);
        Assert.Empty(verify.StockReservations);
    }

    [Fact]
    public async Task Allocation_does_not_take_another_products_stock_on_a_part_number_collision()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var ordered = SeedProduct(db, Bu, 13, "COLLIDE");
        var lookalike = SeedProduct(db, Bu, 14, "COLLIDE-OTHER");
        var warehouse = SeedWarehouse(db, Bu, 25, "WH-C");

        // The other product's inventory row carries the SAME PartNo string. The old candidate
        // predicate was "ProductId matches OR PartNo matches", so this row was fair game.
        using (var ctx = db.ContextFor(null))
        {
            ctx.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
            {
                Id = 35, Buid = Bu, ProductId = lookalike, WarehouseId = warehouse, PartNo = "COLLIDE",
                QtyOnHand = 500m, ReorderPoint = 0m, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
            ctx.SaveChanges();
        }
        SeedOrder(db, Bu, orderId: 43, productId: ordered, quantity: 100m);

        var result = await Service(db).ReserveOrderAsync(Bu, 43, "rep@acme");

        var line = Assert.Single(result.Lines);
        Assert.Equal("NoInventoryMatch", line.Outcome);
        Assert.Equal(0m, line.Reserved);
        using var verify = db.ContextFor(Bu);
        Assert.Equal(500m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Single(x => x.Id == 35).QtyOnHand);
        Assert.Empty(verify.StockReservations);
    }

    [Fact]
    public async Task Reallocating_after_a_partial_restock_tops_the_line_up_without_double_holding()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var productId = SeedProduct(db, Bu, 15, "TOPUP-1");
        var warehouse = SeedWarehouse(db, Bu, 26, "WH-D");
        var inventoryId = SeedStock(db, Bu, 36, productId, warehouse, 30m);
        SeedOrder(db, Bu, orderId: 44, productId: productId, quantity: 50m);

        var first = await Service(db).ReserveOrderAsync(Bu, 44, "rep@acme");
        Assert.Equal(30m, first.Lines.Single().Reserved);
        Assert.Equal(20m, first.Lines.Single().Shortage);

        // Stock arrives, then the order is re-allocated.
        await new StockLedgerService(db.ContextFor(Bu))
            .AdjustAsync(Bu, productId, warehouse, 20m, "restock:1", "ops@acme");
        var second = await Service(db).ReserveOrderAsync(Bu, 44, "rep@acme");

        Assert.Equal(50m, second.Lines.Single().Reserved);
        Assert.Equal(0m, second.Lines.Single().Shortage);
        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, inventoryId);
        Assert.Equal(50m, availability.Reserved); // not 80 — the first hold was not duplicated
        Assert.Equal(0m, availability.Available);
    }

    [Fact]
    public async Task Deleting_an_order_leaks_stock_until_the_orphan_sweep_recovers_it()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var productId = SeedProduct(db, Bu, 16, "ORPHAN-1");
        var warehouse = SeedWarehouse(db, Bu, 27, "WH-E");
        var inventoryId = SeedStock(db, Bu, 37, productId, warehouse, 100m);
        SeedOrder(db, Bu, orderId: 45, productId: productId, quantity: 40m);
        await Service(db).ReserveOrderAsync(Bu, 45, "rep@acme");

        // StockReservation has no FK to Orders, so the row survives the delete with a dangling
        // OrderId and ReleaseForOrderAsync can never be triggered for it again.
        using (var ctx = db.ContextFor(null))
        {
            ctx.Set<OrderItem>().RemoveRange(ctx.Set<OrderItem>().Where(x => x.OrderId == 45));
            ctx.Set<Order>().Remove(ctx.Set<Order>().Single(x => x.Id == 45));
            ctx.SaveChanges();
        }

        var leaked = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, inventoryId);
        Assert.Equal(60m, leaked.Available); // 40 units stranded

        var recovered = await Service(db).ReleaseOrphanedAsync(Bu, "ops@acme");

        Assert.Equal(1, recovered);
        var restored = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, inventoryId);
        Assert.Equal(100m, restored.Available);
    }

    [Fact]
    public async Task Stale_holds_are_expired_auditably_and_recent_holds_are_untouched()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var productId = SeedProduct(db, Bu, 17, "STALE-1");
        var warehouse = SeedWarehouse(db, Bu, 28, "WH-F");
        var inventoryId = SeedStock(db, Bu, 38, productId, warehouse, 100m);

        var availability = new InventoryAvailabilityService(db.ContextFor(Bu));
        var abandoned = await availability.ReserveAsync(Bu, inventoryId, 40m, "abandoned", orderId: 46);
        await new InventoryAvailabilityService(db.ContextFor(Bu))
            .ReserveAsync(Bu, inventoryId, 10m, "fresh", orderId: 47);

        using (var age = db.ContextFor(Bu))
        {
            var row = age.StockReservations.Single(x => x.Id == abandoned.Id);
            row.CreatedOn = DateTime.UtcNow.AddDays(-30);
            age.SaveChanges();
        }

        var expired = await new InventoryAvailabilityService(db.ContextFor(Bu))
            .ExpireStaleAsync(Bu, DateTime.UtcNow.AddHours(-72), "sweeper@acme");

        Assert.Equal(1, expired);
        var after = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, inventoryId);
        Assert.Equal(10m, after.Reserved);  // only the fresh hold survives
        Assert.Equal(90m, after.Available);

        using var verify = db.ContextFor(Bu);
        Assert.Single(verify.ProcurementEvents.Where(x =>
            x.AggregateType == "StockReservation" && x.EventType == "STOCK_RESERVATION_EXPIRED"));
    }

    [Fact]
    public async Task A_line_shortage_does_not_leave_earlier_lines_holding_stock()
    {
        using var db = new TestDb();
        SeedTenant(db, Bu);
        var first = SeedProduct(db, Bu, 18, "MULTI-1");
        var second = SeedProduct(db, Bu, 19, "MULTI-2");
        var warehouse = SeedWarehouse(db, Bu, 29, "WH-G");
        SeedStock(db, Bu, 39, first, warehouse, 100m);
        SeedStock(db, Bu, 50, second, warehouse, 5m);

        using (var ctx = db.ContextFor(null))
        {
            ctx.Set<Order>().Add(new Order
            {
                Id = 48, OrderNo = "SO-48", CustomerId = Bu, BusinessUnitId = Bu, StatusId = 901, TotalAmount = 0m,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow, OrderDate = DateTime.UtcNow, IsActive = true
            });
            ctx.Set<OrderItem>().AddRange(
                new OrderItem { Id = 480, OrderId = 48, ProductId = first, Quantity = 10m, UnitPrice = 1m, Discount = 0m, TaxAmount = 0m, TotalAmount = 10m, CreatedBy = "t", CreatedDate = DateTime.UtcNow, IsActive = true },
                new OrderItem { Id = 481, OrderId = 48, ProductId = second, Quantity = 50m, UnitPrice = 1m, Discount = 0m, TaxAmount = 0m, TotalAmount = 50m, CreatedBy = "t", CreatedDate = DateTime.UtcNow, IsActive = true });
            ctx.SaveChanges();
        }

        var result = await Service(db).ReserveOrderAsync(Bu, 48, "rep@acme");

        // A short line is reported, not thrown: procurement needs the shortage, and the fully
        // allocated line keeps its hold because the whole pass committed as one transaction.
        Assert.Equal(2, result.Lines.Count);
        Assert.True(result.HasShortages);
        Assert.Equal(10m, result.Lines.Single(x => x.OrderItemId == 480).Reserved);
        Assert.Equal(5m, result.Lines.Single(x => x.OrderItemId == 481).Reserved);
        Assert.Equal(45m, result.TotalShortage);
    }
}

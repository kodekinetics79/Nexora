using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the stock availability + reservation engine: available-to-promise is on-hand minus
/// active holds, two orders cannot promise the same stock, reservation is idempotent on retry,
/// release restores availability, consume decrements physical stock exactly once, and reservations
/// are tenant-isolated.
/// </summary>
public class InventoryReservationTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;

    private static long SeedInventory(ErpRfqAutomationContext ctx, long id, long buid, decimal onHand)
    {
        Seed.EnsureBusinessUnit(ctx, buid);
        var productId = 100_000 + id;
        var warehouseId = 200_000 + id;
        ctx.Products.Add(new Product
        {
            Id = productId, Buid = buid, PartNo = $"PRODUCT-{id}", ProductName = $"Item {id}",
            CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true
        });
        ctx.Warehouses.Add(new Warehouse
        {
            Id = warehouseId, BusinessUnitId = buid, WarehouseCode = $"WH-{id}",
            WarehouseName = $"Warehouse {id}", CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true
        });
        ctx.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
        {
            Id = id,
            PartNo = $"PN-{id}",
            ProductName = $"Item {id}",
            QtyOnHand = onHand,
            ReorderPoint = 0m,
            Buid = buid,
            ProductId = productId,
            WarehouseId = warehouseId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        return id;
    }

    private static InventoryAvailabilityService Service(TestDb db, long? tenant)
        => new(db.ContextFor(tenant));

    [Fact]
    public async Task Availability_is_onhand_minus_active_reservations()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 10, Bu1, 100m);

        var svc = Service(db, Bu1);
        await svc.ReserveAsync(Bu1, inventoryId: 10, quantity: 30m, idempotencyKey: "k1", orderId: 500);

        var a = await Service(db, Bu1).GetAvailabilityAsync(Bu1, 10);
        Assert.Equal(100m, a.OnHand);
        Assert.Equal(30m, a.Reserved);
        Assert.Equal(70m, a.Available);
    }

    [Fact]
    public async Task Two_orders_cannot_promise_the_same_stock()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 11, Bu1, 100m);

        await Service(db, Bu1).ReserveAsync(Bu1, 11, 80m, "order-A", orderId: 1);

        // Second order wants 40 but only 20 remain — must be rejected, not silently over-committed.
        var ex = await Assert.ThrowsAsync<InsufficientStockException>(
            () => Service(db, Bu1).ReserveAsync(Bu1, 11, 40m, "order-B", orderId: 2));
        Assert.Equal(20m, ex.Available);

        Assert.Equal(20m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 11)).Available);
    }

    [Fact]
    public async Task Reserve_is_idempotent_on_key()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 12, Bu1, 100m);

        var first = await Service(db, Bu1).ReserveAsync(Bu1, 12, 25m, "dup-key", orderId: 7);
        var second = await Service(db, Bu1).ReserveAsync(Bu1, 12, 25m, "dup-key", orderId: 7);

        Assert.Equal(first.Id, second.Id);
        // Only ONE hold exists — a retried confirmation did not double-reserve.
        Assert.Equal(75m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 12)).Available);
    }

    [Fact]
    public async Task Reserve_rejects_same_key_for_a_different_request()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 120, Bu1, 100m);

        await Service(db, Bu1).ReserveAsync(Bu1, 120, 25m, "strict-key", orderId: 7, orderItemId: 70);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db, Bu1).ReserveAsync(Bu1, 120, 26m, "strict-key", orderId: 7, orderItemId: 70));
        Assert.Contains("different request", exception.Message);
        Assert.Equal(75m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 120)).Available);
    }

    [Fact]
    public async Task Release_for_order_restores_availability()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 13, Bu1, 100m);

        await Service(db, Bu1).ReserveAsync(Bu1, 13, 60m, "rk", orderId: 99);
        Assert.Equal(40m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 13)).Available);

        var released = await Service(db, Bu1).ReleaseForOrderAsync(Bu1, orderId: 99);
        Assert.Equal(1, released);
        Assert.Equal(100m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 13)).Available);
        using var verify = db.ContextFor(Bu1);
        var lifecycleEvent = Assert.Single(verify.ProcurementEvents.Where(x =>
            x.AggregateType == "StockReservation" && x.EventType == "STOCK_RESERVATION_RELEASED"));
        Assert.Equal(2, lifecycleEvent.AggregateVersion);
    }

    [Fact]
    public async Task Consume_decrements_onhand_once_and_is_idempotent()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 14, Bu1, 100m);

        var r = await Service(db, Bu1).ReserveAsync(Bu1, 14, 40m, "ck", orderId: 3);
        await Service(db, Bu1).ConsumeAsync(Bu1, r.Id);

        var a = await Service(db, Bu1).GetAvailabilityAsync(Bu1, 14);
        Assert.Equal(60m, a.OnHand);   // physical stock left the building
        Assert.Equal(0m, a.Reserved);  // hold is consumed, no longer active
        Assert.Equal(60m, a.Available);

        // Replaying consume must not decrement a second time.
        await Service(db, Bu1).ConsumeAsync(Bu1, r.Id);
        Assert.Equal(60m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 14)).OnHand);
        using var verify = db.ContextFor(Bu1);
        var movement = Assert.Single(verify.InventoryMovements);
        Assert.Equal(ERP_RFQ_Automation.Inventory.Commercial.InventoryMovementType.Issue, movement.Type);
        Assert.Equal(40m, movement.Quantity);
        var lifecycleEvent = Assert.Single(verify.ProcurementEvents.Where(x =>
            x.AggregateType == "StockReservation" && x.EventType == "STOCK_RESERVATION_CONSUMED"));
        Assert.Equal(2, lifecycleEvent.AggregateVersion);
    }

    [Fact]
    public async Task Reservations_are_tenant_isolated()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedInventory(seed, 15, Bu1, 100m);
            SeedInventory(seed, 16, Bu2, 100m);
        }

        var r1 = await Service(db, Bu1).ReserveAsync(Bu1, 15, 10m, "t1", orderId: 1);

        // Tenant 2 cannot see or consume tenant 1's reservation.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(db, Bu2).ConsumeAsync(Bu2, r1.Id));

        using var ctx2 = db.ContextFor(Bu2);
        Assert.Empty(ctx2.StockReservations.ToList());
    }
}

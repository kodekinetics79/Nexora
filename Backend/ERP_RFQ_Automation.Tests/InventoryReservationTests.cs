using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies this release: historical reservations remain tenant-isolated and unchanged,
/// reservation writes are refused, and availability still excludes quality and safety buckets.
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
    public async Task Availability_ignores_historical_active_reservations()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 10, Bu1, 100m);
        using var history = db.ContextFor(Bu1);
        await HistoricalReservations.SeedAsync(history, Bu1, 10, 30m, "history", orderId: 500);
        var a = await Service(db, Bu1).GetAvailabilityAsync(Bu1, 10);
        Assert.Equal(100m, a.OnHand);
        Assert.Equal(0m, a.Reserved);
        Assert.Equal(100m, a.Available);
        Assert.Equal(30m, (await history.StockReservations.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task Availability_deducts_every_protected_inventory_bucket()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedInventory(seed, 17, Bu1, 100m);
            var inventory = seed.Set<ERP_RFQ_Automation.Models.Inventory>().Single(x => x.Id == 17);
            inventory.AllocatedQuantity = 5m;
            inventory.QuarantineQuantity = 7m;
            inventory.DamagedQuantity = 3m;
            inventory.ExpiredQuantity = 4m;
            inventory.SafetyStockQuantity = 11m;
            seed.SaveChanges();
        }

        using var history = db.ContextFor(Bu1);
        await HistoricalReservations.SeedAsync(history, Bu1, 17, 10m, "protected-buckets", orderId: 17);
        var availability = await Service(db, Bu1).GetAvailabilityAsync(Bu1, 17);

        Assert.Equal(70m, availability.Available);
        Assert.Equal(5m, availability.Allocated);
        Assert.Equal(7m, availability.Quarantine);
        Assert.Equal(3m, availability.Damaged);
        Assert.Equal(4m, availability.Expired);
        Assert.Equal(11m, availability.SafetyStock);
    }

    [Fact]
    public async Task Manual_release_rejects_current_stale_and_foreign_requests_without_changing_history()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 18, Bu1, 100m);
        using var history = db.ContextFor(Bu1);
        var row = await HistoricalReservations.SeedAsync(history, Bu1, 18, 25m, "release-source", orderId: 18);
        foreach (var (tenant, version) in new[] { (Bu1, row.Version), (Bu1, 999u), (Bu2, row.Version) })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db, tenant).ReleaseAsync(tenant, row.Id, version, "manual-release"));
        await history.Entry(row).ReloadAsync();
        Assert.Equal(StockReservationStatus.Active, row.Status);
        Assert.Equal(1u, row.Version);
        Assert.Null(row.ReleasedOn);
        Assert.Empty(history.ProcurementEvents);
    }

    [Fact]
    public async Task Neither_order_can_create_a_reservation_in_this_release()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 11, Bu1, 100m);
        foreach (var order in new[] { 1L, 2L })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db, Bu1).ReserveAsync(Bu1, 11, 80m, $"order-{order}", orderId: order));
            Assert.Equal(InventoryReleaseScope.ReservationsDisabledMessage, error.Message);
        }
        using var verify = db.ContextFor(Bu1);
        Assert.Empty(verify.StockReservations);
        Assert.Equal(100m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 11)).Available);
    }

    [Fact]
    public async Task Repeated_create_requests_remain_rejected_without_writing_a_hold()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 12, Bu1, 100m);
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db, Bu1).ReserveAsync(Bu1, 12, 25m, "dup-key", orderId: 7));
        using var verify = db.ContextFor(Bu1);
        Assert.Empty(verify.StockReservations);
        Assert.Equal(100m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 12)).Available);
    }

    [Fact]
    public async Task Historical_idempotency_key_cannot_reactivate_or_change_a_reservation()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 120, Bu1, 100m);
        using var history = db.ContextFor(Bu1);
        var row = await HistoricalReservations.SeedAsync(history, Bu1, 120, 25m, "strict-key", orderId: 7, orderItemId: 70);
        foreach (var quantity in new[] { 25m, 26m })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db, Bu1).ReserveAsync(Bu1, 120, quantity, "strict-key", orderId: 7, orderItemId: 70));
        await history.Entry(row).ReloadAsync();
        Assert.Equal(25m, row.Quantity);
        Assert.Equal(1u, row.Version);
        Assert.Single(history.StockReservations);
    }

    [Fact]
    public async Task Cancellation_preserves_history_and_does_not_change_availability()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 13, Bu1, 100m);
        using var history = db.ContextFor(Bu1);
        var row = await HistoricalReservations.SeedAsync(history, Bu1, 13, 60m, "rk", orderId: 99);
        Assert.Equal(100m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 13)).Available);
        Assert.Equal(0, await Service(db, Bu1).ReleaseForOrderAsync(Bu1, 99));
        await history.Entry(row).ReloadAsync();
        Assert.Equal(StockReservationStatus.Active, row.Status);
        Assert.Empty(history.ProcurementEvents);
        Assert.Equal(100m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 13)).Available);
    }

    [Fact]
    public async Task Legacy_consume_and_split_cannot_modify_history_or_physical_stock()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) SeedInventory(seed, 14, Bu1, 100m);
        using var history = db.ContextFor(Bu1);
        var row = await HistoricalReservations.SeedAsync(history, Bu1, 14, 40m, "ck", orderId: 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, Bu1).ConsumeAsync(Bu1, row.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, Bu1).SplitAsync(Bu1, row.Id, 10m));
        await history.Entry(row).ReloadAsync();
        Assert.Equal(StockReservationStatus.Active, row.Status);
        Assert.Equal(40m, row.Quantity);
        Assert.Null(row.ConsumedOn);
        Assert.Empty(history.InventoryMovements);
        Assert.Empty(history.ProcurementEvents);
        Assert.Equal(100m, (await Service(db, Bu1).GetAvailabilityAsync(Bu1, 14)).OnHand);
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

        using var history = db.ContextFor(Bu1);
        var r1 = await HistoricalReservations.SeedAsync(history, Bu1, 15, 10m, "t1", orderId: 1);

        // Tenant 2 cannot see or consume tenant 1's reservation.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(db, Bu2).ConsumeAsync(Bu2, r1.Id));

        using var ctx2 = db.ContextFor(Bu2);
        Assert.Empty(ctx2.StockReservations.ToList());
    }
}

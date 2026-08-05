using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the stock ledger invariants an ERP is judged on: opening stock can reach the
/// authoritative per-warehouse ledger without raising a supplier purchase order, on-hand can never
/// drift from the sum of its movements, physical stock can never go negative or below what is
/// already committed, non-sellable buckets are writable and reduce availability without moving
/// physical units, and warehouse transfers conserve total stock.
/// </summary>
public class StockLedgerIntegrityTests
{
    private const long Bu = 1;
    private const long OtherBu = 2;

    private static (long ProductId, long WarehouseA, long WarehouseB) SeedCatalog(
        TestDb db, long businessUnitId = Bu, long seed = 1)
    {
        using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        var productId = 1_000 + seed;
        var warehouseA = 2_000 + (seed * 10);
        var warehouseB = 2_001 + (seed * 10);
        ctx.Products.Add(new Product
        {
            Id = productId, Buid = businessUnitId, PartNo = $"PN-{seed}", ProductName = $"Widget {seed}",
            ReorderPoint = 0m, CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true
        });
        ctx.Warehouses.Add(new Warehouse
        {
            Id = warehouseA, BusinessUnitId = businessUnitId, WarehouseCode = $"WH-A{seed}",
            WarehouseName = $"Warehouse A{seed}", IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        ctx.Warehouses.Add(new Warehouse
        {
            Id = warehouseB, BusinessUnitId = businessUnitId, WarehouseCode = $"WH-B{seed}",
            WarehouseName = $"Warehouse B{seed}", IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        ctx.SaveChanges();
        return (productId, warehouseA, warehouseB);
    }

    private static StockLedgerService Ledger(TestDb db, long tenant = Bu) => new(db.ContextFor(tenant));

    /// <summary>
    /// The invariant that makes every other stock number believable: the persisted balance equals
    /// the signed sum of the movement ledger, for every row, after any sequence of operations.
    /// </summary>
    private static async Task AssertLedgerBalancedAsync(TestDb db, long tenant = Bu)
    {
        var drift = await new InventoryAvailabilityService(db.ContextFor(tenant))
            .ReconcileLedgerAsync(tenant, driftOnly: true);
        Assert.Empty(drift);
    }

    [Fact]
    public async Task Opening_stock_reaches_the_ledger_without_a_purchase_order()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);

        // No supplier PO, no goods receipt — the route a client migrating existing stock needs.
        var result = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 250m, "opening:1", "ops@acme");

        Assert.Equal(250m, result.OnHand);
        Assert.NotNull(result.MovementId);

        using var verify = db.ContextFor(Bu);
        var inventory = Assert.Single(verify.Set<ERP_RFQ_Automation.Models.Inventory>());
        Assert.Equal(250m, inventory.QtyOnHand);
        Assert.Equal(warehouseA, inventory.WarehouseId);
        var movement = Assert.Single(verify.InventoryMovements);
        Assert.Equal(InventoryMovementType.AdjustmentIncrease, movement.Type);
        Assert.Equal(250m, movement.Quantity);
        await AssertLedgerBalancedAsync(db);
    }

    [Fact]
    public async Task Counted_stock_is_immediately_available_to_promise()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        var result = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 80m, "opening:2", "ops@acme");

        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu))
            .GetAvailabilityAsync(Bu, result.InventoryId);

        Assert.Equal(80m, availability.OnHand);
        Assert.Equal(80m, availability.Available);
    }

    [Fact]
    public async Task Ledger_writes_are_idempotent_on_the_key()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);

        await Ledger(db).AdjustAsync(Bu, productId, warehouseA, 40m, "adjust:same", "ops@acme");
        var replay = await Ledger(db).AdjustAsync(Bu, productId, warehouseA, 40m, "adjust:same", "ops@acme");

        Assert.Equal(40m, replay.OnHand); // not 80 — the replay posted nothing
        using var verify = db.ContextFor(Bu);
        Assert.Single(verify.InventoryMovements);
        await AssertLedgerBalancedAsync(db);
    }

    [Fact]
    public async Task Adjustment_cannot_drive_physical_stock_negative()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 10m, "opening:3", "ops@acme");

        await Assert.ThrowsAsync<StockLedgerException>(() =>
            Ledger(db).AdjustAsync(Bu, productId, warehouseA, -25m, "adjust:negative", "ops@acme"));

        using var verify = db.ContextFor(Bu);
        Assert.Equal(10m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Single().QtyOnHand);
        await AssertLedgerBalancedAsync(db);
    }

    [Fact]
    public async Task Adjustment_cannot_write_off_stock_that_is_already_reserved()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        var stock = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:4", "ops@acme");
        await new InventoryAvailabilityService(db.ContextFor(Bu))
            .ReserveAsync(Bu, stock.InventoryId, 80m, "hold:1", orderId: 5);

        // Only 20 units are uncommitted; writing off 50 would leave the reservation unbacked.
        await Assert.ThrowsAsync<StockLedgerException>(() =>
            Ledger(db).AdjustAsync(Bu, productId, warehouseA, -50m, "adjust:overcommit", "ops@acme"));

        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu))
            .GetAvailabilityAsync(Bu, stock.InventoryId);
        Assert.Equal(100m, availability.OnHand);
        Assert.Equal(20m, availability.Available);
    }

    [Fact]
    public async Task Quarantine_reduces_availability_without_moving_physical_stock()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        var stock = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:5", "ops@acme");

        await Ledger(db).ReclassifyAsync(Bu, productId, warehouseA, StockBucket.Quarantine, 30m,
            "quarantine:1", "qa@acme");

        var held = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, stock.InventoryId);
        Assert.Equal(100m, held.OnHand);     // still physically present
        Assert.Equal(30m, held.Quarantine);
        Assert.Equal(70m, held.Available);   // but not sellable
        await AssertLedgerBalancedAsync(db); // quarantine is on-hand neutral in the ledger

        await Ledger(db).ReclassifyAsync(Bu, productId, warehouseA, StockBucket.Quarantine, -30m,
            "quarantine:release:1", "qa@acme");
        var released = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, stock.InventoryId);
        Assert.Equal(0m, released.Quarantine);
        Assert.Equal(100m, released.Available);
        await AssertLedgerBalancedAsync(db);

        using var verify = db.ContextFor(Bu);
        var types = verify.InventoryMovements.Select(x => x.Type).ToList();
        Assert.Contains(InventoryMovementType.Quarantine, types);
        Assert.Contains(InventoryMovementType.QuarantineRelease, types);
    }

    [Fact]
    public async Task Cannot_release_more_than_is_held_in_a_bucket()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:6", "ops@acme");
        await Ledger(db).ReclassifyAsync(Bu, productId, warehouseA, StockBucket.Damaged, 5m, "damage:1", "qa@acme");

        await Assert.ThrowsAsync<StockLedgerException>(() =>
            Ledger(db).ReclassifyAsync(Bu, productId, warehouseA, StockBucket.Damaged, -9m, "damage:2", "qa@acme"));
    }

    [Fact]
    public async Task Cannot_quarantine_more_stock_than_is_physically_present()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 10m, "opening:7", "ops@acme");

        await Assert.ThrowsAsync<StockLedgerException>(() =>
            Ledger(db).ReclassifyAsync(Bu, productId, warehouseA, StockBucket.Quarantine, 25m, "q:overflow", "qa@acme"));
    }

    [Fact]
    public async Task Safety_stock_is_writable_and_protects_availability()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        var stock = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:8", "ops@acme");

        await Ledger(db).SetSafetyStockAsync(Bu, productId, warehouseA, 25m, "planner@acme");

        var availability = await new InventoryAvailabilityService(db.ContextFor(Bu)).GetAvailabilityAsync(Bu, stock.InventoryId);
        Assert.Equal(25m, availability.SafetyStock);
        Assert.Equal(75m, availability.Available);
        // Safety stock is a planning policy, not a stock movement.
        await AssertLedgerBalancedAsync(db);
    }

    [Fact]
    public async Task Transfer_moves_stock_between_warehouses_and_conserves_the_total()
    {
        using var db = new TestDb();
        var (productId, warehouseA, warehouseB) = SeedCatalog(db);
        await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:9", "ops@acme");

        var (from, to) = await Ledger(db).TransferAsync(Bu, productId, warehouseA, warehouseB, 40m,
            "transfer:1", "ops@acme");

        Assert.Equal(60m, from.OnHand);
        Assert.Equal(40m, to.OnHand);
        using var verify = db.ContextFor(Bu);
        Assert.Equal(100m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Sum(x => x.QtyOnHand));
        var types = verify.InventoryMovements.Select(x => x.Type).ToList();
        Assert.Contains(InventoryMovementType.TransferOut, types);
        Assert.Contains(InventoryMovementType.TransferIn, types);
        await AssertLedgerBalancedAsync(db);
    }

    [Fact]
    public async Task Transfer_cannot_move_stock_that_is_not_there()
    {
        using var db = new TestDb();
        var (productId, warehouseA, warehouseB) = SeedCatalog(db);
        await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 10m, "opening:10", "ops@acme");

        await Assert.ThrowsAsync<StockLedgerException>(() =>
            Ledger(db).TransferAsync(Bu, productId, warehouseA, warehouseB, 40m, "transfer:2", "ops@acme"));

        using var verify = db.ContextFor(Bu);
        Assert.Equal(10m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Sum(x => x.QtyOnHand));
    }

    [Fact]
    public async Task Transfer_is_idempotent_on_replay()
    {
        using var db = new TestDb();
        var (productId, warehouseA, warehouseB) = SeedCatalog(db);
        await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:11", "ops@acme");

        await Ledger(db).TransferAsync(Bu, productId, warehouseA, warehouseB, 40m, "transfer:3", "ops@acme");
        await Ledger(db).TransferAsync(Bu, productId, warehouseA, warehouseB, 40m, "transfer:3", "ops@acme");

        using var verify = db.ContextFor(Bu);
        Assert.Equal(60m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Single(x => x.WarehouseId == warehouseA).QtyOnHand);
        Assert.Equal(40m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Single(x => x.WarehouseId == warehouseB).QtyOnHand);
        await AssertLedgerBalancedAsync(db);
    }

    [Fact]
    public async Task Ledger_writes_are_tenant_isolated()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db, Bu, seed: 1);
        SeedCatalog(db, OtherBu, seed: 2);

        // Tenant 2 cannot write stock against tenant 1's product/warehouse ids.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Ledger(db, OtherBu).RecordCountAsync(OtherBu, productId, warehouseA, 50m, "cross:1", "attacker@evil"));

        using var verify = db.ContextFor(null);
        Assert.Empty(verify.Set<ERP_RFQ_Automation.Models.Inventory>());
    }

    [Fact]
    public async Task Reconciliation_detects_a_balance_changed_without_a_movement()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        var stock = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:12", "ops@acme");
        await AssertLedgerBalancedAsync(db);

        // Simulate exactly what a legacy path that writes QtyOnHand directly does.
        using (var tamper = db.ContextFor(Bu))
        {
            var tampered = tamper.Set<ERP_RFQ_Automation.Models.Inventory>().Single(x => x.Id == stock.InventoryId);
            tampered.QtyOnHand += 17m;
            tamper.SaveChanges();
        }

        var drift = await new InventoryAvailabilityService(db.ContextFor(Bu)).ReconcileLedgerAsync(Bu);
        var row = Assert.Single(drift);
        Assert.Equal(117m, row.RecordedOnHand);
        Assert.Equal(100m, row.LedgerOnHand);
        Assert.Equal(17m, row.Drift);
        Assert.False(row.IsBalanced);
    }

    [Fact]
    public async Task Goods_issue_keeps_the_ledger_balanced()
    {
        using var db = new TestDb();
        var (productId, warehouseA, _) = SeedCatalog(db);
        var stock = await Ledger(db).RecordCountAsync(Bu, productId, warehouseA, 100m, "opening:13", "ops@acme");

        var availability = new InventoryAvailabilityService(db.ContextFor(Bu));
        var reservation = await availability.ReserveAsync(Bu, stock.InventoryId, 30m, "issue:hold", orderId: 9);
        await new InventoryAvailabilityService(db.ContextFor(Bu)).ConsumeAsync(Bu, reservation.Id);

        using var verify = db.ContextFor(Bu);
        Assert.Equal(70m, verify.Set<ERP_RFQ_Automation.Models.Inventory>().Single().QtyOnHand);
        // Receipt(+100) and Issue(-30) must reconcile to the persisted 70.
        await AssertLedgerBalancedAsync(db);
    }

    [Theory]
    [InlineData(InventoryMovementType.Receipt, 1)]
    [InlineData(InventoryMovementType.TransferIn, 1)]
    [InlineData(InventoryMovementType.AdjustmentIncrease, 1)]
    [InlineData(InventoryMovementType.ReturnReceipt, 1)]
    [InlineData(InventoryMovementType.Issue, -1)]
    [InlineData(InventoryMovementType.TransferOut, -1)]
    [InlineData(InventoryMovementType.AdjustmentDecrease, -1)]
    [InlineData(InventoryMovementType.Quarantine, 0)]
    [InlineData(InventoryMovementType.QuarantineRelease, 0)]
    [InlineData(InventoryMovementType.Damage, 0)]
    [InlineData(InventoryMovementType.Expiration, 0)]
    public void Every_movement_type_has_a_defined_effect_on_physical_stock(InventoryMovementType type, int expected)
        => Assert.Equal(expected, InventoryQuantityMath.OnHandDelta(type));

    [Fact]
    public void Availability_never_reports_a_negative_promise()
        => Assert.Equal(0m, InventoryQuantityMath.AvailableToPromise(
            onHand: 10m, reserved: 5m, allocated: 3m, quarantine: 2m, damaged: 1m, expired: 1m, safetyStock: 4m));
}

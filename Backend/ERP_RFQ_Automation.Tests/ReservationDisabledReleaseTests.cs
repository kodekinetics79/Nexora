using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class ReservationDisabledReleaseTests
{
    [Fact]
    public void Availability_ignores_historical_holds_but_retains_all_other_exclusions()
    {
        Assert.False(InventoryReleaseScope.ReservationsEnabled);
        Assert.Equal(635m, InventoryQuantityMath.AvailableToPromise(1000m, 500m, 31m, 53m, 71m, 97m, 113m));
        Assert.Equal(10m, InventoryQuantityMath.AvailableToPromise(10m, 50m, 0m, 0m, 0m, 0m, 0m));
        Assert.Equal(0m, InventoryQuantityMath.AvailableToPromise(10m, 0m, 0m, 11m, 0m, 0m, 0m));
    }

    [Fact]
    public async Task Confirmation_and_all_reservation_mutations_preserve_historical_records()
    {
        using var scenario = new LotScenario();
        await using var context = scenario.Context();
        var stock = await context.Set<Models.Inventory>().SingleAsync();
        var hold = new StockReservation
        {
            BusinessUnitId = scenario.BusinessUnitId, InventoryId = stock.Id, OrderId = LotScenario.OrderId,
            OrderItemId = LotScenario.OrderItemId, Quantity = 1m, IdempotencyKey = "historical-hold",
            CreatedOn = DateTime.UtcNow.AddDays(-10), CreatedBy = "history", Version = 7,
        };
        context.StockReservations.Add(hold);
        await context.SaveChangesAsync();
        var availability = new InventoryAvailabilityService(context);
        var orders = InventoryServices.OrderStock(context);
        var allocation = await orders.ReserveOrderAsync(scenario.BusinessUnitId, LotScenario.OrderId);
        Assert.Empty(allocation.Lines);
        Assert.Equal(0, await orders.ReleaseOrderAsync(scenario.BusinessUnitId, LotScenario.OrderId));
        Assert.Equal(0, await orders.ReleaseOrphanedAsync(scenario.BusinessUnitId));
        Assert.Equal(0, await availability.ExpireStaleAsync(scenario.BusinessUnitId, DateTime.UtcNow));
        Assert.Empty(await availability.ReleaseForQuarantineAsync(scenario.BusinessUnitId, stock.Id, 1m));
        Assert.Empty(await availability.ReleaseHoldsOnLotAsync(scenario.BusinessUnitId, 999));
        await Assert.ThrowsAsync<InvalidOperationException>(() => availability.ReserveAsync(scenario.BusinessUnitId, stock.Id, 1m, "new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => availability.ReleaseAsync(scenario.BusinessUnitId, hold.Id, 7, "release"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => availability.SplitAsync(scenario.BusinessUnitId, hold.Id, 0.5m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => availability.ConsumeAsync(scenario.BusinessUnitId, hold.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.ConsumeOrderAsync(scenario.BusinessUnitId, LotScenario.OrderId));
        var snapshot = await availability.GetAvailabilityAsync(scenario.BusinessUnitId, stock.Id);
        Assert.Equal(0m, snapshot.Reserved);
        Assert.Equal(stock.QtyOnHand, snapshot.Available);
        await context.Entry(hold).ReloadAsync();
        Assert.Equal(StockReservationStatus.Active, hold.Status);
        Assert.Equal(7u, hold.Version);
        Assert.Null(hold.ReleasedOn);
        Assert.Null(hold.ConsumedOn);
        Assert.Single(await context.StockReservations.ToListAsync());
    }

    [Fact]
    public async Task Shipment_deducts_only_actual_quantity_declares_lot_and_retries_without_another_deduction()
    {
        using var scenario = new LotScenario();
        var lotId = await scenario.ReceiveLotAsync("RELEASE-LOT", 5m);
        var shipmentId = await scenario.RecordShipmentAsync(3m);
        decimal before;
        await using (var context = scenario.Context()) before = (await context.Set<Models.Inventory>().SingleAsync()).QtyOnHand;
        var issued = await scenario.IssueAsync(3m, shipmentId);
        Assert.Equal(3m, issued.Lines.Single().Issued);
        Assert.Equal(3m, issued.Lines.Single().IssuedFromLots);
        Assert.Equal(0m, issued.Lines.Single().StillReserved);
        await scenario.IssueAsync(3m, shipmentId);
        await using var verify = scenario.Context();
        Assert.Equal(before - 3m, (await verify.Set<Models.Inventory>().SingleAsync()).QtyOnHand);
        Assert.Empty(await verify.StockReservations.ToListAsync());
        Assert.Equal(3m, (await verify.Set<MaterialLot>().SingleAsync(x => x.Id == lotId)).QuantityConsumed);
        Assert.Single(await verify.InventoryMovements.Where(x => x.Type == InventoryMovementType.Issue).ToListAsync());
        Assert.Single(await verify.Set<MaterialLotConsumption>().ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.IssueAsync(2m, shipmentId));
    }

    [Fact]
    public async Task Quality_and_safety_buckets_remain_unavailable_and_short_shipment_rolls_back()
    {
        using var scenario = new LotScenario();
        await using (var context = scenario.Context())
        {
            var stock = await context.Set<Models.Inventory>().SingleAsync();
            stock.QtyOnHand = 10m;
            stock.AllocatedQuantity = 1m;
            stock.QuarantineQuantity = 1m;
            stock.DamagedQuantity = 1m;
            stock.ExpiredQuantity = 1m;
            stock.SafetyStockQuantity = 1m;
            await context.SaveChangesAsync();
        }
        var shortShipment = await scenario.RecordShipmentAsync(6m);
        await Assert.ThrowsAsync<IncompleteGoodsIssueException>(() => scenario.IssueAsync(6m, shortShipment));
        await using (var verify = scenario.Context())
        {
            Assert.Equal(10m, (await verify.Set<Models.Inventory>().SingleAsync()).QtyOnHand);
            Assert.Empty(await verify.InventoryMovements.Where(x => x.Type == InventoryMovementType.Issue).ToListAsync());
        }
        var shipment = await scenario.RecordShipmentAsync(5m);
        await scenario.IssueAsync(5m, shipment);
        await using var final = scenario.Context();
        Assert.Equal(5m, (await final.Set<Models.Inventory>().SingleAsync()).QtyOnHand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quarantined_or_expired_lots_cannot_escape_as_anonymous_stock(bool quarantine)
    {
        using var scenario = new LotScenario();
        var lot = await scenario.ReceiveLotAsync("UNSELLABLE", 5m,
            expiry: quarantine ? null : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        if (quarantine) await scenario.ForceQuarantineStatusAsync(lot);
        await using (var context = scenario.Context())
        {
            var stock = await context.Set<Models.Inventory>().SingleAsync();
            stock.QtyOnHand = 5m; // no un-lotted opening stock can satisfy this request
            await context.SaveChangesAsync();
        }
        var shipment = await scenario.RecordShipmentAsync(1m);
        await Assert.ThrowsAsync<IncompleteGoodsIssueException>(() => scenario.IssueAsync(1m, shipment));
        await using var verify = scenario.Context();
        Assert.Equal(5m, (await verify.Set<Models.Inventory>().SingleAsync()).QtyOnHand);
        Assert.Empty(await verify.Set<MaterialLotConsumption>().ToListAsync());
    }

    [Fact]
    public async Task Shipment_cannot_exceed_order_balance_or_issue_a_foreign_order_line()
    {
        using var scenario = new LotScenario();
        await scenario.ReceiveLotAsync("ENOUGH", 5m);
        var shipment = await scenario.RecordShipmentAsync(11m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.IssueAsync(11m, shipment));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.IssueAsync(1m, shipment,
            orderItemId: LotScenario.SecondOrderItemId));
        await using var missingShipment = scenario.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => InventoryServices.OrderStock(missingShipment)
            .ConsumeOrderLinesAsync(scenario.BusinessUnitId, LotScenario.OrderId,
                new Dictionary<long, decimal> { [LotScenario.OrderItemId] = 1m }));
        await using var context = scenario.Context();
        Assert.Empty(await context.InventoryMovements.Where(x => x.Type == InventoryMovementType.Issue).ToListAsync());
    }
}

using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;
using InventoryRow = ERP_RFQ_Automation.Models.Inventory;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 6 — FR-INV-01..03. Lot-level reservation, the goods issue that declares what it moved,
/// and the two silent failures the despatch path was carrying.
///
/// <para><b>What each test is for.</b> None of these assert that a column round-trips. Gate 5 left
/// a reservation that named an inventory row rather than the physical units on it, so a picker
/// could still walk to a recalled rack and take from it: the assertions here are on the
/// <b>refusals</b> and on <b>which orders are displaced</b>, because those are the behaviours that
/// disappear if the lot column stops being load-bearing.</para>
///
/// <para>PostgreSQL CHECK constraints are not exercised here — the portable lane runs SQLite with
/// <c>ignore_check_constraints</c> on. Every invariant is enforced in the service and that is what
/// these tests hit; the constraint text itself is certified in the PostgreSQL lane.</para>
/// </summary>
public sealed class Gate6LotReservationTests
{
    // ===================================================== FR-INV-01: the hold names what it holds

    [Fact]
    public async Task An_allocated_hold_names_the_material_lot_it_is_holding()
    {
        using var scenario = new LotScenario();
        await scenario.ReceiveLotAsync("BATCH-A", 5m);

        var result = await scenario.AllocateAsync(quantity: 4m);

        Assert.True(result.FullyAllocated);
        var line = Assert.Single(result.Lines);
        Assert.Equal(4m, line.ReservedFromLots);
        Assert.Equal(0m, line.ReservedWithoutLot);

        await using var verify = scenario.Context();
        var holds = await verify.Set<StockReservation>()
            .Where(x => x.Status == StockReservationStatus.Active).ToListAsync();
        Assert.All(holds, hold => Assert.NotNull(hold.MaterialLotId));
        Assert.Equal(4m, holds.Sum(x => x.Quantity));
    }

    [Fact]
    public async Task Allocation_takes_the_earliest_expiring_lot_first()
    {
        using var scenario = new LotScenario();
        // Received in the WRONG order on purpose: the older receipt has the later expiry, so plain
        // first-in-first-out would pick it and quietly age out the batch that expires first.
        var longDated = await scenario.ReceiveLotAsync("BATCH-LONG", 3m,
            expiry: new DateOnly(2030, 1, 1));
        var shortDated = await scenario.ReceiveLotAsync("BATCH-SHORT", 3m,
            expiry: new DateOnly(2027, 1, 1));

        await scenario.AllocateAsync(quantity: 3m);

        await using var verify = scenario.Context();
        var holds = await verify.Set<StockReservation>()
            .Where(x => x.Status == StockReservationStatus.Active).ToListAsync();
        Assert.Equal(3m, holds.Where(x => x.MaterialLotId == shortDated).Sum(x => x.Quantity));
        Assert.Equal(0m, holds.Where(x => x.MaterialLotId == longDated).Sum(x => x.Quantity));
    }

    [Fact]
    public async Task Two_orders_cannot_both_name_the_same_units_of_one_lot()
    {
        using var scenario = new LotScenario();
        var lotId = await scenario.ReceiveLotAsync("BATCH-A", 5m);

        await scenario.AllocateAsync(quantity: 4m);
        await scenario.AllocateAsync(quantity: 4m, orderId: LotScenario.SecondOrderId,
            orderItemId: LotScenario.SecondOrderItemId);

        await using var verify = scenario.Context();
        var onLot = await verify.Set<StockReservation>()
            .Where(x => x.MaterialLotId == lotId && x.Status == StockReservationStatus.Active)
            .SumAsync(x => x.Quantity);

        // The lot holds five; the two orders wanted eight between them. Without the lot-level
        // check the inventory-level total would have allowed both to name the same physical units,
        // because the row also carries un-lotted opening stock that covers the difference.
        Assert.Equal(5m, onLot);
    }

    [Fact]
    public async Task Stock_with_no_lot_behind_it_is_reported_as_a_gap_rather_than_as_coverage()
    {
        using var scenario = new LotScenario();
        // No receipt at all: the inventory row carries only its opening balance, which entered by
        // count and therefore has no lot. Reserving it must work — it is real stock — and must say
        // so, because a hold nobody can trace is what a recall discovers too late.
        var result = await scenario.AllocateAsync(quantity: 2m);

        var line = Assert.Single(result.Lines);
        Assert.Equal(2m, line.Reserved);
        Assert.Equal(0m, line.ReservedFromLots);
        Assert.Equal(2m, line.ReservedWithoutLot);
    }

    // ================================================ FR-INV-01 / FR-MTR-05: the physical control

    [Fact]
    public async Task Quarantining_one_lot_releases_only_the_orders_that_were_holding_that_lot()
    {
        using var scenario = new LotScenario();
        var lotA = await scenario.ReceiveLotAsync("BATCH-A", 4m);
        var lotB = await scenario.ReceiveLotAsync("BATCH-B", 4m);

        // Order 1 takes lot A (received first, so FEFO reaches it first). Order 2 takes lot B.
        await scenario.AllocateAsync(quantity: 4m);
        await scenario.AllocateAsync(quantity: 4m, orderId: LotScenario.SecondOrderId,
            orderItemId: LotScenario.SecondOrderItemId);

        var quarantine = await scenario.QuarantineAsync(lotA);

        // THE POINT OF THIS GATE. Gate 5 could only free stock by quantity, newest hold first, so
        // recalling lot A displaced the order holding lot B and left the order holding the
        // recalled material untouched. Now the displaced order is the one that was actually
        // holding the recalled lot, and the other customer's promise survives the recall.
        var displaced = Assert.Single(quarantine.DisplacedReservations);
        Assert.Equal(LotScenario.OrderId, displaced.OrderId);

        await using var verify = scenario.Context();
        var stillHeld = await verify.Set<StockReservation>()
            .Where(x => x.Status == StockReservationStatus.Active).ToListAsync();
        Assert.All(stillHeld, hold => Assert.Equal(lotB, hold.MaterialLotId));
        Assert.Equal(LotScenario.SecondOrderId, Assert.Single(stillHeld.Select(x => x.OrderId).Distinct()));
    }

    [Fact]
    public async Task A_hold_that_names_a_quarantined_lot_cannot_be_issued()
    {
        using var scenario = new LotScenario();
        var lotId = await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AllocateAsync(quantity: 4m);

        // The lot is put on hold WITHOUT going through the release path, which is the race the
        // physical control exists for: a quality hold raised while a picker is already at the
        // rack, or any future writer of the lot status that forgets to release the holds. The
        // guard must not depend on the release having run.
        await scenario.ForceQuarantineStatusAsync(lotId);

        var reservationId = await scenario.FirstActiveHoldIdAsync();
        await using var context = scenario.Context();
        var failure = await Assert.ThrowsAsync<QuarantinedLotIssueException>(
            () => InventoryServices.Availability(context)
                .ConsumeAsync(scenario.BusinessUnitId, reservationId, "qa"));

        Assert.Equal(lotId, failure.MaterialLotId);

        // Nothing moved. A refusal that had already decremented on-hand would be worse than none.
        await using var verify = scenario.Context();
        Assert.Equal(7m, await verify.Set<InventoryRow>()
            .Where(x => x.Id == ProcurementTestData.Inventory).Select(x => x.QtyOnHand).SingleAsync());
    }

    [Fact]
    public async Task A_recall_names_the_orders_holding_the_lot_not_everyone_holding_the_product()
    {
        using var scenario = new LotScenario();
        var lotA = await scenario.ReceiveLotAsync("BATCH-A", 4m);
        await scenario.ReceiveLotAsync("BATCH-B", 4m);

        await scenario.AllocateAsync(quantity: 4m);
        await scenario.AllocateAsync(quantity: 4m, orderId: LotScenario.SecondOrderId,
            orderItemId: LotScenario.SecondOrderItemId);

        await using var context = scenario.Context();
        var commitments = await InventoryServices.Availability(context)
            .GetLotCommitmentsAsync(scenario.BusinessUnitId, lotA);

        // Both orders hold the same PRODUCT. Only one holds the recalled lot.
        Assert.Equal([LotScenario.OrderId], commitments.AffectedOrderIds);
        Assert.Equal(4m, commitments.HeldQuantity);
        Assert.Equal(0m, commitments.ConsumedQuantity);
    }

    // ============================================ FR-INV-03: the issue declares what it moved

    [Fact]
    public async Task A_goods_issue_declares_the_lots_it_moved_without_anyone_being_asked_to()
    {
        using var scenario = new LotScenario();
        var lotId = await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AllocateAsync(quantity: 4m);
        var shipmentId = await scenario.RecordShipmentAsync(4m);

        var issue = await scenario.IssueAsync(quantity: 4m, shipmentId: shipmentId);

        Assert.Equal(4m, issue.TotalIssued);
        Assert.Equal(4m, issue.TotalIssuedFromLots);
        Assert.Equal(0m, issue.TotalIssuedWithoutLot);

        await using var verify = scenario.Context();
        var declaration = Assert.Single(await verify.MaterialLotConsumptions.ToListAsync());
        Assert.Equal(lotId, declaration.MaterialLotId);
        Assert.Equal(4m, declaration.Quantity);
        // The despatch note is named on the declaration, so where-used trace answers "which
        // delivery note did this lot leave on" from the record rather than from a quantity join
        // that only happens to add up.
        Assert.Equal(shipmentId, declaration.ShipmentId);
        Assert.Equal(4m, (await verify.MaterialLots.SingleAsync(x => x.Id == lotId)).QuantityConsumed);
    }

    [Fact]
    public async Task The_where_used_trace_has_no_undeclared_gap_once_the_issue_declares_for_itself()
    {
        using var scenario = new LotScenario();
        await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AllocateAsync(quantity: 4m);
        var shipmentId = await scenario.RecordShipmentAsync(4m);
        await scenario.IssueAsync(quantity: 4m, shipmentId: shipmentId);

        await using var context = scenario.Context();
        var trace = await InventoryServices.Traceability(context)
            .GetOrderTraceAsync(scenario.BusinessUnitId, LotScenario.OrderId);

        var line = Assert.Single(trace.Lines);
        Assert.Equal(4m, line.ShippedQuantity);
        Assert.Equal(4m, line.DeclaredQuantity);
        Assert.Equal(0m, line.UntracedQuantity);
        Assert.DoesNotContain(trace.Gaps, g => g.Kind == MaterialTraceGapKinds.UndeclaredFulfilment);
    }

    [Fact]
    public async Task A_lapsed_certificate_stops_the_despatch_until_somebody_signs_for_it()
    {
        using var scenario = new LotScenario();
        var lotId = await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AddExpiredCertificateAsync(lotId);
        await scenario.AllocateAsync(quantity: 4m);
        var shipmentId = await scenario.RecordShipmentAsync(4m);

        await Assert.ThrowsAsync<MaterialTraceabilityConflictException>(
            () => scenario.IssueAsync(quantity: 4m, shipmentId: shipmentId));

        // Signed for: the despatch goes, and the signature is on the record permanently.
        var issue = await scenario.IssueAsync(quantity: 4m, shipmentId: shipmentId,
            overrideReason: "Renewal in transit; customer accepted in writing on 2026-08-08.");
        Assert.Equal(4m, issue.TotalIssuedFromLots);

        await using var verify = scenario.Context();
        var declaration = Assert.Single(await verify.MaterialLotConsumptions.ToListAsync());
        Assert.Equal(MaterialLotComplianceStates.CertificateExpired, declaration.ComplianceStateAtDeclaration);
        Assert.Equal("qa", declaration.ComplianceOverrideBy);
        Assert.Contains("customer accepted in writing", declaration.ComplianceOverrideReason);
    }

    [Fact]
    public async Task An_override_offered_for_lots_that_are_all_in_date_is_refused()
    {
        using var scenario = new LotScenario();
        await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AllocateAsync(quantity: 4m);

        // An override that is always present stops meaning anything, so the despatch that supplies
        // one it does not need is refused rather than quietly accepted.
        var failure = await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.IssueAsync(quantity: 4m, shipmentId: 96_703,
                overrideReason: "Signing for it just in case."));
        Assert.Contains("in date", failure.Message);
    }

    // ================================================ Defect: the goods issue discarded its result

    [Fact]
    public async Task A_goods_issue_that_could_not_move_the_declared_quantity_reports_itself_short()
    {
        using var scenario = new LotScenario();
        var lotId = await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AllocateAsync(quantity: 4m);

        // The stock is recalled between confirmation and despatch. The holds are released, so the
        // issue has nothing to consume — and it used to return that fact to a caller that threw it
        // away, producing a delivery note for goods that never moved.
        await scenario.QuarantineAsync(lotId);

        var issue = await scenario.IssueAsync(quantity: 4m, shipmentId: 96_704);

        Assert.True(issue.IsShort);
        Assert.Equal(0m, issue.TotalIssued);
        var shortLine = Assert.Single(issue.ShortLines);
        Assert.Equal(4m, shortLine.Declared);
        Assert.Equal(0m, shortLine.Issued);
    }

    // ============================================================ FR-INV-05 and FR-INV-06 reports

    [Fact]
    public async Task A_stock_count_returns_the_variance_it_used_to_throw_away()
    {
        using var scenario = new LotScenario();
        await using var context = scenario.Context();
        var ledger = InventoryServices.Ledger(context);

        var result = await ledger.RecordCountAsync(scenario.BusinessUnitId, ProcurementTestData.Product,
            ProcurementTestData.Warehouse, countedQuantity: 9m, "count-1", "counter@qa");

        Assert.Equal(2m, result.BookQuantity);
        Assert.Equal(9m, result.CountedQuantity);
        Assert.Equal(7m, result.Variance);
        Assert.False(result.CountAgreed);
    }

    [Fact]
    public async Task The_variance_report_is_rebuilt_from_the_append_only_ledger()
    {
        using var scenario = new LotScenario();
        await using (var write = scenario.Context())
        {
            var ledger = InventoryServices.Ledger(write);
            await ledger.RecordCountAsync(scenario.BusinessUnitId, ProcurementTestData.Product,
                ProcurementTestData.Warehouse, countedQuantity: 1m, "count-short", "counter@qa",
                "Night shift count");
            // An ordinary adjustment is not a count and must not appear in a stock-take report.
            await ledger.AdjustAsync(scenario.BusinessUnitId, ProcurementTestData.Product,
                ProcurementTestData.Warehouse, delta: 5m, "adjust-1", "counter@qa", "Write-on");
        }

        await using var read = scenario.Context();
        var rows = await InventoryServices.Ledger(read)
            .GetCountVarianceAsync(scenario.BusinessUnitId);

        var row = Assert.Single(rows);
        Assert.Equal(2m, row.BookQuantity);
        Assert.Equal(1m, row.CountedQuantity);
        Assert.Equal(-1m, row.Variance);
        Assert.Equal(-50m, row.VariancePercent);
        Assert.Equal("counter@qa", row.CountedBy);
    }

    [Fact]
    public async Task Stock_ageing_dates_a_row_from_its_last_issue_not_its_last_receipt()
    {
        using var scenario = new LotScenario();
        await scenario.ReceiveLotAsync("BATCH-A", 5m);
        await scenario.AllocateAsync(quantity: 4m);
        await scenario.IssueAsync(quantity: 4m, shipmentId: await scenario.RecordShipmentAsync(4m));
        // The receipt is fresh and the issue is old. Receipt-based ageing would call this the
        // freshest stock in the warehouse; it is in fact material that stopped selling a year ago.
        await scenario.BackdateLastIssueAsync(DateTime.UtcNow.AddDays(-400));

        await using var context = scenario.Context();
        var rows = await InventoryServices.Ledger(context).GetStockAgeingAsync(scenario.BusinessUnitId);

        var row = Assert.Single(rows.Where(x => x.InventoryId == ProcurementTestData.Inventory));
        Assert.Equal(StockAgeingBands.Obsolete, row.Band);
        Assert.True(row.DaysSinceLastIssue >= 399);
        Assert.True(row.DaysSinceLastReceipt < 2);
    }
}

/// <summary>
/// A tenant with a stocked product, two sales orders and a supplier purchase order that receipts
/// can be posted against — so every lot in these tests was created the only way a lot can be
/// created, by a goods receipt.
/// </summary>
internal sealed class LotScenario : IDisposable
{
    public const long OrderId = 96_800;
    public const long OrderItemId = 96_810;
    public const long SecondOrderId = 96_820;
    public const long SecondOrderItemId = 96_830;
    public const long OrderStatusId = 96_840;
    public const long ShipmentStatusId = 96_841;

    private readonly ProcurementScenario _procurement = new();
    private long _purchaseOrderId;
    private long _purchaseOrderVersion;
    private long _purchaseOrderLineId;
    private int _shipmentSequence;

    public LotScenario()
    {
        using var seed = _procurement.Context();
        Seed.Customer(seed, BusinessUnitId, BusinessUnitId, "QA Customer");
        seed.SetupMasters.Add(new SetupMaster
        {
            SetupId = OrderStatusId, SetupType = "OrderStatus", SetupCode = "OPEN", SetupValue = "OPEN",
            BusinessUnitId = BusinessUnitId, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow
        });
        seed.SetupMasters.Add(new SetupMaster
        {
            SetupId = ShipmentStatusId, SetupType = "ShipmentStatus", SetupCode = "SHIPPED",
            SetupValue = "SHIPPED", BusinessUnitId = BusinessUnitId, IsActive = true,
            CreatedBy = "qa", CreatedOn = DateTime.UtcNow
        });
        foreach (var (orderId, itemId) in new[] { (OrderId, OrderItemId), (SecondOrderId, SecondOrderItemId) })
        {
            seed.Set<Order>().Add(new Order
            {
                Id = orderId, OrderNo = $"SO-LOT-{orderId}", CustomerId = BusinessUnitId,
                BusinessUnitId = BusinessUnitId, StatusId = OrderStatusId, TotalAmount = 100m,
                OrderDate = DateTime.UtcNow, CreatedBy = "qa", CreatedOn = DateTime.UtcNow, IsActive = true
            });
            seed.Set<OrderItem>().Add(new OrderItem
            {
                Id = itemId, OrderId = orderId, ProductId = ProcurementTestData.Product,
                WarehouseId = ProcurementTestData.Warehouse, Quantity = 10m, UnitPrice = 10m,
                Discount = 0m, TaxAmount = 0m, TotalAmount = 100m, CreatedBy = "qa",
                CreatedDate = DateTime.UtcNow, IsActive = true
            });
        }
        seed.SaveChanges();
    }

    public long BusinessUnitId => _procurement.BusinessUnitId;

    public ErpRfqAutomationContext Context(long? tenant = null) => _procurement.Context(tenant);

    /// <summary>Posts a goods receipt and returns the id of the lot it created.</summary>
    public async Task<long> ReceiveLotAsync(string lotNumber, decimal quantity, DateOnly? expiry = null)
    {
        await EnsurePurchaseOrderAsync();
        await using (var product = Context())
        {
            var row = await product.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
            row.BatchTracking = true;
            await product.SaveChangesAsync();
        }

        var key = $"receipt-{Guid.NewGuid():N}";
        await using var context = Context();
        await new ERP_RFQ_Automation.Procurement.ProcurementApplicationService(context)
            .PostGoodsReceiptAsync(new ERP_RFQ_Automation.Procurement.PostGoodsReceiptCommand(
                BusinessUnitId, _purchaseOrderId, ProcurementTestData.Warehouse,
                $"GRN-{Guid.NewGuid():N}"[..12], DateTime.UtcNow, _purchaseOrderVersion,
                [new ERP_RFQ_Automation.Procurement.PostGoodsReceiptLine(_purchaseOrderLineId, quantity,
                    new ReceiptLotDeclaration(LotNumber: lotNumber, ExpiryDate: expiry))],
                key, "qa", $"corr-{key}"));
        _purchaseOrderVersion++;

        await using var read = Context();
        return await read.MaterialLots.Where(x => x.LotNumber == lotNumber).Select(x => x.Id).SingleAsync();
    }

    public async Task<OrderAllocationResult> AllocateAsync(
        decimal quantity, long orderId = OrderId, long orderItemId = OrderItemId)
    {
        await using (var line = Context())
        {
            var item = await line.Set<OrderItem>().SingleAsync(x => x.Id == orderItemId);
            item.Quantity = quantity;
            await line.SaveChangesAsync();
        }
        await using var context = Context();
        return await InventoryServices.OrderStock(context).ReserveOrderAsync(BusinessUnitId, orderId, "qa");
    }

    public async Task<OrderIssueResult> IssueAsync(
        decimal quantity, long? shipmentId = null, string? overrideReason = null,
        long orderId = OrderId, long orderItemId = OrderItemId)
    {
        await using var context = Context();
        return await InventoryServices.OrderStock(context).ConsumeOrderLinesAsync(
            BusinessUnitId, orderId, new Dictionary<long, decimal> { [orderItemId] = quantity },
            "qa", shipmentId, overrideReason);
    }

    public async Task<LotQuarantineResult> QuarantineAsync(long lotId)
    {
        await using var read = Context();
        var version = await read.MaterialLots.Where(x => x.Id == lotId).Select(x => x.Version).SingleAsync();
        await using var context = Context();
        return await InventoryServices.Traceability(context).QuarantineAsync(new QuarantineLotCommand(
            BusinessUnitId, lotId, version, MaterialLotQuarantineReasons.SupplierRecall,
            "Supplier recall notice RC-4417 covers this batch.", "qa", "corr-quarantine",
            $"quarantine-{lotId}-{version}"));
    }

    /// <summary>
    /// Flips the lot status WITHOUT the release path, so the goods-issue guard is tested on its
    /// own rather than on the release having already emptied the holds.
    /// </summary>
    public async Task ForceQuarantineStatusAsync(long lotId)
    {
        await using var context = Context();
        var lot = await context.MaterialLots.SingleAsync(x => x.Id == lotId);
        lot.Status = MaterialLotStatuses.Quarantined;
        lot.QuarantineReasonCode = MaterialLotQuarantineReasons.QualityHold;
        lot.QuarantineReason = "Held at the rack by the quality inspector.";
        lot.QuarantinedOn = DateTime.UtcNow;
        lot.QuarantinedBy = "qa";
        await context.SaveChangesAsync();
    }

    public async Task<long> FirstActiveHoldIdAsync()
    {
        await using var context = Context();
        return await context.Set<StockReservation>()
            .Where(x => x.Status == StockReservationStatus.Active)
            .OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    }

    public async Task AddExpiredCertificateAsync(long lotId)
    {
        await using var context = Context();
        var attachment = new Attachment
        {
            ParentType = "MaterialLotCertificate", ParentId = lotId,
            FileName = "coc.pdf", FilePath = $"evidence://{Guid.NewGuid():N}",
            ContentSha256 = new string('a', 64), CreatedOn = DateTime.UtcNow,
        };
        context.Attachments.Add(attachment);
        await context.SaveChangesAsync();
        context.MaterialLotCertificates.Add(new MaterialLotCertificate
        {
            BusinessUnitId = BusinessUnitId, MaterialLotId = lotId,
            CertificateType = MaterialCertificateTypes.CertificateOfConformity,
            ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3),
            AttachmentId = attachment.Id, ContentSha256 = new string('a', 64),
            FileName = "coc.pdf", UploadedOn = DateTime.UtcNow, UploadedBy = "qa",
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Writes the despatch note the where-used trace measures declared quantity against.</summary>
    public async Task<long> RecordShipmentAsync(decimal quantity, long orderId = OrderId,
        long orderItemId = OrderItemId)
    {
        await using var context = Context();
        var shipment = new Shipment
        {
            Id = 96_900 + _shipmentSequence++,
            ShipmentNo = $"DN-{_shipmentSequence}",
            OrderId = orderId, BusinessUnitId = BusinessUnitId, StatusId = ShipmentStatusId,
            ShipmentDate = DateTime.UtcNow, CreatedBy = "qa", CreatedOn = DateTime.UtcNow, IsActive = true,
        };
        shipment.ShipmentItems.Add(new ShipmentItem
        {
            OrderItemId = orderItemId, Quantity = quantity, CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow, IsActive = true
        });
        context.Shipments.Add(shipment);
        await context.SaveChangesAsync();
        return shipment.Id;
    }

    public async Task BackdateLastIssueAsync(DateTime occurredOn)
    {
        await using var context = Context();
        var issues = await context.InventoryMovements.AsNoTracking()
            .Where(x => x.Type == ERP_RFQ_Automation.Inventory.Commercial.InventoryMovementType.Issue)
            .ToListAsync();
        foreach (var movement in issues)
            context.InventoryMovements.Update(movement with { OccurredOn = occurredOn });
        await context.SaveChangesAsync();
    }

    private async Task EnsurePurchaseOrderAsync()
    {
        if (_purchaseOrderId != 0) return;
        // 8 is the fixture ceiling: the RFQ line asks for 10 and the tenant already holds 2, so the net
        // sourcing requirement an award may cover is 8. Every test here receipts well inside it.
        var issued = await _procurement.CreatePurchaseOrderAsync("lot", quantity: 8m);
        _purchaseOrderId = issued.Id;
        _purchaseOrderVersion = issued.Version;
        _purchaseOrderLineId = await _procurement.PurchaseOrderLineIdAsync(issued.Id);
    }

    public void Dispose() => _procurement.Dispose();
}

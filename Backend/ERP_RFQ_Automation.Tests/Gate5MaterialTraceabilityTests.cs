using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;
using InventoryRow = ERP_RFQ_Automation.Models.Inventory;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 5 Module 5 — FR-MTR-01..05.
///
/// <para>The load-bearing tests in this file are the quarantine ones. FR-MTR-05 is worthless if the
/// flag is advisory, so three separate things are proved: that quarantined stock stops being
/// promisable, that a hold taken BEFORE the recall is given back rather than left free to ship, and
/// that a quarantined lot cannot be declared onto a customer fulfilment. Each fails if its
/// enforcement is removed — the assertions are on the refusal, not on the flag.</para>
///
/// <para>PostgreSQL CHECK constraints are not exercised here: the portable lane runs SQLite with
/// <c>ignore_check_constraints</c> on. Every invariant those constraints express is also enforced
/// in the service, and that is what these tests hit.</para>
/// </summary>
public sealed class Gate5MaterialTraceabilityTests
{
    // ============================================================ FR-MTR-01: receipt creates lots

    [Fact]
    public async Task A_goods_receipt_creates_a_traceable_lot_even_for_untracked_material()
    {
        using var scenario = new TraceabilityScenario();
        var received = await scenario.ReceiveAsync(quantity: 8m);

        await using var verify = scenario.Context();
        var lot = Assert.Single(await verify.MaterialLots.ToListAsync());

        // Nobody typed a batch number for bulk material, and the lot exists anyway. A receipt that
        // could produce stock with no lot behind it is the one outcome this module exists to stop.
        Assert.Equal(MaterialLotTrackingModes.Untracked, lot.TrackingMode);
        Assert.Equal(8m, lot.QuantityReceived);
        Assert.Equal(MaterialLotStatuses.Available, lot.Status);
        Assert.Contains(received.Number.ToUpperInvariant(), lot.LotNumber);

        // Where-from is carried, not derived.
        Assert.Equal(scenario.PurchaseOrderId, lot.SupplierPurchaseOrderId);
        Assert.Equal(ProcurementTestData.Supplier, lot.SupplierId);
        Assert.Equal(scenario.GoodsReceiptId, lot.GoodsReceiptId);
    }

    [Fact]
    public async Task A_batch_tracked_line_is_refused_without_the_suppliers_lot_number()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetTrackingAsync(batch: true, serial: false);

        var failure = await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.ReceiveAsync(quantity: 8m));

        Assert.Contains("lot or batch number is required", failure.Message);

        // The refusal is inside the receipt transaction, so nothing landed at all.
        await using var verify = scenario.Context();
        Assert.Empty(await verify.MaterialLots.ToListAsync());
        Assert.Empty(await verify.GoodsReceipts.ToListAsync());
        Assert.Equal(ProcurementTestData.InitialOnHand, await verify.Set<InventoryRow>()
            .Where(x => x.Id == ProcurementTestData.Inventory).Select(x => x.QtyOnHand).SingleAsync());
    }

    [Fact]
    public async Task A_serial_tracked_receipt_creates_one_lot_per_unit()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetTrackingAsync(batch: false, serial: true);

        await scenario.ReceiveAsync(quantity: 3m,
            declaration: new ReceiptLotDeclaration(SerialNumbers: ["SN-1", "SN-2", "SN-3"]));

        await using var verify = scenario.Context();
        var lots = await verify.MaterialLots.OrderBy(x => x.LotNumber).ToListAsync();
        Assert.Equal(3, lots.Count);
        Assert.All(lots, lot =>
        {
            Assert.Equal(MaterialLotTrackingModes.Serial, lot.TrackingMode);
            Assert.Equal(1m, lot.QuantityReceived);
        });
        Assert.Equal(["SN-1", "SN-2", "SN-3"], lots.Select(x => x.LotNumber));
    }

    [Fact]
    public async Task A_serial_count_that_disagrees_with_the_received_quantity_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetTrackingAsync(batch: false, serial: true);

        var failure = await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.ReceiveAsync(quantity: 3m,
                declaration: new ReceiptLotDeclaration(SerialNumbers: ["SN-1", "SN-2"])));

        Assert.Contains("declared 2 serial number", failure.Message);
    }

    [Fact]
    public async Task A_duplicate_serial_number_on_one_receipt_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetTrackingAsync(batch: false, serial: true);

        await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.ReceiveAsync(quantity: 2m,
                declaration: new ReceiptLotDeclaration(SerialNumbers: ["SN-1", "sn-1"])));
    }

    // ============================================================ FR-MTR-04: origin and manufacturer

    [Fact]
    public async Task The_lot_records_both_the_ordered_origin_and_the_origin_that_actually_arrived()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetOrderedOriginAsync("CN");

        await scenario.ReceiveAsync(quantity: 8m, declaration: new ReceiptLotDeclaration(
            LotNumber: "B-100", CountryOfOrigin: "VN", ManufacturerName: "Acme Cables"));

        await using var verify = scenario.Context();
        var lot = Assert.Single(await verify.MaterialLots.ToListAsync());

        // The purchase-order line says what was ordered; the lot says what turned up. One field for
        // both would have silently overwritten the ordered position at receipt.
        Assert.Equal("CN", lot.OrderedCountryOfOrigin);
        Assert.Equal("VN", lot.CountryOfOrigin);
        Assert.Equal("Acme Cables", lot.ManufacturerName);
        Assert.True(lot.OriginDiffersFromOrder);

        var trace = await scenario.Service(verify).GetLotAsync(scenario.BusinessUnitId, lot.Id);
        Assert.Contains(trace.Gaps, gap => gap.Kind == MaterialTraceGapKinds.OriginMismatch);
    }

    [Fact]
    public async Task An_unstated_origin_defaults_to_the_purchase_order_line()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetOrderedOriginAsync("DE");

        await scenario.ReceiveAsync(quantity: 8m);

        await using var verify = scenario.Context();
        var lot = Assert.Single(await verify.MaterialLots.ToListAsync());
        Assert.Equal("DE", lot.CountryOfOrigin);
        Assert.False(lot.OriginDiffersFromOrder);
    }

    // ============================================================ FR-MTR-05: quarantine BLOCKS

    /// <summary>
    /// The central claim of FR-MTR-05, asserted on the refusal rather than on the flag.
    ///
    /// <para>Remove the <c>ReclassifyAsync(... StockBucket.Quarantine ...)</c> call from
    /// <c>QuarantineAsync</c> and this test fails: available-to-promise stays at 10 and
    /// <c>ReserveAsync</c> succeeds.</para>
    /// </summary>
    [Fact]
    public async Task A_quarantined_lot_is_not_promisable_and_reserving_it_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        // Before: 2 opening + 8 received = 10 on hand, all of it promisable.
        await using (var before = scenario.Context())
        {
            var availability = await new InventoryAvailabilityService(before)
                .GetAvailabilityAsync(scenario.BusinessUnitId, ProcurementTestData.Inventory);
            Assert.Equal(10m, availability.OnHand);
            Assert.Equal(10m, availability.Available);
        }

        var result = await scenario.QuarantineAsync(lotId, version: 1);

        Assert.Equal(MaterialLotStatuses.Quarantined, result.Status);
        Assert.Equal(8m, result.QuarantinedQuantity);
        Assert.Equal(8m, result.InventoryQuarantineQuantity);
        // The 8 recalled units stop being promisable; the 2 units of opening stock do not.
        Assert.Equal(2m, result.AvailableToPromiseAfter);

        await using var context = scenario.Context();
        var availabilityService = new InventoryAvailabilityService(context);

        // Reserving more than the un-quarantined balance is refused AT THE POINT OF ALLOCATION.
        var refusal = await Assert.ThrowsAsync<InsufficientStockException>(() =>
            availabilityService.ReserveAsync(scenario.BusinessUnitId, ProcurementTestData.Inventory,
                3m, "reserve-after-recall"));
        Assert.Equal(2m, refusal.Available);

        // Quarantine is on-hand neutral: the units are still in the building, they have just
        // stopped being sellable. A quarantine that decremented on-hand would double-count against
        // the ATP formula, which already subtracts the bucket.
        Assert.Equal(10m, await context.Set<InventoryRow>()
            .Where(x => x.Id == ProcurementTestData.Inventory).Select(x => x.QtyOnHand).SingleAsync());
        var movement = Assert.Single(await context.InventoryMovements
            .Where(x => x.Type == InventoryMovementType.Quarantine).ToListAsync());
        Assert.Equal(8m, movement.Quantity);
    }

    /// <summary>
    /// The hole a status flag alone would leave. <c>ConsumeAsync</c> never re-reads availability —
    /// it only checks that physical on-hand covers the hold — so stock reserved before a recall
    /// would still ship. Delete the <c>ReleaseForQuarantineAsync</c> call and this test fails.
    /// </summary>
    [Fact]
    public async Task Quarantine_gives_back_holds_that_were_taken_before_the_recall()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        long reservationId;
        await using (var reserve = scenario.Context())
        {
            var hold = await new InventoryAvailabilityService(reserve).ReserveAsync(
                scenario.BusinessUnitId, ProcurementTestData.Inventory, 10m, "hold-before-recall",
                orderId: TraceabilityScenario.OrderId, orderItemId: TraceabilityScenario.OrderItemId);
            reservationId = hold.Id;
        }

        var result = await scenario.QuarantineAsync(lotId, version: 1);

        var displaced = Assert.Single(result.DisplacedReservations);
        Assert.Equal(reservationId, displaced.ReservationId);
        Assert.Equal(TraceabilityScenario.OrderId, displaced.OrderId);

        await using var verify = scenario.Context();
        Assert.Equal(StockReservationStatus.Released, await verify.StockReservations
            .Where(x => x.Id == reservationId).Select(x => x.Status).SingleAsync());
        // Released BECAUSE of the recall, not because an order was cancelled. The two facts must be
        // distinguishable in the ledger.
        Assert.True(await verify.ProcurementEvents.AnyAsync(x =>
            x.AggregateType == "StockReservation"
            && x.EventType == "STOCK_RESERVATION_RELEASED_ON_QUARANTINE"));
    }

    /// <summary>
    /// The second enforcement point. Reducing availability stops the next reservation; this stops
    /// the material physically leaving. Delete the status check in <c>DeclareConsumptionAsync</c>
    /// and this test fails.
    /// </summary>
    [Fact]
    public async Task A_quarantined_lot_cannot_be_declared_against_a_customer_fulfilment()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        await scenario.QuarantineAsync(lotId, version: 1);

        var refusal = await Assert.ThrowsAsync<MaterialTraceabilityConflictException>(
            () => scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-quarantined"));

        Assert.Contains("quarantined", refusal.Message);
        Assert.Contains("released by an authorized user", refusal.Message);

        await using var verify = scenario.Context();
        Assert.Empty(await verify.MaterialLotConsumptions.ToListAsync());
    }

    [Fact]
    public async Task A_released_lot_can_be_fulfilled_again_and_its_stock_is_promisable()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        var quarantined = await scenario.QuarantineAsync(lotId, version: 1);

        var released = await scenario.ReleaseAsync(lotId, quarantined.Version,
            "Supplier confirmed the recall did not cover this batch. QA sign-off QA-2291.");

        Assert.Equal(MaterialLotStatuses.Available, released.Status);
        Assert.Equal(0m, released.InventoryQuarantineQuantity);
        Assert.Equal(10m, released.AvailableToPromiseAfter);

        var declaration = await scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-after-release");
        Assert.Equal(1m, declaration.Quantity);

        await using var verify = scenario.Context();
        var lot = await verify.MaterialLots.SingleAsync(x => x.Id == lotId);
        Assert.NotNull(lot.ReleasedOn);
        Assert.Equal("qa", lot.ReleasedBy);
        Assert.Contains("QA-2291", lot.ReleaseReason);
        Assert.True(await verify.ProcurementEvents.AnyAsync(x =>
            x.AggregateType == "MaterialLot" && x.EventType == "MATERIAL_LOT_RELEASED"));
    }

    [Fact]
    public async Task A_release_without_a_reason_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        var quarantined = await scenario.QuarantineAsync(lotId, version: 1);

        await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.ReleaseAsync(lotId, quarantined.Version, "   "));

        await using var verify = scenario.Context();
        Assert.Equal(MaterialLotStatuses.Quarantined,
            await verify.MaterialLots.Where(x => x.Id == lotId).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task A_quarantine_with_an_unrecognised_reason_code_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.QuarantineAsync(lotId, version: 1, reasonCode: "BECAUSE_I_SAID_SO"));
    }

    [Fact]
    public async Task Quarantining_a_lot_twice_under_the_same_key_is_idempotent()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        var first = await scenario.QuarantineAsync(lotId, version: 1, key: "recall-1");
        var replay = await scenario.QuarantineAsync(lotId, version: first.Version, key: "recall-1");

        Assert.True(replay.Replayed);
        await using var verify = scenario.Context();
        // The bucket did not take the quantity twice.
        Assert.Equal(8m, await verify.Set<InventoryRow>()
            .Where(x => x.Id == ProcurementTestData.Inventory)
            .Select(x => x.QuarantineQuantity).SingleAsync());
    }

    [Fact]
    public async Task A_second_quarantine_under_a_different_key_is_refused_as_a_conflict()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        var first = await scenario.QuarantineAsync(lotId, version: 1, key: "recall-1");

        await Assert.ThrowsAsync<MaterialTraceabilityConflictException>(
            () => scenario.QuarantineAsync(lotId, version: first.Version, key: "recall-2"));
    }

    // ============================================================ FR-MTR-02: certificates and expiry

    [Fact]
    public async Task An_expired_certificate_blocks_the_fulfilment_declaration_until_a_reason_is_recorded()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        await scenario.AddCertificateAsync(lotId, MaterialCertificateTypes.CertificateOfConformity,
            expiresOn: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), number: "COC-1");

        var refusal = await Assert.ThrowsAsync<MaterialTraceabilityConflictException>(
            () => scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-expired"));
        Assert.Contains("expired certificate", refusal.Message);

        var accepted = await scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-expired-override",
            overrideReason: "Customer accepted in writing; renewal in progress, ref SABER-8811.");

        Assert.Equal(MaterialLotComplianceStates.CertificateExpired, accepted.ComplianceStateAtDeclaration);

        await using var verify = scenario.Context();
        var consumption = Assert.Single(await verify.MaterialLotConsumptions.ToListAsync());
        Assert.Equal("qa", consumption.ComplianceOverrideBy);
        Assert.Contains("SABER-8811", consumption.ComplianceOverrideReason);
    }

    [Fact]
    public async Task A_renewed_certificate_makes_the_lot_compliant_again_with_no_workflow()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await scenario.AddCertificateAsync(lotId, MaterialCertificateTypes.CertificateOfConformity,
            expiresOn: today.AddDays(-1), number: "COC-1");
        await scenario.AddCertificateAsync(lotId, MaterialCertificateTypes.CertificateOfConformity,
            expiresOn: today.AddDays(365), number: "COC-2");

        // The governing expiry for a type is its LATEST, so uploading the renewal is the whole
        // remedy. Nobody has to remember to retire the lapsed row.
        var declaration = await scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-renewed");
        Assert.Equal(MaterialLotComplianceStates.Compliant, declaration.ComplianceStateAtDeclaration);
    }

    [Fact]
    public async Task A_valid_certificate_of_one_type_does_not_cover_a_lapsed_certificate_of_another()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await scenario.AddCertificateAsync(lotId, MaterialCertificateTypes.CertificateOfOrigin,
            expiresOn: today.AddDays(400), number: "COO-1");
        await scenario.AddCertificateAsync(lotId, MaterialCertificateTypes.Saber,
            expiresOn: today.AddDays(-5), number: "SABER-1");

        await Assert.ThrowsAsync<MaterialTraceabilityConflictException>(
            () => scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-mixed"));
    }

    [Fact]
    public async Task A_lot_with_no_certificate_is_reported_as_a_gap_but_is_not_blocked()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        var declaration = await scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-no-cert");
        Assert.Equal(MaterialLotComplianceStates.NoCertificate, declaration.ComplianceStateAtDeclaration);

        await using var verify = scenario.Context();
        var trace = await scenario.Service(verify).GetLotAsync(scenario.BusinessUnitId, lotId);
        Assert.Contains(trace.Gaps, gap => gap.Kind == MaterialTraceGapKinds.NoCertificate);
    }

    [Fact]
    public async Task An_override_reason_on_a_compliant_lot_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        await scenario.AddCertificateAsync(lotId, MaterialCertificateTypes.CertificateOfConformity,
            expiresOn: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(90), number: "COC-1");

        // An override that is always present stops meaning anything.
        await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-unneeded-override",
                overrideReason: "just in case"));
    }

    [Fact]
    public async Task Certificate_expiry_is_computed_live_and_is_never_stored()
    {
        var today = new DateOnly(2026, 8, 9);
        var certificate = new MaterialLotCertificate { ExpiresOn = new DateOnly(2026, 8, 8) };
        Assert.Equal(CertificateExpiryStates.Expired, certificate.ExpiryState(today));
        Assert.Equal(CertificateExpiryStates.ExpiringSoon,
            new MaterialLotCertificate { ExpiresOn = today.AddDays(10) }.ExpiryState(today));
        Assert.Equal(CertificateExpiryStates.Valid,
            new MaterialLotCertificate { ExpiresOn = today.AddDays(90) }.ExpiryState(today));
        // A certificate of origin does not expire. Absent must never read as "unknown, assume fine"
        // and must never read as "expired" either.
        Assert.Equal(CertificateExpiryStates.NotApplicable,
            new MaterialLotCertificate { ExpiresOn = null }.ExpiryState(today));
    }

    // ============================================================ FR-MTR-03: trace, and its gaps

    [Fact]
    public async Task Where_from_reaches_the_supplier_purchase_order_that_bought_the_material()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        await using var verify = scenario.Context();
        var trace = await scenario.Service(verify).GetLotAsync(scenario.BusinessUnitId, lotId);

        Assert.Equal(scenario.PurchaseOrderId, trace.SupplierPurchaseOrderId);
        Assert.Equal("QA Supplier", trace.SupplierName);
        Assert.Equal(scenario.GoodsReceiptId, trace.GoodsReceiptId);
        Assert.NotNull(trace.PurchaseOrderNumber);
        Assert.NotNull(trace.RfqNumber);
    }

    /// <summary>
    /// The whole point of the where-used screen. The order shipped 4 units; only 1 states a lot, so
    /// 3 are untraceable and the trace says so out loud rather than showing a tidy list of one lot.
    /// </summary>
    [Fact]
    public async Task Where_used_reports_shipped_quantity_that_no_lot_explains()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        await scenario.ShipAsync(quantity: 4m);
        await scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-partial");

        await using var verify = scenario.Context();
        var trace = await scenario.Service(verify)
            .GetOrderTraceAsync(scenario.BusinessUnitId, TraceabilityScenario.OrderId);

        var line = Assert.Single(trace.Lines);
        Assert.Equal(4m, line.ShippedQuantity);
        Assert.Equal(1m, line.DeclaredQuantity);
        Assert.Equal(3m, line.UntracedQuantity);

        var gap = Assert.Single(trace.Gaps, g => g.Kind == MaterialTraceGapKinds.UndeclaredFulfilment);
        Assert.Equal(3m, gap.Quantity);
    }

    /// <summary>
    /// Membership is the declaration, never a join. A second lot of the same product sitting in the
    /// same warehouse is not evidence that it shipped, and must not appear.
    /// </summary>
    [Fact]
    public async Task Where_used_lists_only_the_lot_the_fulfilment_declared()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 4m, receiptNumber: "GRN-A");
        await scenario.ReceiveAsync(quantity: 4m, receiptNumber: "GRN-B");
        var lots = await scenario.LotIdsAsync();
        Assert.Equal(2, lots.Count);

        await scenario.ShipAsync(quantity: 4m);
        await scenario.DeclareAsync(lots[0], quantity: 4m, key: "declare-first-lot");

        await using var verify = scenario.Context();
        var trace = await scenario.Service(verify)
            .GetOrderTraceAsync(scenario.BusinessUnitId, TraceabilityScenario.OrderId);

        var line = Assert.Single(trace.Lines);
        var used = Assert.Single(line.Lots);
        Assert.Equal(lots[0], used.MaterialLotId);
        Assert.Equal(0m, line.UntracedQuantity);
    }

    /// <summary>
    /// The recall query the business actually runs: which customers hold material we have since
    /// recalled. It is answerable only because the fulfilment declared its lot.
    /// </summary>
    [Fact]
    public async Task Where_used_flags_a_lot_that_shipped_and_was_recalled_afterwards()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        await scenario.ShipAsync(quantity: 4m);
        await scenario.DeclareAsync(lotId, quantity: 4m, key: "declare-shipped");
        await scenario.QuarantineAsync(lotId, version: 2, reasonCode: MaterialLotQuarantineReasons.SupplierRecall);

        await using var verify = scenario.Context();
        var service = scenario.Service(verify);

        var orderTrace = await service.GetOrderTraceAsync(scenario.BusinessUnitId, TraceabilityScenario.OrderId);
        Assert.Contains(orderTrace.Gaps, g => g.Kind == MaterialTraceGapKinds.ShippedLotQuarantined);

        var lotTrace = await service.GetLotAsync(scenario.BusinessUnitId, lotId);
        Assert.Contains(lotTrace.Gaps, g => g.Kind == MaterialTraceGapKinds.ShippedLotQuarantined);
        Assert.Single(lotTrace.FulfilledInto);
    }

    [Fact]
    public async Task Declaring_more_than_the_lot_holds_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();
        await scenario.DeclareAsync(lotId, quantity: 8m, key: "declare-all");

        await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-over"));
    }

    [Fact]
    public async Task Declaring_a_lot_against_a_line_that_belongs_to_another_order_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        var failure = await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.DeclareAsync(lotId, quantity: 1m, key: "declare-foreign-line",
                orderItemId: TraceabilityScenario.OrderItemId + 500));

        Assert.Contains("does not belong to order", failure.Message);
    }

    // ============================================================ tenant isolation

    [Fact]
    public async Task A_lot_belonging_to_another_tenant_is_not_readable()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        await using var intruder = scenario.Context(scenario.OtherBusinessUnitId);
        await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(
            () => scenario.Service(intruder).GetLotAsync(scenario.OtherBusinessUnitId, lotId));
    }

    [Fact]
    public async Task Another_tenant_cannot_quarantine_this_tenants_lot()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);
        var lotId = await scenario.SingleLotIdAsync();

        await using (var intruder = scenario.Context(scenario.OtherBusinessUnitId))
        {
            await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(() =>
                scenario.Service(intruder).QuarantineAsync(new QuarantineLotCommand(
                    scenario.OtherBusinessUnitId, lotId, 1, MaterialLotQuarantineReasons.QualityHold,
                    "cross tenant", "intruder", "corr-x", "key-x")));
        }

        await using var verify = scenario.Context();
        Assert.Equal(MaterialLotStatuses.Available,
            await verify.MaterialLots.Where(x => x.Id == lotId).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task A_lot_search_never_returns_another_tenants_material()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(quantity: 8m);

        await using var intruder = scenario.Context(scenario.OtherBusinessUnitId);
        var results = await scenario.Service(intruder)
            .SearchLotsAsync(scenario.OtherBusinessUnitId, new LotSearchQuery());
        Assert.Empty(results);
    }

    // ============================================================ idempotency of the receipt itself

    /// <summary>
    /// The lot declaration is part of the receipt's business identity. Without it in the hash, a
    /// retry under the same key that named a DIFFERENT batch would be accepted as a replay of the
    /// first — returning success while the record described material that never arrived.
    /// </summary>
    [Fact]
    public async Task A_receipt_replay_that_names_a_different_lot_number_is_refused()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.SetTrackingAsync(batch: true, serial: false);
        await scenario.ReceiveAsync(quantity: 4m, key: "receipt-1", receiptNumber: "GRN-1",
            declaration: new ReceiptLotDeclaration(LotNumber: "BATCH-A"));

        await Assert.ThrowsAsync<ProcurementConflictException>(
            () => scenario.ReceiveAsync(quantity: 4m, key: "receipt-1", receiptNumber: "GRN-1",
                declaration: new ReceiptLotDeclaration(LotNumber: "BATCH-B")));

        await using var verify = scenario.Context();
        Assert.Equal("BATCH-A", await verify.MaterialLots.Select(x => x.LotNumber).SingleAsync());
    }
}

/// <summary>
/// Walks the real procurement chain to an issued purchase order, then posts receipts through the
/// real <c>PostGoodsReceiptAsync</c> — so the lots under test are the lots a receipt actually
/// produces, not fixtures inserted to look like them. A sales order and one order line are seeded
/// alongside so the forward link has somewhere real to point.
/// </summary>
internal sealed class TraceabilityScenario : IDisposable
{
    public const long OrderId = 96_500;
    public const long OrderItemId = 96_510;
    public const long OrderStatusId = 96_520;
    public const long ShipmentStatusId = 96_521;

    private readonly ProcurementScenario _procurement = new();
    private long _purchaseOrderId;
    private long _purchaseOrderVersion;
    private long _purchaseOrderLineId;
    private int _shipmentSequence;

    public TraceabilityScenario()
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
        seed.Set<Order>().Add(new Order
        {
            Id = OrderId, OrderNo = "SO-TRACE-1", CustomerId = BusinessUnitId, BusinessUnitId = BusinessUnitId,
            StatusId = OrderStatusId, TotalAmount = 80m, OrderDate = DateTime.UtcNow, CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow, IsActive = true
        });
        seed.Set<OrderItem>().Add(new OrderItem
        {
            Id = OrderItemId, OrderId = OrderId, ProductId = ProcurementTestData.Product,
            WarehouseId = ProcurementTestData.Warehouse, Quantity = 8m, UnitPrice = 10m, Discount = 0m,
            TaxAmount = 0m, TotalAmount = 80m, CreatedBy = "qa", CreatedDate = DateTime.UtcNow, IsActive = true
        });
        seed.SaveChanges();
    }

    public long BusinessUnitId => _procurement.BusinessUnitId;
    public long OtherBusinessUnitId => _procurement.OtherBusinessUnitId;
    public long PurchaseOrderId => _purchaseOrderId;
    public long GoodsReceiptId { get; private set; }

    public ErpRfqAutomationContext Context(long? tenant = null) => _procurement.Context(tenant);

    public MaterialTraceabilityService Service(ErpRfqAutomationContext context)
        => new(context, new StockLedgerService(context), new InventoryAvailabilityService(context));

    public async Task SetTrackingAsync(bool batch, bool serial)
    {
        await using var context = Context();
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.BatchTracking = batch;
        product.SerialTracking = serial;
        await context.SaveChangesAsync();
    }

    /// <summary>Sets what the purchase order says was ordered, so the receipt has something to reconcile against.</summary>
    public async Task SetOrderedOriginAsync(string countryOfOrigin)
    {
        await EnsurePurchaseOrderAsync();
        await using var context = Context();
        var line = await context.SupplierPurchaseOrderLines.SingleAsync(x => x.Id == _purchaseOrderLineId);
        line.CountryOfOrigin = countryOfOrigin;
        await context.SaveChangesAsync();
    }

    public async Task<GoodsReceiptResult> ReceiveAsync(
        decimal quantity,
        ReceiptLotDeclaration? declaration = null,
        string? key = null,
        string? receiptNumber = null)
    {
        await EnsurePurchaseOrderAsync();
        key ??= $"receipt-{Guid.NewGuid():N}";
        receiptNumber ??= $"GRN-{Guid.NewGuid():N}"[..12];

        await using var context = Context();
        var service = new ProcurementApplicationService(context);
        var result = await service.PostGoodsReceiptAsync(new PostGoodsReceiptCommand(
            BusinessUnitId, _purchaseOrderId, ProcurementTestData.Warehouse, receiptNumber,
            DateTime.UtcNow, _purchaseOrderVersion,
            [new PostGoodsReceiptLine(_purchaseOrderLineId, quantity, declaration)],
            key, "qa", $"corr-{key}"));
        GoodsReceiptId = result.Id;
        _purchaseOrderVersion++;
        return result;
    }

    public async Task<long> SingleLotIdAsync()
    {
        await using var context = Context();
        return await context.MaterialLots.Select(x => x.Id).SingleAsync();
    }

    public async Task<IReadOnlyList<long>> LotIdsAsync()
    {
        await using var context = Context();
        return await context.MaterialLots.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
    }

    public async Task<LotQuarantineResult> QuarantineAsync(
        long lotId, long version, string? reasonCode = null, string? key = null)
    {
        await using var context = Context();
        return await Service(context).QuarantineAsync(new QuarantineLotCommand(
            BusinessUnitId, lotId, version, reasonCode ?? MaterialLotQuarantineReasons.SupplierRecall,
            "Supplier recall notice RC-4417 covers this batch.", "qa", "corr-quarantine",
            key ?? $"quarantine-{lotId}-{version}"));
    }

    public async Task<LotQuarantineResult> ReleaseAsync(long lotId, long version, string reason)
    {
        await using var context = Context();
        return await Service(context).ReleaseAsync(new ReleaseLotCommand(
            BusinessUnitId, lotId, version, reason, "qa", "corr-release", $"release-{lotId}-{version}"));
    }

    public async Task<LotConsumptionResult> DeclareAsync(
        long lotId, decimal quantity, string key, string? overrideReason = null, long? orderItemId = null)
    {
        await using var context = Context();
        return await Service(context).DeclareConsumptionAsync(new DeclareLotConsumptionCommand(
            BusinessUnitId, lotId, OrderId, orderItemId ?? OrderItemId, null, quantity, overrideReason,
            "qa", "corr-declare", key));
    }

    /// <summary>Certificates are inserted directly: the upload path needs the inspection and
    /// evidence-storage stack, which is covered by its own suites.</summary>
    public async Task AddCertificateAsync(
        long lotId, string certificateType, DateOnly? expiresOn, string? number = null)
    {
        await using var context = Context();
        var attachment = new Attachment
        {
            ParentType = "MaterialLotCertificate", ParentId = lotId,
            FileName = $"{certificateType}.pdf", FilePath = $"evidence://{Guid.NewGuid():N}",
            ContentSha256 = new string('a', 64), CreatedOn = DateTime.UtcNow,
        };
        context.Attachments.Add(attachment);
        await context.SaveChangesAsync();
        context.MaterialLotCertificates.Add(new MaterialLotCertificate
        {
            BusinessUnitId = BusinessUnitId,
            MaterialLotId = lotId,
            CertificateType = certificateType,
            CertificateNumber = number,
            ExpiresOn = expiresOn,
            AttachmentId = attachment.Id,
            ContentSha256 = new string('a', 64),
            FileName = $"{certificateType}.pdf",
            UploadedOn = DateTime.UtcNow,
            UploadedBy = "qa",
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Records a despatch against the sales order — the reconciliation evidence the
    /// where-used trace measures declared lot quantity against.</summary>
    public async Task ShipAsync(decimal quantity)
    {
        await using var context = Context();
        var shipment = new Shipment
        {
            Id = 96_600 + _shipmentSequence++,
            ShipmentNo = $"DN-{_shipmentSequence}",
            OrderId = OrderId,
            BusinessUnitId = BusinessUnitId,
            StatusId = ShipmentStatusId,
            ShipmentDate = DateTime.UtcNow,
            CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow,
            IsActive = true,
        };
        shipment.ShipmentItems.Add(new ShipmentItem
        {
            OrderItemId = OrderItemId, Quantity = quantity, CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow, IsActive = true
        });
        context.Shipments.Add(shipment);
        await context.SaveChangesAsync();
    }

    private async Task EnsurePurchaseOrderAsync()
    {
        if (_purchaseOrderId != 0) return;
        var issued = await _procurement.CreatePurchaseOrderAsync("trace", quantity: 8m);
        _purchaseOrderId = issued.Id;
        _purchaseOrderVersion = issued.Version;
        _purchaseOrderLineId = await _procurement.PurchaseOrderLineIdAsync(issued.Id);
    }

    public void Dispose() => _procurement.Dispose();
}

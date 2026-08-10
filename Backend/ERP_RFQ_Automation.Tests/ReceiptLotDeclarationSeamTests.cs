using System.Text.Json;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The seam between two product switches and the screen that has to satisfy them.
///
/// <para><c>Product.BatchTracking</c> and <c>Product.SerialTracking</c> are two toggles in the
/// product dialog. Turning either on made <c>MaterialLotRecorder</c> refuse every goods receipt for
/// that product, inside the receipt transaction, naming a field the receipt screen did not offer —
/// so a product could be put into a state from which it could never be received. The recorder was
/// right; the screen was missing, and so was the tracking mode it needed to know which fields to
/// ask for.</para>
///
/// <para>These tests pin both halves: the purchase-order line tells the screen what will be
/// demanded, and the receipt wire format carries the answer through to the lot. All lot numbers,
/// serials and country codes below are obviously synthetic.</para>
/// </summary>
public sealed class ReceiptLotDeclarationSeamTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, false, MaterialLotTrackingModes.Untracked)]
    [InlineData(true, false, MaterialLotTrackingModes.Lot)]
    [InlineData(false, true, MaterialLotTrackingModes.Serial)]
    [InlineData(true, true, MaterialLotTrackingModes.Serial)]
    public async Task A_purchase_order_line_tells_the_receipt_screen_what_the_recorder_will_demand(
        bool batchTracking, bool serialTracking, string expectedMode)
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("tracking-mode", quantity: 4m);
        await SetTrackingAsync(fixture, batchTracking, serialTracking);

        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));

        var line = Assert.Single(workbench.PurchaseOrders.Single(x => x.Id == purchaseOrder.Id).Lines);
        Assert.Equal(expectedMode, line.TrackingMode);
    }

    /// <summary>
    /// The JSON the receipt dialog now posts, parsed by the same contract the controller binds, and
    /// carried all the way to the lot. Drop <c>lot</c> from the wire format and this fails at the
    /// first assertion; drop the declaration from the command and it fails at the receipt.
    /// </summary>
    [Fact]
    public async Task The_receipt_wire_format_carries_the_declaration_the_recorder_demands()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("batch-receipt", quantity: 4m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        await SetTrackingAsync(fixture, batchTracking: true, serialTracking: false);
        await SetOrderedOriginAsync(fixture, "XA");

        var body = $$"""
        {
          "purchaseOrderId": {{purchaseOrder.Id}},
          "warehouseId": {{ProcurementTestData.Warehouse}},
          "receiptNumber": "GR-SYNTHETIC-1",
          "receivedOn": "2026-08-10T09:00:00.000Z",
          "expectedPurchaseOrderVersion": 3,
          "lines": [
            {
              "purchaseOrderLineId": {{lineId}},
              "quantity": 4,
              "lot": {
                "lotNumber": "SYNTHETIC-BATCH-0001",
                "countryOfOrigin": "XB",
                "supplierBatchReference": "SYNTHETIC-SUPPLIER-REF-1",
                "expiryDate": "2027-03-31"
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<PostGoodsReceiptRequest>(body, Wire)!;
        var declaration = Assert.Single(request.Lines).Lot;
        Assert.NotNull(declaration);
        Assert.Equal("SYNTHETIC-BATCH-0001", declaration!.LotNumber);
        Assert.Equal("XB", declaration.CountryOfOrigin);
        Assert.Equal(new DateOnly(2027, 3, 31), declaration.ExpiryDate);

        await fixture.Execute(service => service.PostGoodsReceiptAsync(new PostGoodsReceiptCommand(
            fixture.BusinessUnitId, request.PurchaseOrderId, request.WarehouseId, request.ReceiptNumber,
            request.ReceivedOn, request.ExpectedPurchaseOrderVersion, request.Lines,
            "receipt-wire-format", "qa", "corr-receipt-wire-format")));

        await using var verify = fixture.Context();
        var lot = await verify.MaterialLots.SingleAsync();
        Assert.Equal("SYNTHETIC-BATCH-0001", lot.LotNumber);
        Assert.Equal(MaterialLotTrackingModes.Lot, lot.TrackingMode);
        // First-expiring-first-out has a writer at last; without one every lot sorted by receipt
        // date and FEFO was FIFO wearing its name.
        Assert.Equal(new DateOnly(2027, 3, 31), lot.ExpiryDate);
        // The customs origin gap can now actually fire: what arrived is stated separately from what
        // was ordered, so the comparison has two values to compare.
        Assert.Equal("XA", lot.OrderedCountryOfOrigin);
        Assert.Equal("XB", lot.CountryOfOrigin);
        Assert.True(lot.OriginDiffersFromOrder);
    }

    /// <summary>
    /// A serial-tracked line takes one serial per received unit, and the count is the guard. The
    /// screen mirrors this rule, but the server remains the authority.
    /// </summary>
    [Fact]
    public async Task A_serial_tracked_line_receives_one_lot_per_declared_serial()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("serial-receipt", quantity: 4m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        await SetTrackingAsync(fixture, batchTracking: false, serialTracking: true);

        var request = JsonSerializer.Deserialize<PostGoodsReceiptRequest>($$"""
        {
          "purchaseOrderId": {{purchaseOrder.Id}},
          "warehouseId": {{ProcurementTestData.Warehouse}},
          "receiptNumber": "GR-SYNTHETIC-2",
          "receivedOn": "2026-08-10T09:00:00.000Z",
          "expectedPurchaseOrderVersion": 3,
          "lines": [
            {
              "purchaseOrderLineId": {{lineId}},
              "quantity": 3,
              "lot": { "serialNumbers": ["SYNTH-SN-1", "SYNTH-SN-2", "SYNTH-SN-3"] }
            }
          ]
        }
        """, Wire)!;

        await fixture.Execute(service => service.PostGoodsReceiptAsync(new PostGoodsReceiptCommand(
            fixture.BusinessUnitId, request.PurchaseOrderId, request.WarehouseId, request.ReceiptNumber,
            request.ReceivedOn, request.ExpectedPurchaseOrderVersion, request.Lines,
            "receipt-serials", "qa", "corr-receipt-serials")));

        await using var verify = fixture.Context();
        var lots = await verify.MaterialLots.OrderBy(x => x.LotNumber).ToListAsync();
        Assert.Equal(["SYNTH-SN-1", "SYNTH-SN-2", "SYNTH-SN-3"], lots.Select(x => x.LotNumber));
        Assert.All(lots, lot => Assert.Equal(1m, lot.QuantityReceived));
        Assert.All(lots, lot => Assert.Equal(MaterialLotTrackingModes.Serial, lot.TrackingMode));
    }

    /// <summary>
    /// The refusal that used to be unreachable from any screen. It stays exactly as it is — the
    /// screen is what changed — and it must arrive at the operator as a stated instruction.
    /// </summary>
    [Fact]
    public async Task A_receipt_that_declares_nothing_for_a_tracked_line_is_still_refused()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("undeclared-receipt", quantity: 4m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        await SetTrackingAsync(fixture, batchTracking: true, serialTracking: false);

        var refusal = await Assert.ThrowsAsync<MaterialTraceabilityValidationException>(() =>
            fixture.Execute(service => service.PostGoodsReceiptAsync(
                fixture.Receipt(purchaseOrder.Id, lineId, 4m, 3, "undeclared", "GR-SYNTHETIC-3"))));

        Assert.Contains("batch-tracked", refusal.Message, StringComparison.Ordinal);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.MaterialLots.ToListAsync());
        Assert.Empty(await verify.GoodsReceipts.ToListAsync());
    }

    private static async Task SetTrackingAsync(
        ProcurementScenario fixture, bool batchTracking, bool serialTracking)
    {
        await using var context = fixture.Context();
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.BatchTracking = batchTracking;
        product.SerialTracking = serialTracking;
        await context.SaveChangesAsync();
    }

    private static async Task SetOrderedOriginAsync(ProcurementScenario fixture, string countryOfOrigin)
    {
        await using var context = fixture.Context();
        var line = await context.SupplierPurchaseOrderLines.SingleAsync();
        line.CountryOfOrigin = countryOfOrigin;
        await context.SaveChangesAsync();
    }
}

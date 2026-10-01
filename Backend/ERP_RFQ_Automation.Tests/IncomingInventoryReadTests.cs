using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.InboundLogistics;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Traceability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class IncomingInventoryReadTests
{
    [Fact]
    public async Task Incoming_uses_recorded_purchase_order_link_and_preserves_unlinked_supply()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(2m, receiptNumber: "GRN-READ-001");
        await using var db = scenario.Context();
        var linked = await db.IncomingInventory.SingleAsync();
        db.IncomingInventory.Add(new IncomingInventory
        {
            BusinessUnitId = scenario.BusinessUnitId, ProductId = linked.ProductId,
            WarehouseId = linked.WarehouseId, OrderedQuantity = 4m,
            ExpectedOn = linked.ExpectedOn.AddDays(1), Status = IncomingInventoryStatus.Ordered,
            SourceType = "Legacy", SourceId = "legacy-source",
        });
        await db.SaveChangesAsync();
        var po = await db.SupplierPurchaseOrders.SingleAsync();
        var supplier = await db.Suppliers.SingleAsync(x => x.Id == po.SupplierId);
        var receipt = await db.GoodsReceipts.SingleAsync();
        receipt.SupplierInvoiceNumber = "SUP-INV-001";
        receipt.BillOfLadingNumber = "BL-001";
        var poLine = await db.SupplierPurchaseOrderLines.SingleAsync();
        db.SupplierShipments.Add(new SupplierShipment
        {
            BusinessUnitId = scenario.BusinessUnitId, SupplierPurchaseOrderId = po.Id,
            ShipmentNumber = "SHIP-001", TrackingReference = "AIR-TRACK-001", IdempotencyKey = "ship-read-001",
            RequestHash = "read-fixture", CreatedBy = "qa", CreatedOn = DateTime.UtcNow,
            Lines = [new SupplierShipmentLine
            {
                BusinessUnitId = scenario.BusinessUnitId, SupplierPurchaseOrderLineId = poLine.Id,
                ProductId = linked.ProductId, ShippedQuantity = 1m,
            }],
        });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await Controller(db, scenario.BusinessUnitId).Incoming(null, default));
        var rows = JsonSerializer.SerializeToElement(result.Value).EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(po.PurchaseOrderNumber, rows[0].GetProperty("purchaseOrderNumber").GetString());
        Assert.Equal(supplier.Name, rows[0].GetProperty("supplierName").GetString());
        Assert.Equal(linked.SourceId, rows[0].GetProperty("sourceReference").GetString());
        Assert.NotEqual(linked.SourceId, rows[0].GetProperty("purchaseOrderNumber").GetString());
        Assert.Equal("SUP-INV-001", rows[0].GetProperty("supplierInvoiceNumbers")[0].GetString());
        Assert.Equal("BL-001", rows[0].GetProperty("billOfLadingNumbers")[0].GetString());
        Assert.Equal("AIR-TRACK-001", rows[0].GetProperty("trackingReferences")[0].GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("purchaseOrderNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("supplierName").ValueKind);
        Assert.Equal(0, rows[1].GetProperty("supplierInvoiceNumbers").GetArrayLength());
        Assert.Equal(0, rows[1].GetProperty("trackingReferences").GetArrayLength());

        var otherTenant = Assert.IsType<OkObjectResult>(await Controller(db, scenario.OtherBusinessUnitId).Incoming(null, default));
        Assert.Equal(0, JsonSerializer.SerializeToElement(otherTenant.Value).GetArrayLength());
    }

    [Fact]
    public async Task Receipt_list_exposes_the_persisted_goods_receipt_number()
    {
        using var scenario = new TraceabilityScenario();
        await scenario.ReceiveAsync(2m, receiptNumber: "GRN-READ-002");
        await using var db = scenario.Context();
        var receipt = await db.GoodsReceipts.SingleAsync();
        receipt.SupplierInvoiceNumber = "SUP-INV-002";
        receipt.BillOfLadingNumber = "BL-002";
        await db.SaveChangesAsync();
        var lots = await scenario.Service(db).SearchLotsAsync(scenario.BusinessUnitId, new LotSearchQuery());
        var lot = Assert.Single(lots);
        Assert.Equal("GRN-READ-002", lot.ReceiptNumber);
        Assert.Equal("SUP-INV-002", lot.SupplierInvoiceNumber);
        Assert.Equal("BL-002", lot.BillOfLadingNumber);
        var detail = await scenario.Service(db).GetLotAsync(scenario.BusinessUnitId, lot.Id);
        Assert.Equal(lot.SupplierInvoiceNumber, detail.SupplierInvoiceNumber);
        Assert.Equal(lot.BillOfLadingNumber, detail.BillOfLadingNumber);
        Assert.Empty(await scenario.Service(db).SearchLotsAsync(scenario.OtherBusinessUnitId, new LotSearchQuery()));
    }

    private static InventoryIntelligenceController Controller(ErpRfqAutomationContext db, long tenant)
    {
        var controller = new InventoryIntelligenceController(db, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("businessUnitId", tenant.ToString())], "test")),
        } };
        return controller;
    }
}

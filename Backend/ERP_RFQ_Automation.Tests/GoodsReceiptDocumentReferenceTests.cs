using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class GoodsReceiptDocumentReferenceTests
{
    [Fact]
    public async Task Receipt_documents_are_persisted_trimmed_and_part_of_immutable_receipt_identity()
    {
        using var fixture = new ProcurementScenario();
        var po = await fixture.CreatePurchaseOrderAsync("receipt-documents", 8m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(po.Id);
        var command = fixture.Receipt(po.Id, lineId, 3m, 3, "receipt-documents", "GR-DOCUMENTS") with
        {
            SupplierInvoiceNumber = "  INV-SUP-42  ",
            BillOfLadingNumber = "  BL-SEA-17  "
        };
        var first = await fixture.Execute(service => service.PostGoodsReceiptAsync(command));
        var replay = await fixture.Execute(service => service.PostGoodsReceiptAsync(command with
        {
            SupplierInvoiceNumber = "INV-SUP-42", BillOfLadingNumber = "BL-SEA-17"
        }));
        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Id);

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(command with { SupplierInvoiceNumber = "INV-DIFFERENT" })));
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(command with
            {
                BillOfLadingNumber = "BL-DIFFERENT", IdempotencyKey = "receipt-documents-new-key"
            })));

        await using var check = fixture.Context();
        var receipt = await check.GoodsReceipts.SingleAsync();
        Assert.Equal("INV-SUP-42", receipt.SupplierInvoiceNumber);
        Assert.Equal("BL-SEA-17", receipt.BillOfLadingNumber);
        await fixture.AssertReceiptStateAsync(1, 1, 3m, ProcurementTestData.InitialOnHand + 3m);
    }

    [Fact]
    public async Task Omitted_and_blank_document_references_remain_compatible_and_store_null()
    {
        using var fixture = new ProcurementScenario();
        var po = await fixture.CreatePurchaseOrderAsync("receipt-no-documents", 8m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(po.Id);
        var command = fixture.Receipt(po.Id, lineId, 3m, 3, "receipt-no-documents", "GR-NO-DOCUMENTS");
        await fixture.Execute(service => service.PostGoodsReceiptAsync(command));
        var replay = await fixture.Execute(service => service.PostGoodsReceiptAsync(command with
        {
            SupplierInvoiceNumber = "  ", BillOfLadingNumber = "\t"
        }));
        Assert.True(replay.Replayed);
        await using var check = fixture.Context();
        var receipt = await check.GoodsReceipts.SingleAsync();
        Assert.Null(receipt.SupplierInvoiceNumber);
        Assert.Null(receipt.BillOfLadingNumber);
    }

    [Theory]
    [InlineData(true, "Supplier invoice number")]
    [InlineData(false, "Bill of lading number")]
    public async Task Overlong_document_reference_is_refused_before_any_stock_changes(bool invoice, string label)
    {
        using var fixture = new ProcurementScenario();
        var command = fixture.Receipt(1, 1, 1m, 1, "overlong-reference", "GR-INVALID") with
        {
            SupplierInvoiceNumber = invoice ? new string('I', 101) : null,
            BillOfLadingNumber = invoice ? null : new string('B', 101)
        };
        var error = await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(command)));
        Assert.Contains(label, error.Message);
        await using var check = fixture.Context();
        Assert.Empty(await check.GoodsReceipts.ToListAsync());
        Assert.Empty(await check.InventoryMovements.ToListAsync());
    }
}

using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-COM-01. A customer purchase order arrives as a document, and until now the only way to get
/// one into Nexora was to retype it — PO number, date and every line — into a form that helpfully
/// pre-filled the quantities and prices from OUR OWN quotation. That made the quote-versus-PO
/// discrepancy check compare the system against itself.
///
/// <para>These tests hold the two properties that make the feature worth having: the values come
/// out of the BUYER's file, and a document that cannot be read says so instead of producing an
/// empty purchase order that agrees with everything.</para>
/// </summary>
public sealed class CustomerPurchaseOrderDocumentTests
{
    // ---- spreadsheet ------------------------------------------------------

    [Fact]
    public async Task A_spreadsheet_purchase_order_yields_its_number_date_and_every_line()
    {
        using var harness = new PoDocumentHarness();

        var extraction = await harness.ExtractAsync("customer-po.xlsx", SpreadsheetPurchaseOrder());

        Assert.Equal("PO-77120", extraction.ExternalPoNumber);
        Assert.Equal(new DateTime(2026, 6, 14), extraction.PoDate);
        Assert.Equal(2, extraction.Lines.Count);

        var first = extraction.Lines[0];
        Assert.Equal("Ball valve DN50 PN16 stainless", first.Description);
        Assert.Equal(12m, first.OrderedQuantity);
        Assert.Equal(235.50m, first.UnitPrice);
        Assert.Equal("EA", first.UnitOfMeasure);
        // "Item Code" is the BUYER's code, not a manufacturer part number. Recording one under
        // the other's name makes the three-key match wrong rather than merely absent.
        Assert.Equal("IC-9001", first.CustomerItemCode);
        Assert.Null(first.ManufacturerPartNumber);
        Assert.Equal("Velan", first.ManufacturerName);
        Assert.Empty(first.ReviewReasons);

        var second = extraction.Lines[1];
        Assert.Equal("Gate valve DN80 PN16", second.Description);
        Assert.Equal(4m, second.OrderedQuantity);
        Assert.Equal(410m, second.UnitPrice);
        Assert.False(extraction.RequiresReview);
    }

    [Fact]
    public async Task A_quantity_the_document_states_unreadably_is_flagged_rather_than_defaulted()
    {
        using var harness = new PoDocumentHarness();

        // "1.234" is 1,234 under EU convention and 1.234 under US. The two readings differ a
        // thousandfold; picking one silently is how a thousandfold over-order gets invoiced.
        var extraction = await harness.ExtractAsync("customer-po.csv", Csv(
            "Purchase Order",
            "PO Number:,PO-51009",
            "PO Date:,2026-06-14",
            "",
            "Item Code,Description,Qty,UOM,Unit Price",
            "IC-1,Ball valve DN50,1.234,EA,235.50"));

        var line = Assert.Single(extraction.Lines);
        Assert.Null(line.OrderedQuantity);
        Assert.Equal("1.234", line.QuantityText);
        Assert.Contains(line.ReviewReasons, reason => reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        Assert.True(extraction.RequiresReview);
    }

    // ---- Word -------------------------------------------------------------

    [Fact]
    public async Task A_word_purchase_order_yields_its_number_date_and_every_line()
    {
        using var harness = new PoDocumentHarness();

        var extraction = await harness.ExtractAsync("customer-po.docx", WordPurchaseOrder());

        Assert.Equal("PO-88450", extraction.ExternalPoNumber);
        Assert.Equal(new DateTime(2026, 6, 14), extraction.PoDate);
        Assert.Equal(2, extraction.Lines.Count);

        var first = extraction.Lines[0];
        Assert.Equal("Pressure transmitter 0-16 bar", first.Description);
        Assert.Equal(6m, first.OrderedQuantity);
        Assert.Equal(1150m, first.UnitPrice);
        Assert.Equal("PT-4400", first.ManufacturerPartNumber);
        Assert.Equal("Rosemount", first.ManufacturerName);

        var second = extraction.Lines[1];
        Assert.Equal("Temperature transmitter", second.Description);
        Assert.Equal(3m, second.OrderedQuantity);
        Assert.Equal(980m, second.UnitPrice);
    }

    // ---- honest failure ---------------------------------------------------

    [Fact]
    public async Task A_document_with_no_readable_line_items_fails_instead_of_producing_an_empty_purchase_order()
    {
        using var harness = new PoDocumentHarness();

        var error = await Assert.ThrowsAsync<CustomerPurchaseOrderDocumentException>(() =>
            harness.ExtractAsync("meeting-notes.csv", Csv(
                "Section,Narrative,Owner",
                "Kick-off,Discussed the framework agreement,Procurement",
                "Next steps,Await revised commercial terms,Sales")));

        Assert.Equal(CustomerPurchaseOrderDocumentErrorCodes.NoLineItems, error.Code);
        Assert.Contains("could not identify a line-item table", error.Message);

        // Nothing was left behind: no attachment claiming a PO document was read, and above all
        // no purchase order. An empty PO agrees with every quotation ever raised.
        harness.Context.ChangeTracker.Clear();
        Assert.Empty(await harness.Context.Attachments.ToListAsync());
        Assert.Empty(await harness.Context.CustomerPurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task A_file_the_reader_cannot_decode_fails_honestly_with_the_readers_own_reason()
    {
        using var harness = new PoDocumentHarness();

        var error = await Assert.ThrowsAsync<CustomerPurchaseOrderDocumentException>(() =>
            harness.ExtractAsync("scan.xlsx", Encoding.UTF8.GetBytes("this is not a workbook")));

        Assert.Equal(CustomerPurchaseOrderDocumentErrorCodes.Unreadable, error.Code);
        Assert.Empty(await harness.Context.CustomerPurchaseOrders.ToListAsync());
    }

    // ---- evidence link ----------------------------------------------------

    [Fact]
    public async Task The_purchase_order_records_the_document_it_was_read_from()
    {
        using var fixture = new CustomerAwardTestFixture();
        using var harness = new PoDocumentHarness(fixture);

        var extraction = await harness.ExtractAsync("customer-po.xlsx", SpreadsheetPurchaseOrder());
        var line = extraction.Lines[0];

        // Every value below is the BUYER's, straight off the extraction. Nothing is taken from the
        // quotation — the quote line is 10 @ 100, and this PO says 12 @ 235.50.
        var command = fixture.PurchaseOrderCommand(extraction.ExternalPoNumber!, line.OrderedQuantity!.Value) with
        {
            PoDate = extraction.PoDate!.Value,
            ReceivedOn = extraction.PoDate!.Value,
            Lines =
            [
                new CreateCustomerPurchaseOrderLineCommand(
                    line.ExternalLineReference, null, line.Description!, line.OrderedQuantity!.Value,
                    null, line.UnitPrice, line.LineAmount,
                    line.CustomerItemCode, line.ManufacturerName, line.ManufacturerPartNumber)
            ],
        };

        var result = await harness.Service.CreateFromDocumentAsync(fixture.BusinessUnitId,
            "po-from-document", "corr-po-from-document",
            new CreateCustomerPurchaseOrderFromDocumentCommand(extraction.SourceAttachmentId, command), "tests");

        harness.Context.ChangeTracker.Clear();
        var stored = await harness.Context.CustomerPurchaseOrders
            .Include(x => x.Lines)
            .SingleAsync(x => x.Id == result.Id);

        Assert.Equal(extraction.SourceAttachmentId, stored.SourceAttachmentId);
        Assert.Equal("PO-77120", stored.ExternalPoNumber);
        Assert.Equal(new DateTime(2026, 6, 14), stored.PoDate);
        Assert.Equal(12m, stored.Lines.Single().OrderedQuantity);
        Assert.Equal(235.50m, stored.Lines.Single().UnitPrice);
        // FR-COM-02's match keys, carried as the buyer printed them.
        Assert.Equal("IC-9001", stored.Lines.Single().CustomerItemCode);
        Assert.Equal("Velan", stored.Lines.Single().ManufacturerName);

        // The evidence row is re-parented to the record it now belongs to, so an abandoned upload
        // stays distinguishable from a committed one.
        var attachment = await harness.Context.Attachments.SingleAsync(x => x.Id == extraction.SourceAttachmentId);
        Assert.Equal(CustomerPurchaseOrderDocumentService.PurchaseOrderParentType, attachment.ParentType);
        Assert.Equal(stored.Id, attachment.ParentId);
        Assert.Equal(extraction.ContentSha256, attachment.ContentSha256);
    }

    [Fact]
    public async Task A_document_uploaded_by_another_tenant_cannot_be_linked()
    {
        using var fixture = new CustomerAwardTestFixture();
        using var harness = new PoDocumentHarness(fixture);

        var extraction = await harness.ExtractAsync("customer-po.xlsx", SpreadsheetPurchaseOrder());

        // A neighbouring tenant quoting the attachment id of a document it never uploaded is
        // refused before the purchase order is created, not after.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.Service.CreateFromDocumentAsync(fixture.BusinessUnitId + 1, "cross-tenant", "corr-cross-tenant",
                new CreateCustomerPurchaseOrderFromDocumentCommand(extraction.SourceAttachmentId,
                    fixture.PurchaseOrderCommand("PO-CROSS", 1m)), "attacker"));

        harness.Context.ChangeTracker.Clear();
        Assert.Empty(await harness.Context.CustomerPurchaseOrders.IgnoreQueryFilters().ToListAsync());
    }

    // ---- fixtures ---------------------------------------------------------

    private static byte[] Csv(params string[] lines)
        => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    /// <summary>
    /// A workbook shaped like a real purchase order: an identity block above the table, then the
    /// items. The header row is not row 1, which is the normal case and the case a naive reader
    /// silently drops.
    /// </summary>
    private static byte[] SpreadsheetPurchaseOrder()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("PO");
        sheet.Cells[1, 1].Value = "PURCHASE ORDER";
        sheet.Cells[2, 1].Value = "PO Number:";
        sheet.Cells[2, 2].Value = "PO-77120";
        sheet.Cells[3, 1].Value = "PO Date:";
        sheet.Cells[3, 2].Value = "2026-06-14";

        sheet.Cells[5, 1].Value = "Item Code";
        sheet.Cells[5, 2].Value = "Description";
        sheet.Cells[5, 3].Value = "Qty";
        sheet.Cells[5, 4].Value = "UOM";
        sheet.Cells[5, 5].Value = "Unit Price";
        sheet.Cells[5, 6].Value = "Manufacturer";

        sheet.Cells[6, 1].Value = "IC-9001";
        sheet.Cells[6, 2].Value = "Ball valve DN50 PN16 stainless";
        sheet.Cells[6, 3].Value = "12";
        sheet.Cells[6, 4].Value = "EA";
        sheet.Cells[6, 5].Value = "235.50";
        sheet.Cells[6, 6].Value = "Velan";

        sheet.Cells[7, 1].Value = "IC-9002";
        sheet.Cells[7, 2].Value = "Gate valve DN80 PN16";
        sheet.Cells[7, 3].Value = "4";
        sheet.Cells[7, 4].Value = "EA";
        sheet.Cells[7, 5].Value = "410.00";
        sheet.Cells[7, 6].Value = "Velan";

        return package.GetAsByteArray();
    }

    /// <summary>A Word purchase order: identity in paragraphs, items in a table.</summary>
    private static byte[] WordPurchaseOrder()
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            body.AppendChild(Text("Purchase Order No.: PO-88450"));
            body.AppendChild(Text("PO Date: 2026-06-14"));
            body.AppendChild(Text("Supplier: Nexora Trading"));

            var table = new Table();
            table.AppendChild(Row("Part No", "Description", "Qty", "Unit Price", "Manufacturer"));
            table.AppendChild(Row("PT-4400", "Pressure transmitter 0-16 bar", "6", "1150.00", "Rosemount"));
            table.AppendChild(Row("TT-2200", "Temperature transmitter", "3", "980.00", "Rosemount"));
            body.AppendChild(table);

            word.AddMainDocumentPart().Document = new Document(body);
        }

        return stream.ToArray();
    }

    private static Paragraph Text(string value)
        => new(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(value)));

    private static TableRow Row(params string[] cells)
    {
        var row = new TableRow();
        foreach (var cell in cells)
            row.AppendChild(new TableCell(Text(cell)));
        return row;
    }

    /// <summary>
    /// The real reading stack, wired the way production wires it: the shared
    /// <see cref="ProductionDocumentReader"/> over immutable evidence storage, behind the same
    /// file-inspection gate. Only the byte store and the scanner are in-memory.
    /// </summary>
    private sealed class PoDocumentHarness : IDisposable
    {
        private readonly CustomerAwardTestFixture _fixture;
        private readonly bool _ownsFixture;

        public PoDocumentHarness(CustomerAwardTestFixture? fixture = null)
        {
            _ownsFixture = fixture is null;
            _fixture = fixture ?? new CustomerAwardTestFixture();
            var storage = new MemoryEvidenceStorage();
            Service = new CustomerPurchaseOrderDocumentService(
                _fixture.Context,
                new ClearingInspection(),
                storage,
                new ProductionDocumentReader(
                    NullLogger<ProductionDocumentReader>.Instance,
                    new TestEnvironment(AppContext.BaseDirectory),
                    storage),
                _fixture.Service,
                NullLogger<CustomerPurchaseOrderDocumentService>.Instance);
        }

        public ErpRfqAutomationContext Context => _fixture.Context;
        public CustomerPurchaseOrderDocumentService Service { get; }

        public Task<CustomerPurchaseOrderDocumentView> ExtractAsync(string fileName, byte[] bytes)
            => Service.ExtractAsync(_fixture.BusinessUnitId, fileName, bytes, null, "tests");

        public void Dispose()
        {
            if (_ownsFixture) _fixture.Dispose();
        }
    }

    private sealed class ClearingInspection : IFileInspectionService
    {
        public Task<FileInspectionResult> InspectAsync(
            FileInspectionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new FileInspectionResult(
                FileInspectionStatus.Cleared,
                MimeFor(Path.GetExtension(request.FileName)),
                request.DeclaredLength ?? 0,
                "No malware was detected.",
                "tests",
                null)
            { MalwareStatus = MalwareScanStatus.Clean });

        private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".csv" => "text/csv",
            _ => "application/octet-stream",
        };
    }

    private sealed class MemoryEvidenceStorage : IEvidenceObjectStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public bool IsDurable => true;

        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            var uri = $"memory://{businessUnitId}/{zone}/{sha256}{extension}";
            _objects[uri] = content.ToArray();
            return Task.FromResult(new EvidenceObject(uri, "memory", uri, sha256, sha256, content.Length));
        }

        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default)
        {
            if (!_objects.TryGetValue(storageUri, out var bytes))
                throw new FileNotFoundException(storageUri);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Stored evidence does not match its recorded digest.");
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class TestEnvironment(string? contentRootPath = null) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath ?? Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

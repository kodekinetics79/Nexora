using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using OfficeOpenXml;
using Wp = DocumentFormat.OpenXml.Wordprocessing;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The amendment fork at the intake doors, closed.
///
/// <para>ManualUploadService and LeadUploaderService used to call
/// <c>EstablishBaselineRevisionAsync</c>, which by design does NO matching — it mints
/// revision 1 and returns. A revised RFQ arriving through either door therefore always became
/// a second, unlinked lead (or, on the manual door, was refused outright by a naive
/// (Rfqno, BuyersName) equality pre-check that could not tell an amendment from a duplicate).
/// Both doors now route through <see cref="ILeadIdentityApplicationService.ReconcileAsync"/>,
/// the same arbiter the extraction-worker door uses, and these tests drive the REAL door
/// services end to end — file parsing included — not the identity service in isolation.</para>
/// </summary>
public sealed class LeadIntakeDoorReconciliationTests
{
    // ------------------------------------------------------------------- manual upload door

    [Fact]
    public async Task Manual_upload_with_no_match_still_creates_a_new_lead_with_revision_one()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(301);
        Seed.BusinessUnit(context, 301); Seed.EmailConfig(context, 3011, 301);
        await context.SaveChangesAsync();

        var llm = new StubLlm(Extraction("RFQ-DOOR-1", "Door Buyer", quantity: 10));
        var result = await ManualDoor(context, llm).ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-door-1.docx", "Request for Quotation RFQ-DOOR-1: 10 nos valve.") }, 301);

        // The new-lead outcome is byte-for-byte the old contract: success, the lead id, the
        // same message — and now a canonical revision 1 established through reconciliation.
        Assert.True(result.Success);
        Assert.Equal("File processed and lead created successfully.", result.Message);
        var lead = Assert.Single(await context.Leads.Include(x => x.LeadItems).ToListAsync());
        Assert.Equal(result.Data, lead.Id);
        Assert.Equal(1, lead.CurrentRevisionNumber);
        Assert.NotNull(lead.CurrentRevisionId);
        var occurrence = Assert.Single(await context.Set<LeadIngestionOccurrence>().ToListAsync());
        Assert.Equal(LeadOccurrenceClassification.New, occurrence.Classification);
        // A real ingestion occurrence, not a content-only identity baseline: the upload is an
        // arrival and must count as one.
        Assert.Equal(LeadOccurrenceRecordKind.Ingestion, occurrence.RecordKind);
        Assert.NotNull(occurrence.ContentHash);
    }

    [Fact]
    public async Task Manual_upload_of_a_revision_versions_the_canonical_lead_instead_of_duplicating_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(302);
        Seed.BusinessUnit(context, 302); Seed.EmailConfig(context, 3021, 302);
        await context.SaveChangesAsync();

        var llm = new StubLlm(
            Extraction("RFQ-DOOR-2", "Door Buyer", quantity: 10),
            Extraction("RFQ-DOOR-2", "Door Buyer", quantity: 25));
        var door = ManualDoor(context, llm);

        var first = await door.ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-door-2.docx", "Request for Quotation RFQ-DOOR-2: 10 nos valve.") }, 302);
        Assert.True(first.Success);
        context.ChangeTracker.Clear();

        // The buyer re-sends the same RFQ with a changed quantity. The old door either refused
        // this as a "duplicate" or minted a second lead; it must become revision 2.
        var second = await door.ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-door-2-rev.docx", "Request for Quotation RFQ-DOOR-2 amended: 25 nos valve.") }, 302);

        Assert.True(second.Success);
        Assert.Equal(first.Data, second.Data);
        Assert.Contains("revision 2", second.Message, StringComparison.Ordinal);
        var lead = Assert.Single(await context.Leads.Include(x => x.LeadItems).ToListAsync());
        Assert.Equal(2, lead.CurrentRevisionNumber);
        Assert.Equal(25, lead.LeadItems.Single().Quantity);
        Assert.Equal(2, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == lead.Id));
        Assert.True(await context.Set<LeadIdentityAuditEvent>()
            .AnyAsync(x => x.LeadId == lead.Id && x.EventType == "LEAD_REVISION_CREATED"));
    }

    [Fact]
    public async Task Manual_upload_of_the_identical_document_is_reported_as_a_duplicate_with_the_canonical_serial()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(303);
        Seed.BusinessUnit(context, 303); Seed.EmailConfig(context, 3031, 303);
        await context.SaveChangesAsync();

        var llm = new StubLlm(
            Extraction("RFQ-DOOR-3", "Door Buyer", quantity: 10),
            Extraction("RFQ-DOOR-3", "Door Buyer", quantity: 10));
        var door = ManualDoor(context, llm);

        var first = await door.ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-door-3.docx", "Request for Quotation RFQ-DOOR-3: 10 nos valve.") }, 303);
        context.ChangeTracker.Clear();
        var replay = await door.ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-door-3.docx", "Request for Quotation RFQ-DOOR-3: 10 nos valve.") }, 303);

        Assert.False(replay.Success);
        Assert.Contains("Duplicate", replay.Message, StringComparison.Ordinal);
        Assert.Equal(1, await context.Leads.CountAsync());
        // The duplicate is RECORDED, not silently refused: the occurrence ledger carries it.
        Assert.Equal(1, await context.Set<LeadIngestionOccurrence>()
            .CountAsync(x => x.Classification == LeadOccurrenceClassification.ExactDuplicate));
        var canonical = await context.Leads.SingleAsync();
        Assert.Equal(first.Data, canonical.Id);
        Assert.Contains(canonical.CommercialCaseReference, replay.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- bulk import door

    [Fact]
    public async Task Bulk_reimport_of_an_amended_row_versions_the_existing_lead_instead_of_duplicating_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(304);
        Seed.BusinessUnit(context, 304); Seed.EmailConfig(context, 3041, 304);
        await context.SaveChangesAsync();
        var door = new LeadUploaderService(context, new NoopLogger<LeadUploaderService>(),
            new LeadIdentityApplicationService(context), new StubLeadCustomerResolution());

        var first = await door.UploadTemplateAsync(Workbook(("RFQ-BULK-9", "Bulk Buyer", "Gate Valve", 10)), 304, "tester");
        Assert.True(first.Success);
        Assert.StartsWith("1 leads and 1 items imported successfully.", first.Data);
        context.ChangeTracker.Clear();
        var lead = Assert.Single(await context.Leads.ToListAsync());
        Assert.Equal(1, lead.CurrentRevisionNumber);

        // The same inquiry re-imported with a corrected quantity: this door is NOT
        // production-fenced, so before the fix this was the one live path that always
        // duplicated an amended lead.
        context.ChangeTracker.Clear();
        var second = await door.UploadTemplateAsync(Workbook(("RFQ-BULK-9", "Bulk Buyer", "Gate Valve", 40)), 304, "tester");

        Assert.True(second.Success);
        Assert.Contains("matched existing leads and were applied as new revisions", second.Data, StringComparison.Ordinal);
        var canonical = Assert.Single(await context.Leads.Include(x => x.LeadItems).ToListAsync());
        Assert.Equal(lead.Id, canonical.Id);
        Assert.Equal(2, canonical.CurrentRevisionNumber);
        Assert.Equal(40, canonical.LeadItems.Single().Quantity);
    }

    [Fact]
    public async Task Bulk_import_of_a_new_inquiry_still_creates_a_lead_with_revision_one()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(305);
        Seed.BusinessUnit(context, 305); Seed.EmailConfig(context, 3051, 305);
        await context.SaveChangesAsync();
        var door = new LeadUploaderService(context, new NoopLogger<LeadUploaderService>(),
            new LeadIdentityApplicationService(context), new StubLeadCustomerResolution());

        var result = await door.UploadTemplateAsync(Workbook(("RFQ-BULK-1", "Bulk Buyer", "Ball Valve", 5)), 305, "tester");

        Assert.True(result.Success);
        Assert.StartsWith("1 leads and 1 items imported successfully.", result.Data);
        var lead = Assert.Single(await context.Leads.Include(x => x.LeadItems).ToListAsync());
        Assert.Equal(1, lead.CurrentRevisionNumber);
        Assert.NotNull(lead.CurrentRevisionId);
        Assert.Equal(5, lead.LeadItems.Single().Quantity);
        var occurrence = Assert.Single(await context.Set<LeadIngestionOccurrence>().ToListAsync());
        Assert.Equal(LeadOccurrenceClassification.New, occurrence.Classification);
        Assert.Equal(LeadOccurrenceRecordKind.Ingestion, occurrence.RecordKind);
    }

    // ---------------------------------------------------------------------- door plumbing

    private static ManualUploadService ManualDoor(ErpRfqAutomationContext context, StubLlm llm)
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexora-door-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        return new ManualUploadService(
            context,
            new DoorEnvironment(temp),
            new NoopLogger<ManualUploadService>(),
            llm,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The legacy direct path — the one that owns the reconcile call under test.
                ["Ingestion:UseUnifiedQueue"] = "false"
            }).Build(),
            new DoorStorage(temp),
            new LeadIdentityApplicationService(context),
            new StubLeadCustomerResolution());
    }

    /// <summary>A scripted extraction: one line whose identity is stable across uploads so a
    /// changed quantity reads as an amendment, not a different product.</summary>
    private static ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult Extraction(string rfq, string buyer, int quantity)
        => Ext.Result(new List<ERP_RFQ_Automation.Services.Interfaces.LeadItemData>
        {
            Ext.Item(.9, "Gate Valve", quantity) with
            {
                ManufacturerPartNumber = "PN-100",
                ProductShortDescription = "Gate valve 6 inch",
                UnitOfMeasure = "EA"
            }
        }, .9) with
        { Rfqno = rfq, BuyersName = buyer, BidClosingDate = null };

    /// <summary>A real .docx, because the legacy manual path extracts text before any AI call
    /// and refuses an upload that yields none.</summary>
    private static IFormFile DocxFile(string fileName, string text)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Wp.Document(new Wp.Body(new Wp.Paragraph(new Wp.Run(new Wp.Text(text)))));
        }
        ms.Position = 0;
        return new FormFile(ms, 0, ms.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
    }

    /// <summary>An upload in Nexora's own bulk template shape (row 1 is the header).</summary>
    private static MemoryStream Workbook(params (string Rfq, string Buyer, string Product, int Quantity)[] rows)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var ws = package.Workbook.Worksheets.Add("LeadTemplate");
        ws.Cells[1, 1].Value = "RFQ No*";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cells[i + 2, 1].Value = rows[i].Rfq;
            ws.Cells[i + 2, 2].Value = rows[i].Buyer;
            ws.Cells[i + 2, 3].Value = "2026-08-01";
            ws.Cells[i + 2, 5].Value = rows[i].Product;
            ws.Cells[i + 2, 6].Value = rows[i].Quantity;
        }
        return new MemoryStream(package.GetAsByteArray());
    }

    private sealed class DoorEnvironment : IWebHostEnvironment
    {
        public DoorEnvironment(string root)
        {
            WebRootPath = root;
            ContentRootPath = root;
        }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class DoorStorage : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
    {
        private readonly string _root;
        public DoorStorage(string root) => _root = root;
        public string RootPath => _root;
        public string ResolvePath(string storagePath) => Path.Combine(_root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([_root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("The door tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The door tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The door tests never delete storage.");
    }
}

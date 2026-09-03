using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Wp = DocumentFormat.OpenXml.Wordprocessing;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The wiring, not the unit: the manual-upload door is driven through its public entry point
/// (<see cref="ManualUploadService.ProcessUploadedFilesAsync"/>) with the switch ON, and the
/// proof is the Attachments row it wrote and the bytes in the object store — nothing on disk.
/// </summary>
public sealed class LegacyWritersObjectStoreWiringTests
{
    [Fact]
    public async Task Manual_upload_stores_the_attachment_in_the_object_store_and_records_the_uri_and_digest()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(301);
        Seed.BusinessUnit(context, 301); Seed.EmailConfig(context, 3011, 301);
        await context.SaveChangesAsync();
        var temp = Path.Combine(Path.GetTempPath(), "nexora-wiring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var evidence = new InMemoryEvidenceStorage();
        var door = ManualDoor(context, temp, new LegacyDocumentStore(new DoorStorage(temp), evidence, routeToObjectStore: true));

        var result = await door.ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-wiring-1.docx", "Request for Quotation RFQ-WIRING-1: 10 nos valve.") }, 301);

        Assert.True(result.Success, result.Message);
        var attachment = Assert.Single(await context.Attachments.ToListAsync());
        Assert.True(EvidenceObjectUris.IsObjectUri(attachment.FilePath), attachment.FilePath);
        Assert.Contains("/tenants/301/legacy/sha256/", attachment.FilePath);
        Assert.NotNull(attachment.ContentSha256);
        Assert.Equal(InMemoryEvidenceStorage.UriFor(301, "legacy", attachment.ContentSha256!, ".docx"), attachment.FilePath);
        Assert.True(evidence.Objects.ContainsKey(attachment.FilePath));
        Assert.Equal(evidence.Objects[attachment.FilePath].LongLength, attachment.FileSize);
        Assert.False(Directory.Exists(Path.Combine(temp, "Manual_Attachments")) && Directory.EnumerateFiles(Path.Combine(temp, "Manual_Attachments")).Any(),
            "with the switch on, nothing may land on disk");
    }

    [Fact]
    public async Task Manual_upload_with_the_switch_off_still_writes_to_disk_exactly_as_before()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(302);
        Seed.BusinessUnit(context, 302); Seed.EmailConfig(context, 3021, 302);
        await context.SaveChangesAsync();
        var temp = Path.Combine(Path.GetTempPath(), "nexora-wiring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var evidence = new InMemoryEvidenceStorage();
        var door = ManualDoor(context, temp, new LegacyDocumentStore(new DoorStorage(temp), evidence, routeToObjectStore: false));

        var result = await door.ProcessUploadedFilesAsync(
            new List<IFormFile> { DocxFile("rfq-wiring-2.docx", "Request for Quotation RFQ-WIRING-2: 10 nos valve.") }, 302);

        Assert.True(result.Success, result.Message);
        var attachment = Assert.Single(await context.Attachments.ToListAsync());
        Assert.StartsWith(Path.Combine("Uploads", "Manual_Attachments"), attachment.FilePath);
        Assert.True(File.Exists(Path.Combine(temp, "Manual_Attachments", Path.GetFileName(attachment.FilePath))));
        Assert.Equal(0, evidence.WriteCalls);
        // The digest is new even on disk (FR-RFQ-08): the row can now be verified and migrated.
        Assert.NotNull(attachment.ContentSha256);
    }

    // ---------------------------------------------------------------------- door plumbing

    private static ManualUploadService ManualDoor(ErpRfqAutomationContext context, string temp, ILegacyDocumentStore store)
    {
        var llm = new StubLlm(Ext.Result(new List<ERP_RFQ_Automation.Services.Interfaces.LeadItemData>
        {
            Ext.Item(.9, "Gate Valve", 10) with { ManufacturerPartNumber = "PN-100", ProductShortDescription = "Gate valve 6 inch", UnitOfMeasure = "EA" }
        }, .9) with { Rfqno = "RFQ-WIRING", BuyersName = "Door Buyer", BidClosingDate = null });
        return new ManualUploadService(
            context,
            new DoorEnvironment(temp),
            new NoopLogger<ManualUploadService>(),
            llm,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:UseUnifiedQueue"] = "false"
            }).Build(),
            new DoorStorage(temp),
            new LeadIdentityApplicationService(context),
            new StubLeadCustomerResolution(),
            legacyDocuments: store);
    }

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

    private sealed class DoorEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class DoorStorage(string root) : IFileStorage
    {
        public string RootPath => root;
        public string ResolvePath(string storagePath) => Path.Combine(root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("The door tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The door tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The door tests never delete storage.");
    }
}

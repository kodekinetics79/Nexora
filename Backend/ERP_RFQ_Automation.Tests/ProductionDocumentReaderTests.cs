using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using ERP_RFQ_Automation.Platform.Entitlements;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProductionDocumentReaderTests
{
    [Fact]
    public async Task VerifiedReadFailure_ThrowsTypedIntegrityFailure()
    {
        var job = new ExtractionJob
        {
            Id = 123,
            BusinessUnitId = 7,
            StoragePath = "s3://evidence/object",
            ContentHash = new string('a', 64),
            FileName = "rfq.csv",
            FileType = "csv"
        };
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(),
            new FailingStorage());

        var error = await Assert.ThrowsAsync<EvidenceIntegrityException>(() => reader.ReadAsync(job));

        Assert.Equal(job.Id, error.ExtractionJobId);
        Assert.Equal("verified_read_failed", error.Code);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.DoesNotContain(job.StoragePath, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rfq.xlsx", "xlsx", "not-an-openxml-workbook")]
    [InlineData("rfq.csv", "csv", "col1,\"unterminated quoted field")]
    public async Task InvalidStructuredDocument_StopsWithTypedParseFailure(
        string fileName,
        string fileType,
        string content)
    {
        var job = new ExtractionJob
        {
            Id = 124,
            BusinessUnitId = 7,
            StoragePath = "memory://evidence/object",
            ContentHash = new string('b', 64),
            FileName = fileName,
            FileType = fileType
        };
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(),
            new MemoryStorage(System.Text.Encoding.UTF8.GetBytes(content)));

        await Assert.ThrowsAsync<DocumentParsingException>(() => reader.ReadAsync(job));
    }

    [Fact]
    public async Task Docx_PreservesParagraphAndTableRowStructure()
    {
        var bytes = CreateDocxWithCommercialTable();
        var reader = CreateReader(bytes);

        var result = await reader.ReadAsync(CreateJob("rfq.docx", "docx"));

        Assert.Equal("RFQ 42\nPart Number\tQuantity\nABC-100\t25\nDelivery: urgent", result.HeaderText);
        Assert.Contains("Part Number\tQuantity", result.LineItemRegions);
        Assert.Contains("ABC-100\t25", result.LineItemRegions);
    }

    [Fact]
    public async Task Docx_MultiParagraphCells_JoinWithSpaces_UnderTheStreamingReader()
    {
        // Regression for the DOM->streaming rewrite (a real 121MB document.xml forced
        // ExtractTextFromDocx onto OpenXmlReader): a cell holding several paragraphs
        // must still join them with spaces into ONE cell text, and the row must still
        // tab-join its cells — the exact shape the old DOM reader produced.
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(
                    new Body(
                        new Table(
                            new TableRow(
                                new TableCell(
                                    ParagraphWithText("GASKET SPIRAL"),
                                    ParagraphWithText("WOUND 300MM")),
                                TableCellWithText("40")))));
                main.Document.Save();
            }
            bytes = stream.ToArray();
        }

        var reader = CreateReader(bytes);
        var result = await reader.ReadAsync(CreateJob("rfq.docx", "docx"));

        Assert.Equal("GASKET SPIRAL WOUND 300MM\t40", result.HeaderText);
    }

    [Fact]
    public async Task MalformedDocx_StopsWithTypedPermanentParseFailure()
    {
        var reader = CreateReader(System.Text.Encoding.UTF8.GetBytes("not-an-openxml-document"));

        var error = await Assert.ThrowsAsync<DocumentParsingException>(() =>
            reader.ReadAsync(CreateJob("rfq.docx", "docx")));

        Assert.Contains("DOCX", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiPageTiff_ProcessesEveryFrame()
    {
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(AppContext.BaseDirectory),
            new MemoryStorage([0x49, 0x49, 0x2A, 0x00]),
            _ => ["RFQ 42 first page commercial text", "ABC-100 quantity 25 second page"]);

        var result = await reader.ReadAsync(CreateJob("rfq.tiff", "tiff"));

        Assert.Equal(2, result.OcrPageCount);
        Assert.False(result.OcrTruncated);
        Assert.Equal(ExtractionProcessingPath.LocalOcr, result.ProcessingPath);
        Assert.Contains("RFQ 42 first page commercial text", result.HeaderText);
        Assert.Contains("ABC-100 quantity 25 second page", result.HeaderText);
    }

    [Fact]
    public async Task MultiPageTiff_DeniesBeforeOcr_WhenTenantPlanDoesNotEnableOcr()
    {
        var ocrInvoked = false;
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(AppContext.BaseDirectory),
            new MemoryStorage([0x49, 0x49, 0x2A, 0x00]),
            _ =>
            {
                ocrInvoked = true;
                return ["must not run"];
            },
            inspection: null,
            entitlements: new FixedEntitlements(allowed: false));

        var denial = await Assert.ThrowsAsync<FeatureEntitlementDeniedException>(() =>
            reader.ReadAsync(CreateJob("rfq.tiff", "tiff")));

        Assert.Equal(TypedEntitlementCatalog.Ocr, denial.Entitlement);
        Assert.False(ocrInvoked);
    }

    private static ProductionDocumentReader CreateReader(byte[] content) => new(
        NullLogger<ProductionDocumentReader>.Instance,
        new TestEnvironment(AppContext.BaseDirectory),
        new MemoryStorage(content));

    private static ExtractionJob CreateJob(string fileName, string fileType) => new()
    {
        Id = 125,
        BusinessUnitId = 7,
        StoragePath = "memory://evidence/object",
        ContentHash = new string('c', 64),
        FileName = fileName,
        FileType = fileType
    };

    private static byte[] CreateDocxWithCommercialTable()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(
                new Body(
                    ParagraphWithText("RFQ 42"),
                    new Table(
                        new TableRow(TableCellWithText("Part Number"), TableCellWithText("Quantity")),
                        new TableRow(TableCellWithText("ABC-100"), TableCellWithText("25"))),
                    ParagraphWithText("Delivery: urgent")));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static Paragraph ParagraphWithText(string value) => new(new Run(new Text(value)));

    private static TableCell TableCellWithText(string value) =>
        new(ParagraphWithText(value));

    private sealed class FailingStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default)
            => Task.FromException<Stream>(new InvalidDataException("hash mismatch"));
    }

    private sealed class MemoryStorage(byte[] content) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    private sealed class FixedEntitlements(bool allowed) : IEntitlementService
    {
        public Task<EntitlementDecision> CheckFeatureAsync(
            long businessUnitId, string entitlement, CancellationToken ct = default)
            => Task.FromResult(allowed
                ? EntitlementDecision.Permit(1, 1)
                : EntitlementDecision.Deny(0, 0, "OCR is disabled by the tenant plan."));

        public Task<EntitlementDecision> CheckSeatAvailabilityAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(EntitlementDecision.Unlimited);
        public Task<EntitlementDecision> CheckDocumentQuotaAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(EntitlementDecision.Unlimited);
        public Task<double> GetQueueWeightAsync(long businessUnitId, double fallbackWeight, CancellationToken ct = default)
            => Task.FromResult(fallbackWeight);
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

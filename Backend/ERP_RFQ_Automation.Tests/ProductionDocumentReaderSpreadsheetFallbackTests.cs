using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Live-bug regression suite: real customer .xls files (C001046140.xls et al.) cleared
/// inspection, parsed via ExcelDataReader, and then DEAD-LETTERED at attempt 1 because
/// the deterministic header mapper recognized none of their columns. An unrecognized
/// layout is the NORMAL case for first-contact customer files: the reader must fall back
/// to rendered sheet text on the same unstructured path PDFs use (still governed by the
/// per-tenant external-provider allow-list), while recognized layouts keep the
/// deterministic LLM-bypassing fast-path.
/// </summary>
public sealed class ProductionDocumentReaderSpreadsheetFallbackTests
{
    // ---- unrecognized layouts fall back instead of dead-lettering ---------

    // This fixture opens with a title row ("Request for Quotation - C001046140") above an
    // otherwise ordinary header: S.No | Material Code | Material Description | UOM | Req Qty |
    // Delivery Location. It used to be classed as unrecognized purely because the header was
    // assumed to be row 1 and none of those column spellings were known — so a perfectly
    // readable RFQ was handed to the LLM, which is blocked for external providers in the
    // deployed configuration, and dead-lettered. It is now read deterministically.
    [Fact]
    public async Task TitleBlockAboveTheHeader_IsReadStructurally_WithTheUnitColumn()
    {
        var bytes = ReadFixture("unrecognized-layout-rfq.xls");
        var reader = CreateReader(bytes);

        var result = await reader.ReadAsync(CreateJob("C001046140.xls", "xls"));

        Assert.True(result.IsStructured);
        Assert.NotNull(result.StructuredRows);
        // The deterministic path, not the LLM fallback — no model is involved at all.
        Assert.Equal(ExtractionProcessingPath.DeterministicRules, result.ProcessingPath);

        var first = result.StructuredRows!.First();
        Assert.Equal("MAT-88001", first.ManufacturerPartNumber);
        Assert.Equal("Ball valve DN50 PN16 stainless", first.ProductName);
        Assert.Equal("EA", first.UnitOfMeasure);
        Assert.Equal("12", first.Quantity);
    }

    [Fact]
    public async Task UnrecognizedXlsxLayout_FallsBackToUnstructuredText()
    {
        var bytes = BuildXlsx(worksheet =>
        {
            worksheet.Cells[1, 1].Value = "Section";
            worksheet.Cells[1, 2].Value = "Narrative";
            worksheet.Cells[1, 3].Value = "Owner";
            worksheet.Cells[1, 4].Value = "Status";
            worksheet.Cells[2, 1].Value = "ENQ-77";
            worksheet.Cells[2, 2].Value = "Gate valve DN80";
            worksheet.Cells[2, 3].Value = "EA";
            worksheet.Cells[2, 4].Value = "6";
        });
        var reader = CreateReader(bytes);

        var result = await reader.ReadAsync(CreateJob("enquiry.xlsx", "xlsx"));

        Assert.False(result.IsStructured);
        Assert.NotNull(result.StructuredFallbackNote);
        Assert.Contains("column layout was not recognized", result.StructuredFallbackNote);
        Assert.Contains("ENQ-77\tGate valve DN80\tEA\t6", result.HeaderText);
    }

    [Fact]
    public async Task UnrecognizedCsvHeaders_WithDataRows_FallsBackToUnstructuredText()
    {
        var csv = "Section,Narrative,Owner,Status\nENQ-1,Ball valve,EA,12\n";
        var reader = CreateReader(Encoding.UTF8.GetBytes(csv));

        var result = await reader.ReadAsync(CreateJob("enquiry.csv", "csv"));

        Assert.False(result.IsStructured);
        Assert.NotNull(result.StructuredFallbackNote);
        Assert.Contains("column layout was not recognized", result.StructuredFallbackNote);
        Assert.Contains("ENQ-1\tBall valve\tEA\t12", result.HeaderText);
    }

    // ---- recognized layouts keep the deterministic fast-path --------------

    [Fact]
    public async Task RecognizedXlsLayout_StillBypassesLlmViaDeterministicStructuredPath()
    {
        var bytes = ReadFixture("recognized-layout-rfq.xls");
        var reader = CreateReader(bytes);

        var result = await reader.ReadAsync(CreateJob("recognized.xls", "xls"));

        Assert.True(result.IsStructured);
        Assert.Equal(ExtractionProcessingPath.DeterministicRules, result.ProcessingPath);
        Assert.Null(result.StructuredFallbackNote);
        var row = Assert.Single(result.StructuredRows!);
        Assert.Equal("RFQ-1046", row.RfqNo);
        Assert.Equal("Acme Industrial", row.BuyerName);
        Assert.Equal("Centrifugal pump 15kW", row.ProductName);
        Assert.Equal("3", row.Quantity);
        Assert.Equal("USD", row.Currency);
    }

    // ---- genuinely unreadable/empty workbooks stay permanent --------------

    [Fact]
    public async Task EmptyXlsWorkbook_RemainsTypedPermanentParseFailure_WithHonestMessage()
    {
        var reader = CreateReader(ReadFixture("empty-workbook.xls"));

        var error = await Assert.ThrowsAsync<DocumentParsingException>(() =>
            reader.ReadAsync(CreateJob("empty.xls", "xls")));

        Assert.Contains("read successfully", error.Message);
        Assert.Contains("no cell content", error.Message);
    }

    // ---- composition: the fallback rides the SAME unstructured path -------

    [Fact]
    public async Task UnrecognizedXlsFallback_ReachesLlmExtraction_WhenLocalProviderIsAvailable()
    {
        var reader = CreateReader(UnrecognizableWorkbook());
        var input = await reader.ReadAsync(CreateJob("enquiry.xlsx", "xlsx"));

        var llm = new StubLlm(AiProviderClass.Local, Ext.Result(Ext.Items(3, 0.9), 0.9));
        var service = new ChunkedExtractionService(
            llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

        var outcome = await service.ExtractUnstructuredAsync(input);

        Assert.NotEqual(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.True(llm.CallCount >= 1);
        Assert.Contains(llm.Prompts, prompt => prompt.Contains("MAT-88001"));
        Assert.Equal(3, outcome.ExtractedItemCount);
    }

    [Fact]
    public async Task UnrecognizedXlsFallback_RespectsExternalAllowListGate_FailsClosedWithZeroEgress()
    {
        var reader = CreateReader(UnrecognizableWorkbook());
        var input = await reader.ReadAsync(CreateJob("enquiry.xlsx", "xlsx"));

        var llm = new StubLlm(AiProviderClass.External, Ext.Result(Ext.Items(3, 0.9), 0.9));
        var service = new ChunkedExtractionService(
            llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

        var outcome = await service.ExtractUnstructuredAsync(input);

        // Fail-closed refusal: a retryable Failed outcome (the worker records it via
        // FailAsync -> Pending/backoff), NOT a DocumentParsingException dead-letter.
        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount); // zero bytes of unauthorized egress
        Assert.Contains("blocked for unstructured documents", outcome.ReviewReason);
        Assert.Contains("human review", outcome.ReviewReason);
    }

    // ---- helpers ----------------------------------------------------------

    private static byte[] ReadFixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>
    /// A workbook whose headers carry no commercial meaning, so the deterministic parser
    /// genuinely cannot map a single column. Used to keep the fail-closed fallback path under
    /// test now that ordinary RFQ column spellings — including title blocks above the header —
    /// are read structurally.
    /// </summary>
    private static byte[] UnrecognizableWorkbook() => BuildXlsx(worksheet =>
    {
        worksheet.Cells[1, 1].Value = "Section";
        worksheet.Cells[1, 2].Value = "Narrative";
        worksheet.Cells[1, 3].Value = "Owner";
        worksheet.Cells[2, 1].Value = "MAT-88001";
        worksheet.Cells[2, 2].Value = "Ball valve DN50 PN16 stainless";
        worksheet.Cells[2, 3].Value = "Jubail Plant";
    });

    private static byte[] BuildXlsx(Action<OfficeOpenXml.ExcelWorksheet> populate)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        populate(package.Workbook.Worksheets.Add("Enquiry"));
        return package.GetAsByteArray();
    }

    private static ProductionDocumentReader CreateReader(byte[] content) => new(
        NullLogger<ProductionDocumentReader>.Instance,
        new TestEnvironment(AppContext.BaseDirectory),
        new MemoryStorage(content));

    private static ExtractionJob CreateJob(string fileName, string fileType) => new()
    {
        Id = 321,
        BusinessUnitId = 7,
        StoragePath = "memory://evidence/object",
        ContentHash = new string('d', 64),
        FileName = fileName,
        FileType = fileType
    };

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

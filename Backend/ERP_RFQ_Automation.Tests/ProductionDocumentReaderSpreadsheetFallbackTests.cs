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

    [Fact]
    public async Task UnrecognizedXlsLayout_FallsBackToUnstructuredText_InsteadOfFailingPermanently()
    {
        var bytes = ReadFixture("unrecognized-layout-rfq.xls");
        var reader = CreateReader(bytes);

        var result = await reader.ReadAsync(CreateJob("C001046140.xls", "xls"));

        Assert.False(result.IsStructured);
        Assert.Null(result.StructuredRows);
        Assert.Equal(ExtractionProcessingPath.NativeParser, result.ProcessingPath);

        // Honest, specific user-facing context: read fine, layout unrecognized, what's next.
        Assert.NotNull(result.StructuredFallbackNote);
        Assert.Contains("read successfully", result.StructuredFallbackNote);
        Assert.Contains("column layout was not recognized", result.StructuredFallbackNote);
        Assert.Contains("AI-assisted extraction", result.StructuredFallbackNote);
        Assert.Contains("held for review", result.StructuredFallbackNote);

        // The rendered text carries sheet name + tab-joined rows for the LLM/reviewer.
        Assert.StartsWith("[SPREADSHEET LAYOUT NOT RECOGNIZED", result.HeaderText);
        Assert.Contains("[Worksheet: Enquiry]", result.HeaderText);
        Assert.Contains("Material Code\tMaterial Description\tUOM", result.HeaderText);
        Assert.Contains("MAT-88001\tBall valve DN50 PN16 stainless\tEA\t12", result.HeaderText);
        Assert.NotEmpty(result.LineItemRegions);
    }

    [Fact]
    public async Task UnrecognizedXlsxLayout_FallsBackToUnstructuredText()
    {
        var bytes = BuildXlsx(worksheet =>
        {
            worksheet.Cells[1, 1].Value = "Enquiry Ref";
            worksheet.Cells[1, 2].Value = "Material Description";
            worksheet.Cells[1, 3].Value = "UOM";
            worksheet.Cells[1, 4].Value = "Req Qty";
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
        var csv = "Enquiry Ref,Material Description,UOM,Req Qty\nENQ-1,Ball valve,EA,12\n";
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
        var reader = CreateReader(ReadFixture("unrecognized-layout-rfq.xls"));
        var input = await reader.ReadAsync(CreateJob("C001046140.xls", "xls"));

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
        var reader = CreateReader(ReadFixture("unrecognized-layout-rfq.xls"));
        var input = await reader.ReadAsync(CreateJob("C001046140.xls", "xls"));

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

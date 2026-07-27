using System.Reflection;
using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProcessingEvidenceTests
{
    [Fact]
    public async Task ProductionReader_RetryKeepsImmutableSourceIdentity()
    {
        var bytes = Encoding.UTF8.GetBytes("RFQ-42\nPN-100, 2 EA");
        var job = new ExtractionJob
        {
            Id = 42,
            BusinessUnitId = 7,
            StoragePath = "evidence://rfq-42",
            ContentHash = new string('a', 64),
            FileName = "rfq.txt",
            FileType = "txt",
            Attempts = 1
        };
        var reader = new ProductionDocumentReader(NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(), new MemoryStorage(bytes));

        var first = await reader.ReadAsync(job);
        job.Attempts = 5;
        var retry = await reader.ReadAsync(job);

        Assert.Equal("job:42", first.SourceId);
        Assert.Equal(first.SourceId, retry.SourceId);
        Assert.Equal(ExtractionProcessingPath.NativeParser, retry.ProcessingPath);
        Assert.Equal(ExtractionOcrStatus.NotRequired, retry.OcrStatus);
    }

    [Fact]
    public void ExternalCost_MissingRateRemainsUnknown()
    {
        var request = ExternalRequest(cost: null, currency: null, status: "RateUnavailable");

        var summary = ProcessingCostAttribution.Summarize([request]);

        Assert.Null(summary.Amount);
        Assert.Null(summary.Currency);
        Assert.Equal("RateUnavailable", summary.Status);
    }

    [Fact]
    public void ExternalCost_PricedRequestsAreSummedOnlyWithinOneCurrency()
    {
        var summary = ProcessingCostAttribution.Summarize([
            ExternalRequest(1.25m, "usd", "Priced"),
            ExternalRequest(2.50m, "USD", "Priced")]);

        Assert.Equal(3.75m, summary.Amount);
        Assert.Equal("USD", summary.Currency);
        Assert.Equal("Priced", summary.Status);
    }

    [Fact]
    public void ExternalCost_ConfiguredEstimateIsPreservedForCommercialEvidence()
    {
        var summary = ProcessingCostAttribution.Summarize([
            ExternalRequest(0.004m, "USD", AiCostStatuses.EstimatedConfiguredRate)]);

        Assert.Equal(0.004m, summary.Amount);
        Assert.Equal("USD", summary.Currency);
        Assert.Equal(AiCostStatuses.EstimatedConfiguredRate, summary.Status);
    }

    [Fact]
    public void ExternalCost_AmountWithoutAuthoritativeRateStatusRemainsUnknown()
    {
        var summary = ProcessingCostAttribution.Summarize([
            ExternalRequest(9.99m, "USD", "RateUnavailable")]);

        Assert.Null(summary.Amount);
        Assert.Equal("RateUnavailable", summary.Status);
    }

    [Fact]
    public void ExtractionRun_PreservesPartialAndTruncatedOcrEvidence()
    {
        var run = ExtractionRun.Create(7, 11, Guid.NewGuid(), 42, 1, "reader/v1", "schema/v1");

        run.RecordProcessingEvidence(ExtractionProcessingPath.LocalOcr,
            ExtractionOcrStatus.Partial, 10, ocrTruncated: true);

        Assert.Equal(ExtractionProcessingPath.LocalOcr, run.ProcessingPath);
        Assert.Equal(ExtractionOcrStatus.Partial, run.OcrStatus);
        Assert.Equal(10, run.OcrPageCount);
        Assert.True(run.OcrTruncated);
    }

    [Fact]
    public void ExtractionRun_ClassifiesNativeSpreadsheetAsDeterministicRules()
    {
        var run = ExtractionRun.Create(7, 11, Guid.NewGuid(), 42, 1,
            "native-spreadsheet/v2", "schema/v1");

        Assert.Equal(ExtractionProcessingPath.DeterministicRules, run.ProcessingPath);
    }

    [Fact]
    public void ProcessingEvidenceEndpoint_RequiresAuthenticationAndLeadViewPermission()
    {
        var controllerAuthorization = typeof(ProcessingEvidenceController)
            .GetCustomAttribute<AuthorizeAttribute>();
        var action = typeof(ProcessingEvidenceController).GetMethod(nameof(ProcessingEvidenceController.Lead))!;
        var permission = action.GetCustomAttribute<RequireModulePermissionAttribute>();

        Assert.NotNull(controllerAuthorization);
        Assert.NotNull(permission);
        Assert.Equal("Leads", permission!.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    [Theory]
    [InlineData(nameof(ProcessingEvidenceController.Rfq), "RFQ Management")]
    [InlineData(nameof(ProcessingEvidenceController.SupplierQuote), "Supplier History")]
    [InlineData(nameof(ProcessingEvidenceController.ClientPurchaseOrder), "Customer Awards")]
    public void CommercialRecordEvidenceEndpoints_RequireResourceViewPermission(
        string actionName, string expectedModule)
    {
        var action = typeof(ProcessingEvidenceController).GetMethod(actionName)!;
        var permission = action.GetCustomAttribute<RequireModulePermissionAttribute>();

        Assert.NotNull(permission);
        Assert.Equal(expectedModule, permission!.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    private static AiRequest ExternalRequest(decimal? cost, string? currency, string status) => new()
    {
        ProviderClass = AiProviderClass.External,
        EstimatedCost = cost,
        CostCurrency = currency,
        CostStatus = status
    };

    private sealed class MemoryStorage(byte[] bytes) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

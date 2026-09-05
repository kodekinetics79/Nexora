using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class ManualUploadControllerTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-manual-controller-" + Guid.NewGuid().ToString("N"));
    private readonly TestDb _database = new();

    [Theory]
    [InlineData(nameof(ManualUploadController.UploadFiles), PermissionAction.Create)]
    [InlineData(nameof(ManualUploadController.UploadCustomerRfqExcel), PermissionAction.Create)]
    [InlineData(nameof(ManualUploadController.ListFiles), PermissionAction.View)]
    public void Routes_RequireExpectedLeadPermission(string actionName, PermissionAction expected)
    {
        var method = typeof(ManualUploadController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing action {actionName}.");
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>());

        Assert.Equal("Leads", permission.ModuleName);
        Assert.Equal(expected, permission.Action);
    }

    [Fact]
    public async Task BodyTenantCannotReplaceMissingAuthenticatedTenantClaim()
    {
        var ingestion = new RecordingIngestion();
        var controller = CreateController(ingestion, tenantClaim: null);

        var result = await controller.UploadCustomerRfqExcel(
            File("sku,qty\nABC,1\n", "rfq.csv"), businessUnitId: 99_001);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(ingestion.BusinessUnitIds);
    }

    [Fact]
    public async Task AuthenticatedTenantClaimOverridesBodyTenant()
    {
        var ingestion = new RecordingIngestion();
        var controller = CreateController(ingestion, tenantClaim: 77_001);

        var result = await controller.UploadCustomerRfqExcel(
            File("sku,qty\nABC,1\n", "rfq.csv"), businessUnitId: 99_001);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(new long[] { 77_001 }, ingestion.BusinessUnitIds);
    }

    /// <summary>
    /// Scenario testing 2026-09-04 (docs/audit/SCENARIOS-INTAKE-2026-09-04.md, finding F4): CSV bytes
    /// uploaded under a ".xlsx" name were refused by document inspection — correctly, with a sentence
    /// naming the fix — and this door answered HTTP 500 "Internal server error", because
    /// <see cref="DocumentInspectionException"/> is an <see cref="IOException"/> and fell into the
    /// generic catch. The sibling action <c>UploadCustomerRfqExcel</c> already answered 422 with the
    /// inspection reason; the multi-file door must say the same thing.
    /// </summary>
    [Fact]
    public async Task InspectionRefusalIsTheCallersOutcomeNotAServerError()
    {
        var reason = "The file is named '.xlsx' but its contents are not in that format. Open it in the "
            + "application that produced it and use Save As to store a real .xlsx file, then upload that.";
        var batchId = Guid.NewGuid();
        var ingestion = new RefusingIngestion(new DocumentInspectionException(
            new FileInspectionResult(FileInspectionStatus.Rejected, null, 12, reason, "not-run", null),
            sourceDocumentOccurrenceId: 91, batchId: batchId));
        var controller = CreateController(ingestion, tenantClaim: 77_001);

        var result = await controller.UploadFiles([File("rfqno,qty\nA,1\n", "rfq.xlsx")]);

        var refusal = Assert.IsType<UnprocessableEntityObjectResult>(result);
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(refusal.Value));
        var root = body.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(reason, root.GetProperty("message").GetString());
        Assert.Equal("Rejected", root.GetProperty("outcome").GetString());
        Assert.Equal("document_rejected", root.GetProperty("errorCode").GetString());
        Assert.Equal(batchId.ToString(), root.GetProperty("batchId").GetString());
        Assert.Equal(91, root.GetProperty("sourceDocumentOccurrenceId").GetInt64());
        Assert.DoesNotContain("Internal server error", root.ToString());
    }

    private ManualUploadController CreateController(IDocumentIngestion ingestion, long? tenantClaim)
    {
        Directory.CreateDirectory(_root);
        var controller = new ManualUploadController(
            null!,
            _database.ContextFor(tenantClaim),
            NullLogger<ManualUploadController>.Instance,
            new LocalFileStorage(_root, _root),
            ingestion);
        var identity = tenantClaim.HasValue
            ? new ClaimsIdentity([new Claim("businessUnitId", tenantClaim.Value.ToString())], "test")
            : new ClaimsIdentity([], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static IFormFile File(string content, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    private sealed class RecordingIngestion : IDocumentIngestion
    {
        public List<long> BusinessUnitIds { get; } = [];

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null,
            CancellationToken ct = default)
        {
            BusinessUnitIds.Add(businessUnitId);
            return Task.FromResult(new IngestedDocument
            {
                JobId = 42,
                BatchId = batchId ?? Guid.NewGuid(),
                ContentHash = new string('a', 64),
                StoragePath = "evidence://test",
                Outcome = EnqueueOutcome.Enqueued
            });
        }
    }

    private sealed class RefusingIngestion(Exception failure) : IDocumentIngestion
    {
        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null,
            CancellationToken ct = default) => throw failure;
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

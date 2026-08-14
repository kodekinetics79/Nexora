using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
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

    private ManualUploadController CreateController(RecordingIngestion ingestion, long? tenantClaim)
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

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

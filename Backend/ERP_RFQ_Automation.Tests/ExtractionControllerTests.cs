using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class ExtractionControllerTests
{
    [Fact]
    public async Task Upload_RejectsInvalidEnvelopeBeforeCreatingBatch()
    {
        var ingestion = new RecordingIngestion();
        var controller = CreateController(ingestion);
        var valid = FormFile("sku,qty\nA,1", "valid.csv");
        var empty = new FormFile(new MemoryStream(), 0, 0, "files", "empty.csv");

        var result = await controller.Upload([valid, empty]);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, ingestion.CallCount);
    }

    private static ExtractionController CreateController(IDocumentIngestion ingestion)
    {
        var controller = new ExtractionController(ingestion, NullLogger<ExtractionController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("businessUnitId", "1")], "test"))
            }
        };
        return controller;
    }

    private static IFormFile FormFile(string content, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name);
    }

    private sealed class RecordingIngestion : IDocumentIngestion
    {
        public int CallCount { get; private set; }

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("Invalid envelopes must not reach ingestion.");
        }
    }
}

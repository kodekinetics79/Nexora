using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
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

    [Fact]
    public async Task Upload_ReusesBatchAndOccurrenceIdentityForSameHttpIdempotencyKey()
    {
        var ingestion = new RecordingIngestion { ReturnSuccess = true };
        var controller = CreateController(ingestion);
        controller.Request.Headers["Idempotency-Key"] = "stable-upload";

        await controller.Upload([FormFile("sku,qty\nA,1", "rfq.csv")]);
        await controller.Upload([FormFile("sku,qty\nA,1", "rfq.csv")]);

        Assert.Equal(2, ingestion.CallCount);
        Assert.Equal(ingestion.BatchIds[0], ingestion.BatchIds[1]);
        Assert.Equal(ingestion.SourceOccurrenceIds[0], ingestion.SourceOccurrenceIds[1]);
    }

    [Fact]
    public async Task Upload_ReturnsBadRequestForMalformedTenantClaim()
    {
        var ingestion = new RecordingIngestion();
        var controller = CreateController(ingestion, "not-a-number");

        var result = await controller.Upload([FormFile("sku,qty\nA,1", "rfq.csv")]);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, ingestion.CallCount);
    }

    /// <summary>
    /// REGRESSION. The storage refusal is only worth having if a healthy upload is untouched:
    /// every file still reaches ingestion, still comes back Enqueued with its job id, and the
    /// new refusal fields appear nowhere. A guard that quietly starts refusing good batches
    /// would be a worse outage than the one it was written for.
    /// </summary>
    [Fact]
    public async Task Upload_StillStoresAndQueuesEveryFileWhenStorageIsHealthy()
    {
        var ingestion = new RecordingIngestion { ReturnSuccess = true };
        var controller = CreateController(ingestion);

        var result = Assert.IsType<ObjectResult>(await controller.Upload([
            FormFile("sku,qty\nA,1", "first.csv"),
            FormFile("sku,qty\nB,2", "second.csv")]));

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal(2, ingestion.CallCount);
        // The tenant came from the authenticated claim, never from the form.
        Assert.Equal([1L, 1L], ingestion.BusinessUnitIds);

        var body = Serialize(result.Value);
        Assert.Contains("\"jobId\":1,", body);
        Assert.Contains("\"jobId\":2,", body);
        Assert.Contains("\"fileName\":\"first.csv\"", body);
        Assert.Contains("\"fileName\":\"second.csv\"", body);
        Assert.Contains("\"outcome\":\"Enqueued\"", body);
        Assert.DoesNotContain("evidence_storage_unavailable", body);
        Assert.DoesNotContain("isConfigurationFault", body);
        Assert.DoesNotContain("ingestion_failed", body);
    }

    /// <summary>
    /// 2026-08-12: four .doc files were each answered "upload this file again" while evidence
    /// storage pointed at a bucket that did not exist. The advice could not work for any of them,
    /// and the readiness probe already knew why. One refusal, and the batch stops on the first file
    /// rather than uploading three more into a store that cannot take them.
    /// </summary>
    [Fact]
    public async Task Upload_RefusesWholeBatchOnceWhenEvidenceStorageIsUnavailable()
    {
        var ingestion = new RecordingIngestion
        {
            ThrowOn = _ => new EvidenceStorageUnavailableException(
                isConfigurationFault: true,
                new InvalidOperationException(
                    "The specified bucket does not exist: NexoraB2 "
                    + "(endpoint https://s3.nexora.internal:9000, key AKIAEXAMPLE7NEXORA)"))
        };
        var controller = CreateController(ingestion);

        var result = Assert.IsType<ObjectResult>(await controller.Upload([
            FormFile("sku,qty\nA,1", "C001046526.doc"),
            FormFile("sku,qty\nB,2", "C001046527.doc"),
            FormFile("sku,qty\nC,3", "C001046528.doc"),
            FormFile("sku,qty\nD,4", "C001046529.doc")]));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        // Stopped at the first file: the remaining three were never handed to ingestion.
        Assert.Equal(1, ingestion.CallCount);

        var body = Serialize(result.Value);
        Assert.Contains("\"errorCode\":\"evidence_storage_unavailable\"", body);
        Assert.Contains("\"isConfigurationFault\":true", body);
        Assert.DoesNotContain("ingestion_failed", body);
        Assert.DoesNotContain("Upload this file again", body);
        // Retrying cannot work against a bucket that does not exist, so the sentence must not
        // offer it. This is the whole defect: the advice, not the status code.
        Assert.Contains(
            "\"detail\":\"Document storage is not configured, so uploads are paused. "
            + "Retrying will not help until an administrator corrects the document storage settings.\"",
            body);
        Assert.DoesNotContain("try again", body);
        // The provider's own account stays in the log line the controller wrote and reaches no
        // part of the response — bucket, endpoint, credential or stack frame alike.
        foreach (var leak in new[]
                 {
                     "NexoraB2", "s3.nexora.internal", "AKIAEXAMPLE7NEXORA", "9000",
                     "Amazon", "S3", "Exception", "bucket", "Bucket", "   at "
                 })
        {
            Assert.DoesNotContain(leak, body);
        }
    }

    /// <summary>
    /// The other half of the same rule: a real poison file must keep its own per-file outcome and
    /// must not be flattened into a storage outage, because "fix the document" and "fix the
    /// configuration" are opposite instructions.
    /// </summary>
    [Fact]
    public async Task Upload_KeepsPerFileOutcomeAndContinuesBatchForAGenuinelyPerFileFailure()
    {
        var ingestion = new RecordingIngestion
        {
            ReturnSuccess = true,
            ThrowOn = fileName => fileName == "poison.csv"
                ? new InvalidDataException("An existing evidence object conflicts with its content address.")
                : null
        };
        var controller = CreateController(ingestion);

        var result = Assert.IsType<ObjectResult>(await controller.Upload([
            FormFile("sku,qty\nA,1", "poison.csv"),
            FormFile("sku,qty\nB,2", "good.csv")]));

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal(2, ingestion.CallCount);

        var body = Serialize(result.Value);
        Assert.Contains("\"errorCode\":\"ingestion_failed\"", body);
        Assert.Contains("\"reason\":\"Failed to enqueue file.\"", body);
        Assert.DoesNotContain("evidence_storage_unavailable", body);
    }

    /// <summary>
    /// A mid-batch outage must report the files that WERE durably stored — erasing them would tell
    /// the operator to re-upload documents that are already processing.
    /// </summary>
    [Fact]
    public async Task Upload_ReportsFilesStoredBeforeStorageFailedAndLeaksNoInfrastructureDetail()
    {
        var ingestion = new RecordingIngestion
        {
            ReturnSuccess = true,
            ThrowOn = fileName => fileName == "second.csv"
                ? new EvidenceStorageUnavailableException(
                    isConfigurationFault: false,
                    new HttpRequestException("Connection refused to https://s3.nexora.internal:9000"))
                : null
        };
        var controller = CreateController(ingestion);

        var result = Assert.IsType<ObjectResult>(await controller.Upload([
            FormFile("sku,qty\nA,1", "first.csv"),
            FormFile("sku,qty\nB,2", "second.csv"),
            FormFile("sku,qty\nC,3", "third.csv")]));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(2, ingestion.CallCount);

        var body = Serialize(result.Value);
        Assert.Contains("\"fileName\":\"first.csv\"", body);
        Assert.Contains("\"isConfigurationFault\":false", body);
        // One file reached durable storage. The count is the server's, because `jobs` also carries
        // per-file REFUSALS and reading its length reported a quarantined document as accepted.
        Assert.Contains("\"accepted\":1", body);
        // The operator sentence rides in `detail`, where every door that renders server text looks
        // for it. As `title` alone it was dropped, and the caller fell back to "try again shortly".
        Assert.Contains("\"detail\":\"Document storage is unavailable, so uploads are paused.", body);
        foreach (var leak in new[]
                 {
                     "NexoraB2", "s3.nexora.internal", "Amazon", "S3", "Exception",
                     "bucket", "Bucket", "9000", "   at "
                 })
        {
            Assert.DoesNotContain(leak, body);
        }
    }

    private static string Serialize(object? payload) =>
        System.Text.Json.JsonSerializer.Serialize(payload);

    private static ExtractionController CreateController(IDocumentIngestion ingestion, string tenantClaim = "1")
    {
        var controller = new ExtractionController(ingestion, NullLogger<ExtractionController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("businessUnitId", tenantClaim)], "test"))
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
        public bool ReturnSuccess { get; init; }
        public List<Guid?> BatchIds { get; } = [];
        public List<string?> SourceOccurrenceIds { get; } = [];
        public List<long> BusinessUnitIds { get; } = [];

        /// <summary>Fault this file name should raise, or null to let it succeed.</summary>
        public Func<string, Exception?>? ThrowOn { get; init; }

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null,
            CancellationToken ct = default)
        {
            CallCount++;
            BatchIds.Add(batchId);
            SourceOccurrenceIds.Add(metadata?.SourceOccurrenceId);
            BusinessUnitIds.Add(businessUnitId);
            if (ThrowOn?.Invoke(fileName) is { } fault)
                throw fault;
            if (ReturnSuccess)
                return Task.FromResult(new IngestedDocument
                {
                    JobId = CallCount,
                    SourceDocumentOccurrenceId = CallCount,
                    BatchId = batchId!.Value,
                    ContentHash = new string('a', 64),
                    StoragePath = "evidence://test",
                    Outcome = EnqueueOutcome.Enqueued
                });
            throw new InvalidOperationException("Invalid envelopes must not reach ingestion.");
        }
    }
}

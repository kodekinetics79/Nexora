using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Additive async ingest surface for the durable extraction pipeline (ADR-0003).
    /// Each uploaded file is persisted to an immutable, content-addressed path and fanned
    /// out to its OWN queue job — files are NEVER merged. Storage + enqueue now live in
    /// the shared <see cref="IDocumentIngestion"/> gateway so all four ingestion doors
    /// (this endpoint, email poller, folder watcher, manual upload) use one code path.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Rfq)]
    public class ExtractionController : ControllerBase
    {
        private readonly IDocumentIngestion _ingestion;
        private readonly ILogger<ExtractionController> _logger;

        // Interactive uploads outrank bulk backfills in the weighted-fair claim ordering.
        private const int InteractivePriority = 10;
        private const long MaxBytesPerFile = 25L * 1024 * 1024; // 25 MB, mirrors ManualUpload
        private const long MaxBytesPerBatch = 200L * 1024 * 1024;
        private const int MaxFilesPerBatch = 50;

        public ExtractionController(
            IDocumentIngestion ingestion,
            ILogger<ExtractionController> logger)
        {
            _ingestion = ingestion;
            _logger = logger;
        }

        /// <summary>
        /// Accepts multipart files, persists each immutably, and enqueues one extraction job
        /// per file. Returns 202 Accepted with the shared batch id and a per-file outcome.
        /// </summary>
        [HttpPost("upload")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequestSizeLimit(MaxBytesPerBatch)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBytesPerBatch)]
        // Uploading documents creates leads — same gate as the manual-upload lead pages.
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> Upload([FromForm] List<IFormFile> files, CancellationToken ct = default)
        {
            if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var businessUnitId)
                || businessUnitId <= 0)
                return BadRequest(new { success = false, message = "A valid businessUnitId claim is required." });

            if (files == null || files.Count == 0)
                return BadRequest(new { success = false, message = "No files uploaded." });
            if (files.Count > MaxFilesPerBatch)
                return BadRequest(new { success = false, message = $"A batch can contain at most {MaxFilesPerBatch} files." });
            if (files.Sum(file => file.Length) > MaxBytesPerBatch)
                return BadRequest(new { success = false, message = "The batch exceeds the 200 MB limit." });
            if (files.Any(file => file.Length == 0))
                return BadRequest(new { success = false, message = "Empty files cannot be uploaded. Remove them and retry the batch." });
            if (files.Any(file => file.Length > MaxBytesPerFile))
                return BadRequest(new { success = false, message = "Each file must be 25 MB or smaller." });

            var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var key)
                ? key.ToString().Trim()
                : null;
            if (idempotencyKey?.Length > 128)
                return BadRequest(new { success = false, message = "Idempotency-Key must be 128 characters or fewer." });
            var batchId = string.IsNullOrWhiteSpace(idempotencyKey)
                ? Guid.NewGuid()
                : StableBatchId(businessUnitId, idempotencyKey);
            var results = new List<object>(files.Count);
            // Counted separately from `results`, which also carries every per-file REFUSAL —
            // rejected, quarantined, held for scanning, failed to enqueue. Reading its length as
            // "documents accepted" would report a malware quarantine as stored and processing.
            var accepted = 0;

            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                var file = files[fileIndex];
                try
                {
                    byte[] bytes;
                    await using (var ms = new MemoryStream())
                    {
                        await file.CopyToAsync(ms, ct);
                        bytes = ms.ToArray();
                    }

                    var ingested = await _ingestion.IngestAsync(
                        bytes, file.FileName, businessUnitId,
                        ExtractionSourceType.ManualUpload,
                        batchId, InteractivePriority,
                        metadata: new ExtractionJobMetadata
                        {
                            SourceOccurrenceId = string.IsNullOrWhiteSpace(idempotencyKey)
                                ? null
                                : $"{idempotencyKey}:{fileIndex}:{file.FileName}",
                            UploadedBy = User.FindFirst(ClaimTypes.Email)?.Value
                                ?? User.Identity?.Name
                                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        },
                        ct: ct);

                    accepted++;
                    results.Add(new
                    {
                        jobId = ingested.JobId,
                        occurrenceId = (long?)ingested.SourceDocumentOccurrenceId,
                        fileName = file.FileName,
                        outcome = ingested.Outcome.ToString(),
                        errorCode = (string?)null
                    });
                }
                catch (DocumentInspectionException ex)
                {
                    _logger.LogWarning(
                        "Upload {FileName} stopped by document inspection: {Status} {Reason}",
                        file.FileName, ex.Inspection.Status, ex.Inspection.Reason);
                    results.Add(new
                    {
                        jobId = 0L,
                        occurrenceId = ex.SourceDocumentOccurrenceId,
                        fileName = file.FileName,
                        outcome = ex.Inspection.IsRetryable
                            ? "AwaitingSecurityScan"
                            : ex.Inspection.Status.ToString(),
                        errorCode = ex.Inspection.ErrorCode,
                        reason = ex.Inspection.Reason
                    });
                }
                catch (Platform.Entitlements.EntitlementDeniedException ex)
                {
                    // Plan entitlement denial (e.g. monthly document quota): every
                    // remaining file in the batch would be denied too — stop with the
                    // typed problem response instead of a per-file "Error".
                    _logger.LogWarning("Upload batch stopped for tenant {BusinessUnitId}: {Reason}",
                        businessUnitId, ex.Message);
                    return new ObjectResult(new
                    {
                        type = ex.ProblemType,
                        title = ex.Message,
                        status = ex.SuggestedStatusCode,
                        batchId,
                        jobs = results
                    })
                    {
                        StatusCode = ex.SuggestedStatusCode,
                        ContentTypes = { "application/problem+json" }
                    };
                }
                catch (Infrastructure.Storage.EvidenceStorageUnavailableException ex)
                {
                    // Nexora stores the immutable source BEFORE it queues anything, so a store
                    // that cannot be written means no file in this batch can be accepted — the
                    // bucket does not exist for the next one either.
                    //
                    // On 2026-08-12 this loop answered four .doc files with "upload this file
                    // again", which could never work: evidence storage had been repointed at a
                    // misspelled bucket and the readiness probe already read Unhealthy. One
                    // honest refusal instead of N useless retries. The provider's own account
                    // (bucket, endpoint, error code) stays in the log line below and never
                    // enters the response.
                    _logger.LogError(ex,
                        "Upload batch stopped for tenant {BusinessUnitId}: durable evidence storage is unavailable "
                        + "(configuration fault: {IsConfigurationFault}). {Accepted} of {Total} file(s) were stored before it failed.",
                        businessUnitId, ex.IsConfigurationFault, accepted, files.Count);

                    return Infrastructure.Storage.EvidenceStorageProblemFilter.ToResult(ex,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["batchId"] = batchId,
                            // Every row decided before the outage, accepted and refused alike, so
                            // the caller keeps the whole record: a mid-batch failure must neither
                            // erase accepted work nor dress a quarantined file up as accepted.
                            ["jobs"] = results,
                            // The count of documents that actually reached durable storage. The
                            // client must not derive this from `jobs.length`, which counts
                            // refusals too.
                            ["accepted"] = accepted
                        });
                }
                catch (Exception ex)
                {
                    // Poison-file isolation at the ingest boundary: one bad file never fails the batch.
                    _logger.LogError(ex, "Failed to enqueue uploaded file {FileName}.", file.FileName);
                    results.Add(new { jobId = 0L, occurrenceId = (long?)null, fileName = file.FileName, outcome = "Error", errorCode = "ingestion_failed", reason = "Failed to enqueue file." });
                }
            }

            return StatusCode(StatusCodes.Status202Accepted, new { batchId, jobs = results });
        }

        private static Guid StableBatchId(long businessUnitId, string idempotencyKey)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"upload:{businessUnitId}:{idempotencyKey}"));
            return new Guid(digest.AsSpan(0, 16));
        }
    }
}

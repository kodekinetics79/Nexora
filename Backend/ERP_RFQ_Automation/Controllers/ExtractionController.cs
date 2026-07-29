using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
                                : $"{idempotencyKey}:{fileIndex}:{file.FileName}"
                        },
                        ct);

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

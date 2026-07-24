using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
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
        [RequestSizeLimit(200L * 1024 * 1024)]
        // Uploading documents creates leads — same gate as the manual-upload lead pages.
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> Upload([FromForm] List<IFormFile> files, CancellationToken ct = default)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0)
                return BadRequest(new { success = false, message = "A valid businessUnitId claim is required." });

            if (files == null || files.Count == 0)
                return BadRequest(new { success = false, message = "No files uploaded." });

            var batchId = Guid.NewGuid();
            var results = new List<object>(files.Count);

            foreach (var file in files)
            {
                if (file.Length == 0)
                {
                    results.Add(new { jobId = 0L, fileName = file.FileName, outcome = "Skipped", reason = "Empty file." });
                    continue;
                }
                if (file.Length > MaxBytesPerFile)
                {
                    results.Add(new { jobId = 0L, fileName = file.FileName, outcome = "Skipped", reason = "File exceeds 25 MB limit." });
                    continue;
                }

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
                            SourceOccurrenceId = Request.Headers.TryGetValue("Idempotency-Key", out var key)
                                ? $"{key}:{file.FileName}"
                                : null
                        },
                        ct);

                    results.Add(new
                    {
                        jobId = ingested.JobId,
                        fileName = file.FileName,
                        outcome = ingested.Outcome.ToString()
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
                        fileName = file.FileName,
                        outcome = ex.Inspection.Status.ToString(),
                        reason = ex.Inspection.Reason
                    });
                }
                catch (Exception ex)
                {
                    // Poison-file isolation at the ingest boundary: one bad file never fails the batch.
                    _logger.LogError(ex, "Failed to enqueue uploaded file {FileName}.", file.FileName);
                    results.Add(new { jobId = 0L, fileName = file.FileName, outcome = "Error", reason = "Failed to enqueue file." });
                }
            }

            return StatusCode(StatusCodes.Status202Accepted, new { batchId, jobs = results });
        }
    }
}

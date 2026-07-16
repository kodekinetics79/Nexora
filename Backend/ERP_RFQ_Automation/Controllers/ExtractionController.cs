using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Extraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Additive async ingest surface for the durable extraction pipeline (ADR-0003).
    /// Each uploaded file is persisted to an immutable, content-addressed path and fanned
    /// out to its OWN queue job — files are NEVER merged. This endpoint does not touch the
    /// existing synchronous ManualUpload path, so the working demo is unaffected.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ExtractionController : ControllerBase
    {
        private readonly IExtractionQueue _queue;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ExtractionController> _logger;
        private readonly string _storageRoot;

        // Interactive uploads outrank bulk backfills in the weighted-fair claim ordering.
        private const int InteractivePriority = 10;
        private const long MaxBytesPerFile = 25L * 1024 * 1024; // 25 MB, mirrors ManualUpload

        public ExtractionController(
            IExtractionQueue queue,
            IWebHostEnvironment env,
            ILogger<ExtractionController> logger)
        {
            _queue = queue;
            _env = env;
            _logger = logger;
            _storageRoot = Path.Combine(_env.ContentRootPath, "Uploads", "Extraction");
        }

        /// <summary>
        /// Accepts multipart files, persists each immutably, and enqueues one extraction job
        /// per file. Returns 202 Accepted with the shared batch id and a per-file outcome.
        /// </summary>
        [HttpPost("upload")]
        [RequestSizeLimit(200L * 1024 * 1024)]
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

                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    var storagePath = await PersistImmutableAsync(bytes, hash, file.FileName, ct);
                    var fileType = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();

                    var enqueue = await _queue.EnqueueAsync(new EnqueueExtractionRequest
                    {
                        BusinessUnitId = businessUnitId,
                        SourceType = ExtractionSourceType.ManualUpload,
                        StoragePath = storagePath,
                        FileName = file.FileName,
                        FileType = string.IsNullOrEmpty(fileType) ? null : fileType,
                        Content = bytes,          // bytes hashed in one place by the queue
                        ContentHash = hash,       // precomputed for the content-addressed path
                        BatchId = batchId,
                        Priority = InteractivePriority
                    }, ct);

                    results.Add(new
                    {
                        jobId = enqueue.JobId,
                        fileName = file.FileName,
                        outcome = enqueue.Outcome.ToString()
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

        /// <summary>
        /// Writes the raw bytes to an immutable, content-addressed path
        /// (Uploads/Extraction/&lt;aa&gt;/&lt;sha256&gt;&lt;ext&gt;). Identical bytes reuse the same
        /// file — a re-upload never rewrites or mutates existing content.
        /// </summary>
        private async Task<string> PersistImmutableAsync(byte[] bytes, string hash, string originalName, CancellationToken ct)
        {
            var ext = Path.GetExtension(originalName);
            var shard = hash[..2];
            var dir = Path.Combine(_storageRoot, shard);
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, hash + ext);
            if (!System.IO.File.Exists(path))
            {
                // Write to a temp file then atomically move so a concurrent reader never sees a partial file.
                var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                await System.IO.File.WriteAllBytesAsync(tmp, bytes, ct);
                try
                {
                    System.IO.File.Move(tmp, path, overwrite: false);
                }
                catch (IOException)
                {
                    // Another request materialized the same content first; the existing file is authoritative.
                    try { System.IO.File.Delete(tmp); } catch { /* best-effort cleanup */ }
                }
            }
            return path;
        }
    }
}

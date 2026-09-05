using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Authorization;

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ManualUploadController : ControllerBase
    {
        private readonly ManualUploadService _manualUploadService;
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<ManualUploadController> _logger;
        private readonly string _attachmentPath;
        private readonly IDocumentIngestion _ingestion;

        public ManualUploadController(
            ManualUploadService manualUploadService,
            ErpRfqAutomationContext context,
            ILogger<ManualUploadController> logger,
            IFileStorage storage,
            IDocumentIngestion ingestion)
        {
            _manualUploadService = manualUploadService;
            _context = context;
            _logger = logger;
            _attachmentPath = storage.GetPath("Manual_Attachments");
            _ingestion = ingestion;
        }

        /// <summary>
        /// Uploads multiple files, processes them to extract lead data, and saves to the database.
        /// </summary>
        /// <param name="files">The files to upload.</param>
        /// <param name="businessUnitId">The BusinessUnitId for the lead.</param>
        /// <returns>The ID of the created Lead.</returns>
        /// <summary>
        /// Uploads multiple files, processes them to extract lead data, and saves to the database.
        /// </summary>
        /// <param name="files">The files to upload.</param>
        /// <param name="businessUnitId">The BusinessUnitId for the lead.</param>
        /// <returns>The ID of the created Lead.</returns>
        [HttpPost("upload")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> UploadFiles(List<IFormFile> files, [FromForm] long? businessUnitId = null)
        {
            if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                return BadRequest(new { success = false, message = "A valid businessUnitId claim is required." });

            if (files == null || !files.Any())
            {
                return BadRequest(new { success = false, message = "No files uploaded." });
            }

            // Declared outside the try so a refusal can still report what it accepted first.
            var accepted = new List<object>();
            try
            {
                foreach (var file in files)
                {
                    if (file.Length == 0 || file.Length > 25L * 1024 * 1024)
                        return BadRequest(new { success = false, message = $"{file.FileName} is empty or exceeds 25 MB." });
                    await using var content = new MemoryStream();
                    await file.CopyToAsync(content, HttpContext.RequestAborted);
                    var metadata = new ExtractionJobMetadata
                    {
                        SourceOccurrenceId = Request.Headers.TryGetValue("Idempotency-Key", out var key)
                            ? $"{key}:{file.FileName}" : null,
                        UploadedBy = User.FindFirst(ClaimTypes.Email)?.Value
                            ?? User.Identity?.Name
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    };
                    var ingested = await _ingestion.IngestAsync(content.ToArray(), file.FileName, targetBUId,
                        ExtractionSourceType.ManualUpload, priority: 10, metadata: metadata,
                        ct: HttpContext.RequestAborted);
                    accepted.Add(new { ingested.JobId, ingested.BatchId, outcome = ingested.Outcome.ToString() });
                }
                return Accepted(new { success = true, message = "Files accepted for governed extraction.", data = accepted });
            }
            catch (DocumentInspectionException ex)
            {
                // The file was refused by document inspection — a decision about the caller's
                // bytes, with a sentence that names the fix. This used to fall into the generic
                // catch below as "Internal server error" (DocumentInspectionException is an
                // IOException), so CSV bytes named ".xlsx" looked like an outage. The refusal is
                // already recorded on its occurrence and batch; say so, exactly as
                // UploadCustomerRfqExcel does (intake scenario audit 2026-09-04, finding F4).
                _logger.LogWarning("Upload stopped by document inspection for tenant {BusinessUnitId}: {Status} {Reason}",
                    targetBUId, ex.Inspection.Status, ex.Inspection.Reason);
                return UnprocessableEntity(new
                {
                    success = false,
                    outcome = ex.Inspection.IsRetryable ? "AwaitingSecurityScan" : ex.Inspection.Status.ToString(),
                    errorCode = ex.Inspection.ErrorCode,
                    message = ex.Inspection.Reason,
                    batchId = ex.BatchId,
                    sourceDocumentOccurrenceId = ex.SourceDocumentOccurrenceId,
                    accepted
                });
            }
            catch (Platform.Entitlements.EntitlementDeniedException ex)
            {
                _logger.LogWarning("Upload denied for tenant {BusinessUnitId}: {Reason}", targetBUId, ex.Message);
                return EntitlementProblem(ex);
            }
            catch (EvidenceStorageUnavailableException ex)
            {
                // Nexora stores the immutable source before it queues anything, so a store it
                // cannot write means nothing further can be accepted — for this file or the next
                // one. A generic 500 would send the caller looking at their documents; this names
                // the one thing that is actually broken. Specifics stay in the log line.
                //
                // `accepted` is not thrown away with the exception: whatever was stored earlier in
                // this same loop is already processing, and a refusal that hid it would report a
                // half-run batch as a clean rejection.
                _logger.LogError(ex,
                    "Upload refused for tenant {BusinessUnitId}: durable evidence storage is unavailable "
                    + "(configuration fault: {IsConfigurationFault}). {Accepted} file(s) were accepted first.",
                    targetBUId, ex.IsConfigurationFault, accepted.Count);
                return EvidenceStorageProblemFilter.ToResult(ex,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["accepted"] = accepted });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during file upload.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        /// <summary>
        /// Uploads a specialized RFQ Excel file and creates an RFQ.
        /// </summary>
        [HttpPost("upload-rfq-excel")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> UploadCustomerRfqExcel(IFormFile file, [FromForm] long? businessUnitId = null, [FromForm] string? createdBy = null)
        {
            if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                return BadRequest(new { success = false, message = "A valid businessUnitId claim is required." });

            if (file == null)
            {
                return BadRequest(new { success = false, message = "No file uploaded." });
            }

            try
            {
                if (file.Length == 0)
                    return BadRequest(new { success = false, message = "The uploaded file is empty." });
                if (file.Length > 25L * 1024 * 1024)
                    return BadRequest(new { success = false, message = "File exceeds 25 MB limit." });

                byte[] bytes;
                await using (var content = new MemoryStream())
                {
                    await file.CopyToAsync(content, HttpContext.RequestAborted);
                    bytes = content.ToArray();
                }

                var metadata = new ExtractionJobMetadata
                {
                    SourceOccurrenceId = Request.Headers.TryGetValue("Idempotency-Key", out var key)
                        ? $"{key}:{file.FileName}" : null,
                    UploadedBy = User.FindFirst(ClaimTypes.Email)?.Value
                        ?? User.Identity?.Name
                        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                };
                var ingested = await _ingestion.IngestAsync(bytes, file.FileName, targetBUId,
                    ExtractionSourceType.ExcelTemplate, priority: 10,
                    metadata: metadata, ct: HttpContext.RequestAborted);
                return Accepted(new
                {
                    success = true,
                    message = "RFQ spreadsheet accepted for governed extraction.",
                    data = new { ingested.JobId, ingested.BatchId, outcome = ingested.Outcome.ToString() }
                });
            }
            catch (DocumentInspectionException ex)
            {
                _logger.LogWarning("RFQ Excel upload stopped by inspection: {Status} {Reason}",
                    ex.Inspection.Status, ex.Inspection.Reason);
                return UnprocessableEntity(new
                {
                    success = false,
                    outcome = ex.Inspection.Status.ToString(),
                    message = ex.Inspection.Reason
                });
            }
            catch (Platform.Entitlements.EntitlementDeniedException ex)
            {
                _logger.LogWarning("RFQ Excel upload denied for tenant {BusinessUnitId}: {Reason}", targetBUId, ex.Message);
                return EntitlementProblem(ex);
            }
            catch (EvidenceStorageUnavailableException ex)
            {
                _logger.LogError(ex,
                    "RFQ spreadsheet upload refused for tenant {BusinessUnitId}: durable evidence storage is "
                    + "unavailable (configuration fault: {IsConfigurationFault}).", targetBUId, ex.IsConfigurationFault);
                return EvidenceStorageProblemFilter.ToResult(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during customer RFQ Excel upload.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        /// <summary>
        /// Lists the uploaded files for manual leads.
        /// </summary>
        /// <returns>A list of file information.</returns>
        [HttpGet("list")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<IActionResult> ListFiles([FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                    return BadRequest(new { success = false, message = "A valid businessUnitId claim is required." });

                // Query attachments from DB where ParentType is Lead and Lead.LeadSource is Manual
                var manualLeads = await _context.Leads
                    .AsNoTracking()
                    .Where(l => l.LeadSource == "Manual" && l.BusinessUnitId == targetBUId)
                    .Select(l => l.Id)
                    .ToListAsync();

                var files = await _context.Attachments
                    .AsNoTracking()
                    .Where(a => a.ParentType == "Lead" && manualLeads.Contains(a.ParentId))
                    .Select(a => new
                    {
                        a.Id,
                        a.FileName,
                        a.FileSize,
                        a.UploadedDate,
                        LeadId = a.ParentId
                    })
                    .OrderByDescending(a => a.UploadedDate)
                    .ToListAsync();

                return Ok(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing files.");
                return StatusCode(500, "Internal server error.");
            }
        }

        private bool TryGetAuthenticatedBusinessUnitId(out long businessUnitId)
            => long.TryParse(User.FindFirstValue("businessUnitId"), out businessUnitId)
               && businessUnitId > 0;

        /// <summary>
        /// Typed plan-entitlement denial (e.g. monthly document quota) as problem+json.
        /// P2-A10: rendered by the single canonical <see cref="Platform.Entitlements.EntitlementProblemFilter"/>
        /// shape so every denial in the product shares one payload and one type base URI.
        /// </summary>
        private static IActionResult EntitlementProblem(Platform.Entitlements.EntitlementDeniedException ex)
            => Platform.Entitlements.EntitlementProblemFilter.ToResult(ex);
    }
}

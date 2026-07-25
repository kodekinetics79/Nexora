using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class LeadUploaderController : ControllerBase
    {
        private readonly LeadUploaderService _service;
        private readonly IDocumentIngestion _ingestion;
        private readonly ILogger<LeadUploaderController> _logger;

        public LeadUploaderController(LeadUploaderService service, IDocumentIngestion ingestion, ILogger<LeadUploaderController> logger)
        {
            _service = service;
            _ingestion = ingestion;
            _logger = logger;
        }

        [HttpGet("download-template")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<IActionResult> DownloadTemplate([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId;

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.GenerateTemplateAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "Lead_Template.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating lead template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("upload-template")]
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> UploadTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId;

            if (targetBUId <= 0)
                return BadRequest(new { success = false, message = "Business Unit ID is required." });

            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, message = "No file uploaded." });

            try
            {
                await using var stream = new MemoryStream();
                await file.CopyToAsync(stream, HttpContext.RequestAborted);
                var batchId = Guid.NewGuid();
                var idempotency = Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : batchId.ToString("N");
                var result = await _ingestion.IngestAsync(stream.ToArray(), file.FileName, targetBUId,
                    ExtractionSourceType.ManualUpload, batchId, priority: 10,
                    metadata: new ExtractionJobMetadata { SourceOccurrenceId = $"template:{idempotency}:{file.FileName}" },
                    HttpContext.RequestAborted);
                return StatusCode(StatusCodes.Status202Accepted, new { success = true, batchId, jobId = result.JobId, outcome = result.Outcome.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading lead template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }
    }
}

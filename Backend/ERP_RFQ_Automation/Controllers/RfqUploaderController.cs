using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RfqUploaderController : ControllerBase
    {
        private readonly RfqUploaderService _service;
        private readonly IFileInspectionService _fileInspection;
        private readonly ILogger<RfqUploaderController> _logger;

        public RfqUploaderController(
            RfqUploaderService service,
            IFileInspectionService fileInspection,
            ILogger<RfqUploaderController> logger)
        {
            _service = service;
            _fileInspection = fileInspection;
            _logger = logger;
        }

        [HttpGet("download-template")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<IActionResult> DownloadTemplate([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.GenerateTemplateAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "RFQ_Template.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating RFQ template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("upload-template")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequireModulePermission("RFQ Management", PermissionAction.Create)]
        public async Task<IActionResult> UploadTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
        {
            await Task.CompletedTask;
            return Conflict(new
            {
                code = "LEAD_WORKBENCH_REQUIRED",
                message = "Direct spreadsheet-to-RFQ creation is retired. Upload the document through governed Lead intake, reconcile the canonical Lead Revision, commit participation, and promote approved lines."
            });
#pragma warning disable CS0162
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest(new { success = false, message = "Business Unit ID is required." });

            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, message = "No file uploaded." });

            try
            {
                var createdBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                // Inspected BEFORE parsing (signature, archive safety, malware verdict), and refused
                // with the shared problem shape when inspection says no or cannot answer.
                await using var inspected = await UploadInspectionGate.InspectAsync(
                    _fileInspection, file, HttpContext.RequestAborted);
                if (!inspected.IsCleared)
                    return UploadInspectionGate.Refuse(this, inspected.Inspection, "RFQ import file rejected");
                var stream = inspected.Content;
                var result = await _service.UploadTemplateAsync(stream, targetBUId, createdBy);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading RFQ template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
#pragma warning restore CS0162
        }

    }
}

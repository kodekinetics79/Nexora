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
    public class QuotationUploaderController : ControllerBase
    {
        private readonly QuotationUploaderService _service;
        private readonly IFileInspectionService _fileInspection;
        private readonly ILogger<QuotationUploaderController> _logger;

        public QuotationUploaderController(
            QuotationUploaderService service,
            IFileInspectionService fileInspection,
            ILogger<QuotationUploaderController> logger)
        {
            _service = service;
            _fileInspection = fileInspection;
            _logger = logger;
        }

        [HttpGet("download-template")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<IActionResult> DownloadTemplate([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUIdAttr = User.FindFirst("businessUnitId")?.Value;
                var claimBUId = string.IsNullOrEmpty(claimBUIdAttr) ? 0 : long.Parse(claimBUIdAttr);
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.GenerateTemplateAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "Quotation_Template.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating Quotation template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("upload-template")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<IActionResult> UploadTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
        {
            var claimBUIdAttr = User.FindFirst("businessUnitId")?.Value;
            var claimBUId = string.IsNullOrEmpty(claimBUIdAttr) ? 0 : long.Parse(claimBUIdAttr);
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
                    return UploadInspectionGate.Refuse(this, inspected.Inspection, "Quotation import file rejected");
                var stream = inspected.Content;
                var result = await _service.UploadTemplateAsync(stream, targetBUId, createdBy);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading Quotation template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        /// <summary>
        /// Bulk BACK-FILL: quotes the tenant issued before Nexora existed.
        ///
        /// <para>A separate route rather than a flag on the ordinary upload, deliberately. A blank
        /// 'Customer RFQ No' on a normal sheet is overwhelmingly a mistake, and a mode that
        /// silently invented an inquiry to absorb it would corrupt the commercial spine to save
        /// the operator a correction. Choosing this endpoint is the operator saying, explicitly,
        /// that these quotes predate the system.</para>
        ///
        /// <para>Rows that DO name an RFQ are resolved normally, so a mixed sheet still attaches
        /// each quote to its real inquiry where one exists.</para>
        /// </summary>
        [HttpPost("upload-backfill")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<IActionResult> UploadBackfill(IFormFile file, [FromForm] long? businessUnitId = null)
        {
            var claimBUIdAttr = User.FindFirst("businessUnitId")?.Value;
            var claimBUId = string.IsNullOrEmpty(claimBUIdAttr) ? 0 : long.Parse(claimBUIdAttr);
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
                    return UploadInspectionGate.Refuse(this, inspected.Inspection, "Quotation back-fill file rejected");
                var stream = inspected.Content;
                var result = await _service.UploadTemplateAsync(stream, targetBUId, createdBy, backfill: true);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error back-filling quotations.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }
    }
}

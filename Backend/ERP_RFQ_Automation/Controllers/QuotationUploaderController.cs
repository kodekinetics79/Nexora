using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Authorization;
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
        private readonly ILogger<QuotationUploaderController> _logger;

        public QuotationUploaderController(QuotationUploaderService service, ILogger<QuotationUploaderController> logger)
        {
            _service = service;
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
                using var stream = file.OpenReadStream();
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
    }
}

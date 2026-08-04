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
    public class RfqUploaderController : ControllerBase
    {
        private readonly RfqUploaderService _service;
        private readonly ILogger<RfqUploaderController> _logger;

        public RfqUploaderController(RfqUploaderService service, ILogger<RfqUploaderController> logger)
        {
            _service = service;
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
        [RequireModulePermission("RFQ Management", PermissionAction.Create)]
        public async Task<IActionResult> UploadTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest(new { success = false, message = "Business Unit ID is required." });

            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, message = "No file uploaded." });

            var intakeError = await ValidateSpreadsheetUploadAsync(file);
            if (intakeError != null)
                return BadRequest(new { success = false, message = intakeError });

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
                _logger.LogError(ex, "Error uploading RFQ template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        private static async Task<string?> ValidateSpreadsheetUploadAsync(IFormFile file)
        {
            const long maxUploadBytes = 20 * 1024 * 1024;
            if (file.Length > maxUploadBytes)
                return "RFQ upload exceeds the 20 MB safety limit.";

            var buffer = new byte[4];
            await using var stream = file.OpenReadStream();
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));

            if (read < 4 || buffer[0] != 0x50 || buffer[1] != 0x4B || buffer[2] != 0x03 || buffer[3] != 0x04)
                return "RFQ upload must be a valid XLSX file. File type is verified from content, not the extension.";

            return null;
        }
    }
}

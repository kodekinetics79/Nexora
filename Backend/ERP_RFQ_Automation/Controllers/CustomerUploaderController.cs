using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CustomerUploaderController : ControllerBase
    {
        private readonly CustomerUploaderService _service;
        private readonly ILogger<CustomerUploaderController> _logger;

        public CustomerUploaderController(CustomerUploaderService service, ILogger<CustomerUploaderController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet("download-template")]
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
                    "CustomerTemplate.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating customer template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("upload-template")]
        public async Task<IActionResult> UploadTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
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
                _logger.LogError(ex, "Error uploading customer template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpGet("export")]
        public async Task<IActionResult> ExportData([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.ExportCustomersAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "Customers.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting customer data.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }
    }
}

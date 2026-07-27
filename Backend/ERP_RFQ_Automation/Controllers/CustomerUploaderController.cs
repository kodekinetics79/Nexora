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
    public class CustomerUploaderController : ControllerBase
    {
        private readonly CustomerUploaderService _service;
        private readonly IFileInspectionService _fileInspection;
        private readonly ILogger<CustomerUploaderController> _logger;

        public CustomerUploaderController(
            CustomerUploaderService service,
            IFileInspectionService fileInspection,
            ILogger<CustomerUploaderController> logger)
        {
            _service = service;
            _fileInspection = fileInspection;
            _logger = logger;
        }

        [HttpGet("download-template")]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<IActionResult> DownloadTemplate()
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
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
        [RequireModulePermission("Customers", PermissionAction.Create)]
        [RequireModulePermission("Customers", PermissionAction.Edit)]
        public async Task<IActionResult> UploadTemplate(IFormFile file, CancellationToken cancellationToken)
        {
            if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                return BadRequest(new { success = false, message = "Business Unit ID is required." });

            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, message = "No file uploaded." });

            try
            {
                var createdBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                await using var stream = new MemoryStream();
                await file.CopyToAsync(stream, cancellationToken);
                stream.Position = 0;
                var inspection = await _fileInspection.InspectAsync(
                    new FileInspectionRequest(
                        stream,
                        Path.GetFileName(file.FileName),
                        file.ContentType,
                        file.Length),
                    cancellationToken);
                if (!inspection.IsCleared)
                    return BadRequest(new { success = false, message = "The customer import file failed security inspection." });

                stream.Position = 0;
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
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<IActionResult> ExportData()
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
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

        private bool TryGetAuthenticatedBusinessUnitId(out long businessUnitId) =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId) && businessUnitId > 0;
    }
}

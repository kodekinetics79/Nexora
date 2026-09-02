using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Security.DocumentInspection;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ProductUploaderController : ControllerBase
    {
        private readonly ProductUploaderService _productUploaderService;
        private readonly IFileInspectionService _fileInspection;
        private readonly ILogger<ProductUploaderController> _logger;

        public ProductUploaderController(
            ProductUploaderService productUploaderService,
            IFileInspectionService fileInspection,
            ILogger<ProductUploaderController> logger)
        {
            _productUploaderService = productUploaderService;
            _fileInspection = fileInspection;
            _logger = logger;
        }

        [HttpGet("download-template")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<IActionResult> DownloadTemplate([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _productUploaderService.GenerateTemplateAsync(targetBUId);
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "ProductTemplate.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating product template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("upload-template")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
        [RequireModulePermission("Products", PermissionAction.Create)]
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
                // Inspected BEFORE parsing (signature, archive safety, malware verdict), and refused
                // with the shared problem shape when inspection says no or cannot answer.
                await using var inspected = await UploadInspectionGate.InspectAsync(
                    _fileInspection, file, HttpContext.RequestAborted);
                if (!inspected.IsCleared)
                    return UploadInspectionGate.Refuse(this, inspected.Inspection, "Product import file rejected");
                var stream = inspected.Content;
                var result = await _productUploaderService.UploadTemplateAsync(stream, targetBUId, createdBy);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message, data = result.Data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading product template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpGet("export")]
        [RequireModulePermission("Products", PermissionAction.View)]
        [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(
            ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Exports)]
        public async Task<IActionResult> ExportProducts([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _productUploaderService.ExportProductsAsync(targetBUId);
                return File(fileBytes, 
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 
                    "Products.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting products.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }
    }
}

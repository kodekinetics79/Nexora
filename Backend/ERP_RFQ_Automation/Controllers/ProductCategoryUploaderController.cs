using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ERP_RFQ_Automation.Services;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ProductCategoryUploaderController : ControllerBase
    {
        private readonly ProductCategoryUploaderService _service;
        private readonly ILogger<ProductCategoryUploaderController> _logger;

        public ProductCategoryUploaderController(
            ProductCategoryUploaderService service,
            ILogger<ProductCategoryUploaderController> logger)
        {
            _service = service;
            _logger = logger;
        }

        // ─── PRODUCT CATEGORY ───────────────────────────

        [HttpGet("category/download-template")]
        public async Task<IActionResult> DownloadCategoryTemplate([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.GenerateCategoryTemplateAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "ProductCategoryTemplate.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating product category template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("category/upload-template")]
        public async Task<IActionResult> UploadCategoryTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
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
                var result = await _service.UploadCategoryTemplateAsync(stream, targetBUId, createdBy);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message, data = result.Data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading product category template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpGet("category/export")]
        public async Task<IActionResult> ExportCategoryData([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.ExportCategoriesAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "ProductCategories.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting product category data.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        // ─── PRODUCT SUB-CATEGORY ───────────────────────

        [HttpGet("sub-category/download-template")]
        public async Task<IActionResult> DownloadSubCategoryTemplate([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.GenerateSubCategoryTemplateAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "ProductSubCategoryTemplate.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating product sub-category template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpPost("sub-category/upload-template")]
        public async Task<IActionResult> UploadSubCategoryTemplate(IFormFile file, [FromForm] long? businessUnitId = null)
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
                var result = await _service.UploadSubCategoryTemplateAsync(stream, targetBUId, createdBy);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message, data = result.Data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading product sub-category template.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        [HttpGet("sub-category/export")]
        public async Task<IActionResult> ExportSubCategoryData([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                var fileBytes = await _service.ExportSubCategoriesAsync(targetBUId);
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "ProductSubCategories.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting product sub-category data.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }
    }
}

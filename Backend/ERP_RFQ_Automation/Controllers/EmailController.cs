using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces; // Add this
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class EmailController : ControllerBase
    {
        private readonly IEmailService _emailService; // Change to Interface
        private readonly FolderService _folderService;
        private readonly ILogger<EmailController> _logger;

        public EmailController(IEmailService emailService, FolderService folderService,
                               ILogger<EmailController> logger)
        {
            _emailService = emailService;
            _folderService = folderService;
            _logger = logger;
        }

        [HttpPost("fetch")]
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> ManualFetchAndSaveLeads([FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var claimBUId) || claimBUId <= 0)
                    return Forbid();
                if (businessUnitId.HasValue && businessUnitId.Value != claimBUId) return Forbid();

                _logger.LogInformation("Manual email fetch requested for BU: {BU}", claimBUId);
                await _emailService.FetchAndSaveLeadsAsync(claimBUId);
                _logger.LogInformation("Manual email fetch completed successfully.");
                return Ok("Email data fetched and inserted into the database successfully.");
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during manual email fetch.");
                return StatusCode(500, "An error occurred while fetching email data.");
            }
        }

        [HttpPost("upload-leads-folder")]
        [RequestSizeLimit(200L * 1024 * 1024)]
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> UploadLeadsToFolder(
            [FromForm] List<IFormFile> files,
            [FromQuery] string folderType = "Shared",
            CancellationToken cancellationToken = default)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No files uploaded.");

            try
            {
                if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var businessUnitId) || businessUnitId <= 0)
                    return Forbid();
                await _folderService.SaveFilesToSharedFolderAsync(
                    files, folderType, businessUnitId, cancellationToken);
                return Ok(new { message = $"{files.Count} files uploaded successfully to the {folderType} leads folder." });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading files to {FolderType} folder.", folderType);
                return StatusCode(500, "An error occurred while uploading files.");
            }
        }

        [HttpPost("process-all-folder-leads")]
        [RequireModulePermission("Leads", PermissionAction.Create)]
        public async Task<IActionResult> ProcessAllFolderLeads(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var businessUnitId) || businessUnitId <= 0)
                    return Forbid();
                _logger.LogInformation("Manual folder processing requested for BU {BusinessUnitId}.", businessUnitId);
                var report = await _folderService.ProcessAllFoldersAsync(businessUnitId, cancellationToken);
                return Accepted(report);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating folder process.");
                return StatusCode(500, "An error occurred while initiating folder processing.");
            }
        }
    }
}

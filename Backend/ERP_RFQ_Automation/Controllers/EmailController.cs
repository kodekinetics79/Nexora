using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces; // Add this
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;  // For IServiceScopeFactory

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class EmailController : ControllerBase
    {
        private readonly IEmailService _emailService; // Change to Interface
        private readonly FolderService _folderService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EmailController> _logger;

        public EmailController(IEmailService emailService, FolderService folderService, // Change to Interface
                               IServiceScopeFactory scopeFactory, ILogger<EmailController> logger)
        {
            _emailService = emailService;
            _folderService = folderService;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        [HttpPost("fetch")]
        public async Task<IActionResult> ManualFetchAndSaveLeads([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                _logger.LogInformation("Manual email fetch requested for BU: {BU}", targetBUId);
                await _emailService.FetchAndSaveLeadsAsync(targetBUId > 0 ? targetBUId : null);
                _logger.LogInformation("Manual email fetch completed successfully.");
                return Ok("Email data fetched and inserted into the database successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during manual email fetch.");
                return StatusCode(500, $"An error occurred while fetching email data: {ex.Message}");
            }
        }

        [HttpPost("upload-leads-folder")]
        public async Task<IActionResult> UploadLeadsToFolder([FromForm] List<IFormFile> files, [FromQuery] string folderType = "Shared")
        {
            if (files == null || files.Count == 0)
                return BadRequest("No files uploaded.");

            try
            {
                await _folderService.SaveFilesToSharedFolderAsync(files, folderType);
                return Ok(new { message = $"{files.Count} files uploaded successfully to the {folderType} leads folder." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading files to {FolderType} folder.", folderType);
                return StatusCode(500, "An error occurred while uploading files.");
            }
        }

        [HttpPost("process-all-folder-leads")]
        public IActionResult ProcessAllFolderLeads()
        {
            try
            {
                _logger.LogInformation("Manual process requested for ALL folder leads (SEC & Aramco).");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var folderService = scope.ServiceProvider.GetRequiredService<FolderService>();
                        await folderService.ProcessAllFoldersAsync();
                        _logger.LogInformation("All folder processing completed successfully.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during folder processing.");
                    }
                });
                return Ok(new { message = "Background processing of all folder leads started successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating folder process.");
                return StatusCode(500, "An error occurred while initiating folder processing.");
            }
        }
    }
}
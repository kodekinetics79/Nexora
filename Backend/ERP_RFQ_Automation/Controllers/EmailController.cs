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
                // ING-08: the same lie lived here — a 200 with "fetched successfully" was
                // returned even when every mailbox had refused authentication. The caller is
                // told what actually happened.
                var report = await _emailService.FetchAndSaveLeadsAsync(claimBUId);
                if (report.AnyFailed)
                {
                    _logger.LogError("Manual email fetch failed for {Failed} of {Total} mailbox(es): {Reasons}",
                        report.Failed, report.Polled, report.FailureSummary);
                    return StatusCode(502, new
                    {
                        message = $"{report.Failed} of {report.Polled} mailbox(es) could not be polled. "
                            + "No mail has been ingested from them.",
                        reasons = report.Failures.Select(f => new
                        {
                            mailbox = f.EmailAddress,
                            reason = f.FailureReason,
                            lastSuccessfulPoll = f.LastSuccessfulPollOn
                        })
                    });
                }
                if (report.Polled == 0)
                {
                    _logger.LogWarning("Manual email fetch found no active IMAP mailbox for BU {BU}.", claimBUId);
                    return Ok(new { message = "No active IMAP mailbox is configured, so no mail was fetched." });
                }
                _logger.LogInformation("Manual email fetch completed successfully for {Total} mailbox(es).", report.Polled);
                return Ok(new
                {
                    message = "Email data fetched and inserted into the database successfully.",
                    mailboxes = report.Polled,
                    newMessages = report.Mailboxes.Sum(m => m.MessagesDownloaded)
                });
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
                // The count WRITTEN, not the count posted. FolderService skips a zero-byte file, an
                // unusable filename and a path-traversal filename and carries on, so `files.Count`
                // is the number of files the browser sent — a number this endpoint had no way of
                // contradicting and therefore always reported as success.
                var saved = await _folderService.SaveFilesToSharedFolderAsync(
                    files, folderType, businessUnitId, cancellationToken);
                var skipped = files.Count - saved;
                return Ok(new
                {
                    message = skipped > 0
                        ? $"{saved} of {files.Count} files were uploaded to the {folderType} leads folder. "
                          + $"{skipped} could not be stored — they were empty, or their filename was rejected."
                        : $"{saved} files uploaded successfully to the {folderType} leads folder.",
                    uploaded = saved,
                    skipped
                });
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

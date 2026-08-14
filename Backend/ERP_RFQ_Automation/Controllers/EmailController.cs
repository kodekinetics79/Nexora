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
        // Poll Now and the Email Intake screen are ONE capability behind two doors, and only one
        // of them was gated. EmailTriageController carries this at class level; without it here a
        // tenant whose EmailIntake entitlement had lapsed could not read the intake list but
        // could still trigger the poll that fills it — which is the more expensive half, because
        // it consumes mailbox, storage and extraction resources.
        [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(
            ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.EmailIntake)]
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
                    // The failure branch reports the SAME work detail as the success branch. A
                    // partly-failed cycle can still have captured mail from the mailboxes that
                    // answered, and a 502 carrying only the failures reads as "nothing happened"
                    // — which sends an operator looking for messages that are already ingested.
                    return StatusCode(502, new
                    {
                        message = $"{report.Failed} of {report.Polled} mailbox(es) could not be polled. "
                            + "No mail has been ingested from them.",
                        reasons = report.Failures.Select(f => new
                        {
                            mailbox = f.EmailAddress,
                            reason = f.FailureReason,
                            lastSuccessfulPoll = f.LastSuccessfulPollOn
                        }),
                        mailboxes = report.Polled,
                        newMessages = report.MessagesDownloaded,
                        totals = Totals(report),
                        polled = Detail(report)
                    });
                }
                if (report.Polled == 0)
                {
                    _logger.LogWarning("Manual email fetch found no active IMAP mailbox for BU {BU}.", claimBUId);
                    return Ok(new { message = "No active IMAP mailbox is configured, so no mail was fetched." });
                }
                _logger.LogInformation(
                    "Manual email fetch completed for {Total} mailbox(es): {Found} message(s) in the window, "
                    + "{Downloaded} downloaded, {Captured} captured, {Scheduled} component(s) scheduled, "
                    + "{Held} held for review, {Rejected} rejected, {Unacknowledged} left for retry.",
                    report.Polled, report.MessagesFound, report.MessagesDownloaded, report.MessagesCaptured,
                    report.ComponentsScheduled, report.MessagesHeldForReview, report.MessagesRejected,
                    report.MessagesNotAcknowledged);
                // "Fetched and inserted successfully" was true of the HTTP call and told the
                // operator nothing: a cycle that downloaded four messages, rejected three as
                // noise and could not queue the fourth's attachment reported exactly the same
                // sentence as a cycle that turned four emails into four inquiries. The response
                // now says what happened to the mail, per mailbox and in total.
                return Ok(new
                {
                    message = Describe(report),
                    // `mailboxes` stays the COUNT of mailboxes polled. The web client treats its
                    // absence as "nothing polled" (see leadService.fetchEmails), so its type is
                    // part of the contract; the per-mailbox breakdown is added beside it.
                    mailboxes = report.Polled,
                    newMessages = report.MessagesDownloaded,
                    totals = Totals(report),
                    polled = Detail(report)
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

        /// <summary>
        /// One sentence that survives being read quickly. It never claims an inquiry was created
        /// — that decision belongs to the message barrier, minutes later — and it never hides a
        /// message left unread, because "0 new" and "1 that we could not store" are the two
        /// results an operator must be able to tell apart at a glance.
        /// </summary>
        private static string Describe(ERP_RFQ_Automation.Services.MailboxPollReport report)
        {
            var parts = new List<string>
            {
                $"{report.Polled} mailbox(es) polled",
                $"{report.MessagesFound} message(s) in the poll window",
                $"{report.MessagesDownloaded} new",
                $"{report.MessagesCaptured} captured"
            };
            if (report.ComponentsScheduled > 0)
                parts.Add($"{report.ComponentsScheduled} part(s) queued for extraction");
            if (report.MessagesRejected > 0)
                parts.Add($"{report.MessagesRejected} stopped by intake triage and replayable");
            if (report.MessagesHeldForReview > 0)
                parts.Add($"{report.MessagesHeldForReview} held for review");
            if (report.MessagesNotAcknowledged > 0)
                parts.Add($"{report.MessagesNotAcknowledged} left unread for the next cycle");
            return string.Join(", ", parts) + ".";
        }

        private static object Totals(ERP_RFQ_Automation.Services.MailboxPollReport report) => new
        {
            mailboxesPolled = report.Polled,
            mailboxesFailed = report.Failed,
            messagesFound = report.MessagesFound,
            messagesDownloaded = report.MessagesDownloaded,
            messagesAlreadyIngested = report.MessagesAlreadyIngested,
            messagesCaptured = report.MessagesCaptured,
            componentsScheduled = report.ComponentsScheduled,
            messagesHeldForReview = report.MessagesHeldForReview,
            messagesRejected = report.MessagesRejected,
            messagesNotAcknowledged = report.MessagesNotAcknowledged
        };

        /// <summary>
        /// Per mailbox, because a tenant with two mailboxes and one broken credential needs to
        /// know WHICH one went quiet. The mailbox is named by its address only — no host, no
        /// port, no username.
        /// </summary>
        private static IEnumerable<object> Detail(ERP_RFQ_Automation.Services.MailboxPollReport report)
            => report.Mailboxes.Select(m => new
            {
                mailbox = m.EmailAddress,
                succeeded = m.Succeeded,
                reason = m.FailureReason,
                lastSuccessfulPoll = m.LastSuccessfulPollOn,
                windowSince = m.WindowSinceUtc,
                // Non-zero means mail exists that this poll could NOT see. It is on the response
                // for the same reason it is a warning in the log: it is the one place this design
                // can still lose a message.
                lookbackCappedDays = m.LookbackCappedDays,
                messagesFound = m.MessagesFound,
                messagesDownloaded = m.MessagesDownloaded,
                messagesAlreadyIngested = m.MessagesAlreadyIngested,
                messagesCaptured = m.MessagesCaptured,
                componentsScheduled = m.ComponentsScheduled,
                messagesHeldForReview = m.MessagesHeldForReview,
                messagesRejected = m.MessagesRejected,
                messagesNotAcknowledged = m.MessagesNotAcknowledged
            });

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

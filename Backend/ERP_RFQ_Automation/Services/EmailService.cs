using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Tesseract;
using UglyToad.PdfPig;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.MultiTenancy;

namespace ERP_RFQ_Automation.Services
{
    /// <summary>
    /// What polling ONE mailbox actually achieved. Returned instead of <c>void</c> because the
    /// poll loop used to log "Email fetch completed successfully." on a cycle that had thrown
    /// <c>MailKit.Security.AuthenticationException</c> 1.5 ms earlier — the caller had no way to
    /// know, so it beat its heartbeat and the channel stayed green for a week.
    /// </summary>
    public sealed record MailboxPollOutcome(
        long EmailConfigurationId,
        string EmailAddress,
        bool Succeeded,
        string? FailureReason,
        bool FailureIsPermanent,
        DateTime? LastSuccessfulPollOn,
        DateTime WindowSinceUtc,
        int LookbackCappedDays,
        int MessagesDownloaded,
        int MessagesAlreadyIngested);

    /// <summary>The truthful result of one poll cycle across every configured mailbox.</summary>
    public sealed record MailboxPollReport(IReadOnlyList<MailboxPollOutcome> Mailboxes)
    {
        public static readonly MailboxPollReport Empty = new(Array.Empty<MailboxPollOutcome>());

        public int Polled => Mailboxes.Count;
        public IReadOnlyList<MailboxPollOutcome> Failures =>
            Mailboxes.Where(m => !m.Succeeded).ToList();
        public int Failed => Mailboxes.Count(m => !m.Succeeded);
        public bool AnyFailed => Failed > 0;

        /// <summary>True ONLY when at least one mailbox was polled and none failed. Zero
        /// configured mailboxes is deliberately not success: nothing proved the door works.</summary>
        public bool AllSucceeded => Mailboxes.Count > 0 && Failed == 0;

        public bool AnyPermanentFailure => Mailboxes.Any(m => !m.Succeeded && m.FailureIsPermanent);

        public DateTime? OldestLastSuccessfulPoll => Mailboxes
            .Where(m => !m.Succeeded)
            .Select(m => m.LastSuccessfulPollOn)
            .DefaultIfEmpty(null)
            .Min();

        public string FailureSummary => string.Join("; ",
            Mailboxes.Where(m => !m.Succeeded).Select(m => $"{m.EmailAddress}: {m.FailureReason}"));
    }

    public class EmailService : IEmailService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<EmailService> _logger;
        private readonly ILLMService _llmService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _attachmentPath;
        private readonly string _rawEmailPath;
        private readonly string _tessDataPath;
        // Configuration constants
        private const double MIN_CONFIDENCE_THRESHOLD = 0.3; // Minimum AI confidence to accept
        private const double MIN_CONFIDENCE_WITH_VALIDATION = 0.25; // Minimum if email passes validation
        // ING-08: lookback defaults. The window used to be a FIXED 7 days
        // (`SentSince(Today-7) AND NotSeen`), which meant an outage longer than a week made
        // every older message permanently invisible: no row, no log, nothing. The window is now
        // derived from EmailConfiguration.LastSuccessfulPollOn and only these bounds are fixed.
        // Overridable via Ingestion:Email:* for a tenant with an unusual mailbox volume.
        private const double DEFAULT_INITIAL_LOOKBACK_DAYS = 7;   // first ever poll for a mailbox
        private const double DEFAULT_MIN_LOOKBACK_DAYS = 1;       // always re-scan at least this
        private const double DEFAULT_MAX_LOOKBACK_DAYS = 30;      // never scan further back than this
        private const long MAX_ATTACHMENT_SIZE = Ingestion.Triage.EmailIngestEnqueuer.MaxAttachmentBytes; // 25 MB
        // Token/Text limiting for LLM to prevent context length errors and excessive costs
        private const int MAX_CHARS_FOR_LLM = 32000; // ~8k tokens (safe for most models)
        private const int MAX_CHARS_PER_ATTACHMENT = 10000; // Limit per attachment during extraction
        private const int PRIORITY_EMAIL_BODY_CHARS = 5000; // Reserve chars for email body
        // ING-01/ING-04: durable EmailIngest.ParseStatus values (free text, HasMaxLength(50)).
        private const string STATUS_PENDING = "Pending";
        private const string STATUS_SUCCESS = "Success";
        private const string STATUS_FAILED = "Failed";
        // ING-05 (unified queue): body + attachments handed to the durable extraction
        // queue; the LeadPersister flips this to Success/NeedsReview when the job lands.
        private const string STATUS_QUEUED = "Queued";
        // ING-07: a message stopped by the intake gate. The WHY now lives in structured
        // columns (EmailIngest.TriageOutcome + TriageReasonJson) instead of in this string,
        // and the raw .eml is retained so /api/email-triage can replay it on demand.
        private const string STATUS_REJECTED = "Rejected";                      // <= 50 chars
        private const string STATUS_SCANNED_PDF = "NeedsReview - Scanned PDF";  // <= 50 chars
        // ING-09: terminal state for a stranded Pending row whose retained raw .eml is gone.
        // Without the bytes there is nothing to replay — not by the sweeper below and not by
        // the manual /api/email-triage reprocess either — so leaving it Pending would be a
        // promise of progress nothing can keep. Visible and honest instead.
        internal const string STATUS_RAW_MESSAGE_LOST = "Failed - raw message lost"; // <= 50 chars
        // ING-04: internal marker returned by PDF extraction when a page image-only/scanned PDF
        // could not be OCR'd. Never fed to the LLM; used only to route the ingest to review.
        private const string SCANNED_PDF_SENTINEL = "SCANNED_PDF_NO_TEXT";
        // pdfium (Docnet) and the Tesseract native engine are not thread-safe; serialize OCR so
        // parallel attachment extraction cannot crash the native libraries.
        private static readonly object _ocrLock = new object();
        // ING-05: when true (default) the email door no longer runs its own LLM
        // extraction — the body and every attachment are enqueued as durable extraction
        // jobs instead. Config: Ingestion:UseUnifiedQueue (set false to restore the
        // legacy direct-extraction path unchanged).
        private readonly bool _useUnifiedQueue;
        // ING-08: channel (not loop) health for the inbound mailbox. Optional so the intake
        // unit tests can construct the service without the readiness surface.
        private readonly IEmailPollerHealth? _pollerHealth;
        // Suspension enforcement for the one background path that spends real money. Optional so
        // the intake unit tests can construct the service without it; absent means "poll every
        // mailbox", which is the behaviour that existed before it.
        private readonly ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate? _workGate;
        // SEC-ING-01: the ambient tenant scope every mailbox is polled inside. Optional only so the
        // existing construction sites stay source-compatible; when it is not supplied it is
        // resolved from the container instead (the accessor is a singleton, so any scope yields the
        // same instance). It is NEVER substituted with a fresh TenantScopeAccessor: the scope is an
        // AsyncLocal read by the DbContext, so a second accessor would be pushed into and read from
        // two different objects and every ingest query would silently run unscoped again.
        private readonly ITenantScopeAccessor? _tenantScope;
        private readonly TimeSpan _initialLookback;
        private readonly TimeSpan _minLookback;
        private readonly TimeSpan _maxLookback;
        // ING-09: how old a "Pending" ingest must be before the sweeper treats it as stranded
        // rather than in-flight. Conservative on purpose: the window between the ingest commit
        // and the enqueue is milliseconds, so anything Pending for this long is a crash
        // remnant, not work in progress. Overridable via Ingestion:Email:* like the lookback.
        private const double DEFAULT_STRANDED_PENDING_SWEEP_MINUTES = 15;
        // Bound on rows recovered per tenant per cycle so a large backlog cannot stall the
        // poll loop; the next cycle takes the rest (the query is oldest-first).
        internal const int StrandedSweepBatchSize = 50;
        private readonly TimeSpan _strandedPendingAge;
        public EmailService(ErpRfqAutomationContext context, IWebHostEnvironment env,
            ILogger<EmailService> logger, ILLMService llmService, IServiceScopeFactory scopeFactory,
            IConfiguration configuration, IFileStorage storage,
            IEmailPollerHealth? pollerHealth = null,
            ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate? workGate = null,
            ITenantScopeAccessor? tenantScope = null)
        {
            _tenantScope = tenantScope;
            _context = context;
            _env = env;
            _logger = logger;
            _llmService = llmService;
            _scopeFactory = scopeFactory;
            _pollerHealth = pollerHealth;
            _workGate = workGate;
            _useUnifiedQueue = configuration.GetValue("Ingestion:UseUnifiedQueue", true);
            _initialLookback = PositiveDays(
                configuration.GetValue("Ingestion:Email:InitialLookbackDays", DEFAULT_INITIAL_LOOKBACK_DAYS),
                DEFAULT_INITIAL_LOOKBACK_DAYS);
            _minLookback = PositiveDays(
                configuration.GetValue("Ingestion:Email:MinLookbackDays", DEFAULT_MIN_LOOKBACK_DAYS),
                DEFAULT_MIN_LOOKBACK_DAYS);
            _maxLookback = PositiveDays(
                configuration.GetValue("Ingestion:Email:MaxLookbackDays", DEFAULT_MAX_LOOKBACK_DAYS),
                DEFAULT_MAX_LOOKBACK_DAYS);
            if (_maxLookback < _minLookback) _maxLookback = _minLookback;
            var sweepMinutes = configuration.GetValue(
                "Ingestion:Email:StrandedPendingSweepMinutes", DEFAULT_STRANDED_PENDING_SWEEP_MINUTES);
            _strandedPendingAge = TimeSpan.FromMinutes(
                sweepMinutes > 0 ? sweepMinutes : DEFAULT_STRANDED_PENDING_SWEEP_MINUTES);
            _attachmentPath = storage.GetPath("RFQ_Attachments");
            _rawEmailPath = storage.GetPath("Raw_Emails");
            _tessDataPath = Path.Combine(_env.ContentRootPath, "tessdata");
            Directory.CreateDirectory(_attachmentPath);
            Directory.CreateDirectory(_rawEmailPath);
        }

        private static TimeSpan PositiveDays(double configured, double fallback)
            => TimeSpan.FromDays(configured > 0 ? configured : fallback);

        /// <summary>
        /// SEC-ING-01: the ONE query in this service that runs with no tenant pushed, and therefore
        /// under the BYPASSRLS pipeline role. It reads nothing but the ids and the two facts the
        /// suspension gate and its warning need. The mailbox's host, username and PASSWORD are
        /// re-read per mailbox inside the pushed scope below, where RLS binds.
        /// </summary>
        private sealed record MailboxHandle(
            long Id, long BusinessUnitId, string EmailAddress, DateTime? LastSuccessfulPollOn);

        /// <summary>The container's accessor, which is a singleton, so any scope yields the same one.</summary>
        private ITenantScopeAccessor TenantScope()
        {
            if (_tenantScope is not null) return _tenantScope;
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>();
        }

        public async Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
        {
            var query = _context.EmailConfigurations
                .AsNoTracking()
                .Where(e => e.IsActive && e.Protocol.ToUpper() == "IMAP");

            if (businessUnitId.HasValue && businessUnitId.Value > 0)
            {
                query = query.Where(e => e.BusinessUnitId == businessUnitId.Value);
            }

            var configs = await query
                .OrderBy(e => e.BusinessUnitId).ThenBy(e => e.Id)
                .Select(e => new MailboxHandle(
                    e.Id, e.BusinessUnitId, e.EmailAddress, e.LastSuccessfulPollOn))
                .ToListAsync();

            // The most expensive gate in the product. ProcessConfigAsync resolves ILLMService from
            // its own scope and enqueues every attachment for extraction, so an unpolled mailbox is
            // the difference between a suspended tenant costing nothing and a suspended tenant
            // spending inference tokens on documents nobody will be invoiced for.
            //
            // Skipping is genuine DEFERRAL, not loss: the mail stays on the customer's IMAP server
            // (nothing is deleted, only flagged Seen after a successful ingest) and
            // LastSuccessfulPollOn is not advanced for a mailbox that was never polled, so the
            // lookback window on reinstatement still starts where the tenant left off. The one
            // bound is Ingestion:Email:MaxLookbackDays — a suspension longer than that window
            // leaves the oldest mail outside it, and the operator has to widen the setting before
            // reinstating a long-suspended tenant.
            //
            // Called BEFORE any tenant scope is pushed; see ITenantWorkGate for why that matters.
            if (_workGate is not null && configs.Count > 0)
            {
                var serviceable = await _workGate.FilterServiceableAsync(
                    configs.Select(c => c.BusinessUnitId));
                var admitted = serviceable.ToHashSet();
                var deferred = configs.Where(c => !admitted.Contains(c.BusinessUnitId)).ToList();

                if (deferred.Count > 0)
                    _logger.LogInformation(
                        "Skipping {Blocked} mailbox(es) whose tenant is suspended or archived; their mail "
                        + "stays on the server and is ingested when the tenant is reinstated.",
                        deferred.Count);

                foreach (var config in deferred) WarnIfSuspensionHasOutrunTheLookback(config);
                configs = configs.Where(c => admitted.Contains(c.BusinessUnitId)).ToList();
            }

            _logger.LogInformation("Found {Count} active IMAP email configurations to process.", configs.Count);

            var outcomes = new List<MailboxPollOutcome>(configs.Count);
            var tenantScope = TenantScope();
            foreach (var handle in configs)
            {
                MailboxPollOutcome outcome;

                // SEC-ING-01. THE PUSH COMES FIRST, AND IT MUST.
                //
                // ITenantContext captures ITenantScopeAccessor.BusinessUnitId in its CONSTRUCTOR
                // (HttpTenantContext, MultiTenancy/ITenantContext.cs), and ErpRfqAutomationContext
                // captures that ITenantContext in ITS constructor — so the tenant a DI scope's
                // DbContext believes in is fixed at the moment the scope first resolves it. A push
                // issued after CreateScope() changes nothing for that scope.
                //
                // Without it the whole ingest ran with BusinessUnitId == null, which turns the EF
                // global query filters into no-ops AND routes the connection to nexora_pipeline_app
                // — created BYPASSRLS — so both isolation layers were off at once.
                using var tenant = tenantScope.Push(handle.BusinessUnitId);
                using var mailboxScope = _scopeFactory.CreateScope();
                var mailboxContext = mailboxScope.ServiceProvider
                    .GetRequiredService<ErpRfqAutomationContext>();

                EmailConfiguration? config = null;
                try
                {
                    // Fail closed, the SlaSweepWorker contract. If the DbContext in this scope did
                    // not pick the pushed scope up, every query the ingest runs below would be
                    // cross-tenant under the bypass role again — so this refuses to poll rather
                    // than poll unscoped. The refusal is per MAILBOX and lands in the durable poll
                    // ledger, so the operator sees a red channel instead of a dead poll loop.
                    if (mailboxContext.ScopedTenantId != handle.BusinessUnitId)
                    {
                        throw new InvalidOperationException(
                            $"Mailbox poll refused for {handle.EmailAddress} (BU {handle.BusinessUnitId}): "
                            + $"the DbContext resolved tenant "
                            + $"{mailboxContext.ScopedTenantId?.ToString() ?? "<none>"}. "
                            + "Tenant scope is mandatory for this worker.");
                    }

                    // Re-read INSIDE the scope, with the tenant predicate stated as well as
                    // inherited: this is the read that carries the mailbox credential, and it must
                    // be the tenant's own row or nothing.
                    config = await mailboxContext.EmailConfigurations
                        .FirstOrDefaultAsync(e => e.Id == handle.Id
                            && e.BusinessUnitId == handle.BusinessUnitId);
                    if (config is null)
                    {
                        throw new InvalidOperationException(
                            $"Mailbox {handle.EmailAddress} (configuration {handle.Id}, BU "
                            + $"{handle.BusinessUnitId}) was discovered but is not readable inside "
                            + "its own tenant scope.");
                    }

                    _logger.LogInformation("Starting process for configuration: {Email}", config.EmailAddress);
                    outcome = await ProcessConfigAsync(mailboxScope.ServiceProvider, config);
                }
                catch (Exception ex)
                {
                    // A catastrophic failure inside ProcessConfigAsync must not stop the loop
                    // from reaching the next mailbox — but it must never be mistaken for
                    // success either, which is precisely what this method used to do.
                    _logger.LogError(ex, "Unexpected failure processing email configuration {Email}. Moving to next.", handle.EmailAddress);
                    outcome = FailedOutcome(handle, ex, ResolveLookbackWindow(handle.LastSuccessfulPollOn, DateTime.UtcNow));
                }

                // ING-08: the durable ledger is written for BOTH outcomes, on every cycle. Written
                // through the tenant-scoped context so the UPDATE is bound by the same RLS policy
                // as every other write in this cycle. When the guard above refused, `config` is
                // null and there is nothing tracked to write — the failure is still reported.
                if (config is not null)
                    await RecordPollOutcomeAsync(mailboxContext, config, outcome);
                outcomes.Add(outcome);

                // The mailbox is named from the HANDLE, never from `config`. When the tenant-scope
                // guard above refuses, `config` is still null — and the failure branch below is
                // precisely the branch that runs then, so dereferencing `config` there threw a
                // NullReferenceException out of FetchAndSaveLeadsAsync while REPORTING a refusal.
                // It replaced the operator's one clear sentence ("tenant scope is mandatory") with
                // a stack trace and killed the loop before the remaining mailboxes were polled:
                // an exception raised while logging a refusal hides the refusal. The handle carries
                // the same address, is non-null on every path, and is the value the guard's own
                // message already uses.
                if (outcome.Succeeded)
                {
                    _logger.LogInformation(
                        "Finished process for configuration: {Email} ({Downloaded} new message(s), "
                        + "{AlreadyIngested} already in the ingestion ledger).",
                        handle.EmailAddress, outcome.MessagesDownloaded, outcome.MessagesAlreadyIngested);
                }
                else
                {
                    _logger.LogError(
                        "Mailbox {Email} could NOT be polled: {Reason} Last successful poll: {LastSuccess}. "
                        + "No message from this mailbox has been ingested since then.",
                        handle.EmailAddress, outcome.FailureReason,
                        outcome.LastSuccessfulPollOn?.ToString("O") ?? "never");
                }
            }

            // ING-09: recover ingests stranded at "Pending" by a crash between the durable
            // ingest commit and the enqueue. Runs AFTER the mailbox loop, inside the same
            // poller cycle (and therefore under the same advisory-lease leadership — see
            // EmailBackgroundService), and deliberately runs even for a mailbox whose IMAP
            // poll just failed: the raw .eml is on local storage, so a broken mailbox
            // credential is no reason to leave already-captured mail stuck.
            //
            // Fully fenced off from the poll verdict: the sweep is a RECOVERY channel, and a
            // sweep failure must never repaint a mailbox result (or the reverse).
            foreach (var sweepBusinessUnitId in configs.Select(c => c.BusinessUnitId).Distinct())
            {
                try
                {
                    // Same push-before-CreateScope ordering, and the same fail-closed scope
                    // guard, as the mailbox loop above — the sweep writes tenant data.
                    using var tenant = tenantScope.Push(sweepBusinessUnitId);
                    using var sweepScope = _scopeFactory.CreateScope();
                    var sweepContext = sweepScope.ServiceProvider
                        .GetRequiredService<ErpRfqAutomationContext>();
                    if (sweepContext.ScopedTenantId != sweepBusinessUnitId)
                    {
                        _logger.LogError(
                            "Stranded-Pending sweep refused for BU {BusinessUnitId}: the DbContext "
                            + "resolved tenant {ResolvedTenant}. Tenant scope is mandatory for this sweep.",
                            sweepBusinessUnitId,
                            sweepContext.ScopedTenantId?.ToString() ?? "<none>");
                        continue;
                    }
                    // No IDocumentIngestion registered means there is no durable queue to
                    // re-enqueue into (legacy hosts, minimal test harnesses); the rows stay
                    // Pending and the operator still sees them on the triage screen.
                    var sweepIngestion = sweepScope.ServiceProvider
                        .GetService<ERP_RFQ_Automation.Extraction.IDocumentIngestion>();
                    if (sweepIngestion is null) continue;
                    var sweepLlm = sweepScope.ServiceProvider.GetService<ILLMService>() ?? _llmService;
                    await SweepStrandedPendingIngestsAsync(
                        sweepContext, sweepIngestion, sweepLlm, sweepBusinessUnitId, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "The stranded-Pending sweep failed for BU {BusinessUnitId} this cycle; "
                        + "the rows stay Pending and the next cycle retries. The mailbox verdict is unaffected.",
                        sweepBusinessUnitId);
                }
            }

            var report = new MailboxPollReport(outcomes);
            PublishChannelHealth(report);
            return report;
        }

        /// <summary>
        /// ING-08: mirrors the cycle's real outcome into the readiness surface. A mailbox that
        /// refused authentication is a PERMANENT failure — it will not heal by being retried —
        /// so <c>/ready</c> is allowed to go red on the first occurrence instead of after three.
        /// </summary>
        private void PublishChannelHealth(MailboxPollReport report)
        {
            if (_pollerHealth is null) return;
            var now = DateTimeOffset.UtcNow;
            if (report.AnyFailed)
                _pollerHealth.RecordFailure(report.FailureSummary, report.AnyPermanentFailure, now);
            else if (report.AllSucceeded)
                _pollerHealth.RecordSuccess(now);
            // Zero configured mailboxes: neither success nor failure was demonstrated, so
            // nothing is recorded. Claiming success here would advance "last successful poll"
            // for a door that was never opened.
        }

        /// <summary>
        /// ING-08: persists the mailbox's poll ledger. This is the durable, operator-visible
        /// half of the fix: <c>LastSuccessfulPollOn</c> is what the next lookback window is
        /// derived from, and <c>LastPollError</c> is the reason an operator reads.
        /// </summary>
        private async Task RecordPollOutcomeAsync(
            ErpRfqAutomationContext context, EmailConfiguration config, MailboxPollOutcome outcome)
        {
            var now = DateTime.UtcNow;
            config.LastPollAttemptOn = now;
            if (outcome.Succeeded)
            {
                config.LastSuccessfulPollOn = now;
                config.LastPollError = null;
                config.ConsecutivePollFailures = 0;
            }
            else
            {
                // LastSuccessfulPollOn is deliberately NOT touched: a failed cycle must never
                // move the recovery point forward, or the outage window is lost with it.
                config.LastPollError = Truncate(outcome.FailureReason, 500);
                config.ConsecutivePollFailures += 1;
            }

            try
            {
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to persist the poll ledger for {Email}; the in-process health state still reports the outcome.",
                    config.EmailAddress);
            }
        }

        /// <summary>
        /// SEC-ING-01: takes the caller's ALREADY tenant-scoped provider instead of creating its
        /// own. The scope has to be created after <c>ITenantScopeAccessor.Push</c> — every
        /// <c>ITenantContext</c> implementation captures the ambient tenant in its constructor — so
        /// a scope created here, out of the caller's sight, is exactly where the tenant used to get
        /// lost. The caller has already asserted <c>ScopedTenantId == config.BusinessUnitId</c> on
        /// this provider's DbContext.
        /// </summary>
        private async Task<MailboxPollOutcome> ProcessConfigAsync(
            IServiceProvider scopedServices, EmailConfiguration config)
        {
            var localContext = scopedServices.GetRequiredService<ErpRfqAutomationContext>();
            var localLlm = scopedServices.GetRequiredService<ILLMService>();
            // ING-05: unified-queue gateway from the SAME scope as localContext (null when
            // not registered -> the legacy direct path below still works).
            var ingestion = scopedServices.GetService<ERP_RFQ_Automation.Extraction.IDocumentIngestion>();

            var window = ResolveLookbackWindow(config, DateTime.UtcNow);
            LogLookbackWindow(config, window);

            var downloaded = 0;
            var alreadyIngested = 0;
            using var client = new ImapClient();
            try
            {
                // SSRF: the host and port on this row are supplied by a TENANT ADMINISTRATOR
                // and this poller dialled them directly, on a 300-second loop, from inside the
                // trust boundary — the last mail path in the product that had not been
                // converted (the SMTP send below closed the identical defect). A mailbox row
                // whose host resolves to 169.254.169.254, 127.0.0.1 or any RFC 1918 address
                // turned this background service into an instance-metadata reader and an
                // internal port scanner, with the connect outcome surfaced back through
                // mailbox status. MailEndpointPolicy resolves the name, refuses unless EVERY
                // returned address is publicly routable, and hands back a socket already
                // connected to one of those exact addresses, so there is no window in which a
                // rebinding answer can be dialled. The TLS mode and the credential are
                // deliberately unchanged: nothing about a legitimate host behaves differently.
                var pollSocket = await ERP_RFQ_Automation.Security.MailEndpointPolicy
                    .ConnectAsync(config.Host, config.Port, CancellationToken.None);
                try
                {
                    await client.ConnectAsync(pollSocket, config.Host, config.Port,
                        config.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None);
                }
                catch
                {
                    // MailKit owns the socket only once it has accepted it.
                    pollSocket.Dispose();
                    throw;
                }
                await client.AuthenticateAsync(config.EmailAddress, config.Password);
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadWrite);
                var query = BuildRFQSearchQuery(window.SinceUtc);
                var uids = await inbox.SearchAsync(query);
                _logger.LogInformation("Found {Count} message(s) in the poll window for {Email}", uids.Count, config.EmailAddress);

                // ING-08: envelopes first, full messages only for what the ledger has not seen.
                // Dropping `NotSeen` means the window is re-searched every cycle; downloading
                // every message in it every cycle would be unacceptable, and is unnecessary —
                // the Message-Id in the envelope is enough to ask the ledger.
                var summaries = uids.Count == 0
                    ? new List<IMessageSummary>()
                    : (await inbox.FetchAsync(uids,
                        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope)).ToList();
                var ledger = await LoadIngestedMessageIdsAsync(localContext, config, summaries);

                foreach (var summary in summaries)
                {
                    var envelopeId = NormalizeMessageId(summary.Envelope?.MessageId);
                    if (envelopeId is not null && ledger.Contains(envelopeId))
                    {
                        // Handled on an earlier cycle. The DURABLE record says so — not the
                        // IMAP \Seen flag, which a human reading the mailbox also sets.
                        alreadyIngested++;
                        continue;
                    }

                    var uid = summary.UniqueId;
                    try
                    {
                        var message = await inbox.GetMessageAsync(uid);
                        downloaded++;
                        // ING-01: only mark \Seen once a durable record (EmailIngest + raw .eml)
                        // exists, so a message we fail to persist is retried on the next cycle
                        // instead of vanishing.
                        bool durablyPersisted = await ProcessSingleEmailAsync(message, config, localContext, localLlm, ingestion);

                        // Check if connection is still alive before marking as seen
                        if (!client.IsConnected)
                        {
                            _logger.LogWarning("Connection lost during processing email {UID}. Reconnecting...", uid);
                            // A reconnect is a SECOND outbound dial to the same tenant-supplied
                            // host, so it is re-resolved and re-validated rather than trusted
                            // because the first connect succeeded. Skipping the policy here
                            // would leave the whole SSRF primitive reachable by dropping one
                            // connection mid-poll.
                            var reconnectSocket = await ERP_RFQ_Automation.Security.MailEndpointPolicy
                                .ConnectAsync(config.Host, config.Port, CancellationToken.None);
                            try
                            {
                                await client.ConnectAsync(reconnectSocket, config.Host, config.Port,
                                    config.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None);
                            }
                            catch
                            {
                                reconnectSocket.Dispose();
                                throw;
                            }
                            await client.AuthenticateAsync(config.EmailAddress, config.Password);
                            await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
                        }

                        if (durablyPersisted)
                        {
                            // Courtesy flag for the humans who also read this mailbox. It is NOT
                            // consulted on the way in any more; the EmailIngests row is.
                            await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true);
                        }
                        else
                        {
                            _logger.LogWarning("Email UID {UID} not persisted durably; it will be retried next cycle.", uid);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing email UID {UID}", uid);
                    }
                }
                await client.DisconnectAsync(true);

                return new MailboxPollOutcome(
                    config.Id, config.EmailAddress, Succeeded: true, FailureReason: null,
                    FailureIsPermanent: false, config.LastSuccessfulPollOn, window.SinceUtc,
                    window.CappedDays, downloaded, alreadyIngested);
            }
            catch (Exception ex)
            {
                // NOT swallowed into silence and NOT rethrown into a dead loop: the reason is
                // recorded, the caller reports the failure, and the poller keeps retrying while
                // remaining visibly failed.
                _logger.LogError(ex, "IMAP error for config: {Email}", config.EmailAddress);
                return FailedOutcome(config, ex, window, downloaded, alreadyIngested);
            }
        }

        /// <summary>The ingest keys already recorded for the messages in this window. One query
        /// per cycle, keyed on the RFC 5322 Message-Id.</summary>
        private static async Task<HashSet<string>> LoadIngestedMessageIdsAsync(
            ErpRfqAutomationContext context, EmailConfiguration config,
            IReadOnlyList<IMessageSummary> summaries)
        {
            var candidates = summaries
                .Select(s => NormalizeMessageId(s.Envelope?.MessageId))
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
                return new HashSet<string>(StringComparer.Ordinal);

            var known = await context.EmailIngests
                .Where(e => e.EmailConfigurationId == config.Id && candidates.Contains(e.MessageId))
                .Select(e => e.MessageId)
                .ToListAsync();
            return new HashSet<string>(known, StringComparer.Ordinal);
        }

        private MailboxPollOutcome FailedOutcome(
            EmailConfiguration config, Exception ex, LookbackWindow window,
            int downloaded = 0, int alreadyIngested = 0)
            => new(config.Id, config.EmailAddress, Succeeded: false,
                DescribeFailure(ex), IsPermanentFailure(ex), config.LastSuccessfulPollOn,
                window.SinceUtc, window.CappedDays, downloaded, alreadyIngested);

        /// <summary>Same outcome from the discovery handle, for the failures that happen BEFORE the
        /// mailbox row could be read inside its own tenant scope (including the scope guard).</summary>
        private MailboxPollOutcome FailedOutcome(
            MailboxHandle handle, Exception ex, LookbackWindow window,
            int downloaded = 0, int alreadyIngested = 0)
            => new(handle.Id, handle.EmailAddress, Succeeded: false,
                DescribeFailure(ex), IsPermanentFailure(ex), handle.LastSuccessfulPollOn,
                window.SinceUtc, window.CappedDays, downloaded, alreadyIngested);

        /// <summary>
        /// A one-line, operator-readable reason. Deliberately names the exception type: the
        /// production symptom was <c>AuthenticationException: Authentication failed</c>, and
        /// "the mailbox rejected our credentials" is the sentence that gets it fixed.
        /// </summary>
        internal static string DescribeFailure(Exception ex) => ex switch
        {
            AuthenticationException =>
                $"The mailbox rejected the configured credentials (authentication failed): {ex.Message}",
            ServiceNotAuthenticatedException =>
                $"The mail server refused the session before authentication completed: {ex.Message}",
            SslHandshakeException =>
                $"The TLS handshake with the mail server failed: {ex.Message}",
            System.Net.Sockets.SocketException =>
                $"The mail server could not be reached: {ex.Message}",
            TimeoutException =>
                $"The mail server did not respond in time: {ex.Message}",
            _ => $"{ex.GetType().Name}: {ex.Message}"
        };

        /// <summary>
        /// True when retrying cannot help. An expired or revoked credential is the canonical
        /// case: three more failed cycles only delay the moment an operator is told.
        /// </summary>
        internal static bool IsPermanentFailure(Exception ex)
            => ex is AuthenticationException or ServiceNotAuthenticatedException;

        /// <param name="SinceUtc">Inclusive floor of the search window.</param>
        /// <param name="CappedDays">Days of the outage the cap excluded — 0 when nothing was cut.
        /// Non-zero means messages exist that this poll cannot see, which is announced loudly.</param>
        /// <param name="FirstEverPoll">True when this mailbox has never been polled successfully.</param>
        internal readonly record struct LookbackWindow(DateTime SinceUtc, int CappedDays, bool FirstEverPoll);

        /// <summary>
        /// ING-08: derives the search window from the LAST SUCCESSFUL POLL rather than a fixed
        /// 7 days.
        ///
        /// The old `SentSince(Today - 7)` was a permanent loss window: the poller had not
        /// contacted the mailbox since 2026-07-30, so by 2026-08-06 every message older than a
        /// week was invisible forever — no row, no log, nothing to replay. Deriving the window
        /// from <see cref="EmailConfiguration.LastSuccessfulPollOn"/> makes an outage
        /// RECOVERABLE: the first successful poll after it re-reads the whole gap.
        ///
        /// Bounds, and why each exists:
        /// <list type="bullet">
        ///   <item><description>FLOOR (1 day) — always re-scan the recent past, covering
        ///   server/client clock skew and the day-granularity of the IMAP SENTSINCE key.</description></item>
        ///   <item><description>CAP (30 days) — the FIRST deploy of this change must not turn
        ///   into an unbounded mailbox re-read, and an abandoned mailbox must not scan years of
        ///   history every 5 minutes. When the cap actually cuts an outage short, that is
        ///   potential loss, so it is logged as a warning naming the exact days.</description></item>
        ///   <item><description>INITIAL (7 days) — a mailbox with no recorded success keeps
        ///   exactly today's behaviour, so shipping this change re-reads at most the same week
        ///   it already re-read. Everything already in the ledger is skipped without a
        ///   download, so the "re-ingestion storm" is bounded to envelope fetches.</description></item>
        /// </list>
        /// </summary>
        internal LookbackWindow ResolveLookbackWindow(EmailConfiguration config, DateTime nowUtc)
            => ResolveLookbackWindow(config.LastSuccessfulPollOn, nowUtc);

        internal LookbackWindow ResolveLookbackWindow(DateTime? lastSuccess, DateTime nowUtc)
        {
            var firstEver = lastSuccess is null;
            var since = firstEver ? nowUtc - _initialLookback : lastSuccess!.Value;

            var cappedDays = 0;
            var oldestAllowed = nowUtc - _maxLookback;
            if (since < oldestAllowed)
            {
                cappedDays = (int)Math.Ceiling((oldestAllowed - since).TotalDays);
                since = oldestAllowed;
            }

            var newestAllowed = nowUtc - _minLookback;
            if (since > newestAllowed) since = newestAllowed;

            return new LookbackWindow(since, cappedDays, firstEver);
        }

        /// <summary>
        /// Says out loud, WHILE the tenant is still suspended, how much of their mail reinstating
        /// them will no longer reach.
        ///
        /// <para>The deferral this gate creates is only lossless while the suspension is shorter
        /// than <c>Ingestion:Email:MaxLookbackDays</c> — and the default cap is 30 days while the
        /// default retention window before deletion is also 30, so for the commonest reason a
        /// tenant is suspended (non-payment) the two are the same length and the loss begins on
        /// the day somebody was going to make a decision anyway.</para>
        ///
        /// <para>The cap already warns, but only inside <see cref="LogLookbackWindow"/> — which
        /// runs when the mailbox is polled, i.e. AFTER reinstatement, when the mail is already out
        /// of reach and nobody can widen the setting in time. Warning on every skipped cycle puts
        /// it in front of the operator during the window in which it is still actionable: widen
        /// the cap before reinstating, or recover the gap from the mailbox by hand.</para>
        /// </summary>
        private void WarnIfSuspensionHasOutrunTheLookback(MailboxHandle config)
        {
            if (config.LastSuccessfulPollOn is not DateTime lastSuccess) return;

            var oldestReachable = DateTime.UtcNow - _maxLookback;
            if (lastSuccess >= oldestReachable) return;

            var lostDays = (int)Math.Ceiling((oldestReachable - lastSuccess).TotalDays);
            _logger.LogWarning(
                "Mailbox {Email} (business unit {BusinessUnitId}) has been deferred by tenant "
                + "suspension since {LastSuccess:O}, which is now longer ago than the {MaxDays}-day "
                + "lookback cap. Reinstating this tenant today will NOT ingest mail sent before "
                + "{OldestReachable:O} — {LostDays} day(s) of it. Widen "
                + "Ingestion:Email:MaxLookbackDays before reinstating, or recover that period from "
                + "the mailbox manually.",
                config.EmailAddress, config.BusinessUnitId, lastSuccess, _maxLookback.TotalDays,
                oldestReachable, lostDays);
        }

        private void LogLookbackWindow(EmailConfiguration config, LookbackWindow window)
        {
            if (window.CappedDays > 0)
            {
                // The cap is the ONE place this design can still lose a message, so it is never
                // silent: the operator is told exactly how much history is out of reach.
                _logger.LogWarning(
                    "Mailbox {Email} was last polled successfully on {LastSuccess}. The lookback is capped at "
                    + "{MaxDays} days, so mail sent before {Since:O} — {CappedDays} day(s) of the outage — is NOT "
                    + "visible to this poll and must be recovered manually from the mailbox.",
                    config.EmailAddress, config.LastSuccessfulPollOn?.ToString("O") ?? "never",
                    _maxLookback.TotalDays, window.SinceUtc, window.CappedDays);
            }
            else
            {
                _logger.LogInformation(
                    "Polling {Email} for mail sent since {Since:O} (last successful poll: {LastSuccess}).",
                    config.EmailAddress, window.SinceUtc,
                    config.LastSuccessfulPollOn?.ToString("O") ?? "never — first poll for this mailbox");
            }
        }
        /// <summary>
        /// ING-07: fetch EVERY unseen message in the window and let the triage gate decide.
        ///
        /// This used to be a server-side keyword filter (rfq|quote|tender|inquiry|…). It was
        /// the FIRST place a real deal could be lost and the hardest to notice, because a
        /// message it excluded never produced a row, a log line or a rejection — it simply
        /// never existed as far as the system was concerned. "Kindly send your best price for
        /// 40 nos cable tray 300mm, delivery Jebel Ali" contains none of those keywords.
        ///
        /// The cost of the change is bounded and small: this segment sees tens of messages a
        /// day per tenant, autoreplies/bulk mail/no-reply senders are stopped by the gate
        /// WITHOUT an AI call, and everything else is one governed prose call under the
        /// existing token ledger and per-tenant caps.
        ///
        /// ING-08: `AND NotSeen` is GONE as well, and for the same reason. The IMAP \Seen flag
        /// is set by any human who opens the mailbox in Outlook, so a message someone glanced at
        /// before the poller reached it was never ingested — no row, no log, no trace. The flag
        /// is a READING state, not an ingestion ledger; the EmailIngests row (unique on
        /// EmailConfigurationID + MessageID) is the authority, and it is consulted from the
        /// envelope before any message is downloaded.
        /// </summary>
        internal static SearchQuery BuildRFQSearchQuery(DateTime sinceUtc)
            // SENTSINCE compares dates, not instants, and is inclusive of the given day.
            => SearchQuery.SentSince(sinceUtc.Date);
        /// <summary>
        /// Processes a single fetched message. Returns true when a durable record
        /// (EmailIngest row + raw .eml) exists for the message, meaning the caller may safely
        /// mark it \Seen; returns false only when nothing could be persisted (retry next cycle).
        /// Internal rather than private so the thread-identity round-trip test can drive the
        /// REAL ingest write path — the header capture, normalization and persisted row —
        /// instead of a re-implementation of it.
        /// </summary>
        internal async Task<bool> ProcessSingleEmailAsync(MimeMessage message, EmailConfiguration config,
            ErpRfqAutomationContext context, ILLMService llmService,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion? ingestion = null)
        {
            var messageId = ResolveIngestKey(message);
            var from = message.From.ToString();
            var to = message.To.ToString();
            var subject = message.Subject ?? "";

            // ING-08: the skip is keyed on the MESSAGE, not on (From, To, Subject).
            //
            // The old check also skipped anything matching an existing From+To+Subject triple.
            // "RFQ", "Quotation Request" and every reply in a thread reuse a subject line, so a
            // customer's SECOND, genuinely different enquiry was dropped on arrival — a Debug
            // log line and no row anywhere. The RFC 5322 Message-Id is the identifier that is
            // stable for this message and different for the next one, and it is already the
            // durable ingestion key (unique index on EmailConfigurationID + MessageID).
            if (await context.EmailIngests.AnyAsync(e =>
                    e.EmailConfigurationId == config.Id && e.MessageId == messageId))
            {
                _logger.LogDebug("Skipping already-ingested email: {MessageId} (From: {From}, Subject: {Subject})",
                    messageId, from, subject);
                return true;
            }

            // ING-01: persist a durable record for EVERY fetched message BEFORE classification,
            // so a real RFQ the keyword filter misjudges is never silently dropped.
            var ingest = new EmailIngest
            {
                MessageId = messageId,
                EmailSubject = subject,
                FromEmail = from,
                ToEmail = to,
                EmailConfigurationId = config.Id,
                CreatedOn = DateTime.UtcNow,
                ParseStatus = STATUS_PENDING,
                // FR-RFQ-05/06: thread identity is captured HERE, the one place the parsed
                // MimeMessage is in hand, or it is lost — these headers are not recoverable from
                // the occurrence graph later without re-reading every raw .eml. Reconciliation
                // uses them as a strong (but never solitary) signal that a reply carrying an
                // amendment belongs to an already-ingested inquiry's thread.
                InReplyToMessageId = ResolveInReplyTo(message),
                ReferencesJson = SerializeReferences(message)
            };

            // Save the raw email bytes first so the original is never lost, even for rejected mail.
            var rawPath = Path.Combine(_rawEmailPath, $"{Guid.NewGuid()}.eml");
            try
            {
                message.WriteTo(rawPath);
                ingest.RawEmailPath = rawPath;
            }
            catch (Exception ex)
            {
                // If we cannot even persist the raw bytes, do NOT let the caller mark it seen.
                _logger.LogError(ex, "Failed to persist raw email bytes for {MessageId}. Will retry next cycle.", messageId);
                return false;
            }

            context.EmailIngests.Add(ingest);
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                // A row already exists (concurrent/duplicate delivery) -> durable record present.
                _logger.LogWarning("Duplicate messageId detected: {MessageId}", messageId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist EmailIngest for {MessageId}. Will retry next cycle.", messageId);
                try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { /* best-effort cleanup */ }
                return false;
            }

            // From here a durable record exists: the caller may mark the message \Seen regardless
            // of the classification/extraction outcome below.
            await RouteIngestAsync(message, ingest, config, context, llmService, ingestion);
            return true;
        }

        /// <summary>
        /// The post-persist half of ingesting one message: triage, then fan-out (or the legacy
        /// direct extraction), then the resulting ParseStatus. Factored out of
        /// <see cref="ProcessSingleEmailAsync"/> UNCHANGED so the ING-09 stranded-Pending
        /// sweeper below can re-run exactly the step a crash cut off — a recovered message
        /// that took a different routing path than a fresh one would not be a recovery.
        /// Every branch leaves the ingest saved with a truthful ParseStatus.
        /// </summary>
        private async Task RouteIngestAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration config,
            ErpRfqAutomationContext context, ILLMService llmService,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion? ingestion)
        {
            // ING-07: RECOGNITION. The old gate treated "quote"/"quotation" as strong RFQ
            // evidence (so every supplier reply and order confirmation passed) while a bare
            // prose enquiry with none of its 22 keywords was dropped — simultaneously too
            // permissive and capable of losing a real deal. The gate now stops a message ONLY
            // on positive, machine-verifiable evidence that it is not business mail; absence
            // of RFQ vocabulary is never a reason to stop.
            var bodyParts = EmailBodyNormalizer.Normalize(GetEmailBody(message));
            var senderPartyType = await SenderPartyResolver.ResolveAsync(
                context, config.BusinessUnitId, message.From.Mailboxes.FirstOrDefault()?.Address);
            var triage = DeterministicEmailTriage.Evaluate(
                BuildTriageSignals(message, bodyParts, senderPartyType));

            // Persist the decision BEFORE branching on it: a message that is stopped must still
            // be recorded, with its reason, and be retrievable (raw .eml is already on disk).
            ingest.TriageOutcome = triage.Outcome.ToString();
            ingest.TriageReasonJson = SerializeReasonCodes(triage.ReasonCodes);
            ingest.TriageDecidedOn = DateTime.UtcNow;
            // …and persist means SAVE, not "assign and hope the branch saves later". The
            // unified-queue branch below hands this context to DocumentIngestionService, whose
            // execution-strategy hygiene clears the change tracker per document — which
            // DETACHED this very ingest, so a save issued only after the fan-out silently
            // wrote NOTHING: the triage verdict was lost and the row stayed "Pending" until
            // the ING-09 sweeper healed the status a cycle later (the reasons never came
            // back). The acceptance journey against the real graph is what caught it.
            await context.SaveChangesAsync();

            if (triage.Outcome == EmailTriageOutcome.Noise)
            {
                _logger.LogInformation(
                    "Email triaged as noise ({Reasons}); no extraction job enqueued, raw message retained: {Subject}",
                    string.Join(",", triage.ReasonCodes), message.Subject ?? "");
                ingest.ParseStatus = STATUS_REJECTED;
                ingest.ParsedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
                return;
            }

            // ING-05: unified queue — the RFQ email's body and each attachment become
            // durable, content-addressed extraction jobs (one batch), replacing the
            // direct LLM extraction below. The pre-created EmailIngest is referenced via
            // the job's provenance sidecar so the produced lead(s) link to the REAL
            // ingest (from/subject) instead of a synthetic one.
            if (_useUnifiedQueue && ingestion != null)
            {
                var queued = await EnqueueEmailForExtractionAsync(
                    message, ingest, config, ingestion, triage, bodyParts);
                if (queued > 0)
                {
                    ingest.ParseStatus = STATUS_QUEUED; // persister flips to Success/NeedsReview
                }
                else
                {
                    // Nothing enqueuable (empty body, no supported attachments) — same
                    // terminal state the legacy path produced for unextractable mail.
                    if (ingest.ParseStatus == STATUS_PENDING)
                        ingest.ParseStatus = STATUS_FAILED;
                    ingest.ParsedAt = DateTime.UtcNow;
                }
                // The fan-out above shares this scope's DbContext with the ingestion gateway,
                // and the gateway clears the change tracker per document — detaching this
                // ingest, so the status flip and the enqueuer's skip evidence
                // (SkippedAttachmentsJson) would otherwise be silent no-ops. Re-attach as
                // Modified (the entity alone — not its navigation graph) so the in-memory
                // truth of this routing pass lands durably.
                if (context.Entry(ingest).State == EntityState.Detached)
                    context.Entry(ingest).State = EntityState.Modified;
                await context.SaveChangesAsync();
                return;
            }

            // Legacy direct-extraction path (Ingestion:UseUnifiedQueue=false).
            var (leadId, extractedText) = await SaveLeadFromEmailAndAttachments(
                message, ingest, config, context, llmService);
            ingest.ParsedAt = DateTime.UtcNow;
            if (leadId > 0)
            {
                ingest.ParseStatus = "NeedsReview";
            }
            else if (ingest.ParseStatus == STATUS_PENDING)
            {
                // No lead created and no more specific status (e.g. scanned-PDF review) was set.
                ingest.ParseStatus = STATUS_FAILED;
            }
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// ING-09: the stranded-Pending recovery pass.
        ///
        /// <para>
        /// The ingest write in <see cref="ProcessSingleEmailAsync"/> is two steps — commit the
        /// EmailIngest row (ParseStatus "Pending"), then triage + enqueue and flip it to
        /// Queued/Rejected/Failed. A crash between them leaves a row this system will never
        /// touch again: the ledger pre-check skips any Message-Id already in EmailIngests, so
        /// the message is never re-downloaded, and nothing downstream ever reads a "Pending"
        /// row. The mail was durably captured and then went nowhere, silently — the exact loss
        /// class ING-01 closed at the door, reintroduced one step later.
        /// </para>
        /// <para>
        /// This pass re-runs the cut-off step from the retained raw .eml through
        /// <see cref="RouteIngestAsync"/> — the SAME routine a fresh message takes, so triage,
        /// fan-out and status semantics cannot drift. It is safe to re-run against a message
        /// that was in fact partially enqueued: the evidence store's (BusinessUnitId,
        /// ContentHash) idempotency resolves every already-ingested document as Duplicate,
        /// which counts as handled work and flips the row to Queued. A row whose enqueue
        /// completed but whose status flip was the thing that crashed is detected cheaply (its
        /// extraction occurrences already exist under the message's logical group key) and just
        /// gets the flip. A row whose raw .eml is gone gets the terminal
        /// <see cref="STATUS_RAW_MESSAGE_LOST"/> instead of an unpayable promise.
        /// </para>
        /// <para>
        /// Per-row fault isolation: one unreadable message must never stop the sweep, so each
        /// row is handled in its own try/catch and a failed row simply stays Pending for the
        /// next cycle. Runs after the mailbox loop under the same poller advisory lease, so it
        /// never executes concurrently with itself. Internal so the tests can drive it with a
        /// deterministic clock.
        /// </para>
        /// </summary>
        internal async Task<int> SweepStrandedPendingIngestsAsync(
            ErpRfqAutomationContext context,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion ingestion,
            ILLMService llmService,
            long businessUnitId,
            DateTime nowUtc)
        {
            var cutoff = nowUtc - _strandedPendingAge;
            var stranded = await context.EmailIngests
                .Include(e => e.EmailConfiguration)
                .Where(e => e.EmailConfiguration.BusinessUnitId == businessUnitId
                    && e.ParseStatus == STATUS_PENDING
                    && e.CreatedOn < cutoff)
                .OrderBy(e => e.CreatedOn).ThenBy(e => e.Id)
                .Take(StrandedSweepBatchSize)
                .ToListAsync();
            if (stranded.Count == 0) return 0;

            _logger.LogWarning(
                "Found {Count} email ingest(s) stranded at Pending for over {Minutes:F0} minute(s) "
                + "in BU {BusinessUnitId}; re-running the enqueue step from the retained raw messages.",
                stranded.Count, _strandedPendingAge.TotalMinutes, businessUnitId);

            var recovered = 0;
            foreach (var ingest in stranded)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(ingest.RawEmailPath) || !File.Exists(ingest.RawEmailPath))
                    {
                        ingest.ParseStatus = STATUS_RAW_MESSAGE_LOST;
                        ingest.ParsedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync();
                        _logger.LogError(
                            "Stranded ingest {IngestId} ({MessageId}) has no retained raw message at "
                            + "'{Path}'; it cannot be recovered and is marked '{Status}'.",
                            ingest.Id, ingest.MessageId, ingest.RawEmailPath, STATUS_RAW_MESSAGE_LOST);
                        continue;
                    }

                    // The enqueue itself completed but the flip to Queued did not: the message's
                    // extraction occurrences already exist under its logical group key (the same
                    // key the triage screen joins on). Only the missing status write is replayed
                    // — re-reading and re-hashing every document would prove nothing more.
                    var groupKey = $"email:{ingest.MessageId}";
                    var alreadyEnqueued = await context
                        .Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>()
                        .AsNoTracking()
                        .AnyAsync(o => o.BusinessUnitId == businessUnitId && o.LogicalGroupKey == groupKey);
                    if (alreadyEnqueued)
                    {
                        ingest.ParseStatus = STATUS_QUEUED; // persister flips to Success/NeedsReview
                        await context.SaveChangesAsync();
                        recovered++;
                        _logger.LogInformation(
                            "Stranded ingest {IngestId} already had extraction work under {GroupKey}; "
                            + "ParseStatus restored to Queued.", ingest.Id, groupKey);
                        continue;
                    }

                    MimeMessage message;
                    await using (var stream = File.OpenRead(ingest.RawEmailPath))
                    {
                        message = await MimeMessage.LoadAsync(stream);
                    }

                    await RouteIngestAsync(
                        message, ingest, ingest.EmailConfiguration, context, llmService, ingestion);
                    recovered++;
                    _logger.LogInformation(
                        "Stranded ingest {IngestId} ({MessageId}) re-routed; ParseStatus is now '{Status}'.",
                        ingest.Id, ingest.MessageId, ingest.ParseStatus);
                }
                catch (Exception ex)
                {
                    // Poison-row isolation: the row stays Pending and is retried next cycle;
                    // the remaining stranded rows are still swept.
                    _logger.LogError(ex,
                        "Failed to recover stranded ingest {IngestId} ({MessageId}); it stays Pending "
                        + "and will be retried on the next sweep.", ingest.Id, ingest.MessageId);
                }
            }
            return recovered;
        }

        /// <summary>
        /// ING-05/ING-07: fans one email out to the durable extraction queue — one job per
        /// supported attachment plus one job for the sender's FRESH body text, all sharing a
        /// batch id and a provenance sidecar that names the real EmailIngest. The fan-out
        /// itself lives in <see cref="ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer"/>
        /// so the mailbox poller and the manual reprocess endpoint cannot drift apart.
        /// Returns the number of jobs enqueued (duplicates count — they are handled work).
        /// </summary>
        internal async Task<int> EnqueueEmailForExtractionAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration config,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion ingestion,
            EmailTriageDecision triage, EmailBodyParts bodyParts)
        {
            var result = await EmailIngestEnqueuer.EnqueueAsync(
                message, ingest, config.BusinessUnitId, config.EmailAddress,
                ingestion, triage, bodyParts, _logger);

            // ING-06: the per-file reasons are recorded on ingest.SkippedAttachmentsJson by the
            // enqueuer itself, unconditionally and on every path. This only raises the loss into
            // the 50-char lifecycle status for the one case where the message produced NOTHING
            // — a "Queued" ingest with no jobs would read as normal progress.
            if (result.Queued == 0 && result.SkippedAttachments.Count > 0)
            {
                ingest.ParseStatus = Truncate(
                    $"Failed - {result.SkippedAttachments.Count} attachment(s) skipped", 50);
            }

            return result.Queued;
        }
        /// <summary>
        /// Builds the pure input for <see cref="DeterministicEmailTriage"/>: the sender's own
        /// words, the resolved party type, and the raw headers that constitute positive
        /// evidence of automated/bulk mail.
        /// </summary>
        private static EmailTriageSignals BuildTriageSignals(
            MimeMessage message, EmailBodyParts parts, string? senderPartyType)
        {
            var from = message.From.Mailboxes.FirstOrDefault()?.Address;
            return new EmailTriageSignals
            {
                Subject = message.Subject ?? "",
                FreshBody = parts.Fresh,
                Signature = parts.Signature,
                FromAddress = from,
                FromDomain = SenderPartyResolver.ExtractDomain(from),
                SenderPartyType = senderPartyType,
                HasInReplyTo = !string.IsNullOrWhiteSpace(message.InReplyTo),
                HasReferences = message.References?.Count > 0,
                HasAttachments = message.Attachments.Any(),
                BodyEmptyAfterStrip = parts.BodyEmptyAfterStrip,
                AutoSubmitted = Header(message, "Auto-Submitted"),
                XAutoreply = Header(message, "X-Autoreply"),
                XAutoResponseSuppress = Header(message, "X-Auto-Response-Suppress"),
                Precedence = Header(message, "Precedence"),
                ListId = Header(message, "List-Id"),
                ListUnsubscribe = Header(message, "List-Unsubscribe"),
                ContentClass = Header(message, "Content-Class")
            };
        }

        private static string? Header(MimeMessage message, string name)
        {
            var value = message.Headers[name];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string SerializeReasonCodes(IReadOnlyList<string> reasonCodes)
        {
            // TriageReasonJson is varchar(1000); the codes are short and few, but the column
            // must never be the thing that fails an ingest.
            var json = JsonSerializer.Serialize(reasonCodes);
            return json.Length <= 1000 ? json : json.Substring(0, 1000);
        }

        /// <summary>
        /// ING-08: the durable ingestion key for a message.
        ///
        /// Prefers the RFC 5322 Message-Id. Falls back to a deterministic content hash when the
        /// header is absent or unusable — never to <c>Guid.NewGuid()</c>, which the old code
        /// used and which quietly guaranteed the OPPOSITE failure: a header-less message looked
        /// brand new on every single cycle and was ingested again and again.
        /// </summary>
        internal static string ResolveIngestKey(MimeMessage message)
        {
            var messageId = NormalizeMessageId(message.MessageId);
            // MessageID is varchar(255). Truncating a pathological header would manufacture a
            // collision between two different messages, so hash it instead.
            if (messageId is not null && messageId.Length <= 255)
                return messageId;
            return ComputeContentKey(message);
        }

        /// <summary>
        /// Message-Ids reach us from two places — the parsed message and the IMAP ENVELOPE —
        /// and the angle brackets are not guaranteed to be stripped identically by both. One
        /// normalizer keeps the ledger lookup and the ledger write on the same key.
        /// </summary>
        internal static string? NormalizeMessageId(string? messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return null;
            var trimmed = messageId.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[^1] == '>')
                trimmed = trimmed[1..^1].Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>
        /// The normalized In-Reply-To id, or null. An id longer than the 255-char MessageID key
        /// space is DROPPED rather than truncated or hashed: it exists only to join against
        /// stored MessageIDs, which are all ≤255, so a truncation could never match honestly and
        /// could match falsely.
        /// </summary>
        internal static string? ResolveInReplyTo(MimeMessage message)
        {
            var id = NormalizeMessageId(message.InReplyTo);
            return id is { Length: <= 255 } ? id : null;
        }

        /// <summary>
        /// The References chain as a JSON array of normalized Message-Ids, oldest first, or null
        /// when the message carries none. ReferencesJson is varchar(2000); when the chain does
        /// not fit, the OLDEST ids are dropped first — the nearest ancestors are the ones most
        /// likely to sit in this mailbox's ingest ledger, and In-Reply-To (the immediate parent)
        /// is persisted separately regardless. The column must never be the thing that fails an
        /// ingest.
        /// </summary>
        internal static string? SerializeReferences(MimeMessage message)
        {
            var ids = (message.References ?? Enumerable.Empty<string>())
                .Select(NormalizeMessageId)
                .Where(id => id is { Length: <= 255 })
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0) return null;
            var json = JsonSerializer.Serialize(ids);
            while (json.Length > 2000 && ids.Count > 1)
            {
                ids.RemoveAt(0); // drop the oldest ancestor first
                json = JsonSerializer.Serialize(ids);
            }
            return json.Length <= 2000 ? json : null;
        }

        /// <summary>
        /// The thread-ancestor keys of an ingested message, in occurrence EmailThreadId form
        /// ("email:{Message-Id}"), for the reconciliation descriptor. Union of In-Reply-To and
        /// the References chain — RFC 5322 obliges neither header, so each covers the other's
        /// absence.
        /// </summary>
        internal static IReadOnlyList<string> ThreadAncestorKeys(
            string? inReplyToMessageId, string? referencesJson)
        {
            var ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(inReplyToMessageId)) ids.Add(inReplyToMessageId);
            if (!string.IsNullOrWhiteSpace(referencesJson))
            {
                try
                {
                    ids.AddRange(JsonSerializer.Deserialize<string[]>(referencesJson) ?? []);
                }
                catch (JsonException)
                {
                    // A malformed legacy value yields no thread evidence rather than no lead.
                }
            }
            return ids.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => $"email:{id}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Deterministic stand-in for a missing Message-Id: SHA-256 over the full header block
        /// (which carries the per-delivery Received trail) plus the body text. The SAME message
        /// re-read next cycle hashes identically and is skipped; a DIFFERENT message under the
        /// same subject hashes differently and is ingested.
        /// </summary>
        internal static string ComputeContentKey(MimeMessage message)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var header in message.Headers)
                    sb.Append(header.Field).Append(':').Append(header.Value ?? string.Empty).Append('\n');
            }
            catch
            {
                // A malformed header must not stop ingestion; the envelope facts below still
                // produce a stable key.
            }
            sb.Append('\n')
              .Append(message.From?.ToString() ?? string.Empty).Append('\n')
              .Append(message.To?.ToString() ?? string.Empty).Append('\n')
              .Append(message.Subject ?? string.Empty).Append('\n')
              .Append(message.Date.ToString("O")).Append('\n')
              .Append(message.GetTextBody(TextFormat.Plain)
                      ?? message.GetTextBody(TextFormat.Html)
                      ?? string.Empty);
            foreach (var attachment in message.Attachments.OfType<MimePart>())
                sb.Append('\n').Append(attachment.FileName ?? "(unnamed)");

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
        }
        private bool IsDuplicateKeyException(DbUpdateException ex)
        {
            // SQL Server unique/PK violation error numbers.
            if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                return sqlEx.Number == 2627 || sqlEx.Number == 2601;
            }
            // PostgreSQL unique_violation (SQLSTATE 23505) — after the Npgsql
            // migration duplicate-key errors surface as PostgresException.
            if (ex.InnerException is Npgsql.PostgresException pgEx)
            {
                return pgEx.SqlState == "23505";
            }
            return false;
        }
        /// <summary>
        /// Internal rather than private so the cross-tenant duplicate regression can drive the REAL
        /// ingest write path — the duplicate check, the transaction, the Lead + LeadItems graph and
        /// the identity baseline — instead of a re-implementation of the query under test.
        /// </summary>
        internal async Task<(long leadId, string extractedText)> SaveLeadFromEmailAndAttachments(
            MimeMessage message, EmailIngest ingest, EmailConfiguration config,
            ErpRfqAutomationContext context, ILLMService llmService)
        {
            // Extract text from email body and attachments
            string extracted = GetEmailBody(message);
            string attachmentsText = "";
            var fileTypes = new HashSet<string>();
            // Process attachments
            // ING-06: every skip below is collected, not just logged. The legacy path is behind
            // Ingestion:UseUnifiedQueue=false but it is still a door, and a door that drops an
            // attachment without a record is the same defect wherever it lives.
            var skippedAttachments = new List<string>();
            void RecordSkipped(string fileName, string reason)
            {
                skippedAttachments.Add($"{fileName} ({reason})");
                _logger.LogWarning(
                    "Skipping email attachment {FileName} for ingest {IngestId}: {Reason}.",
                    fileName, ingest.Id, reason);
            }

            var attachmentStreams = new List<(string FileName, MemoryStream Stream, string Extension)>();
            var attachmentOrdinal = 0;
            foreach (var att in message.Attachments)
            {
                attachmentOrdinal++;
                if (att is not MimePart part)
                {
                    RecordSkipped(
                        att.ContentDisposition?.FileName ?? $"attachment #{attachmentOrdinal}",
                        "embedded email message is not ingested");
                    continue;
                }
                if (part.FileName == null)
                {
                    RecordSkipped($"attachment #{attachmentOrdinal}", "attachment has no filename");
                    continue;
                }
                var ext = Path.GetExtension(part.FileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext))
                {
                    RecordSkipped(part.FileName, $"unsupported file type '{ext}'");
                    continue;
                }
                fileTypes.Add(GetFileTypeLabel(ext));
                try
                {
                    var ms = new MemoryStream();
                    await part.Content.DecodeToAsync(ms);
                    ms.Position = 0;
                    attachmentStreams.Add((part.FileName, ms, ext));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read attachment: {FileName}", part.FileName);
                    RecordSkipped(part.FileName, $"could not be read from the message: {ex.GetType().Name}");
                }
            }
            // Recorded BEFORE any extraction/AI outcome can short-circuit this method — every
            // early `return (0, extracted)` below would otherwise lose the list.
            EmailIngestEnqueuer.RecordSkippedAttachments(ingest, skippedAttachments);
            // Extract text from attachments in parallel for efficiency
            var attachmentTasks = attachmentStreams.Select(async (item) =>
            {
                try
                {
                    var text = await ExtractTextFromAttachment(item.Stream, item.Extension);
                    return !string.IsNullOrWhiteSpace(text) ? $"\n\n[Attachment: {item.FileName}]\n{text}" : "";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract text from: {FileName}", item.FileName);
                    return "";
                }
                finally
                {
            item.Stream.Dispose();
                }
            });
            var attachmentResults = await Task.WhenAll(attachmentTasks);
            // ING-04: separate genuine text from the scanned-PDF marker. The marker never reaches
            // the LLM; it only signals that a scanned/image-only PDF could not be OCR'd so the
            // ingest is routed to review instead of silently producing an empty lead.
            bool scannedPdfNeedsReview = false;
            var usableResults = new List<string>();
            foreach (var r in attachmentResults)
            {
                if (r.Contains(SCANNED_PDF_SENTINEL))
                {
                    scannedPdfNeedsReview = true;
                    continue;
                }
                usableResults.Add(r);
            }
            attachmentsText = string.Join("", usableResults);
            extracted += attachmentsText;
            if (scannedPdfNeedsReview)
            {
                // Tentative status: kept if no lead is created; overridden to Success by the caller
                // if a lead is still extracted from the remaining (e.g. email body) content.
                ingest.ParseStatus = STATUS_SCANNED_PDF;
                _logger.LogWarning("Scanned/image-only PDF detected that could not be OCR'd for: {Subject}", message.Subject);
            }
            string emailSource = fileTypes.Count > 0
                ? string.Join(", ", fileTypes.OrderBy(x => x))
                : "Text Only";
            
            // Apply smart token limiting to prevent LLM context length errors
            string emailBodyText = GetEmailBody(message);
            string limitedText = LimitTextForLLM(emailBodyText, attachmentsText, message.Subject ?? "");
            
            // Log if truncation occurred for visibility
            if (limitedText.Length < extracted.Length)
            {
                _logger.LogWarning(
                    "Text truncated for LLM: Original {Original} chars -> Limited {Limited} chars. Subject: {Subject}",
                    extracted.Length, limitedText.Length, message.Subject);
            }
            
            // AI Extraction with validation
            try
            {
                var ai = await llmService.ExtractLeadDataAsync(limitedText,
                    new AiCallContext(config.BusinessUnitId, AiPurposes.RfqExtraction,
                        $"email-ingest:{ingest.Id}", AiPromptVersions.StructuredRfqExtraction));
                // Smart validation: Lower threshold if email clearly looks like RFQ
                var minConfidence = HasStrongRFQIndicators(message, ai)
                    ? MIN_CONFIDENCE_WITH_VALIDATION
                    : MIN_CONFIDENCE_THRESHOLD;
                // Validate AI extraction
                if (ai == null || ai.OverallConfidence < minConfidence)
                {
                    _logger.LogWarning(
                        "AI extraction failed validation. Confidence: {Confidence:F2}, Required: {Required:F2}, Subject: {Subject}",
                        ai?.OverallConfidence ?? 0, minConfidence, message.Subject);
                    return (0, extracted);
                }
                // Additional validation: Must have at least RFQ number or buyer name
                if (string.IsNullOrWhiteSpace(ai.Rfqno) && string.IsNullOrWhiteSpace(ai.BuyersName))
                {
                    _logger.LogWarning(
                        "AI extraction missing critical fields but will save due to strong RFQ indicators: {Subject}",
                        message.Subject);
                    // If the email has strong RFQ indicators, try to extract basic info from subject
                    if (HasStrongRFQIndicators(message, ai))
                    {
                        ai = EnrichWithSubjectData(ai, message.Subject);
                    }
                    else
                    {
                        return (0, extracted);
                    }
                }
                // Check for existing lead with same RFQno to avoid duplicate leads.
                //
                // SEC-ING-01. THE TENANT PREDICATE IS THE POINT OF THIS BLOCK, not decoration.
                // Both inputs — the RFQ number and the buyer name — are extracted from an inbound
                // email, so an outside party who can send mail to this mailbox chooses them. With
                // no BusinessUnitId predicate the query asked "does ANY tenant on the platform
                // hold this lead", which is two defects at once:
                //   * an existence oracle — a silent drop told the sender whether a named buyer is
                //     running a named tender through a competitor; and
                //   * cross-tenant denial of ingest on day one — one buyer issues one RFQ number
                //     to many vendors, so two Nexora tenants bidding the same tender suppressed
                //     each other's leads, first-come-first-served, with only a LogWarning.
                // Stated explicitly rather than left to the global query filter: the filter is a
                // no-op when the ambient tenant is null, which is exactly the state this path used
                // to run in. Duplicate detection is a per-tenant question and now says so.
                var businessUnitId = config.BusinessUnitId;
                bool isDuplicate = false;

                if (!string.IsNullOrWhiteSpace(ai.Rfqno))
                {
                    isDuplicate = await context.Leads.AnyAsync(l =>
                        l.BusinessUnitId == businessUnitId &&
                        l.Rfqno == ai.Rfqno &&
                        l.BuyersName == ai.BuyersName);
                }
                else if (!string.IsNullOrWhiteSpace(ai.BuyersName))
                {
                    // For no-RFQ# cases, check buyer + first item's main description + quantity
                    var firstItem = ai.Items.FirstOrDefault();
                    if (firstItem != null && firstItem.Quantity > 0)
                    {
                        isDuplicate = await context.Leads.AnyAsync(l =>
                            l.BusinessUnitId == businessUnitId &&
                            l.BuyersName == ai.BuyersName &&
                            l.NoOfLineItems == ai.Items.Count &&
                            l.LeadItems.Any(li =>
                                li.CommodityProduct == firstItem.CommodityProduct &&
                                li.Quantity == firstItem.Quantity));
                    }
                }

                if (isDuplicate)
                {
                    _logger.LogWarning(
                        "Skipping duplicate lead for RFQ {Rfqno} from {Buyer} in business unit {BusinessUnitId}; "
                        + "this business unit already holds it.",
                        ai.Rfqno, ai.BuyersName, businessUnitId);
                    return (0, extracted);
                }
                
                // Use transaction to ensure atomic Lead + Items + Attachments creation.
                //
                // The transaction is opened INSIDE the configured execution strategy's delegate.
                // Program.cs enables retry-on-failure, so NpgsqlRetryingExecutionStrategy is
                // installed and refuses a user-initiated transaction opened outside one — the email
                // door's lead write threw "does not support user-initiated transactions" on every
                // PostgreSQL run, and the outer catch below reported it only as "Failed to save RFQ
                // from email", which reads like an extraction problem.
                //
                // The change tracker is deliberately NOT cleared on entry: the EmailIngest for this
                // message is staged above and is part of the same unit of work.
                var strategy = context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                await using var transaction = await context.Database.BeginTransactionAsync();
                try
                {
                    // Parse dates
                    DateTime recDate = ParseDate(ai.RecDate) ?? message.Date.DateTime;
                    DateTime? bidClosingDate = ParseDate(ai.BidClosingDate);
                    DateTime? acknowledgmentDate = ParseDate(ai.AcknowledgmentDate);
                    DateTime? subDate = ParseDate(ai.SubDate);
                    // Every extracted line is kept. Filtering on Quantity > 0 silently discarded any line
                    // whose quantity the document did not state — the extractor is instructed to return
                    // null in exactly that case — and the line count was taken from the filtered list, so
                    // the loss was self-consistent and invisible. A line a reviewer can see and correct is
                    // always better than a line that never existed.
                    var items = ai.Items.ToList();
                    // Create lead record
                    var lead = new Lead
                    {
                        Rfqno = Truncate(ai.Rfqno, 100),
                        BuyersName = Truncate(ai.BuyersName, 255),
                        RecDate = recDate,
                        BidClosingDate = bidClosingDate,
                        BiddingDecision = Truncate(ai.BiddingDecision, 100),
                        AcknowledgmentDate = acknowledgmentDate,
                        SubDate = subDate,
                        HeaderRemarks = Truncate(BuildHeaderRemarks(message, ai, extracted), 8000),
                        OpportunityNo = Truncate(ai.OpportunityNo, 100),
                        NoOfLineItems = items.Count,
                        Rfqtype = Truncate(ai.Rfqtype, 50),
                        DurationAgreement = Truncate(ai.DurationAgreement, 100),
                        LeadSource = "Email",
                        EmailSource = emailSource,
                        Clientemail = message.From.Mailboxes.FirstOrDefault()?.Address,
                        Aiconfidence = (decimal?)ai.OverallConfidence,
                        ReviewVersion = 1,
                        RequiresCommercialReview = true,
                        CommercialFactsVerified = false,
                        CreatedBy = "System",
                        CreatedDate = DateTime.UtcNow,
                        BusinessUnitId = config.BusinessUnitId,
                        EmailIngestsId = ingest.Id
                    };
                    context.Leads.Add(lead);
                    await context.SaveChangesAsync();
                    
                    // Add line items
                    foreach (var aiItem in items)
                    {
                        context.LeadItems.Add(CreateLeadItem(lead.Id, aiItem));
                    }
                    if (items.Count > 0)
                        await context.SaveChangesAsync();

                    // Canonical identity, in the SAME transaction as the lead and its lines.
                    //
                    // This door used to add a Lead directly and never call the identity service,
                    // so every emailed lead was born with line items and NO revision — and was
                    // therefore permanently unconvertible to an RFQ, because commercial line
                    // resolution refuses a lead with no immutable current revision.
                    //
                    // DELIBERATELY still the baseline writer, NOT ReconcileAsync — unlike the
                    // manual-upload and bulk-import doors, which were rewired to full
                    // reconciliation when the amendment fork was closed. This legacy direct path
                    // runs only when Ingestion:UseUnifiedQueue=false, and
                    // UnifiedDocumentIngestionGuard refuses to boot production in that state: in
                    // production every emailed document reaches ReconcileAsync through
                    // ExtractionWorker.LeadPersister, thread headers included. What remains here
                    // is a development/test fallback whose naive (Rfqno, BuyersName) pre-check
                    // above already refuses amendments before this line could reconcile them —
                    // making it amendment-correct would mean rebuilding the whole intake shape of
                    // a fenced-off path. Known, accepted limitation of the fallback; the fence is
                    // the guarantee.
                    //
                    // Constructed on the SAME `context` this method was handed, so it enlists in
                    // this transaction. The service takes only the DbContext, so this is
                    // behaviourally identical to resolving it from the owning scope — and it
                    // avoids changing the constructor for three existing test construction sites
                    // without making the wiring any more correct.
                    await new ERP_RFQ_Automation.LeadIdentity.LeadIdentityApplicationService(context)
                        .EstablishBaselineRevisionAsync(config.BusinessUnitId, lead.Id,
                            new ERP_RFQ_Automation.LeadIdentity.LeadIdentityBaselineRequest(
                                "Email",
                                "Inbound email: commercial facts were extracted from the message and its "
                                + "attachments. Canonical identity established at creation.",
                                "Service", "email-poller", $"email-lead:{config.BusinessUnitId}:{lead.Id}"));

                    // Save attachments. Anything storage refuses is appended to the SAME durable
                    // skip record, so the ingest carries one complete list.
                    var storageSkips = await SaveAttachmentsAsync(message, lead.Id, context);
                    if (storageSkips.Count > 0)
                    {
                        skippedAttachments.AddRange(storageSkips);
                        EmailIngestEnqueuer.RecordSkippedAttachments(ingest, skippedAttachments);
                    }

                    // Commit transaction - all or nothing
                    await transaction.CommitAsync();
                    
                    _logger.LogInformation(
                        "Successfully created lead {LeadId} from email: {Subject}",
                        lead.Id, message.Subject);
                    return (lead.Id, extracted);
                }
                catch (Exception txEx)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(txEx, "Transaction rolled back for email: {Subject}", message.Subject);
                    throw; // Re-throw to outer catch
                }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save RFQ from email: {Subject}", message.Subject);
                return (0, extracted);
            }
        }
        private string BuildHeaderRemarks(MimeMessage message, LeadExtractionResult ai, string extracted)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Email: From {message.From}, Subject: {message.Subject}, Date: {message.Date:yyyy-MM-dd}");
            
            if (!string.IsNullOrWhiteSpace(ai.HeaderRemarks))
            {
                sb.AppendLine();
                sb.AppendLine("Summary:");
                sb.AppendLine(ai.HeaderRemarks);
            }
            
            return sb.ToString();
        }
        private bool HasStrongRFQIndicators(MimeMessage message, LeadExtractionResult? ai)
        {
            var subject = message.Subject?.ToLowerInvariant() ?? "";
            var body = GetEmailBody(message).ToLowerInvariant();
            // Check for RFQ number pattern in subject (very strong indicator)
            if (Regex.IsMatch(subject, @"rfq\s*#?\s*([A-Z0-9-]+)", RegexOptions.IgnoreCase))
                return true;
            // Check if AI found an RFQ number even with low confidence
            if (!string.IsNullOrWhiteSpace(ai?.Rfqno))
                return true;
            // Check for tender/procurement reference numbers
            if (Regex.IsMatch(subject + " " + body,
                @"(tender|procurement|quotation)\s*#?\s*[A-Z0-9-]+", RegexOptions.IgnoreCase))
                return true;
            // Check for explicit RFQ keywords in subject
            var strongKeywords = new[] { "request for quotation", "rfq", "tender invitation" };
            if (strongKeywords.Any(k => subject.Contains(k)))
                return true;
            return false;
        }
        private LeadExtractionResult EnrichWithSubjectData(LeadExtractionResult ai, string subject)
        {
            // Try to extract RFQ number from subject if missing
            if (string.IsNullOrWhiteSpace(ai.Rfqno))
            {
                var match = Regex.Match(subject, @"rfq\s*#?\s*([A-Z0-9-]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    ai = ai with { Rfqno = match.Groups[1].Value.Trim() };
                    _logger.LogInformation("Extracted RFQ number from subject: {RfqNo}", ai.Rfqno);
                }
            }
            // Try to extract buyer/company name from subject
            if (string.IsNullOrWhiteSpace(ai.BuyersName))
            {
                // Look for company name patterns in subject
                var parts = subject.Split(new[] { '-', '|', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    // Often company name is before or after RFQ number
                    foreach (var part in parts)
                    {
                        if (!part.ToLowerInvariant().Contains("rfq") && part.Trim().Length > 3)
                        {
                            ai = ai with { BuyersName = part.Trim() };
                            _logger.LogInformation("Extracted buyer name from subject: {BuyerName}", ai.BuyersName);
                            break;
                        }
                    }
                }
            }
            return ai;
        }
        // DRIFT GUARD: the field-by-field mapping — including the ONE unit-of-measure
        // assignment — lives in LeadItemMapper, shared with the folder, manual-upload and
        // async-worker doors. Only this door's date conventions stay here.
        private LeadItem CreateLeadItem(long leadId, LeadItemData aiItem)
            => LeadItemMapper.Map(aiItem, ParseDate, leadId);
        private async Task<string> ExtractTextFromAttachment(MemoryStream ms, string ext)
        {
            return ext switch
            {
                ".pdf" => ExtractTextFromPdf(ms.ToArray()),
                ".doc" => ExtractTextFromLegacyDoc(ms),
                ".docx" => ExtractTextFromDocx(ms),
                ".xlsx" or ".xlsm" => ExtractTextFromExcel(ms),
                ".xls" => ExtractTextFromLegacyXls(ms),
                // .pptx is intentionally absent: the intake filter refuses it because the
                // security inspection allow-list does not include PowerPoint.
                ".csv" or ".txt" => ExtractTextFromPlainText(ms),
                ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp"
                    => ExtractTextFromImage(ms.ToArray()),
                _ => ""
            };
        }
        // DRIFT GUARD: one owner for the file-type label, shared with the queue fan-out, so a
        // lead ingested by the poller and the same lead replayed from the triage surface can
        // never be labelled differently.
        private string GetFileTypeLabel(string ext) => EmailIngestEnqueuer.GetFileTypeLabel(ext);
        // Text extraction methods
        private string ExtractTextFromPdf(byte[] bytes)
        {
            string pdfPigText = "";
            try
            {
                using var doc = PdfDocument.Open(bytes);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                    sb.AppendLine(page.Text);
                pdfPigText = sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF text extraction failed");
            }

            // Fast path: the PDF already has an embedded text layer.
            if (!IsNearEmptyText(pdfPigText))
                return pdfPigText;

            // ING-04: image-only / scanned PDF yields (near) zero embedded text. Rasterize the
            // pages with Docnet and OCR them with Tesseract as a fallback.
            _logger.LogInformation(
                "PDF has little/no embedded text ({Chars} non-whitespace chars); attempting OCR fallback.",
                CountNonWhitespace(pdfPigText));
            var ocrText = TryOcrScannedPdf(bytes);
            if (!IsNearEmptyText(ocrText))
            {
                // OCR text is inherently lower-confidence: label it so the LLM and human reviewers
                // treat it with appropriate caution (the model naturally lowers confidence on noisy input).
                return "[OCR-EXTRACTED TEXT FROM SCANNED PDF - lower confidence, may contain recognition errors]\n" + ocrText;
            }

            // Could not obtain text from a scanned PDF (OCR unavailable/failed or blank page).
            // Emit a marker so the caller routes the ingest to review instead of a silent empty lead.
            _logger.LogWarning("Scanned PDF could not be OCR'd (OCR unavailable or produced no text).");
            return SCANNED_PDF_SENTINEL;
        }

        /// <summary>
        /// ING-04: rasterizes a scanned/image-only PDF with Docnet.Core and OCRs each page with the
        /// existing Tesseract engine. Returns recognized text, or "" if OCR is unavailable/failed.
        /// </summary>
        private string TryOcrScannedPdf(byte[] pdfBytes)
        {
            const int MAX_OCR_PAGES = 10;      // bound runtime for large documents
            const double RENDER_SCALE = 2.0;   // ~144 DPI: balances OCR accuracy vs. memory/time
            try
            {
                var sb = new StringBuilder();
                // Serialize native pdfium + Tesseract access (neither is thread-safe) because
                // attachment extraction runs in parallel.
                lock (_ocrLock)
                {
                    using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(RENDER_SCALE));
                    int pageCount = docReader.GetPageCount();
                    int pagesToProcess = Math.Min(pageCount, MAX_OCR_PAGES);
                    if (pageCount > MAX_OCR_PAGES)
                        _logger.LogWarning("Scanned PDF has {Total} pages; OCR limited to first {Limit}.", pageCount, MAX_OCR_PAGES);

                    using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                    for (int i = 0; i < pagesToProcess; i++)
                    {
                        try
                        {
                            using var pageReader = docReader.GetPageReader(i);
                            // Composite any transparency over white for cleaner OCR; returns BGRA.
                            var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover());
                            int width = pageReader.GetPageWidth();
                            int height = pageReader.GetPageHeight();
                            if (rawBytes == null || width <= 0 || height <= 0 ||
                                rawBytes.Length < width * height * 4)
                                continue;

                            var bmp = BgraToBmp24(rawBytes, width, height);
                            using var pix = Pix.LoadFromMemory(bmp);
                            using var page = engine.Process(pix);
                            var pageText = page.GetText();
                            if (!string.IsNullOrWhiteSpace(pageText))
                                sb.AppendLine(pageText);
                        }
                        catch (Exception exPage)
                        {
                            _logger.LogWarning(exPage, "OCR failed for scanned PDF page {Page}", i);
                        }
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                // Docnet native lib unavailable (unsupported RID), malformed/encrypted PDF, etc.
                _logger.LogWarning(ex, "Scanned-PDF OCR fallback unavailable or failed.");
                return "";
            }
        }

        /// <summary>Encodes a top-down BGRA buffer as a 24-bit (BGR) bottom-up BMP that Leptonica/Tesseract can read.</summary>
        private static byte[] BgraToBmp24(byte[] bgra, int width, int height)
        {
            int rowSize = ((24 * width + 31) / 32) * 4; // rows padded to a 4-byte boundary
            int pixelDataSize = rowSize * height;
            const int headerSize = 54;
            var bmp = new byte[headerSize + pixelDataSize];

            // BITMAPFILEHEADER
            bmp[0] = 0x42; // 'B'
            bmp[1] = 0x4D; // 'M'
            WriteInt32LE(bmp, 2, bmp.Length);
            WriteInt32LE(bmp, 10, headerSize);
            // BITMAPINFOHEADER
            WriteInt32LE(bmp, 14, 40);
            WriteInt32LE(bmp, 18, width);
            WriteInt32LE(bmp, 22, height); // positive height -> bottom-up
            WriteInt16LE(bmp, 26, 1);      // planes
            WriteInt16LE(bmp, 28, 24);     // bits per pixel
            WriteInt32LE(bmp, 30, 0);      // BI_RGB (uncompressed)
            WriteInt32LE(bmp, 34, pixelDataSize);
            WriteInt32LE(bmp, 38, 2835);   // ~72 DPI (x)
            WriteInt32LE(bmp, 42, 2835);   // ~72 DPI (y)

            int srcStride = width * 4;
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * srcStride;                               // source is top-down
                int dst = headerSize + (height - 1 - y) * rowSize;        // dest is bottom-up
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    bmp[dst++] = bgra[s];     // B
                    bmp[dst++] = bgra[s + 1]; // G
                    bmp[dst++] = bgra[s + 2]; // R
                }
            }
            return bmp;
        }

        private static void WriteInt32LE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteInt16LE(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static int CountNonWhitespace(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (var c in s) if (!char.IsWhiteSpace(c)) n++;
            return n;
        }

        // A PDF that yields fewer than this many non-whitespace characters is treated as scanned/image-only.
        private static bool IsNearEmptyText(string? s) => CountNonWhitespace(s) < 20;
        private string ExtractTextFromDocx(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var doc = WordprocessingDocument.Open(ms, false);
                var sb = new StringBuilder();
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body != null)
                {
                    foreach (var text in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>())
                        sb.Append(text.Text);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DOCX extraction failed");
                return "";
            }
        }
        private string ExtractTextFromExcel(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var doc = SpreadsheetDocument.Open(ms, false);
                var sb = new StringBuilder();
                var workbookPart = doc.WorkbookPart;
                var sstPart = workbookPart?.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
                var sst = sstPart?.SharedStringTable;
                foreach (var sheet in workbookPart.Workbook.Descendants<Sheet>())
                {
                    var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
                    foreach (var cell in worksheetPart.Worksheet.Descendants<Cell>())
                    {
                        if (cell.CellValue == null) continue;
                        string value = cell.CellValue.Text;
                        if (cell.DataType != null && cell.DataType == CellValues.SharedString && sst != null)
                        {
                            int index = int.Parse(value);
                            value = sst.ElementAt(index).InnerText;
                        }
                        sb.Append(value + " ");
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "XLSX extraction failed");
                return "";
            }
        }
        /// <summary>Legacy Word 97-2003 (.doc) binary — shared OLE/piece-table reader.</summary>
        private string ExtractTextFromLegacyDoc(MemoryStream ms)
        {
            try
            {
                return ERP_RFQ_Automation.Services.DocumentIntelligence
                    .WordBinaryTextExtractor.Extract(ms.ToArray(), _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy DOC extraction failed");
                return "";
            }
        }
        // ExcelDataReader needs the code-pages provider for legacy .xls encodings.
        private static int _codePagesRegistered;
        private string ExtractTextFromLegacyXls(MemoryStream ms)
        {
            try
            {
                if (Interlocked.Exchange(ref _codePagesRegistered, 1) == 0)
                    Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                ms.Position = 0;
                using var reader = ExcelDataReader.ExcelReaderFactory.CreateBinaryReader(
                    ms, new ExcelDataReader.ExcelReaderConfiguration
                    {
                        FallbackEncoding = Encoding.GetEncoding(1252),
                        LeaveOpen = true
                    });
                var sb = new StringBuilder();
                do
                {
                    while (reader.Read())
                    {
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var value = reader.GetValue(i);
                            if (value != null) sb.Append(value).Append(' ');
                        }
                        sb.AppendLine();
                    }
                } while (reader.NextResult());
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy XLS extraction failed");
                return "";
            }
        }
        private string ExtractTextFromPlainText(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var reader = new StreamReader(ms, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plain-text extraction failed");
                return "";
            }
        }
        private string ExtractTextFromPptx(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var doc = PresentationDocument.Open(ms, false);
                var sb = new StringBuilder();
                var presentationPart = doc.PresentationPart;
                if (presentationPart == null) return "";
                foreach (var slidePart in presentationPart.SlideParts)
                {
                    foreach (var text in slidePart.Slide.Descendants<DocumentFormat.OpenXml.Presentation.Text>())
                    {
                        sb.Append(text.Text + " ");
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PPTX extraction failed");
                return "";
            }
        }
        private string ExtractTextFromImage(byte[] bytes)
        {
            try
            {
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromMemory(bytes);
                using var page = engine.Process(img);
                return page.GetText();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR failed");
                return "";
            }
        }
        // Utility methods
        private string LimitTextForLLM(string emailBody, string attachmentsText, string subject)
        {
            var totalChars = emailBody.Length + attachmentsText.Length + subject.Length;

            // If under limit, return as-is
            if (totalChars <= MAX_CHARS_FOR_LLM)
            {
                return $"Subject: {subject}\n\nBody:\n{emailBody}\n\nAttachments:\n{attachmentsText}";
            }

            _logger.LogWarning(
                "Text exceeds LLM limit. Total: {Total} chars, Limit: {Limit} chars. Truncating intelligently.",
                totalChars, MAX_CHARS_FOR_LLM);

            var sb = new StringBuilder();
            sb.AppendLine($"Subject: {subject}");
            sb.AppendLine();

            // Priority 1: Email body (always include, truncate if needed)
            sb.AppendLine("Body:");
            if (emailBody.Length > PRIORITY_EMAIL_BODY_CHARS)
            {
                sb.AppendLine(emailBody.Substring(0, PRIORITY_EMAIL_BODY_CHARS));
                sb.AppendLine($"[... EMAIL BODY TRUNCATED - {emailBody.Length - PRIORITY_EMAIL_BODY_CHARS} chars omitted ...]");
            }
            else
            {
                sb.AppendLine(emailBody);
            }
            sb.AppendLine();

            // Priority 2: Attachments (distribute remaining space)
            var remainingChars = MAX_CHARS_FOR_LLM - sb.Length;
            sb.AppendLine("Attachments:");

            if (attachmentsText.Length > remainingChars)
            {
                // Extract first N chars of attachments (likely has most important info)
                sb.AppendLine(attachmentsText.Substring(0, Math.Min(remainingChars, attachmentsText.Length)));
                sb.AppendLine($"[... ATTACHMENTS TRUNCATED - {attachmentsText.Length - remainingChars} chars omitted ...]");
                sb.AppendLine("NOTE: Large document detected. Manual review may be needed for complete details.");
            }
            else
            {
                sb.AppendLine(attachmentsText);
            }

            return sb.ToString();
        }
        // ING-07: HasRFQKeywordsInFilename / QuickScanAttachmentContentAsync / ExtractTextSnippetAsync
        // are GONE. They existed to answer "does this email deserve to be processed?" by
        // keyword — a question the triage gate now answers on positive evidence instead. The
        // attachment content pre-scan is additionally redundant: attachments are ALWAYS
        // enqueued, so parsing every attachment twice bought nothing but latency and a second
        // chance to lose an RFQ whose PDF happened to spell "quotation" as "Offer".
        private string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }
        // Shared with every other ingestion door — see RfqDateParser for why this is no longer
        // a per-service copy. This path previously had no sentinel-year guard, so an extracted
        // "0001-01-01" reached the database as a real closing date.
        private DateTime? ParseDate(string? s) => Extraction.RfqDateParser.Parse(s);
        private string GetEmailBody(MimeMessage message)
        {
            var body = message.GetTextBody(TextFormat.Plain);
            if (!string.IsNullOrWhiteSpace(body)) return body;
            var html = message.GetTextBody(TextFormat.Html);
            return html != null
                ? Regex.Replace(html, "<.*?>", " ")
                    .Replace("&nbsp;", " ")
                    .Replace("\r\n", "\n")
                : "";
        }
        private string SanitizeFileName(string fileName) => EmailIngestEnqueuer.SanitizeFileName(fileName);
        // DRIFT GUARD: the email attachment filter is DERIVED from the security
        // inspection allow-list — never keep a private copy here. A private list
        // previously accepted .pptx (which inspection then rejected) and silently
        // dropped .xls/.csv supplier quotes and customer RFQs (which inspection
        // accepts), losing leads. See DocumentIntakeAllowList and its tests.
        internal static bool IsSupportedExtension(string ext) =>
            ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList.IsAllowed(ext);
        /// <summary>
        /// Persists the message's attachments against the created lead.
        /// ING-06: returns "filename (reason)" for every attachment it did NOT store. Three of
        /// these were bare `continue` statements — most notably `.eml`, which was dropped with
        /// no log, no row and no reason at all — so an attached forwarded message simply ceased
        /// to exist between the mailbox and the lead.
        /// </summary>
        private async Task<IReadOnlyList<string>> SaveAttachmentsAsync(MimeMessage message, long leadId,
            ErpRfqAutomationContext context)
        {
            var skipped = new List<string>();
            void RecordSkipped(string fileName, string reason)
            {
                skipped.Add($"{fileName} ({reason})");
                _logger.LogWarning(
                    "Not storing email attachment {FileName} against lead {LeadId}: {Reason}.",
                    fileName, leadId, reason);
            }

            // FIXED: Removed parallel Task.Run to fix DbContext thread-safety issue
            // DbContext is NOT thread-safe - process sequentially instead
            var ordinal = 0;
            foreach (var entity in message.Attachments)
            {
                ordinal++;
                if (entity is not MimePart part)
                {
                    RecordSkipped(
                        entity.ContentDisposition?.FileName ?? $"attachment #{ordinal}",
                        "embedded email message is not stored");
                    continue;
                }
                if (part.FileName == null)
                {
                    RecordSkipped($"attachment #{ordinal}", "attachment has no filename");
                    continue;
                }
                if (part.FileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
                {
                    // Unchanged behaviour, now with a reason: .eml is off the intake allow-list
                    // (DocumentIntakeAllowList), so it is recorded-but-not-processed. The raw
                    // parent message is retained on disk, so the bytes are still recoverable.
                    RecordSkipped(part.FileName,
                        "'.eml' is not an accepted intake format; recorded but not processed");
                    continue;
                }

                var safeName = SanitizeFileName(part.FileName);
                var fileName = $"{leadId}_{Guid.NewGuid()}_{safeName}";
                var relativePath = Path.Combine("Uploads", "RFQ_Attachments", fileName);
                var physicalPath = Path.Combine(_attachmentPath, fileName);

                try
                {
                    // Check file size BEFORE writing to disk (security fix)
                    long size = 0;
                    using (var tempStream = new MemoryStream())
                    {
                        await part.Content.DecodeToAsync(tempStream);
                        size = tempStream.Length;
                        
                        if (size > MAX_ATTACHMENT_SIZE)
                        {
                            RecordSkipped(safeName,
                                $"exceeds the {MAX_ATTACHMENT_SIZE / (1024 * 1024)} MB size limit ({size} bytes)");
                            continue; // Skip this attachment
                        }
                        
                        // Write to disk only if size is acceptable
                        tempStream.Position = 0;
                        await using var fileStream = File.Create(physicalPath);
                        await tempStream.CopyToAsync(fileStream);
                        await fileStream.FlushAsync();
                    }

                    // Add to database (thread-safe since we're sequential now)
                    context.Attachments.Add(new Attachment
                    {
                        ParentType = "Lead",
                        ParentId = leadId,
                        FileName = safeName,
                        FilePath = relativePath,
                        MimeType = part.ContentType?.MimeType,
                        FileSize = size,
                        ContentType = part.ContentType?.MediaType,
                        CreatedOn = DateTime.UtcNow,
                        UploadedDate = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save attachment: {File}", safeName);
                    RecordSkipped(safeName, $"could not be stored: {ex.GetType().Name}");
                    // Clean up file if it was created
                    if (File.Exists(physicalPath))
                    {
                        try { File.Delete(physicalPath); } catch { /* Ignore cleanup errors */ }
                    }
                }
            }

            // Save all attachments at once
            if (message.Attachments.Any())
                await context.SaveChangesAsync();

            return skipped;
        }
        public async Task SendEmailAsync(string to, string subject, string body, List<(string FileName, byte[] FileContent, string ContentType)> attachments = null, string fromEmail = null, long? businessUnitId = null)
        {
            var query = _context.EmailConfigurations.Where(e => e.IsActive && e.Protocol.ToUpper() == "SMTP");

            if (!string.IsNullOrEmpty(fromEmail))
            {
                query = query.Where(e => e.EmailAddress == fromEmail);
            }

            if (businessUnitId.HasValue)
            {
                query = query.Where(e => e.BusinessUnitId == businessUnitId.Value);
            }

            var config = await query.FirstOrDefaultAsync();

            // Fallback within the SAME business unit only: if a specific fromEmail was requested but
            // not found, fall back to any active SMTP config belonging to that business unit.
            // We must never fall back to another tenant's SMTP account — doing so would send this
            // tenant's quote (customer names, pricing, terms) through a different tenant's mail server.
            if (config == null && !string.IsNullOrEmpty(fromEmail) && businessUnitId.HasValue)
            {
                config = await _context.EmailConfigurations
                    .FirstOrDefaultAsync(e => e.IsActive && e.Protocol.ToUpper() == "SMTP" && e.BusinessUnitId == businessUnitId);
            }

            if (config == null)
            {
                throw new InvalidOperationException(
                    businessUnitId.HasValue
                        ? $"No active SMTP configuration found for business unit {businessUnitId.Value}. Configure an email account for this business unit before sending."
                        : "No active SMTP configuration found for sending emails.");
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(config.ConfigurationName, config.EmailAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = body };

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    builder.Attachments.Add(attachment.FileName, attachment.FileContent, ContentType.Parse(attachment.ContentType));
                }
            }

            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            try
            {
                // The host and port on this row are supplied by a TENANT ADMINISTRATOR, and this
                // path dialled them directly — the only quote-delivery send in the product that did
                // not go through MailEndpointPolicy. That made it a working SSRF primitive: a
                // mailbox row pointed at 169.254.169.254 or an RFC 1918 address had this server open
                // the socket from inside the trust boundary. Resolving and connecting through the
                // policy closes it; the TLS mode and the credential below are deliberately
                // unchanged, so nothing about a legitimate host behaves differently.
                using var socket = await ERP_RFQ_Automation.Security.MailEndpointPolicy
                    .ConnectAsync(config.Host, config.Port, CancellationToken.None);
                await client.ConnectAsync(socket, config.Host, config.Port, config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(config.EmailAddress, config.Password);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {To} using {From} (Host: {Host}, Port: {Port}, SSL: {SSL})", to, config.EmailAddress, config.Host, config.Port, config.UseSsl);
                throw;
            }
        }
    }
}

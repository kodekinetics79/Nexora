using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.Services.Interfaces;
namespace ERP_RFQ_Automation.Services
{
    /// <summary>
    /// Polls the configured IMAP mailboxes and sweeps the watched folders.
    ///
    /// SINGLE-INSTANCE BY CONSTRUCTION. There is no per-message lease anywhere in the
    /// IMAP path, so N instances polling the same mailbox concurrently fetch, extract
    /// and ingest the same messages. The cycle therefore runs only inside a PostgreSQL
    /// advisory lock (<see cref="PostgresAdvisoryLease"/>); instances that do not win
    /// the lock stand by and retry next tick, and the lock is released automatically if
    /// the holder dies. This makes the poller safe to scale horizontally: extra
    /// instances serve HTTP and take over polling the moment the leader disappears.
    ///
    /// ING-08 — THE HEARTBEAT TELLS THE TRUTH NOW. This loop used to beat
    /// <see cref="BackgroundWorkerNames.EmailPoller"/> unconditionally at the bottom of every
    /// iteration, whatever had just happened inside it. In production that meant
    /// <c>MailKit.Security.AuthenticationException: Authentication failed</c> followed 1.5 ms
    /// later by "Email fetch completed successfully.", a green heartbeat, a green
    /// <c>/ready</c>, and a mailbox that had not been read since 2026-07-30. The loop was
    /// alive; the door was shut; nothing in the system said so.
    ///
    /// The rule was then: a cycle beats the success heartbeat ONLY if it succeeded. A failed
    /// cycle records WHY — durably, per mailbox — and lets the surfaces go red, while the loop
    /// itself keeps retrying forever.
    ///
    /// ING-08b, 2026-08-24 — AND THAT RULE DISARMED THE ALARM IT WAS BUILT TO RAISE.
    /// "Succeeded" meant <c>report.AnyFailed == false</c>: EVERY mailbox in the cycle. Production
    /// ran two — <c>Email_Configurations</c> 5, which had never authenticated once in its life
    /// (11,782 consecutive failures, <c>LastSuccessfulPollOn</c> null), and 9, which polled
    /// cleanly every seventy seconds. <c>AnyFailed</c> was therefore true on every cycle, the
    /// poller never beat once since process start, and <c>background-workers</c> reported
    /// "Background worker(s) stopped beating: email-poller" about a loop that was demonstrably
    /// alive. A REAL poller death became indistinguishable from that standing false alarm — the
    /// exact failure the heartbeat exists to catch, masked by the heartbeat's own reporting.
    ///
    /// <para><b>The heartbeat now beats once per completed iteration, unconditionally on mailbox
    /// outcomes.</b> It answers ONE question — is this loop still turning — and mailbox
    /// correctness is answered by <c>email-poll-channel</c>
    /// (<see cref="EmailPollerHealthCheck"/>), per mailbox, by name. Beating only on
    /// "at least one mailbox succeeded" was rejected as a half-measure: with a single configured
    /// mailbox, the common case, that collapses straight back into "every mailbox succeeded" and
    /// the false "worker stopped beating" returns. Two questions, two signals, no sharing.</para>
    ///
    /// <para>Nothing is lost by that. A loop that faults, hangs or exits stops completing
    /// iterations, so it stops beating and <c>background-workers</c> goes red on its own
    /// evidence; a loop that turns while every mailbox refuses authentication is reported —
    /// loudly, and with the mailbox's id and address — by the channel check.</para>
    ///
    /// <para><b>AND THE BROKEN MAILBOX KEEPS BEING RETRIED. DELIBERATELY.</b> The obvious
    /// tidy-up for a row with 11,782 consecutive failures and no lifetime success is to
    /// auto-quiesce it — set <c>IsActive = false</c> and stop dialling. It is refused, for four
    /// reasons, and the refusal is written down here so it is a decision rather than an
    /// omission:</para>
    /// <list type="number">
    ///   <item><description><b>A disabled mailbox disappears from the alarm.</b> Every surface
    ///   that reports mailbox health filters on <c>IsActive</c> —
    ///   <c>EmailService.FetchAndSaveLeadsAsync</c>, <c>SeedChannelHealthAsync</c> below, and
    ///   <c>MailboxController.Health</c>, which reports a deactivated row as the flat "Disabled".
    ///   Auto-quiescing would therefore turn a loud, TRUE red light green by deciding on the
    ///   customer's behalf that a broken mailbox no longer counts. That is the same class of
    ///   defect as the one this file is being fixed for: an alarm that switches itself
    ///   off.</description></item>
    ///   <item><description><b>It risks losing mail permanently.</b> The lookback window is
    ///   derived from <c>LastSuccessfulPollOn</c> and bounded by
    ///   <c>Ingestion:Email:MaxLookbackDays</c>. A mailbox quiesced for longer than that window
    ///   has its oldest mail fall outside it on reinstatement, and nothing ever reads those
    ///   messages — the exact hole ING-08 was opened to close.</description></item>
    ///   <item><description><b>The failure often heals without us.</b> A rotated app password, a
    ///   provider re-enabling IMAP, an expired token refreshed: retrying is what makes recovery
    ///   automatic. Quiescing converts a self-healing fault into one that waits for a human who
    ///   has not been told to act.</description></item>
    ///   <item><description><b>It is the system overwriting a customer's setting.</b>
    ///   <c>IsActive</c> is a choice a tenant administrator made on Setup &gt; Email Inboxes.
    ///   Inferring a new value for it and writing it back — with no audit row, no notification,
    ///   and a health line that then says only "Disabled" — is precisely the silent disable this
    ///   must not be.</description></item>
    /// </list>
    /// <para>What the mailbox gets instead is a NAME and a diagnosis. The channel check reports
    /// it by id and address, and distinguishes "has never once been read since it was configured"
    /// — an unfinished setup, whose remedy is to fix the credentials or deactivate it, and that
    /// remains the operator's call — from "worked until 30 July and then stopped", which is an
    /// incident. The cost of continuing to retry is one IMAP handshake per mailbox per poll
    /// interval; that is the price of being able to notice the moment it starts working
    /// again.</para>
    /// </summary>
    public class EmailBackgroundService : BackgroundService
    {
        internal const string PollLockName = "nexora:email-poller";

        /// <summary>
        /// THE UNIT OF <c>EmailConfiguration.PollingInterval</c> IS MINUTES.
        ///
        /// <para>It is stated here because it used to be stated nowhere, and every surface a
        /// human touches disagreed with the one line of code that consumed it. The API DTOs
        /// documented "Minutes between polls" and constrained it to <c>[1, 1440]</c>; the
        /// mailbox screen labelled the input "Check for new mail every … minutes"; the
        /// mailbox health text said "Polling every N minute(s)" — and this loop read the same
        /// number as SECONDS. An operator who typed 5, meaning five minutes, got a five-second
        /// IMAP poll: sixty times the intended rate, which is the standard route to provider
        /// throttling or an account lock, and it would have taken out the primary intake
        /// channel silently. Nothing caught it because no test polls a real mailbox.</para>
        ///
        /// <para>Minutes wins because minutes is what every human-facing surface already
        /// promised. Every stored value keeps its number and changes meaning from seconds to
        /// minutes, which is a sixty-fold SLOWDOWN — no existing row can start polling faster
        /// than it does today, so the transition cannot create the hazard it removes, and no
        /// backfill is required to make it safe.</para>
        /// </summary>
        internal const int MinimumPollIntervalMinutes = 1;

        /// <inheritdoc cref="MinimumPollIntervalMinutes"/>
        internal const int MaximumPollIntervalMinutes = 1440;

        /// <summary>
        /// Used when no active mailbox states an interval, or when the interval cannot be read.
        /// Five minutes — deliberately the same number the create/update DTOs default to, so
        /// the fallback and the operator default mean the same thing.
        /// </summary>
        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(5);

        private readonly IServiceProvider _services;
        private readonly IBackgroundWorkerHeartbeats? _heartbeats;
        private readonly IEmailPollerHealth? _pollerHealth;
        private readonly ILogger<EmailBackgroundService> _logger;

        public EmailBackgroundService(
            IServiceProvider services,
            ILogger<EmailBackgroundService> logger,
            IBackgroundWorkerHeartbeats? heartbeats = null,
            IEmailPollerHealth? pollerHealth = null)
        {
            _services = services;
            _logger = logger;
            _heartbeats = heartbeats;
            _pollerHealth = pollerHealth;
            _heartbeats?.Register(BackgroundWorkerNames.EmailPoller, DefaultPollInterval);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Email Background Service started.");
            // NOT a success beat: the loop has started, it has not polled anything. The
            // registry's startup grace covers a worker that never reaches its first cycle.
            await SeedChannelHealthAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = DefaultPollInterval;
                using (var scope = _services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

                    PostgresAdvisoryLease? lease = null;
                    // "The lock said no" and "we could not ask" are different facts. The second
                    // one means this instance polled nothing and does not know why, which is a
                    // channel failure — not a standby.
                    var lockEvaluated = true;
                    try
                    {
                        lease = await PostgresAdvisoryLease.TryAcquireAsync(dbContext, PollLockName, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Never poll without the lock: a transient failure to acquire it must
                        // degrade to "skip this cycle", not to "poll concurrently".
                        lockEvaluated = false;
                        _logger.LogError(ex, "Could not evaluate the email-poller lock; skipping this cycle.");
                        // Recorded, not swallowed. Before this, a lock that could never be
                        // evaluated left the loop beating liveness forever while the channel
                        // check happily reported "has not completed a poll cycle yet" — no
                        // mail ingested, every surface green.
                        _pollerHealth?.RecordCycleFailure(
                            $"The email-poller lock could not be evaluated ({ex.GetType().Name}: {ex.Message}), "
                            + "so no mailbox was polled this cycle.",
                            isPermanent: false, DateTimeOffset.UtcNow);
                    }

                    if (lease is null)
                    {
                        if (lockEvaluated)
                        {
                            _logger.LogDebug(
                                "Another instance holds the email-poller lock; standing by for {Interval}s.",
                                interval.TotalSeconds);
                            // Standing by is a legitimate, healthy state — the loop is turning
                            // and another instance is doing the work — so it must never clear
                            // or fabricate a mailbox result.
                            _pollerHealth?.StandBy();
                        }
                    }
                    else
                    {
                        await using (lease)
                        {
                            try
                            {
                                await RunPollCycleAsync(scope.ServiceProvider, stoppingToken);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                // BackgroundServiceExceptionBehavior is Ignore, so an escaping
                                // exception would kill the poller for the lifetime of the
                                // process. It is caught — but recorded as a FAILURE, never
                                // logged-and-forgotten while the heartbeat carries on.
                                _logger.LogError(ex, "Email poll cycle failed; retrying next interval.");
                                _pollerHealth?.RecordCycleFailure(
                                    $"The email poll cycle threw {ex.GetType().Name}: {ex.Message}",
                                    isPermanent: false, DateTimeOffset.UtcNow);
                            }

                            interval = await ResolvePollIntervalAsync(dbContext, stoppingToken);
                        }
                    }
                }

                // LIVENESS, AND ONLY LIVENESS. Reaching this line means the iteration ran to
                // completion, which is the entire claim the heartbeat makes. Whether a mailbox
                // authenticated is a different question with its own check — see the class
                // comment for why gating this beat on mailbox outcomes silenced the alarm for
                // 11,782 consecutive cycles.
                _heartbeats?.Beat(BackgroundWorkerNames.EmailPoller, interval);

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            _logger.LogInformation("Email Background Service stopped.");
        }

        /// <summary>
        /// ING-08: hydrates the in-process channel ledger from the durable per-mailbox state so
        /// a restart cannot launder a broken mailbox back to green, and so a STANDBY instance —
        /// which never polls and would otherwise learn nothing — still reports the truth.
        /// Best-effort by design: a failure to read it must not stop the poller.
        /// </summary>
        private async Task SeedChannelHealthAsync(CancellationToken stoppingToken)
        {
            if (_pollerHealth is null) return;
            try
            {
                using var scope = _services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                var mailboxes = await dbContext.EmailConfigurations
                    .AsNoTracking()
                    .Where(e => e.IsActive && e.Protocol.ToUpper() == "IMAP")
                    .Select(e => new
                    {
                        e.Id,
                        e.EmailAddress,
                        e.LastSuccessfulPollOn,
                        e.LastPollAttemptOn,
                        e.LastPollError,
                        e.ConsecutivePollFailures
                    })
                    .ToListAsync(stoppingToken);
                if (mailboxes.Count == 0) return;

                // PER MAILBOX. This used to be smeared into one channel-wide row — the OLDEST
                // last-successful-poll and the WORST failure count across every mailbox — which
                // is how a mailbox that had never worked came to report "Last successful poll:
                // never" for a deployment whose other mailbox was being read every minute.
                var seeded = mailboxes
                    .Select(m => new EmailMailboxChannelStatus(
                        MailboxId: m.Id,
                        Mailbox: m.EmailAddress,
                        LastSuccessUtc: m.LastSuccessfulPollOn is { } success
                            ? new DateTimeOffset(DateTime.SpecifyKind(success, DateTimeKind.Utc))
                            : null,
                        LastFailureUtc: string.IsNullOrWhiteSpace(m.LastPollError)
                            ? null
                            : m.LastPollAttemptOn is { } attempt
                                ? new DateTimeOffset(DateTime.SpecifyKind(attempt, DateTimeKind.Utc))
                                : null,
                        ConsecutiveFailures: string.IsNullOrWhiteSpace(m.LastPollError)
                            ? 0
                            : m.ConsecutivePollFailures,
                        LastFailureReason: string.IsNullOrWhiteSpace(m.LastPollError) ? null : m.LastPollError,
                        // Conservative: the durable row does not record whether the failure was
                        // permanent, so it is seeded as transient and the first real cycle
                        // reclassifies it. This delays red at most one poll interval; claiming
                        // permanence we did not observe would be a different kind of lie.
                        LastFailureIsPermanent: false))
                    .ToList();
                _pollerHealth.SeedMailboxes(seeded);

                var failing = seeded.Where(m => m.IsFailing).ToList();
                foreach (var mailbox in failing)
                {
                    _logger.LogWarning(
                        "Email poller starting with mailbox {MailboxId} ({Mailbox}) in a failed state from the "
                        + "durable poll ledger: {Reason} Consecutive failures: {Failures}. Last successful read: {LastSuccess}.",
                        mailbox.MailboxId, mailbox.Mailbox, mailbox.LastFailureReason,
                        mailbox.ConsecutiveFailures, mailbox.LastSuccessUtc?.ToString("O") ?? "never");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not seed the email channel health from the durable poll ledger; "
                    + "the first poll cycle will establish it.");
            }
        }

        /// <summary>
        /// Runs one poll cycle. The per-mailbox verdicts are published by
        /// <c>EmailService.PublishChannelHealth</c>, mailbox by mailbox; what this method owns is
        /// the CYCLE-scope verdict — did the fetch call itself get to run at all.
        ///
        /// <para><b>It no longer returns a heartbeat decision.</b> It used to return
        /// <c>false</c> whenever <c>report.AnyFailed</c> was true, and the caller withheld the
        /// liveness beat on that basis — so one permanently broken mailbox out of two reported
        /// the poll LOOP as dead. See the class comment.</para>
        ///
        /// The folder sweep below is a separate channel and deliberately does not change the
        /// MAILBOX verdict — a broken watched folder must not mask a working mailbox, or the
        /// reverse.
        /// </summary>
        private async Task RunPollCycleAsync(IServiceProvider scopedServices, CancellationToken stoppingToken)
        {
            var emailService = scopedServices.GetRequiredService<IEmailService>();
            try
            {
                _logger.LogInformation("Starting email fetch...");
                var report = await emailService.FetchAndSaveLeadsAsync();
                // The fetch call returned, so the CYCLE is not what failed. Any mailbox that
                // did fail is already recorded against its own id.
                _pollerHealth?.RecordCycleCompleted(DateTimeOffset.UtcNow);
                // ...and a mailbox that is no longer in the cycle is no longer ours to judge.
                // This is the cycle that polls EVERY tenant (no businessUnitId filter), so the
                // report is the authoritative active set. Without this the operator's own remedy
                // — deactivating the broken mailbox — would leave /ready red until the next
                // deploy, because nothing else ever clears an entry.
                _pollerHealth?.RetireMailboxesExcept(
                    report.Mailboxes.Select(m => m.EmailConfigurationId).ToHashSet());

                if (report.AnyFailed)
                {
                    // NOT "completed successfully". This is the sentence the operator needed
                    // for the seven days the mailbox was unreachable and nobody was told.
                    // It names the failing mailboxes and — since 2026-08-24 — the ones that
                    // are still working, because "1 of 2 failing" and "everything is down"
                    // are different incidents and used to log identically.
                    _logger.LogError(
                        "Email fetch FAILED for {Failed} of {Total} mailbox(es): {Reasons} "
                        + "Oldest last-successful poll among them: {LastSuccess}. "
                        + "No mail is being ingested from those mailboxes. "
                        + "{Healthy} mailbox(es) polled normally this cycle.",
                        report.Failed, report.Polled, report.FailureSummary,
                        report.OldestLastSuccessfulPoll?.ToString("O") ?? "never",
                        report.Polled - report.Failed);
                }
                else if (report.AllSucceeded)
                {
                    _logger.LogInformation(
                        "Email fetch completed successfully for {Total} mailbox(es).", report.Polled);
                }
                else
                {
                    // Zero active IMAP configurations. Nothing failed, but nothing was proved
                    // either, so this must not read as a healthy poll.
                    _logger.LogWarning(
                        "Email fetch ran with NO active IMAP mailbox configured; no inbound mail can be ingested.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in email background service");
                _pollerHealth?.RecordCycleFailure(
                    $"The email fetch threw {ex.GetType().Name}: {ex.Message}",
                    isPermanent: false, DateTimeOffset.UtcNow);
            }

            // Folder discovery is filesystem-only; the sweep itself writes tenant data and
            // therefore runs inside a pushed tenant scope so it executes as
            // nexora_tenant_app under RLS instead of the BYPASSRLS pipeline role.
            //
            // Fully fenced off from the mailbox verdict: the watched folders are a DIFFERENT
            // channel, and letting a folder failure mark the mailbox as failed (or an escaping
            // exception here erase a genuine mailbox success) would put the wrong channel's
            // name on the red light.
            try
            {
                var tenantScope = scopedServices.GetRequiredService<ITenantScopeAccessor>();
                var discovery = scopedServices.GetRequiredService<FolderService>();

                // The tenant list here comes from DIRECTORY NAMES under Uploads/Tenants and has
                // never touched the platform database, so this channel had no way of knowing a
                // tenant was suspended — a directory outlives every lifecycle decision made about
                // its owner. Everything the mailbox poller ingests, this ingests too: documents
                // are enqueued for extraction and reach the inference endpoint the same way.
                //
                // Skipping DEFERS. ProcessAllFoldersAsync is driven by what is sitting in the
                // watched directory; nothing is moved or deleted for a tenant that is skipped, so
                // the same files are picked up on the first sweep after reinstatement. Resolved
                // before the per-tenant push below — see ITenantWorkGate.
                var businessUnitIds = (IReadOnlyList<long>)discovery.DiscoverTenantFolderIds().ToList();
                var workGate = scopedServices
                    .GetService<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>();
                if (workGate is not null && businessUnitIds.Count > 0)
                    businessUnitIds = await workGate.FilterServiceableAsync(businessUnitIds, stoppingToken);

                foreach (var businessUnitId in businessUnitIds)
                {
                    try
                    {
                        using var tenant = tenantScope.Push(businessUnitId);
                        using var tenantServices = _services.CreateScope();
                        var folderService = tenantServices.ServiceProvider.GetRequiredService<FolderService>();
                        var report = await folderService.ProcessAllFoldersAsync(businessUnitId, stoppingToken);
                        if (report.Enqueued + report.Duplicates + report.Rejected + report.Failed > 0)
                        {
                            _logger.LogInformation(
                                "Folder sweep {BatchId} for BU {BusinessUnitId}: {Enqueued} enqueued, {Duplicates} duplicates, {Rejected} rejected, {Failed} retrying.",
                                report.BatchId, businessUnitId, report.Enqueued, report.Duplicates, report.Rejected, report.Failed);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Folder sweep failed for BU {BusinessUnitId}; continuing with other tenants.", businessUnitId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "The watched-folder sweep could not run this cycle; the mailbox verdict is unaffected.");
            }

            return;
        }

        // DATA-01: this DB read must NOT be able to fault ExecuteAsync — a single
        // transient DB error here would otherwise (with the default StopHost
        // behavior) take down the whole API host. Guard it and fall back to a
        // safe default polling interval so the loop always survives.
        // internal rather than private so the UNIT can be pinned by a test. Nothing else polls
        // a real mailbox, which is exactly why the seconds/minutes contradiction survived.
        internal async Task<TimeSpan> ResolvePollIntervalAsync(
            ErpRfqAutomationContext dbContext, CancellationToken stoppingToken)
        {
            try
            {
                // IMAP only. The DTOs say "Minutes between polls. Ignored for SMTP" and the
                // mailbox screen hides the input for SMTP — but this read took the minimum
                // across EVERY active mailbox, so an outbound-only SMTP row carrying the
                // column default was setting the inbound poll rate for the whole tenant.
                // A field documented as ignored must actually be ignored.
                var intervals = await dbContext.EmailConfigurations
                    .Where(e => e.IsActive && e.Protocol.ToUpper() == "IMAP")
                    .Select(e => e.PollingInterval)
                    .ToListAsync(stoppingToken);
                // MINUTES — see MinimumPollIntervalMinutes. Clamped to the same [1, 1440]
                // range the create/update DTOs enforce, so a value that could only have
                // arrived by direct SQL cannot poll faster than any operator is allowed to
                // ask for. The clamp is a floor on the RATE, never a way to make a
                // misconfigured mailbox look healthy: the interval actually in force is
                // reported verbatim on the mailbox health line.
                if (intervals.Any())
                    return TimeSpan.FromMinutes(Math.Clamp(
                        intervals.Min(), MinimumPollIntervalMinutes, MaximumPollIntervalMinutes));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // fall through to the default
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to read polling interval; using default {DefaultInterval} minute(s)",
                    DefaultPollInterval.TotalMinutes);
            }

            return DefaultPollInterval;
        }
    }
}

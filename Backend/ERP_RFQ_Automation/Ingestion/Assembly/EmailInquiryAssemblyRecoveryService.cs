using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Validated knobs for the recovery sweep. Both are clamped rather than trusted: a
/// misconfigured interval of zero turns the sweep into a hot loop against the queue's own
/// database, and an unbounded batch lets one tenant's backlog hold the sweep for every other
/// tenant on the platform.
/// </summary>
public sealed class EmailInquiryAssemblyRecoveryOptions
{
    public const string SectionName = "Ingestion:AssemblyRecovery";

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);

    /// <summary>How often the sweep runs after its startup pass.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum assemblies recovered per tenant per sweep.</summary>
    public int BatchSizePerTenant { get; set; } = 50;

    /// <summary>
    /// How long a message must have sat in <see cref="EmailInquiryAssemblyStatus.ReadyForAssembly"/>
    /// before the sweep touches it.
    ///
    /// <para>Not a throttle — a correctness guard. A worker that is mid-assemble right now holds
    /// the assembly row's <c>FOR UPDATE</c> lock, so a sweep that raced it would simply block
    /// until it committed and then no-op. The grace keeps the sweep from queueing behind healthy
    /// work at all, so its lock waits stay a signal of something genuinely stuck.</para>
    /// </summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a COMPONENT must have sat at Pending/Inspecting/Extracting before the sweep
    /// resolves it from the actual state of its extraction job.
    ///
    /// <para>Long, deliberately. A component is genuinely in flight for as long as its job has
    /// attempts left, and the queue's backoff is exponential to a one-hour cap — so a threshold
    /// tight enough to feel responsive would spend every cycle re-examining healthy work. The
    /// sweep never closes a runnable job's component whatever this is set to; the threshold only
    /// decides how long a stranded one waits before anyone looks.</para>
    ///
    /// <para>Bound to <c>Ingestion:Assembly:StrandedComponentSweepMinutes</c> in <c>Program</c>,
    /// which is the documented operator-facing key, as well as to this options section.</para>
    /// </summary>
    public int StrandedComponentSweepMinutes { get; set; } = 30;

    /// <summary>
    /// How long a part HELD WITHOUT A PROCESSING JOB may keep being re-driven from the stored
    /// original before the message is decided instead.
    ///
    /// <para>The bound is the point. A component held with no job has no queue row to exhaust its
    /// attempts, so without a deadline the sweep would re-attempt a permanently unschedulable
    /// part every two minutes forever and the message would never reach anybody. Measured from
    /// the component's CAPTURE time, not from the last attempt, so a part that has been held for
    /// days is decided on the first sweep that sees it rather than being given a fresh window it
    /// has already proved it cannot use.</para>
    /// </summary>
    public int SchedulingResumeWindowMinutes { get; set; } = 240;

    /// <summary>
    /// How long an ingest may read as in-flight ("Pending"/"Queued") before the sweep reconciles
    /// its ledger status with what actually became of the message.
    ///
    /// <para>Generous, because the ledger is a display of progress and a premature correction
    /// would report a healthy message as failed. What decides the correction is never the clock:
    /// it is whether a live extraction job or a non-terminal assembly still exists.</para>
    /// </summary>
    public int LedgerReconciliationMinutes { get; set; } = 60;

    /// <summary>
    /// How long a message that is still <see cref="EmailInquiryAssemblyStatus.Captured"/> is
    /// treated as OWNED BY THE SCHEDULING PASS that captured it, and therefore off limits to this
    /// sweep.
    ///
    /// <para><b>Why this needs its own knob with a floor.</b> Capture and scheduling are one
    /// operation from the caller's point of view but two steps in the database: the assembly and
    /// its component rows are committed first, and the extraction jobs are bound afterwards, one
    /// component at a time. In between, every part of a perfectly healthy message looks exactly
    /// like a part that will never be scheduled — Pending, with no job. The only thing that tells
    /// the two apart is time.</para>
    ///
    /// <para>The stranded-component threshold cannot serve here, because it is legitimately set
    /// to zero in tests: what happens to a component is decided by the durable state of its job,
    /// so age was never load bearing. That stopped being true for a component with NO job, and it
    /// stopped being safe when the barrier learned to move a Captured message to NeedsReview —
    /// before that, the sweep's verdict on a mid-capture message was an illegal transition and was
    /// discarded, which hid the race. It is a real one: the sweep would close the parts of a
    /// message the live scheduler was still binding, and the two would fight over it.</para>
    /// </summary>
    public int CaptureGraceSeconds { get; set; } = 120;

    public bool Enabled { get; set; } = true;

    public TimeSpan ValidatedInterval =>
        Interval < MinimumInterval ? MinimumInterval
        : Interval > MaximumInterval ? MaximumInterval
        : Interval;

    public int ValidatedBatchSize => Math.Clamp(BatchSizePerTenant, 1, 500);

    public TimeSpan ValidatedMinimumAge =>
        MinimumAge < TimeSpan.Zero ? TimeSpan.Zero
        : MinimumAge > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1)
        : MinimumAge;

    /// <summary>
    /// Clamped, not trusted: a negative value would put the cutoff in the future and quietly
    /// disable the sweep, and an absurd one would disable it for a week.
    ///
    /// <para>Zero IS permitted, and it is safe rather than merely tolerated. The threshold decides
    /// only which components are LOOKED at; what happens to them is decided by their job, and a
    /// component queued a microsecond ago has a Pending job with every attempt left, which this
    /// sweep leaves strictly alone. Tests therefore use zero to avoid proving the clock.</para>
    /// </summary>
    public TimeSpan ValidatedStrandedComponentAge =>
        TimeSpan.FromMinutes(Math.Clamp(StrandedComponentSweepMinutes, 0, 7 * 24 * 60));

    /// <summary>
    /// Clamped like the rest. Zero is permitted and means "never re-drive, decide immediately",
    /// which is a legitimate operator choice during an incident and is what the tests that prove
    /// the terminal path use.
    /// </summary>
    public TimeSpan ValidatedSchedulingResumeWindow =>
        TimeSpan.FromMinutes(Math.Clamp(SchedulingResumeWindowMinutes, 0, 7 * 24 * 60));

    public TimeSpan ValidatedLedgerReconciliationAge =>
        TimeSpan.FromMinutes(Math.Clamp(LedgerReconciliationMinutes, 0, 7 * 24 * 60));

    /// <summary>
    /// FLOORED AT THIRTY SECONDS, and unlike every other threshold here it may NOT be set to
    /// zero. Zero would mean "sweep a message the scheduler committed a millisecond ago", and
    /// there is no operator intent that value could express: a genuinely stranded message is
    /// minutes to days old, so nothing is lost by waiting, and a test that wants to prove the
    /// sweep works ages its rows rather than disabling the guard.
    /// </summary>
    public TimeSpan ValidatedCaptureGrace =>
        TimeSpan.FromSeconds(Math.Clamp(CaptureGraceSeconds, 30, 7 * 24 * 60 * 60));
}

/// <summary>
/// What the stranded-COMPONENT half of a sweep did. Counts only; never message content.
/// </summary>
/// <param name="Examined">Components found non-terminal past the threshold.</param>
/// <param name="Reconciled">Closed as Completed because their job had actually succeeded.</param>
/// <param name="Skipped">Closed as Skipped — nothing will ever produce them, so their message
/// finalizes into review rather than waiting forever.</param>
/// <param name="Held">Closed as FailedRecoverable — an infrastructure fault, so the message is
/// held rather than quoted without a document that still exists.</param>
/// <param name="LeftInFlight">Genuinely still running. Untouched, and counted so that "the sweep
/// found nothing" and "the sweep found live work" stay distinguishable.</param>
/// <param name="Rescheduled">
/// Held with no processing job at all, and re-driven from the stored original so the part now
/// holds one. The ONLY disposition in this record that puts work back into the pipeline rather
/// than closing it.
/// </param>
/// <param name="Failed">Rows that threw. One never stops the sweep.</param>
public readonly record struct EmailInquiryStrandedComponentOutcome(
    int Examined, int Reconciled, int Skipped, int Held, int LeftInFlight, int Failed,
    int Rescheduled = 0)
{
    public int Resolved => Reconciled + Skipped + Held;

    public static EmailInquiryStrandedComponentOutcome operator +(
        EmailInquiryStrandedComponentOutcome left, EmailInquiryStrandedComponentOutcome right)
        => new(left.Examined + right.Examined, left.Reconciled + right.Reconciled,
            left.Skipped + right.Skipped, left.Held + right.Held,
            left.LeftInFlight + right.LeftInFlight, left.Failed + right.Failed,
            left.Rescheduled + right.Rescheduled);
}

/// <summary>
/// What the LEDGER half of a sweep did — the EmailIngest rows whose ParseStatus still claims the
/// message is in flight when nothing is moving it.
/// </summary>
/// <param name="Examined">Ingests reading Pending/Queued past the reconciliation age.</param>
/// <param name="Corrected">Ingests whose status was moved to what actually became of them.</param>
/// <param name="StillMoving">Left alone: a live job or a non-terminal assembly still owns them.</param>
/// <param name="Failed">Rows that threw. One never stops the sweep.</param>
public readonly record struct EmailInquiryLedgerReconciliationOutcome(
    int Examined, int Corrected, int StillMoving, int Failed)
{
    public static EmailInquiryLedgerReconciliationOutcome operator +(
        EmailInquiryLedgerReconciliationOutcome left, EmailInquiryLedgerReconciliationOutcome right)
        => new(left.Examined + right.Examined, left.Corrected + right.Corrected,
            left.StillMoving + right.StillMoving, left.Failed + right.Failed);
}

/// <summary>What one sweep did. Counts only; never message content.</summary>
public sealed record EmailInquiryRecoverySweepResult(
    int TenantsSwept,
    int Candidates,
    int Recovered,
    int AlreadyCompleted,
    int HeldForReview,
    int StillRecoverable,
    int Unexpected,
    int Failed,
    TimeSpan Duration,
    EmailInquiryStrandedComponentOutcome StrandedComponents = default,
    EmailInquiryLedgerReconciliationOutcome Ledger = default)
{
    public static readonly EmailInquiryRecoverySweepResult Skipped =
        new(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);
}

public interface IEmailInquiryAssemblyRecoveryService
{
    Task<EmailInquiryRecoverySweepResult> SweepOnceAsync(CancellationToken ct = default);
}

/// <summary>
/// THE rule that decides whether an <c>EmailIngest.ParseStatus</c> is still telling the truth,
/// expressed as a pure function so it can be asserted without a database.
///
/// <para><b>The defect it closes.</b> "Queued" is written when a message's parts are handed to
/// the extraction queue, and it is only ever cleared by the persist path or by a worker's
/// dead-letter annotation. Neither runs when the queue's own claim statement dead-letters a job:
/// the exhausted-lease and lineage-quarantine CTEs move a row to <c>DeadLetter</c> with no worker
/// in the loop, so nothing tells the ledger. The result is a terminal state that presents itself
/// as in-flight — an inbox showing work in progress over jobs that stopped days ago, which is
/// precisely why nobody noticed. The mirror case is just as bad: a message whose assembly is
/// already decided (NeedsReview, NoInquiry) while its ledger row still says Queued.</para>
///
/// <para><b>What it must never do</b> is contradict live work. A correction is only ever made
/// when the assembly has reached a decision, or when the message has no live extraction job left
/// at all. Age decides which rows are LOOKED at; durable state decides what happens to them.</para>
/// </summary>
public static class EmailInquiryLedgerReconciliation
{
    /// <summary>ParseStatus values that CLAIM the message is still being worked on.</summary>
    public const string InFlightQueued = "Queued";

    public const string InFlightPending = "Pending";

    /// <summary>A person has to look at this message.</summary>
    public const string NeedsReview = "NeedsReview";

    /// <summary>The message was decided to be something other than an inquiry.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Nothing on this message was ever handed to processing.</summary>
    public const string NothingToExtract = "Failed - nothing to extract";

    public static bool ClaimsInFlight(string? parseStatus) =>
        parseStatus is InFlightQueued or InFlightPending;

    /// <summary>
    /// The status this ingest should read, or null to leave it exactly as it is.
    /// </summary>
    /// <param name="assemblyStatus">Null when the message predates the assembly barrier.</param>
    /// <param name="hasRunnableJob">A queue row a worker will still claim.</param>
    /// <param name="hasStoppedJob">A queue row that exists and has stopped trying.</param>
    /// <param name="hasSweepableComponent">
    /// Whether any part of this message is still in a state the component sweep claims — one of
    /// <see cref="EmailInquiryAssemblyRecoveryService.SweptRegardlessOfJob"/>, or
    /// <see cref="EmailInquiryAssemblyRecoveryService.SweptOnlyWithoutJob"/> holding no job. It is
    /// the difference between a hold that is being worked and a hold that has come to rest, and it
    /// is consulted only for a held assembly.
    ///
    /// <para>Defaults to the conservative answer — assume a sweep is still coming — because a
    /// caller that cannot see the component rows must not be the one to declare a message
    /// finished. The recovery sweep reads the parts and passes what they actually say;
    /// <c>EmailService</c>'s stranded-ingest pass has no assembly parts in hand and keeps the
    /// default. Leaving a row alone for another cycle costs a cycle. Reporting live work as
    /// stopped is the same lie this class exists to remove, pointing the other way.</para>
    /// </param>
    public static string? StatusFor(
        string? parseStatus,
        EmailInquiryAssemblyStatus? assemblyStatus,
        bool hasRunnableJob,
        bool hasStoppedJob,
        bool hasSweepableComponent = true)
    {
        // Only a claim of progress can be wrong in the way this fixes. Every other value is a
        // decision something else made with more information.
        if (!ClaimsInFlight(parseStatus)) return null;

        // A live job outranks everything, including a decided assembly: the message really is
        // still moving, and re-labelling it would be the same lie in the other direction.
        if (hasRunnableJob) return null;

        return assemblyStatus switch
        {
            // The barrier reached a decision and the ledger never heard.
            EmailInquiryAssemblyStatus.Assembled => NeedsReview,
            EmailInquiryAssemblyStatus.NeedsReview => NeedsReview,
            EmailInquiryAssemblyStatus.NoInquiry => Rejected,
            EmailInquiryAssemblyStatus.RejectedSecurity => Rejected,

            // HELD, WITH NOTHING LEFT TO HOLD IT FOR. A hold belongs to the component sweep, and
            // the sweep can only own a part it queries: one of SweptRegardlessOfJob, or a held
            // part carrying no job. When no part of the message is either of those the hold has
            // come to rest — which is the ordinary shape the moment the sweep itself closes a
            // JOB-BOUND part as an infrastructure fault, because a held part that keeps its job
            // id is never looked at again by any sweep.
            //
            // Saying nothing here was the ten-day failure repeated one layer up: the sweep
            // counted the message as recovered while its ledger row went on reading "Queued", so
            // the Inbound Mail "Stopped" tab — which matches on the "Failed" prefix and exists to
            // answer exactly this question — counted it as zero. The operator IS the mover for a
            // rested hold, through the audited triage reopen, and they cannot be the mover for a
            // message the screen is still reporting as in flight.
            EmailInquiryAssemblyStatus.FailedRecoverable when !hasSweepableComponent =>
                ERP_RFQ_Automation.Extraction.ExtractionWorker.DeadLetterParseStatus,

            // Still genuinely in the pipeline. The component sweep and the assembly sweep own
            // these, and a ledger correction here would report a message as finished while the
            // machinery that finishes it is still running.
            not null => null,

            // No assembly at all — a message ingested before the barrier existed. The jobs are
            // the only evidence there is.
            null when hasStoppedJob => ERP_RFQ_Automation.Extraction.ExtractionWorker.DeadLetterParseStatus,
            null => NothingToExtract
        };
    }
}

/// <summary>
/// Finishes messages the pipeline stopped moving: first the PARTS that will never report, then
/// the MESSAGES whose parts all completed but whose Lead was never built.
///
/// <para><b>The second half was the older gap.</b> A component's extraction job could die —
/// process loss, a dead letter recorded before anything closed the barrier, a job row purged —
/// and the component stayed at Pending or Extracting for good. The state machine will not
/// finalize a message until EVERY component is terminal, so the message reported "1 of 4 parts
/// assembled" in perpetuity: no lead, no review item, no error, nothing anyone could see. That is
/// the exact opposite of the rule this module exists to enforce — an ingested email ends at a
/// lead or at an explicit rejection, never in silence.</para>
///
/// <para><b>The component sweep consults the JOB, never the clock.</b> Age only decides which
/// components are looked at; what happens to one is decided by the durable state of the work
/// that owes it. A succeeded job means the result exists and the component is reconciled to
/// Completed. A dead-lettered or exhausted job is closed exactly as the live path closes it, via
/// the shared <see cref="EmailInquiryComponentClosure"/> — infrastructure faults HOLD, content
/// faults finalize into review. A job with attempts left is genuinely in flight and is left
/// strictly alone. No job at all means nothing will ever produce this part, so it is Skipped.
/// Nothing is re-extracted and no evidence is re-read.</para>
///
/// <para><b>The gap this closes.</b> The worker commits the queue job and assembles the message
/// afterwards. That order is deliberate — assembling first and completing second means a crash
/// in between is retried and builds the Lead twice — but it leaves a real window: if the process
/// dies after the commit, every component job reads <c>Succeeded</c>, every component result is
/// durable, and the message sits at <c>ReadyForAssembly</c> with no Lead, no error and nothing
/// that would ever look at it again. The customer's RFQ silently does not exist. The window is
/// entered on every deploy and every pod eviction, not only on crashes.</para>
///
/// <para><b>The status IS the work item.</b> There is no outbox, no retry table and no requeue.
/// <c>ReadyForAssembly</c> with a null <c>AssembledLeadId</c> is already a durable, exactly-
/// meaningful record of "this message finished its parts and owes a Lead", and inventing a second
/// record of the same fact is how two sources of truth start disagreeing. Nothing is re-extracted
/// and no evidence is re-read: the component results are already durable, which is the entire
/// point of having built them.</para>
///
/// <para><b>Correctness is not this class's job.</b> It calls
/// <see cref="IEmailInquiryLeadAssembler.AssembleAsync"/> and nothing else. The merge, the Lead
/// write, the <c>FOR UPDATE</c> lock and the state transition all live there, so a sweep and a
/// live worker racing the same message are the same race two workers already are — and it is
/// settled by the row lock, not by the advisory lease. The lease only stops N instances doing N
/// identical global scans.</para>
/// </summary>
public sealed class EmailInquiryAssemblyRecoveryService : IEmailInquiryAssemblyRecoveryService
{
    /// <summary>
    /// The component states this sweep claims WHATEVER their job says — the states in which a
    /// part is waiting on work someone else was supposed to be doing.
    ///
    /// <para>Declared once and used by the queries themselves (EF translates
    /// <c>Contains</c> to <c>IN</c>), so the list a test asserts against is the list production
    /// runs. The alternative — a predicate in the query and a copy of it in a test — asserts that
    /// the copy is correct, which is how a component state gets added with no sweep behind it and
    /// nobody notices for a month.</para>
    ///
    /// <para><see cref="EmailInquiryComponentStatus.FailedRecoverable"/> is deliberately NOT
    /// here: it is claimed conditionally, only when the component holds no job, because a
    /// job-bound hold belongs to the audited dead-letter recovery command instead. Both halves
    /// together must cover every non-terminal state — that is the invariant
    /// <c>EmailInquiryStrandingInvariantTests</c> enforces over the whole enum.</para>
    /// </summary>
    public static readonly EmailInquiryComponentStatus[] SweptRegardlessOfJob =
    [
        EmailInquiryComponentStatus.Pending,
        EmailInquiryComponentStatus.Inspecting,
        EmailInquiryComponentStatus.Extracting
    ];

    /// <summary>
    /// The one non-terminal state claimed only when the component has NO durable job — see
    /// <see cref="SweptRegardlessOfJob"/> for why it is separate.
    /// </summary>
    public const EmailInquiryComponentStatus SweptOnlyWithoutJob =
        EmailInquiryComponentStatus.FailedRecoverable;

    /// <summary>
    /// The one ASSEMBLY state this sweep claims in its own right — phase 2's whole query, minus
    /// the null-lead predicate. Declared here and used by that query for the same reason the
    /// component lists are: the state a test asserts about has to be the state production reads.
    /// </summary>
    public const EmailInquiryAssemblyStatus SweptWhenOwedALead =
        EmailInquiryAssemblyStatus.ReadyForAssembly;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly EmailInquiryAssemblyRecoveryOptions _options;
    private readonly ILogger<EmailInquiryAssemblyRecoveryService> _log;
    private readonly TimeProvider _time;

    public EmailInquiryAssemblyRecoveryService(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        EmailInquiryAssemblyRecoveryOptions options,
        ILogger<EmailInquiryAssemblyRecoveryService> log,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _options = options;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public async Task<EmailInquiryRecoverySweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var tenants = await ResolveTenantsWithStrandedWorkAsync(ct);

        var candidates = 0;
        var recovered = 0;
        var alreadyCompleted = 0;
        var heldForReview = 0;
        var stillRecoverable = 0;
        var unexpected = 0;
        var failed = 0;
        var components = default(EmailInquiryStrandedComponentOutcome);
        var ledger = default(EmailInquiryLedgerReconciliationOutcome);

        foreach (var businessUnitId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // PARTS FIRST, then messages. Resolving a stranded component is what MAKES a
                // message ready (or reviewable), so doing it second would leave every message it
                // unblocked waiting an extra cycle for no reason. The assemblies it touched are
                // handed to the second phase explicitly, so a message unblocked microseconds ago
                // is not then excluded by the minimum-age grace it has not had time to clear.
                var (componentOutcome, touched) =
                    await SweepStrandedComponentsAsync(businessUnitId, ct);
                components += componentOutcome;

                var tenantResult = await SweepReadyAssembliesAsync(businessUnitId, touched, ct);
                candidates += tenantResult.Candidates;
                recovered += tenantResult.Recovered;
                alreadyCompleted += tenantResult.AlreadyCompleted;
                heldForReview += tenantResult.HeldForReview;
                stillRecoverable += tenantResult.StillRecoverable;
                unexpected += tenantResult.Unexpected;
                failed += tenantResult.Failed;

                // LAST, and it has to be. Both phases above can move a message to a decision in
                // this same cycle, and reconciling the ledger before them would read the status
                // they are about to change and then leave the row one cycle behind the truth.
                ledger += await ReconcileLedgerAsync(businessUnitId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // One tenant's problem is not every tenant's problem. A fail-closed tenant-scope
                // refusal, a lock timeout, a transient connection fault — none of them may stop
                // the platform's other stranded messages from being finished.
                failed++;
                _log.LogError(exception,
                    "Email inquiry assembly recovery failed for business unit {BusinessUnitId}; "
                    + "continuing with the next tenant.", businessUnitId);
            }
        }

        var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var result = new EmailInquiryRecoverySweepResult(
            tenants.Count, candidates, recovered, alreadyCompleted, heldForReview,
            stillRecoverable, unexpected, failed, duration, components, ledger);

        // Logged at Information only when it did something, so an idle platform does not emit a
        // line every two minutes that operators learn to scroll past.
        if (candidates > 0 || failed > 0 || components.Examined > 0 || ledger.Corrected > 0)
        {
            _log.LogInformation(
                "Email inquiry assembly recovery swept {Tenants} tenant(s) in {DurationMs}ms: "
                + "{Candidates} candidate(s), {Recovered} recovered, {AlreadyCompleted} already "
                + "complete, {HeldForReview} held for review, {StillRecoverable} still "
                + "recoverable, {Unexpected} in an unexpected state, {Failed} failed; "
                + "{ComponentsExamined} stranded part(s) examined, {ComponentsReconciled} "
                + "reconciled, {ComponentsRescheduled} rescheduled, {ComponentsSkipped} skipped, "
                + "{ComponentsHeld} held, {ComponentsInFlight} still in flight, "
                + "{ComponentsFailed} failed; {LedgerExamined} ledger row(s) examined, "
                + "{LedgerCorrected} corrected, {LedgerStillMoving} still moving, "
                + "{LedgerFailed} failed.",
                result.TenantsSwept, (long)duration.TotalMilliseconds, result.Candidates,
                result.Recovered, result.AlreadyCompleted, result.HeldForReview,
                result.StillRecoverable, result.Unexpected, result.Failed,
                components.Examined, components.Reconciled, components.Rescheduled,
                components.Skipped, components.Held, components.LeftInFlight, components.Failed,
                ledger.Examined, ledger.Corrected, ledger.StillMoving, ledger.Failed);
        }

        return result;
    }

    /// <summary>
    /// The ONE query that runs without a tenant scope, and therefore under the BYPASSRLS
    /// pipeline role. It reads tenant ids and nothing else — no subject, no sender, no
    /// evidence path — so the widest-privileged step in the sweep also carries the least.
    ///
    /// <para>The gate is consulted HERE, in the scope with no tenant pushed, because that is its
    /// hard requirement: called inside a pushed scope its platform read is refused at column
    /// level and fails open, admitting suspended and archived tenants silently.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveTenantsWithStrandedWorkAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var cutoff = _time.GetUtcNow() - _options.ValidatedMinimumAge;
        var componentCutoff = _time.GetUtcNow() - _options.ValidatedStrandedComponentAge;
        var captureCutoff = _time.GetUtcNow() - _options.ValidatedCaptureGrace;

        // No IgnoreQueryFilters. With no tenant pushed the filter
        // (`CurrentTenantId == null || ...`) is already a no-op, so the call bought nothing and
        // removed a layer in the one method described as the widest-privileged step. The
        // precondition it implied is now ENFORCED instead of commented.
        if (context.ScopedTenantId is not null)
            throw new InvalidOperationException(
                "The recovery sweep's tenant enumeration must run with NO tenant scope. It "
                + $"resolved tenant {context.ScopedTenantId}, which means it would run under a "
                + "tenant's own role and silently enumerate only that tenant.");

        var owedALead = await context.EmailInquiryAssemblies
            .AsNoTracking()
            .Where(a => a.Status == EmailInquiryAssemblyStatus.ReadyForAssembly
                        && a.AssembledLeadId == null
                        && a.UpdatedAtUtc <= cutoff)
            .Select(a => a.BusinessUnitId)
            .Distinct()
            .ToListAsync(ct);

        // Asked SEPARATELY, and it has to be. A message with a stranded part is not
        // ReadyForAssembly and never will be on its own — it is stuck at Captured, Inspecting or
        // Extracting — so the query above cannot see it and its tenant would never be enumerated.
        // The two populations are disjoint by construction, which is precisely why one query
        // cannot find both.
        var withStrandedParts = await context.EmailInquiryComponents
            .AsNoTracking()
            .Where(c => (SweptRegardlessOfJob.Contains(c.Status)
                         // HELD WITH NO JOB — the population that had no path out of the system
                         // at all. Not in flight (nothing is running), not dead-lettered (there
                         // is no queue row to dead-letter), and invisible to the query above
                         // because its message can never reach ReadyForAssembly on its own.
                         || (c.Status == SweptOnlyWithoutJob && c.ExtractionJobId == null))
                        && c.UpdatedAtUtc <= componentCutoff
                        && !(c.ExtractionJobId == null
                             && c.Assembly.Status == EmailInquiryAssemblyStatus.Captured
                             && c.Assembly.UpdatedAtUtc > captureCutoff))
            .Select(c => c.BusinessUnitId)
            .Distinct()
            .ToListAsync(ct);

        // A ledger row can claim a message is in flight when neither of the populations above
        // holds it — a pre-assembly ingest whose jobs all died, or a message whose assembly is
        // already decided. Asked separately for the same reason the two above are.
        var ledgerCutoff = _time.GetUtcNow() - _options.ValidatedLedgerReconciliationAge;
        var withStaleLedger = await context.EmailIngests
            .AsNoTracking()
            .Where(e => (e.ParseStatus == EmailInquiryLedgerReconciliation.InFlightQueued
                         || e.ParseStatus == EmailInquiryLedgerReconciliation.InFlightPending)
                        && e.CreatedOn <= ledgerCutoff.UtcDateTime)
            .Select(e => e.EmailConfiguration.BusinessUnitId)
            .Distinct()
            .ToListAsync(ct);

        var tenants = owedALead
            .Union(withStrandedParts)
            .Union(withStaleLedger)
            .OrderBy(id => id).ToList();
        if (tenants.Count == 0) return tenants;

        // Resolved from THIS scope, not injected, because the gate must be consulted with no
        // tenant pushed — inside a pushed scope its platform read is refused at column level and
        // fails open.
        // Required, not optional. A host composition that dropped the registration would get a
        // silently ungated platform-wide sweep touching suspended and archived tenants — the
        // failure mode this class's fail-closed posture exists to avoid everywhere else.
        var gate = scope.ServiceProvider.GetRequiredService<ITenantWorkGate>();
        return await gate.FilterServiceableAsync(tenants, ct);
    }

    /// <summary>
    /// PHASE 1 — resolve one tenant's stranded components from the actual state of their jobs,
    /// and return the assemblies that were touched so phase 2 can finish them this cycle.
    ///
    /// <para>Every disposition below is a statement about durable state, never about elapsed
    /// time. Age decides only which rows are looked at.</para>
    /// </summary>
    private async Task<(EmailInquiryStrandedComponentOutcome Outcome, IReadOnlyCollection<long> TouchedAssemblies)>
        SweepStrandedComponentsAsync(long businessUnitId, CancellationToken ct)
    {
        // The push precedes the scope: ITenantContext captures the ambient tenant in its
        // CONSTRUCTOR, so a scope created first resolves a DbContext that believes in no tenant
        // whatever is pushed afterwards.
        using var tenant = _tenantScope.Push(businessUnitId);
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        AssertTenantScope(context, businessUnitId);

        var coordinator = scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>();
        // REQUIRED, not optional. A composition that dropped the intake registration would
        // silently stop re-driving every part held without a job — the exact invisible regression
        // this sweep exists to end — and the per-tenant handler above turns the throw into a
        // counted, logged failure rather than a stopped platform.
        var intake = scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>();
        var cutoff = _time.GetUtcNow() - _options.ValidatedStrandedComponentAge;
        var resumeDeadline = _time.GetUtcNow() - _options.ValidatedSchedulingResumeWindow;
        var captureCutoff = _time.GetUtcNow() - _options.ValidatedCaptureGrace;

        // Oldest first, bounded per cycle. One tenant's backlog of stranded parts cannot hold the
        // sweep for every other tenant on the platform.
        var candidates = await context.EmailInquiryComponents
            .AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId
                        && (SweptRegardlessOfJob.Contains(c.Status)
                            || (c.Status == SweptOnlyWithoutJob && c.ExtractionJobId == null))
                        && c.UpdatedAtUtc <= cutoff
                        // THE MESSAGE IS STILL BEING SCHEDULED. Capture commits the assembly and
                        // its parts, then binds a job to each part in turn; in between, a healthy
                        // part is indistinguishable from one that will never be scheduled. Only
                        // time separates them, so a Captured message is left to the pass that owns
                        // it. Nothing is lost by waiting: real stranded work is minutes old at the
                        // very least, and this window is seconds.
                        && !(c.ExtractionJobId == null
                             && c.Assembly.Status == EmailInquiryAssemblyStatus.Captured
                             && c.Assembly.UpdatedAtUtc > captureCutoff))
            .OrderBy(c => c.UpdatedAtUtc).ThenBy(c => c.Id)
            .Take(_options.ValidatedBatchSize)
            .Select(c => new
            {
                c.Id, c.AssemblyId, c.ComponentKey, c.Status, c.FileName, c.ExtractionJobId,
                c.ReasonCode, c.CreatedAtUtc
            })
            .ToListAsync(ct);

        var outcome = new EmailInquiryStrandedComponentOutcome(
            Examined: candidates.Count, 0, 0, 0, 0, 0);
        var touched = new HashSet<long>();
        // Re-driving is per MESSAGE, not per part: ScheduleAsync walks every component of the
        // assembly. Attempting it once per held component would schedule the first one and then
        // burn a second and third pass discovering there is nothing left to do.
        var resumeAttempted = new HashSet<long>();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // HELD WITH NO PROCESSING JOB. Handled before anything below, because the whole
                // job-consulting apparatus has nothing to consult: there is no queue row.
                if (candidate.Status == EmailInquiryComponentStatus.FailedRecoverable)
                {
                    // RE-READ FIRST, past the change tracker.
                    //
                    // Re-driving is per MESSAGE: one call schedules every held part of the
                    // assembly. So by the time this loop reaches the SECOND held part of a message
                    // it may already be Extracting with a durable job — and closing it as
                    // unrecoverable here would throw away work that is running, which is the one
                    // mistake this sweep must never make. The candidate list was read before any
                    // of that happened, so it cannot be trusted for this branch.
                    var current = await context.EmailInquiryComponents
                        .AsNoTracking()
                        .Where(c => c.BusinessUnitId == businessUnitId && c.Id == candidate.Id)
                        .Select(c => new { c.Status, c.ExtractionJobId })
                        .FirstOrDefaultAsync(ct);

                    if (current is null
                        || current.Status != EmailInquiryComponentStatus.FailedRecoverable
                        || current.ExtractionJobId is not null)
                    {
                        // A sibling's re-drive already moved it. Counted as rescheduled rather
                        // than silently ignored, so "the sweep did nothing" and "the sweep put
                        // this message back into the pipeline" stay distinguishable.
                        outcome = outcome with { Rescheduled = outcome.Rescheduled + 1 };
                        touched.Add(candidate.AssemblyId);
                        continue;
                    }

                    var (disposition, code, detail, rescheduled) = await ResolveUnscheduledHoldAsync(
                        intake, businessUnitId, candidate.AssemblyId, candidate.ComponentKey,
                        candidate.ReasonCode, candidate.CreatedAtUtc, resumeDeadline,
                        resumeAttempted, ct);

                    if (rescheduled)
                    {
                        // Back in the pipeline. Its component row was moved to Extracting by the
                        // coordinator inside the scheduling transaction, so there is nothing to
                        // close and the message is genuinely in flight again.
                        outcome = outcome with { Rescheduled = outcome.Rescheduled + 1 };
                        touched.Add(candidate.AssemblyId);
                        _log.LogInformation(
                            "Stranded assembly component {ComponentId} ('{FileName}') of assembly "
                            + "{AssemblyId} for business unit {BusinessUnitId} was held with no "
                            + "processing job; scheduling was re-driven from the stored original "
                            + "and it is running again.",
                            candidate.Id, candidate.FileName, candidate.AssemblyId, businessUnitId);
                        continue;
                    }

                    await coordinator.RecordComponentOutcomeAsync(
                        businessUnitId, candidate.AssemblyId, candidate.ComponentKey, disposition,
                        code, detail, sourceDocumentOccurrenceId: null, ct);
                    touched.Add(candidate.AssemblyId);
                    outcome = disposition == EmailInquiryComponentStatus.FailedRecoverable
                        ? outcome with { Held = outcome.Held + 1 }
                        : outcome with { Skipped = outcome.Skipped + 1 };
                    _log.LogWarning(
                        "Stranded assembly component {ComponentId} ('{FileName}') of assembly "
                        + "{AssemblyId} for business unit {BusinessUnitId} was held with no "
                        + "processing job and could not be re-driven; it is now {Resolved} "
                        + "({Reason}) so its message reaches a person instead of nobody.",
                        candidate.Id, candidate.FileName, candidate.AssemblyId, businessUnitId,
                        disposition, code);
                    continue;
                }

                // The job is the authority. Read past the change tracker so a previous iteration
                // in this scope cannot answer for this one.
                var job = candidate.ExtractionJobId is { } jobId
                    ? await context.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>()
                        .AsNoTracking()
                        .Where(j => j.BusinessUnitId == businessUnitId && j.Id == jobId)
                        .Select(j => new { j.Id, j.Status, j.Attempts, j.MaxAttempts, j.LastError })
                        .FirstOrDefaultAsync(ct)
                    : null;

                string? reasonCode;
                string? reasonDetail;
                EmailInquiryComponentStatus resolved;

                if (candidate.ExtractionJobId is null || job is null)
                {
                    // NOTHING WILL EVER PRODUCE THIS PART. Either it was never queued (a crash
                    // between planning and scheduling) or its queue row is gone. Skipped is the
                    // honest answer: terminal, commercially significant, and it sends the message
                    // to a human rather than leaving it waiting on work nobody is doing.
                    resolved = EmailInquiryComponentStatus.Skipped;
                    reasonCode = candidate.ExtractionJobId is null
                        ? EmailInquiryHoldReasons.StrandedWithoutJob
                        : EmailInquiryHoldReasons.StrandedJobMissing;
                    reasonDetail = "This part of the message was never processed and no processing "
                        + "record for it exists, so it could not be read. The message is kept for "
                        + "review with everything that was read.";
                }
                else if (job.Status == ERP_RFQ_Automation.Extraction.ExtractionStatus.Succeeded)
                {
                    // The job finished; the component simply never heard. Reconcile it — but only
                    // against a result that actually exists. Marking Completed without one would
                    // trip the assembler's under-quote guard and hold the whole message, and
                    // would be a claim that content was captured when it was not.
                    var hasResult = await context.Set<EmailInquiryComponentResult>()
                        .AsNoTracking()
                        .AnyAsync(r => r.BusinessUnitId == businessUnitId
                                       && r.ComponentId == candidate.Id, ct);
                    if (hasResult)
                    {
                        resolved = EmailInquiryComponentStatus.Completed;
                        reasonCode = null;
                        reasonDetail = null;
                    }
                    else
                    {
                        resolved = EmailInquiryComponentStatus.Skipped;
                        reasonCode = EmailInquiryHoldReasons.StrandedResultMissing;
                        reasonDetail = "Processing of this part reported success but recorded "
                            + "nothing that could be read back, so the message is kept for review "
                            + "rather than quoted without it.";
                    }
                }
                else if (IsStillRunnable(job.Status, job.Attempts, job.MaxAttempts))
                {
                    // GENUINELY IN FLIGHT. Left strictly alone: closing a component whose job is
                    // about to succeed would discard content the customer sent, which is the one
                    // mistake this sweep must never make in the name of tidiness.
                    outcome = outcome with { LeftInFlight = outcome.LeftInFlight + 1 };
                    continue;
                }
                else
                {
                    // Stopped trying. Closed EXACTLY as the live path closes it — same shared
                    // rule, same infrastructure-vs-content split — with the error code recovered
                    // from the durable job row.
                    var errorCode = job.Status == ERP_RFQ_Automation.Extraction.ExtractionStatus.Duplicate
                        ? "duplicate_document"
                        : EmailInquiryComponentClosure.ErrorCodeFromJobError(job.LastError);
                    resolved = EmailInquiryComponentClosure.StatusFor(errorCode);
                    reasonCode = resolved == EmailInquiryComponentStatus.FailedRecoverable
                        ? EmailInquiryHoldReasons.StrandedInfrastructureFault
                        : EmailInquiryHoldReasons.StrandedJobStopped;
                    reasonDetail = resolved == EmailInquiryComponentStatus.FailedRecoverable
                        ? "Processing of this part stopped because a required service was "
                          + "unavailable. The message is held so it is not quoted without a "
                          + "document that still exists."
                        : "Processing of this part stopped and cannot be retried, so it could not "
                          + "be read. The message is kept for review with everything that was read.";
                }

                // Written through the coordinator, not by hand. It is the ONE writer of component
                // state after capture: it re-reads inside a transaction, refuses to walk a
                // terminal component backwards — which is what makes a second sweep a no-op — and
                // re-evaluates the message in the same unit of work, so the barrier's verdict is
                // recomputed rather than inferred.
                await coordinator.RecordComponentOutcomeAsync(
                    businessUnitId, candidate.AssemblyId, candidate.ComponentKey, resolved,
                    reasonCode, reasonDetail, sourceDocumentOccurrenceId: null, ct);

                touched.Add(candidate.AssemblyId);
                outcome = resolved switch
                {
                    EmailInquiryComponentStatus.Completed =>
                        outcome with { Reconciled = outcome.Reconciled + 1 },
                    EmailInquiryComponentStatus.FailedRecoverable =>
                        outcome with { Held = outcome.Held + 1 },
                    _ => outcome with { Skipped = outcome.Skipped + 1 }
                };

                _log.LogInformation(
                    "Stranded assembly component {ComponentId} ('{FileName}') of assembly "
                    + "{AssemblyId} for business unit {BusinessUnitId} was {Previous} with job "
                    + "{JobId} ({JobStatus}); it is now {Resolved} ({Reason}). Its message can "
                    + "finalize instead of waiting on a part that will never arrive.",
                    candidate.Id, candidate.FileName, candidate.AssemblyId, businessUnitId,
                    candidate.Status, candidate.ExtractionJobId, job?.Status.ToString() ?? "<none>",
                    resolved, reasonCode ?? "reconciled");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // ONE BAD ROW NEVER STOPS THE SWEEP. The coordinator is transactional, so a throw
                // leaves this component exactly as it was and the next cycle tries again — while
                // every other stranded part on the platform is still resolved today.
                outcome = outcome with { Failed = outcome.Failed + 1 };
                context.ChangeTracker.Clear();
                _log.LogError(exception,
                    "Could not resolve stranded assembly component {ComponentId} of assembly "
                    + "{AssemblyId} for business unit {BusinessUnitId}; continuing with the next "
                    + "part.", candidate.Id, candidate.AssemblyId, businessUnitId);
            }
        }

        return (outcome, touched);
    }

    /// <summary>
    /// Decides what becomes of a part that is HELD WITH NO PROCESSING JOB — re-drive it, or end
    /// its message's wait.
    ///
    /// <para><b>This population had no path out of the system at all.</b> Four scheduling
    /// failures write it: a manifest refusal, an evidence-storage outage, an inspection fault and
    /// the catch-all. The sweep looked only at Pending/Inspecting/Extracting; the operator
    /// dead-letter queue needs a job that was never created; and the mailbox stops offering the
    /// message once it falls out of the poller's lookback window. So the message was captured,
    /// durable, invisible and finished — the exact opposite of the rule this module enforces.</para>
    ///
    /// <para><b>Two of the reasons can never improve, and pretending otherwise is the trap.</b> A
    /// manifest refusal is a disagreement between two durable records; a lost original cannot come
    /// back. Re-driving either forever would keep the message technically "recoverable" and
    /// permanently undecided, which reads as diligence and behaves as loss. They go straight to a
    /// terminal, explained refusal that puts the message in a person's hands.</para>
    /// </summary>
    /// <returns>
    /// The disposition to record, its reason, and whether scheduling was successfully re-driven —
    /// in which case the caller records nothing, because the scheduler already moved the row.
    /// </returns>
    private async Task<(EmailInquiryComponentStatus Disposition, string Code, string Detail, bool Rescheduled)>
        ResolveUnscheduledHoldAsync(
            IEmailInquiryIntakeService intake,
            long businessUnitId,
            long assemblyId,
            string componentKey,
            string? heldReason,
            DateTimeOffset capturedAtUtc,
            DateTimeOffset resumeDeadline,
            HashSet<long> resumeAttempted,
            CancellationToken ct)
    {
        // A manifest refusal is about two durable records disagreeing. Re-planning the same bytes
        // produces the same disagreement, every time, forever.
        if (string.Equals(heldReason, EmailIngestEnqueuer.ManifestMismatchReason, StringComparison.Ordinal))
            return (EmailInquiryComponentStatus.Skipped,
                EmailInquiryHoldReasons.SchedulingRefusedByManifest,
                EmailInquiryHoldReasons.SchedulingNotRecoveredDetail, false);

        // Measured from CAPTURE, so a part held for days is decided on the first sweep that sees
        // it rather than being handed a fresh window it has already proved it cannot use.
        if (capturedAtUtc <= resumeDeadline)
            return (EmailInquiryComponentStatus.Skipped,
                EmailInquiryHoldReasons.SchedulingNotRecovered,
                EmailInquiryHoldReasons.SchedulingNotRecoveredDetail, false);

        // One attempt per MESSAGE per cycle: scheduling walks every component of the assembly, so
        // a second call for a sibling part would find nothing left to schedule. A part the first
        // attempt could not place stays HELD — never closed — because closing it here would end a
        // message on the strength of a call this cycle deliberately did not make.
        if (!resumeAttempted.Add(assemblyId))
            return (EmailInquiryComponentStatus.FailedRecoverable,
                heldReason ?? EmailIngestEnqueuer.SchedulingFailedReason,
                EmailInquiryHoldReasons.SchedulingNotRecoveredDetail, false);

        // GOVERNED, always — including when the assembly is still inside the pipeline and the
        // narrower automatic authority would have done. The sweep can reopen a message that has
        // already reached a person's tray, so it signs every reopen it makes with the same named
        // actor rather than only the ones that strictly need it. A rescue nobody can attribute is
        // the thing this grant exists to prevent, and "only sometimes attributable" is not a
        // property worth having.
        var resume = await intake.ResumeSchedulingAsync(
            businessUnitId, assemblyId, ct, EmailInquirySchedulingGrant.RecoverySweep);
        return resume.Outcome switch
        {
            EmailInquiryResumeOutcome.Resumed =>
                (EmailInquiryComponentStatus.Extracting, string.Empty, string.Empty, true),

            // Scheduling ran and this part still could not be queued — but the fault it hit may
            // clear, and the deadline above is what stops that being forever. Left held, its
            // reason untouched, for the next cycle.
            EmailInquiryResumeOutcome.StillHeld or EmailInquiryResumeOutcome.NothingToResume =>
                (EmailInquiryComponentStatus.FailedRecoverable,
                    heldReason ?? EmailIngestEnqueuer.SchedulingFailedReason,
                    EmailInquiryHoldReasons.SchedulingNotRecoveredDetail, false),

            EmailInquiryResumeOutcome.EvidenceLost =>
                (EmailInquiryComponentStatus.Skipped,
                    EmailInquiryHoldReasons.SchedulingEvidenceLost,
                    EmailInquiryHoldReasons.SchedulingEvidenceLostDetail, false),

            EmailInquiryResumeOutcome.ManifestRefused =>
                (EmailInquiryComponentStatus.Skipped,
                    EmailInquiryHoldReasons.SchedulingRefusedByManifest,
                    EmailInquiryHoldReasons.SchedulingNotRecoveredDetail, false),

            // Nothing failed. The gate decided this message is a supplier document, so its parts
            // were never owed extraction jobs — Ignored is the terminal status the state machine
            // already treats as "accounted for", not as a part that went unread.
            EmailInquiryResumeOutcome.NotAnInquiry =>
                (EmailInquiryComponentStatus.Ignored,
                    EmailInquiryHoldReasons.SchedulingNotAnInquiry,
                    EmailInquiryHoldReasons.SchedulingNotAnInquiryDetail, false),

            _ => (EmailInquiryComponentStatus.Skipped,
                EmailInquiryHoldReasons.SchedulingNotRecovered,
                EmailInquiryHoldReasons.SchedulingNotRecoveredDetail, false)
        };
    }

    /// <summary>
    /// Whether the queue will still run this job on its own.
    ///
    /// <para><c>Failed</c> is grouped with the terminal states deliberately: the queue's own SQL
    /// only ever writes <c>Pending</c> or <c>DeadLetter</c> on a failure, so a row sitting at
    /// <c>Failed</c> is a legacy or hand-edited state that nothing will pick up again.</para>
    /// </summary>
    private static bool IsStillRunnable(
        ERP_RFQ_Automation.Extraction.ExtractionStatus status, int attempts, int maxAttempts)
        => status is ERP_RFQ_Automation.Extraction.ExtractionStatus.Pending
               or ERP_RFQ_Automation.Extraction.ExtractionStatus.Leased
               or ERP_RFQ_Automation.Extraction.ExtractionStatus.Extracting
               or ERP_RFQ_Automation.Extraction.ExtractionStatus.Persisting
           && attempts < maxAttempts;

    /// <summary>
    /// FAIL CLOSED. Without a resolved tenant the connection routes to the BYPASSRLS pipeline
    /// role AND the EF filters (<c>CurrentTenantId == null || ...</c>) become no-ops — both
    /// isolation layers off at once, on a sweep that enumerates every tenant on the platform.
    /// Refusing this tenant is caught by the per-tenant handler and the sweep continues.
    /// </summary>
    private static void AssertTenantScope(ErpRfqAutomationContext context, long businessUnitId)
    {
        if (context.ScopedTenantId != businessUnitId)
            throw new InvalidOperationException(
                $"Email inquiry assembly recovery refused to run for business unit {businessUnitId}: "
                + $"the DbContext resolved tenant {context.ScopedTenantId?.ToString() ?? "<none>"}. "
                + "Tenant scope is mandatory for this sweep.");
    }

    /// <summary>PHASE 2 — build the Lead for one tenant's messages that are owed one.</summary>
    /// <param name="alsoConsider">
    /// Assemblies phase 1 just unblocked. Included regardless of the minimum-age grace, which
    /// exists to keep the sweep off work a live worker is holding — and nothing is holding a
    /// message this same sweep just finished settling. Without it every unblocked message would
    /// wait a further cycle for no reason.
    /// </param>
    private async Task<EmailInquiryRecoverySweepResult> SweepReadyAssembliesAsync(
        long businessUnitId, IReadOnlyCollection<long> alsoConsider, CancellationToken ct)
    {
        // The push precedes the scope: ITenantContext captures the ambient tenant in its
        // CONSTRUCTOR, so a scope created first resolves a DbContext that believes in no tenant
        // whatever is pushed afterwards.
        using var tenant = _tenantScope.Push(businessUnitId);
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        AssertTenantScope(context, businessUnitId);

        var assembler = scope.ServiceProvider.GetRequiredService<IEmailInquiryLeadAssembler>();
        var cutoff = _time.GetUtcNow() - _options.ValidatedMinimumAge;

        // Ordered oldest-first so a backlog drains in the order the customers sent it, and the
        // batch bound means one tenant's backlog cannot hold the sweep for the others. Runs
        // through the normal query filters under RLS — no IgnoreQueryFilters here.
        var candidates = await context.EmailInquiryAssemblies
            .AsNoTracking()
            .Where(a => a.BusinessUnitId == businessUnitId
                        && a.Status == SweptWhenOwedALead
                        && a.AssembledLeadId == null
                        && (a.UpdatedAtUtc <= cutoff || alsoConsider.Contains(a.Id)))
            .OrderBy(a => a.UpdatedAtUtc).ThenBy(a => a.Id)
            .Take(_options.ValidatedBatchSize)
            .Select(a => a.Id)
            .ToListAsync(ct);

        var recovered = 0;
        var alreadyCompleted = 0;
        var heldForReview = 0;
        var stillRecoverable = 0;
        var unexpected = 0;
        var failed = 0;

        foreach (var assemblyId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var leadId = await assembler.AssembleAsync(businessUnitId, assemblyId, ct);
                if (leadId is not null)
                {
                    recovered++;
                    _log.LogInformation(
                        "Recovered stranded email inquiry assembly {AssemblyId} for business unit "
                        + "{BusinessUnitId} as lead {LeadId}.",
                        assemblyId, businessUnitId, leadId.Value);
                    continue;
                }

                // A null return is NOT a success and must never be counted as one. Re-read the
                // persisted status and say what actually happened.
                switch (await ReadStatusAsync(context, businessUnitId, assemblyId, ct))
                {
                    case EmailInquiryAssemblyStatus.Assembled:
                        // Another instance, or the original worker, got there first.
                        alreadyCompleted++;
                        break;
                    case EmailInquiryAssemblyStatus.NeedsReview:
                        heldForReview++;
                        break;
                    case EmailInquiryAssemblyStatus.ReadyForAssembly:
                        // Legitimately still owed a Lead — the next sweep tries again. Counted
                        // separately so a message that never converges is visible rather than
                        // hiding inside "candidates".
                        stillRecoverable++;
                        _log.LogWarning(
                            "Email inquiry assembly {AssemblyId} for business unit {BusinessUnitId} "
                            + "is still ReadyForAssembly after a recovery attempt.",
                            assemblyId, businessUnitId);
                        break;
                    case var other:
                        unexpected++;
                        _log.LogError(
                            "Email inquiry assembly {AssemblyId} for business unit {BusinessUnitId} "
                            + "produced no lead and is now {Status}, which recovery does not "
                            + "expect. It is NOT being treated as recovered.",
                            assemblyId, businessUnitId, other);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // One poison message must not stop the rest of this tenant's backlog. The
                // assembler is transactional, so a throw leaves the message exactly as it was
                // and the next sweep will try it again.
                failed++;
                _log.LogError(exception,
                    "Recovery of email inquiry assembly {AssemblyId} for business unit "
                    + "{BusinessUnitId} failed; continuing with the next message.",
                    assemblyId, businessUnitId);
            }
        }

        return new EmailInquiryRecoverySweepResult(
            1, candidates.Count, recovered, alreadyCompleted, heldForReview, stillRecoverable,
            unexpected, failed, TimeSpan.Zero);
    }

    /// <summary>
    /// PHASE 3 — make the LEDGER stop claiming a message is in flight when nothing is moving it.
    ///
    /// <para><b>Why a third phase and not a column on the second.</b> The two phases above are
    /// about the assembly aggregate: parts that never reported, and messages that owe a Lead.
    /// This one is about the <c>EmailIngest</c> row, which is the record a person actually reads
    /// on the Inbound Mail screen, and its population is disjoint from both. A message ingested
    /// before the barrier existed has no assembly at all, so no query over assemblies or
    /// components can find it; and a message whose assembly is already NeedsReview is invisible
    /// to phase 1 (its components are terminal) and to phase 2 (it is not ReadyForAssembly), yet
    /// its ledger row can still say "Queued" for as long as the row exists.</para>
    ///
    /// <para><b>The failure it ends.</b> "Queued" has exactly two writers that clear it — the
    /// persist path, and the worker's dead-letter annotation — and NEITHER runs when the queue's
    /// own claim statement dead-letters a job. The exhausted-lease and lineage-quarantine CTEs
    /// move a row to <c>DeadLetter</c> inside the claim, with no worker in the loop, so nothing
    /// tells the ledger and the screen shows work in progress over jobs that stopped days ago.
    /// A terminal state that presents itself as in-flight is worse than a visible failure: it is
    /// the reason nobody looks.</para>
    ///
    /// <para>Nothing here re-drives work or invents an outcome. It reports what already happened,
    /// and it refuses to touch a message with a live job — see
    /// <see cref="EmailInquiryLedgerReconciliation.StatusFor"/>, which holds the whole rule and is
    /// asserted on its own.</para>
    /// </summary>
    private async Task<EmailInquiryLedgerReconciliationOutcome> ReconcileLedgerAsync(
        long businessUnitId, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(businessUnitId);
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        AssertTenantScope(context, businessUnitId);

        var cutoff = (_time.GetUtcNow() - _options.ValidatedLedgerReconciliationAge).UtcDateTime;

        var candidates = await context.EmailIngests
            .AsNoTracking()
            .Where(e => e.EmailConfiguration.BusinessUnitId == businessUnitId
                        && (e.ParseStatus == EmailInquiryLedgerReconciliation.InFlightQueued
                            || e.ParseStatus == EmailInquiryLedgerReconciliation.InFlightPending)
                        && e.CreatedOn <= cutoff)
            .OrderBy(e => e.CreatedOn).ThenBy(e => e.Id)
            .Take(_options.ValidatedBatchSize)
            .Select(e => new { e.Id, e.MessageId, e.ParseStatus })
            .ToListAsync(ct);

        var outcome = new EmailInquiryLedgerReconciliationOutcome(candidates.Count, 0, 0, 0);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var assembly = await context.EmailInquiryAssemblies
                    .AsNoTracking()
                    .Where(a => a.BusinessUnitId == businessUnitId && a.EmailIngestId == candidate.Id)
                    .Select(a => new { a.Id, a.Status })
                    .FirstOrDefaultAsync(ct);
                var assemblyStatus = assembly is null
                    ? (EmailInquiryAssemblyStatus?)null
                    : assembly.Status;

                // Whether a PART of this message is still owed a sweep, asked with the sweep's
                // own declared lists rather than a copy of them — the same reason those lists are
                // public at all. It is what separates a hold that is being worked from a hold
                // that has stopped, and without it the ledger left every job-bound hold reading
                // "Queued" for as long as the row existed.
                var hasSweepableComponent = assembly is not null
                    && await context.EmailInquiryComponents
                        .AsNoTracking()
                        .AnyAsync(c => c.BusinessUnitId == businessUnitId
                                       && c.AssemblyId == assembly.Id
                                       && (SweptRegardlessOfJob.Contains(c.Status)
                                           || (c.Status == SweptOnlyWithoutJob
                                               && c.ExtractionJobId == null)), ct);

                // The message's jobs, found the way the ledger itself joins them: every extraction
                // occurrence recorded under this message's logical group key. It is the ONE link
                // that works for a pre-assembly ingest as well as a modern one, because the key is
                // built from the Message-Id in both.
                var groupKey = $"email:{candidate.MessageId}";
                var jobStates = await (
                    from occurrence in context
                        .Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>()
                        .AsNoTracking()
                    join job in context.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                        on occurrence.ExtractionJobId equals job.Id
                    where occurrence.BusinessUnitId == businessUnitId
                          && occurrence.LogicalGroupKey == groupKey
                          && job.BusinessUnitId == businessUnitId
                    select new { job.Status, job.Attempts, job.MaxAttempts })
                    .ToListAsync(ct);

                var hasRunnableJob = jobStates.Any(
                    j => IsStillRunnable(j.Status, j.Attempts, j.MaxAttempts));
                var hasStoppedJob = jobStates.Count > 0 && !hasRunnableJob;

                var corrected = EmailInquiryLedgerReconciliation.StatusFor(
                    candidate.ParseStatus, assemblyStatus, hasRunnableJob, hasStoppedJob,
                    hasSweepableComponent);
                if (corrected is null)
                {
                    outcome = outcome with { StillMoving = outcome.StillMoving + 1 };
                    continue;
                }

                // Written with a guarded UPDATE rather than a tracked entity: the poller, the
                // persister and this sweep can all touch the same row, and re-asserting the exact
                // status we decided from is what makes a concurrent write win instead of being
                // clobbered by a stale read.
                //
                // The tenant predicate is repeated in the statement even though the candidate came
                // from a tenant-scoped query. EmailIngests carries no BusinessUnitId of its own —
                // it is a tenant of its mailbox — so raw SQL against it has no row-level filter to
                // inherit, and an id that reached here from anywhere else would write across a
                // tenant boundary. Cheap, and the one place a mistake would be invisible.
                var written = await context.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE public."EmailIngests" e
                    SET "ParseStatus" = {corrected}, "ParsedAt" = now()
                    WHERE e."ID" = {candidate.Id}
                      AND e."ParseStatus" = {candidate.ParseStatus}
                      AND EXISTS (
                        SELECT 1 FROM public."Email_Configurations" c
                        WHERE c."ID" = e."EmailConfigurationID"
                          AND c."BusinessUnitID" = {businessUnitId})
                    """, ct);
                if (written == 0)
                {
                    outcome = outcome with { StillMoving = outcome.StillMoving + 1 };
                    continue;
                }

                outcome = outcome with { Corrected = outcome.Corrected + 1 };
                _log.LogWarning(
                    "Email ingest {IngestId} for business unit {BusinessUnitId} read "
                    + "'{Previous}' with no live processing job (assembly {AssemblyStatus}, "
                    + "{JobCount} job(s)); its ledger status is now '{Corrected}' so the screen "
                    + "stops reporting progress on work that stopped.",
                    candidate.Id, businessUnitId, candidate.ParseStatus,
                    assemblyStatus?.ToString() ?? "<none>", jobStates.Count, corrected);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                outcome = outcome with { Failed = outcome.Failed + 1 };
                context.ChangeTracker.Clear();
                _log.LogError(exception,
                    "Could not reconcile the ledger status of email ingest {IngestId} for business "
                    + "unit {BusinessUnitId}; continuing with the next message.",
                    candidate.Id, businessUnitId);
            }
        }

        return outcome;
    }

    /// <summary>
    /// Reads the status the DATABASE holds, past the change tracker.
    ///
    /// <para>The assembler ran on this same scoped context and may still be tracking the
    /// assembly it just decided about, so a tracked read could answer with the in-memory value
    /// rather than the committed one — and the whole purpose of this read is to distinguish what
    /// was persisted from what was attempted.</para>
    /// </summary>
    private static async Task<EmailInquiryAssemblyStatus?> ReadStatusAsync(
        ErpRfqAutomationContext context, long businessUnitId, long assemblyId, CancellationToken ct)
    {
        var statuses = await context.EmailInquiryAssemblies
            .AsNoTracking()
            .Where(a => a.BusinessUnitId == businessUnitId && a.Id == assemblyId)
            .Select(a => (EmailInquiryAssemblyStatus?)a.Status)
            .ToListAsync(ct);

        return statuses.Count == 1 ? statuses[0] : null;
    }
}

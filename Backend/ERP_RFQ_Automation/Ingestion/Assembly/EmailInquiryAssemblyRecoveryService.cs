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
/// <param name="Failed">Rows that threw. One never stops the sweep.</param>
public readonly record struct EmailInquiryStrandedComponentOutcome(
    int Examined, int Reconciled, int Skipped, int Held, int LeftInFlight, int Failed)
{
    public int Resolved => Reconciled + Skipped + Held;

    public static EmailInquiryStrandedComponentOutcome operator +(
        EmailInquiryStrandedComponentOutcome left, EmailInquiryStrandedComponentOutcome right)
        => new(left.Examined + right.Examined, left.Reconciled + right.Reconciled,
            left.Skipped + right.Skipped, left.Held + right.Held,
            left.LeftInFlight + right.LeftInFlight, left.Failed + right.Failed);
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
    EmailInquiryStrandedComponentOutcome StrandedComponents = default)
{
    public static readonly EmailInquiryRecoverySweepResult Skipped =
        new(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);
}

public interface IEmailInquiryAssemblyRecoveryService
{
    Task<EmailInquiryRecoverySweepResult> SweepOnceAsync(CancellationToken ct = default);
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
            stillRecoverable, unexpected, failed, duration, components);

        // Logged at Information only when it did something, so an idle platform does not emit a
        // line every two minutes that operators learn to scroll past.
        if (candidates > 0 || failed > 0 || components.Examined > 0)
        {
            _log.LogInformation(
                "Email inquiry assembly recovery swept {Tenants} tenant(s) in {DurationMs}ms: "
                + "{Candidates} candidate(s), {Recovered} recovered, {AlreadyCompleted} already "
                + "complete, {HeldForReview} held for review, {StillRecoverable} still "
                + "recoverable, {Unexpected} in an unexpected state, {Failed} failed; "
                + "{ComponentsExamined} stranded part(s) examined, {ComponentsReconciled} "
                + "reconciled, {ComponentsSkipped} skipped, {ComponentsHeld} held, "
                + "{ComponentsInFlight} still in flight, {ComponentsFailed} failed.",
                result.TenantsSwept, (long)duration.TotalMilliseconds, result.Candidates,
                result.Recovered, result.AlreadyCompleted, result.HeldForReview,
                result.StillRecoverable, result.Unexpected, result.Failed,
                components.Examined, components.Reconciled, components.Skipped,
                components.Held, components.LeftInFlight, components.Failed);
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
            .Where(c => (c.Status == EmailInquiryComponentStatus.Pending
                         || c.Status == EmailInquiryComponentStatus.Inspecting
                         || c.Status == EmailInquiryComponentStatus.Extracting)
                        && c.UpdatedAtUtc <= componentCutoff)
            .Select(c => c.BusinessUnitId)
            .Distinct()
            .ToListAsync(ct);

        var tenants = owedALead.Union(withStrandedParts).OrderBy(id => id).ToList();
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
        var cutoff = _time.GetUtcNow() - _options.ValidatedStrandedComponentAge;

        // Oldest first, bounded per cycle. One tenant's backlog of stranded parts cannot hold the
        // sweep for every other tenant on the platform.
        var candidates = await context.EmailInquiryComponents
            .AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId
                        && (c.Status == EmailInquiryComponentStatus.Pending
                            || c.Status == EmailInquiryComponentStatus.Inspecting
                            || c.Status == EmailInquiryComponentStatus.Extracting)
                        && c.UpdatedAtUtc <= cutoff)
            .OrderBy(c => c.UpdatedAtUtc).ThenBy(c => c.Id)
            .Take(_options.ValidatedBatchSize)
            .Select(c => new
            {
                c.Id, c.AssemblyId, c.ComponentKey, c.Status, c.FileName, c.ExtractionJobId
            })
            .ToListAsync(ct);

        var outcome = new EmailInquiryStrandedComponentOutcome(
            Examined: candidates.Count, 0, 0, 0, 0, 0);
        var touched = new HashSet<long>();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
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
                        && a.Status == EmailInquiryAssemblyStatus.ReadyForAssembly
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

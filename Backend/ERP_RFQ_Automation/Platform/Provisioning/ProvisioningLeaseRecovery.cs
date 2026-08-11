using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// How an execution's ownership reads at one instant.
///
/// <para>Deliberately six values rather than a boolean. "Can this be recovered?" and "why not?"
/// are the same question asked by the same operator thirty seconds apart, and a bare false forces
/// them to guess between "someone is working on it", "nobody is working on it but not for long
/// enough yet" and "there was never anything to recover" — three situations with three different
/// correct actions.</para>
/// </summary>
public enum ProvisioningLeaseStaleness
{
    /// <summary>
    /// A runner holds an UNEXPIRED lease. Never recoverable, by anyone, for any reason: the
    /// holder is presumed to be mid-step, and taking the execution off it would put two runners
    /// on one half-built tenant.
    /// </summary>
    Live,

    /// <summary>
    /// The lease has lapsed, but the execution has not been silent for the whole grace. A RUNNER
    /// may reclaim this — that race is what the lease is for — but a human may not yet declare
    /// the previous attempt dead.
    /// </summary>
    Cooling,

    /// <summary>
    /// Running, lease lapsed, and silent for longer than the grace. The prior attempt is not
    /// coming back: recoverable.
    /// </summary>
    Abandoned,

    /// <summary>
    /// Not running, yet still carrying lease columns. Nothing holds this execution — the columns
    /// are debris from an attempt that ended without clearing them — but they are debris an
    /// operator reads as ownership, so they are cleared rather than tolerated.
    /// </summary>
    OwnershipResidue,

    /// <summary>Nothing owns it and nothing claims to. There is nothing to recover.</summary>
    Unowned,

    /// <summary>Succeeded or cancelled. Ownership is meaningless and is never rewritten here.</summary>
    Terminal
}

/// <summary>
/// Every ownership fact about an execution at one instant, captured so a recovery can be audited
/// as a BEFORE and an AFTER rather than as an assertion that something changed.
///
/// <para><see cref="IdempotencyKey"/> and <see cref="RequestFingerprint"/> are in here for one
/// reason: a resume must reuse the original request identity, and the only way to prove a
/// remediation did not quietly mint a second identity is to record both sides of it.</para>
/// </summary>
public sealed record ProvisioningOwnershipSnapshot(
    string State,
    string? LeaseOwner,
    Guid? LeaseToken,
    DateTime? LeaseUntil,
    DateTime? LeaseHeartbeatAt,
    string? CurrentStep,
    string? FailedStep,
    int AttemptCount,
    int RecoveredAttemptCount,
    long Version,
    string IdempotencyKey,
    string RequestFingerprint)
{
    public static ProvisioningOwnershipSnapshot Of(ProvisioningExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new ProvisioningOwnershipSnapshot(
            execution.State.ToString(),
            execution.LeaseOwner,
            execution.LeaseToken,
            execution.LeaseUntil,
            execution.LeaseHeartbeatAt,
            execution.CurrentStep,
            execution.FailedStep,
            execution.AttemptCount,
            execution.RecoveredAttemptCount,
            execution.Version,
            execution.IdempotencyKey,
            execution.RequestFingerprint);
    }
}

/// <summary>
/// The read-only verdict: what state the execution is in, whether ownership may be taken from it,
/// where a resume would restart, and every inconsistency found on the way.
/// </summary>
public sealed record ProvisioningRecoveryAssessment(
    long ExecutionId,
    string Slug,
    ProvisioningLeaseStaleness Staleness,
    TimeSpan SilentFor,
    TimeSpan Grace,
    ProvisioningOwnershipSnapshot Ownership,
    string? FirstIncompleteStep,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> Findings)
{
    /// <summary>
    /// Whether <see cref="IProvisioningLeaseRecovery.RecoverAsync"/> would act. The service
    /// re-evaluates this inside its own transaction rather than trusting a caller's copy — an
    /// assessment is a snapshot, and a lease can be taken between reading one and acting on it.
    /// </summary>
    public bool IsRecoverable => Staleness
        is ProvisioningLeaseStaleness.Abandoned or ProvisioningLeaseStaleness.OwnershipResidue;
}

public enum ProvisioningRecoveryOutcome
{
    Recovered,
    NotFound,

    /// <summary>A runner holds an unexpired lease. The one refusal that is never overridable.</summary>
    LeaseStillLive,

    /// <summary>The lease has lapsed but the silence is younger than the grace.</summary>
    NotYetStale,

    /// <summary>Nothing owns it; there is nothing to take.</summary>
    NothingToRecover,

    /// <summary>Succeeded or cancelled. Recovery would be rewriting history, not repairing it.</summary>
    Terminal,

    /// <summary>Somebody else wrote the row between the read and the write.</summary>
    VersionConflict
}

public sealed record ProvisioningRecoveryResult(
    ProvisioningRecoveryOutcome Outcome,
    ProvisioningRecoveryAssessment? Assessment,
    ProvisioningOwnershipSnapshot? Before,
    ProvisioningOwnershipSnapshot? After,
    string? Error = null);

/// <param name="TransferToOwner">
/// Null releases the execution back to the queue, which is what an operator almost always wants:
/// any runner then claims it through the ordinary lease path. A non-null value hands ownership
/// directly to a named runner and keeps the execution <c>Running</c> — used when a specific node
/// must finish the work, and recorded as a transfer rather than a release so the audit trail does
/// not have to infer which happened.
/// </param>
public sealed record ProvisioningRecoveryCommand(
    long ExecutionId,
    string Reason,
    string Actor,
    long? ActorPlatformUserId,
    string? TransferToOwner = null);

/// <summary>
/// The ONLY supported way to take a provisioning execution away from an attempt that is no longer
/// running.
///
/// <para><b>The rule, stated once.</b> Ownership may be taken from an execution when, at the same
/// instant, ALL of the following hold:</para>
/// <list type="number">
/// <item><description>its state is <c>Running</c> — the only state that can legitimately hold a
/// lease; and</description></item>
/// <item><description><c>LeaseUntil</c> is null or has passed — the lease has lapsed; and</description></item>
/// <item><description><c>now - LastProgressAt &gt;= grace</c>, where <c>LastProgressAt</c> is
/// <c>LeaseHeartbeatAt ?? LeaseUntil ?? StartedOn ?? CreatedOn</c> and <c>grace</c> is
/// <c>max(StaleLeaseGrace, LeaseDuration)</c>.</description></item>
/// </list>
/// <para>Plus one narrower case: an execution that is NOT running but still carries lease columns
/// has them cleared, because nothing holds it and the columns read as though something does.</para>
///
/// <para><b>Why two independent conditions and not just the lease.</b> A lapsed lease alone is a
/// sufficient signal for a RUNNER — two runners racing for a lapsed lease is precisely the
/// situation optimistic concurrency was put there to settle, and the loser simply walks away. It
/// is not sufficient for a human, because the heartbeat is written between steps: a runner inside
/// the baseline seed is alive and silent, and an operator who declares it dead on the strength of
/// a lapsed lease starts a second runner on a tenant the first is still building. The grace, which
/// the options validator refuses to let fall below <c>LeaseDuration</c>, is the margin that makes
/// the human's verdict as safe as the machine's.</para>
///
/// <para><b>Why this is a service and not an UPDATE.</b> Three things have to happen together or
/// not at all: ownership changes, the reason is recorded on the row where the next operator will
/// read it, and an audit record carrying the before and after state is written. They share one
/// transaction here. A hand-written UPDATE gets the first and silently skips the other two, which
/// is how an execution ends up mysteriously re-running with nobody able to say who decided that or
/// why. The database refuses the most dangerous half of that shortcut outright — see the
/// <c>nexora_guard_provisioning_lease_transfer</c> trigger, which makes a LIVE lease
/// untransferable by any statement from any role.</para>
/// </summary>
public interface IProvisioningLeaseRecovery
{
    /// <summary>Reads the verdict without writing anything. Safe to poll.</summary>
    Task<ProvisioningRecoveryAssessment?> AssessAsync(long executionId, CancellationToken ct = default);

    /// <summary>Every execution an operator could act on right now, oldest silence first.</summary>
    Task<IReadOnlyList<ProvisioningRecoveryAssessment>> ListRecoverableAsync(
        int take, CancellationToken ct = default);

    /// <summary>
    /// Takes ownership away from an abandoned attempt, transactionally, with audit evidence.
    /// Never touches a live lease.
    /// </summary>
    Task<ProvisioningRecoveryResult> RecoverAsync(
        ProvisioningRecoveryCommand command, CancellationToken ct = default);
}

/// <summary>
/// The staleness rule as a pure function, so it can be reasoned about and tested without a
/// database, and so the assessment endpoint and the write path can never disagree about it.
/// </summary>
public static class ProvisioningLeaseRules
{
    /// <summary>
    /// The effective grace: never shorter than the lease it is a margin over, whatever
    /// configuration says. The options validator enforces the same floor at startup; this
    /// enforces it again at the point of decision, because <c>IOptionsMonitor</c> means the value
    /// can change between the two.
    /// </summary>
    public static TimeSpan GraceFor(ProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.StaleLeaseGrace > options.LeaseDuration
            ? options.StaleLeaseGrace
            : options.LeaseDuration;
    }

    public static ProvisioningLeaseStaleness Evaluate(
        ProvisioningExecution execution, DateTime utcNow, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var carriesOwnership = execution.LeaseOwner is not null
                               || execution.LeaseToken is not null
                               || execution.LeaseUntil is not null;

        if (execution.IsTerminal)
            return ProvisioningLeaseStaleness.Terminal;

        if (execution.State != ProvisioningExecutionState.Running)
            return carriesOwnership
                ? ProvisioningLeaseStaleness.OwnershipResidue
                : ProvisioningLeaseStaleness.Unowned;

        // Running. The lease decides, and the lease is only live while it has not lapsed.
        if (execution.LeaseUntil is { } until && until > utcNow)
            return ProvisioningLeaseStaleness.Live;

        return SilenceAt(execution, utcNow) >= grace
            ? ProvisioningLeaseStaleness.Abandoned
            : ProvisioningLeaseStaleness.Cooling;
    }

    /// <summary>
    /// How long the execution has been quiet. Clamped at zero: a row written by a node whose
    /// clock runs ahead must read as "no silence yet", never as a negative age that a comparison
    /// would happily call stale.
    /// </summary>
    public static TimeSpan SilenceAt(ProvisioningExecution execution, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var silence = utcNow - execution.LastProgressAt;
        return silence < TimeSpan.Zero ? TimeSpan.Zero : silence;
    }
}

public sealed class ProvisioningLeaseRecovery : IProvisioningLeaseRecovery
{
    private readonly ErpRfqAutomationContext _db;
    private readonly IPlatformAuditService _audit;
    private readonly IOptionsMonitor<ProvisioningOptions> _options;
    private readonly ProvisioningRunSignal _signal;
    private readonly ILogger<ProvisioningLeaseRecovery> _log;

    /// <param name="audit">
    /// Scoped over the SAME <see cref="ErpRfqAutomationContext"/> as this service, which is what
    /// makes the audit row enlist in the ownership transfer's transaction instead of opening a
    /// second one beside it. A record of a transfer that rolled back is worse than no record.
    /// </param>
    public ProvisioningLeaseRecovery(
        ErpRfqAutomationContext db,
        IPlatformAuditService audit,
        IOptionsMonitor<ProvisioningOptions> options,
        ProvisioningRunSignal signal,
        ILogger<ProvisioningLeaseRecovery> log)
    {
        _db = db;
        _audit = audit;
        _options = options;
        _signal = signal;
        _log = log;
    }

    public async Task<ProvisioningRecoveryAssessment?> AssessAsync(
        long executionId, CancellationToken ct = default)
    {
        var execution = await _db.Set<ProvisioningExecution>().AsNoTracking()
            .Include(e => e.Steps)
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

        return execution is null ? null : await AssessAsync(execution, DateTime.UtcNow, ct);
    }

    public async Task<IReadOnlyList<ProvisioningRecoveryAssessment>> ListRecoverableAsync(
        int take, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var grace = ProvisioningLeaseRules.GraceFor(_options.CurrentValue);

        var wanted = Math.Clamp(take, 1, 200);

        // Narrowed in the DATABASE to the only shape that can be recoverable at all — an execution
        // that is Running, or one that is not running yet still carries a lease column — and
        // decided in memory after that. The rule coalesces across four nullable columns and clamps
        // a negative interval; expressing that in SQL would give the sweep a second, subtly
        // different copy of a rule that must have exactly one.
        var candidates = await _db.Set<ProvisioningExecution>().AsNoTracking()
            .Include(e => e.Steps)
            .Where(e => e.State == ProvisioningExecutionState.Running
                        || ((e.State == ProvisioningExecutionState.Pending
                             || e.State == ProvisioningExecutionState.Failed)
                            && (e.LeaseOwner != null || e.LeaseToken != null || e.LeaseUntil != null)))
            .OrderBy(e => e.CreatedOn).ThenBy(e => e.Id)
            .Take(wanted * 4)
            .ToListAsync(ct);

        var assessments = new List<ProvisioningRecoveryAssessment>();
        foreach (var execution in candidates)
        {
            if (!ProvisioningLeaseRules.Evaluate(execution, now, grace)
                    .IsRecoverableStaleness())
                continue;

            assessments.Add(await AssessAsync(execution, now, ct));
            if (assessments.Count >= wanted)
                break;
        }

        return assessments
            .OrderByDescending(assessment => assessment.SilentFor)
            .ToList();
    }

    public async Task<ProvisioningRecoveryResult> RecoverAsync(
        ProvisioningRecoveryCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = command.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A recovery reason is required: declaring a provisioning attempt dead is a "
                + "privileged act, and 'somebody clicked it' is not an answer three months later.",
                nameof(command));

        var actor = string.IsNullOrWhiteSpace(command.Actor) ? "platform" : command.Actor.Trim();
        var transferTo = string.IsNullOrWhiteSpace(command.TransferToOwner)
            ? null
            : command.TransferToOwner.Trim();

        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            // Every attempt rebuilds its graph: a rolled-back attempt can leave post-SaveChanges
            // states tracked as though they had committed.
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var execution = await _db.Set<ProvisioningExecution>()
                .Include(e => e.Steps)
                .FirstOrDefaultAsync(e => e.Id == command.ExecutionId, ct);
            if (execution is null)
                return new ProvisioningRecoveryResult(
                    ProvisioningRecoveryOutcome.NotFound, null, null, null,
                    $"There is no provisioning execution {command.ExecutionId}.");

            // Re-evaluated HERE, inside the transaction, rather than trusting an assessment the
            // caller read a moment ago. A lease can be taken between the two, and the whole point
            // of this operation is that it never fires against a live one.
            var now = DateTime.UtcNow;
            var options = _options.CurrentValue;
            var grace = ProvisioningLeaseRules.GraceFor(options);
            var staleness = ProvisioningLeaseRules.Evaluate(execution, now, grace);
            var assessment = await AssessAsync(execution, now, ct);
            var before = ProvisioningOwnershipSnapshot.Of(execution);

            switch (staleness)
            {
                case ProvisioningLeaseStaleness.Live:
                    return new ProvisioningRecoveryResult(
                        ProvisioningRecoveryOutcome.LeaseStillLive, assessment, before, before,
                        $"Runner '{execution.LeaseOwner}' holds execution {execution.Id} until "
                        + $"{execution.LeaseUntil:O}. A live lease is never transferable: the holder is "
                        + "presumed to be mid-step, and a second runner on the same half-built tenant "
                        + "would write the same rows twice. Wait for the lease to lapse.");

                case ProvisioningLeaseStaleness.Cooling:
                    return new ProvisioningRecoveryResult(
                        ProvisioningRecoveryOutcome.NotYetStale, assessment, before, before,
                        $"Execution {execution.Id} has been silent for "
                        + $"{ProvisioningLeaseRules.SilenceAt(execution, now):g}, which is less than the "
                        + $"{grace:g} grace. Its lease has lapsed, so a runner may already be reclaiming "
                        + "it on its own; forcing ownership now could land a second runner on a step the "
                        + "first is still inside.");

                case ProvisioningLeaseStaleness.Terminal:
                    return new ProvisioningRecoveryResult(
                        ProvisioningRecoveryOutcome.Terminal, assessment, before, before,
                        $"Execution {execution.Id} is {execution.State} and holds nothing. Recovering it "
                        + "would rewrite a finished attempt rather than repair a stuck one.");

                case ProvisioningLeaseStaleness.Unowned:
                    return new ProvisioningRecoveryResult(
                        ProvisioningRecoveryOutcome.NothingToRecover, assessment, before, before,
                        $"Execution {execution.Id} is {execution.State} and carries no ownership. There is "
                        + "nothing to take; retry it if it is not moving.");
            }

            // ---- the transfer itself ---------------------------------------------------------
            // Note what is NOT touched: IdempotencyKey, RequestFingerprint, RequestPayload,
            // AdminPasswordHash, and every recorded id. A resume runs under the ORIGINAL identity
            // — that is what makes it a resume and not a second provision wearing the same name —
            // and the before/after snapshots below carry both so the audit record proves it.
            if (transferTo is not null)
            {
                execution.LeaseOwner = transferTo.Length <= 200 ? transferTo : transferTo[..200];
                execution.LeaseToken = Guid.NewGuid();
                execution.LeaseUntil = now.Add(options.LeaseDuration);
                execution.LeaseHeartbeatAt = now;
                execution.State = ProvisioningExecutionState.Running;
            }
            else
            {
                execution.LeaseOwner = null;
                execution.LeaseToken = null;
                execution.LeaseUntil = null;
                execution.LeaseHeartbeatAt = null;

                if (execution.State == ProvisioningExecutionState.Running)
                {
                    // Back to Pending, which is what the runner's claim query treats as available
                    // work with no attempt ceiling. Deliberately NOT Failed: nothing about the
                    // request was wrong, and parking it as a failure would put a red badge on a
                    // tenant whose only problem was a node that died.
                    execution.State = ProvisioningExecutionState.Pending;
                    execution.CurrentStep = null;
                    execution.CompletedOn = null;
                }
            }

            execution.RecoveredAttemptCount++;
            execution.LastRecoveredOn = now;
            execution.LastRecoveredBy = actor.Length <= 320 ? actor : actor[..320];
            execution.LastRecoveryReason = reason.Length <= 1000 ? reason : reason[..1000];
            execution.Version++;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody wrote the row between the read and the write — most likely a runner
                // claiming the very lease this call was about to release. Losing that race is the
                // system working; the caller re-reads and finds it running.
                return new ProvisioningRecoveryResult(
                    ProvisioningRecoveryOutcome.VersionConflict, assessment, before, null,
                    $"Execution {execution.Id} changed while it was being recovered — most likely a "
                    + "runner claimed it. Read it again before acting.");
            }

            var after = ProvisioningOwnershipSnapshot.Of(execution);

            // Written on THIS context, so the audit row and the ownership change commit together
            // or not at all. An ownership transfer with no record of who did it is exactly the
            // state this whole operation exists to make impossible.
            await WriteAuditAsync(execution, command, assessment, staleness, before, after,
                transferTo, grace, ct);

            await tx.CommitAsync(ct);

            _log.LogWarning(
                "Provisioning execution {ExecutionId} ownership was recovered by {Actor}: {Reason}. "
                + "Staleness {Staleness}, silent for {Silence}, grace {Grace}. Lease moved from "
                + "{BeforeOwner}/{BeforeToken} to {AfterOwner}/{AfterToken}; state {BeforeState} -> "
                + "{AfterState}. Recovery {RecoveryCount} for this execution.",
                execution.Id, actor, reason, staleness, assessment.SilentFor, grace,
                before.LeaseOwner ?? "(none)", before.LeaseToken, after.LeaseOwner ?? "(none)",
                after.LeaseToken, before.State, after.State, execution.RecoveredAttemptCount);

            return new ProvisioningRecoveryResult(
                ProvisioningRecoveryOutcome.Recovered, assessment, before, after);
        });

        // Only after the commit, and only when ownership actually moved: the point of the nudge is
        // that the console's next poll already shows the resumed execution in flight.
        if (result.Outcome == ProvisioningRecoveryOutcome.Recovered && transferTo is null)
            _signal.Poke();

        return result;
    }

    // ---- assessment ---------------------------------------------------------------------------

    private async Task<ProvisioningRecoveryAssessment> AssessAsync(
        ProvisioningExecution execution, DateTime utcNow, CancellationToken ct)
    {
        var grace = ProvisioningLeaseRules.GraceFor(_options.CurrentValue);
        var staleness = ProvisioningLeaseRules.Evaluate(execution, utcNow, grace);

        var byCode = execution.Steps.ToDictionary(step => step.StepCode, StringComparer.Ordinal);
        var completed = ProvisioningStepCodes.Ordered
            .Where(code => byCode.TryGetValue(code, out var step)
                           && step.Status is ProvisioningStepStatus.Succeeded
                               or ProvisioningStepStatus.Skipped)
            .ToList();

        // The step a resume would restart at — the FIRST that has not reached a terminal success,
        // which is the same walk the runner performs. Stated here so the operator authorising the
        // recovery can see where the work will pick up before they authorise it.
        var firstIncomplete = ProvisioningStepCodes.Ordered
            .FirstOrDefault(code => !byCode.TryGetValue(code, out var step)
                                    || step.Status is not (ProvisioningStepStatus.Succeeded
                                        or ProvisioningStepStatus.Skipped));

        return new ProvisioningRecoveryAssessment(
            execution.Id,
            execution.Slug,
            staleness,
            ProvisioningLeaseRules.SilenceAt(execution, utcNow),
            grace,
            ProvisioningOwnershipSnapshot.Of(execution),
            firstIncomplete,
            completed,
            await FindInconsistenciesAsync(execution, byCode, ct));
    }

    /// <summary>
    /// Everything that does not add up, reported and never silently repaired.
    ///
    /// <para><b>Why detection and not correction.</b> Each of these has a correct answer that this
    /// service is not entitled to choose. Whether a tenant becomes <c>Active</c> is an activation
    /// decision with its own authority and its own audit; whether a duplicated slug should be
    /// released is a commercial question about which customer owns an address. Recovering a lease
    /// is a mechanical act, and an operation that quietly also decided a customer's status while
    /// it was in there would be the most dangerous thing on this class.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> FindInconsistenciesAsync(
        ProvisioningExecution execution,
        IReadOnlyDictionary<string, ProvisioningStep> byCode,
        CancellationToken ct)
    {
        var findings = new List<string>();

        // ---- tenant status against provisioning state ----
        if (execution.TenantId is long tenantId)
        {
            var tenant = await _db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.Id, t.Status, t.PrimaryBusinessUnitId })
                .FirstOrDefaultAsync(ct);

            if (tenant is null)
                findings.Add(
                    $"Tenant {tenantId} was recorded by an earlier step but no longer exists. This "
                    + "execution cannot be resumed — the steps after the tenant step would attach "
                    + "rows to a ghost. Cancel it and submit again.");
            else
            {
                if (execution.State != ProvisioningExecutionState.Succeeded
                    && tenant.Status == TenantStatus.Active)
                    findings.Add(
                        $"Tenant {tenantId} is Active while its provisioning execution is "
                        + $"{execution.State}. A live tenant over an unfinished provision is the exact "
                        + "state TenantStatus.Provisioning exists to prevent; the activation authority "
                        + "owns the correction, not this service.");

                if (execution.State == ProvisioningExecutionState.Succeeded
                    && tenant.Status == TenantStatus.Provisioning)
                    findings.Add(
                        $"Tenant {tenantId} is still Provisioning although execution {execution.Id} "
                        + "succeeded. Provisioning completion is evidence for the activation decision "
                        + "and does not make it, so this is expected until that decision is taken — "
                        + "reported so nobody reads it as a stuck run.");

                if (execution.ProvisionedBusinessUnitId is long provisionedBusinessUnitId
                    && tenant.PrimaryBusinessUnitId is not null
                    && tenant.PrimaryBusinessUnitId != provisionedBusinessUnitId)
                    findings.Add(
                        $"Tenant {tenantId} points at business unit {tenant.PrimaryBusinessUnitId} but "
                        + $"this execution created {provisionedBusinessUnitId}. Resuming would seed the "
                        + "one it created, which is not the one the tenant resolves through.");
            }
        }
        else if (execution.State == ProvisioningExecutionState.Succeeded)
            findings.Add(
                $"Execution {execution.Id} is Succeeded but recorded no tenant id.");

        // ---- a rival attempt on the same address ----
        // The UX_ProvisioningExecutions_LiveSlug index makes this impossible on PostgreSQL. It is
        // checked anyway because "impossible" is a property of an index that a deployment can be
        // missing, and a resume that races a second live execution for one slug would build two
        // tenants under one address.
        var rival = await _db.Set<ProvisioningExecution>().AsNoTracking()
            .Where(e => e.Id != execution.Id
                        && e.Slug == execution.Slug
                        && (e.State == ProvisioningExecutionState.Pending
                            || e.State == ProvisioningExecutionState.Running
                            || e.State == ProvisioningExecutionState.Failed))
            .Select(e => new { e.Id, e.State })
            .FirstOrDefaultAsync(ct);
        if (rival is not null)
            findings.Add(
                $"Execution {rival.Id} ({rival.State}) is also live for workspace address "
                + $"'{execution.Slug}'. Resuming both would provision one address twice; the "
                + "UX_ProvisioningExecutions_LiveSlug index is missing from this database.");

        // ---- a step marked failed whose work is already on the ground ----
        foreach (var (code, id) in new (string Code, long? Id)[]
                 {
                     (ProvisioningStepCodes.Tenant, execution.TenantId),
                     (ProvisioningStepCodes.BusinessUnit, execution.ProvisionedBusinessUnitId),
                     (ProvisioningStepCodes.FoundingRole, execution.FoundingRoleId),
                     (ProvisioningStepCodes.FoundingAdmin, execution.FoundingUserId),
                     (ProvisioningStepCodes.Invitation, execution.InvitationId)
                 })
        {
            if (id is null || !byCode.TryGetValue(code, out var step))
                continue;
            if (step.Status is ProvisioningStepStatus.Failed or ProvisioningStepStatus.Running)
                findings.Add(
                    $"Step '{code}' is {step.Status} but recorded id {id}, so its side effect "
                    + "committed. A resume reconciles this by probing rather than by re-running it.");
        }

        return findings;
    }

    private async Task WriteAuditAsync(
        ProvisioningExecution execution,
        ProvisioningRecoveryCommand command,
        ProvisioningRecoveryAssessment assessment,
        ProvisioningLeaseStaleness staleness,
        ProvisioningOwnershipSnapshot before,
        ProvisioningOwnershipSnapshot after,
        string? transferTo,
        TimeSpan grace,
        CancellationToken ct)
    {
        // PlatformAuditService enforces a positive-actor invariant on success records — there is no
        // anonymous privileged mutation — so a recovery whose caller carried no resolvable platform
        // user id is recorded as a FAILURE-shaped row against the reserved system actor rather than
        // being fabricated against a real operator or silently dropped. The ownership change itself
        // has already happened; what varies is how honestly it can be attributed.
        var claims = new List<Claim> { new("email", command.Actor ?? "platform") };
        if (command.ActorPlatformUserId is long actorId && actorId > 0)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, actorId.ToString()));
        else
            _log.LogWarning(
                "Provisioning execution {ExecutionId} was recovered by a caller with no resolvable "
                + "platform user id; the audit record is attributed to the reserved system actor.",
                execution.Id);

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, "ProvisioningLeaseRecovery"));

        await _audit.WriteAsync(principal, ProvisioningAuditActions.LeaseRecovered,
            nameof(ProvisioningExecution), execution.Id.ToString(),
            new
            {
                executionId = execution.Id,
                execution.CorrelationId,
                execution.Slug,
                reason = command.Reason,
                disposition = transferTo is null ? "released-to-queue" : "transferred",
                transferredTo = transferTo,
                staleness = staleness.ToString(),
                silentForSeconds = (long)assessment.SilentFor.TotalSeconds,
                graceSeconds = (long)grace.TotalSeconds,
                // The evidence that makes this a remediation record rather than a claim that one
                // happened: both halves of the ownership, side by side, in one immutable row.
                before = new
                {
                    before.State, before.LeaseOwner, before.LeaseToken, before.LeaseUntil,
                    before.LeaseHeartbeatAt, before.CurrentStep, before.FailedStep,
                    before.AttemptCount, before.RecoveredAttemptCount, before.Version
                },
                after = new
                {
                    after.State, after.LeaseOwner, after.LeaseToken, after.LeaseUntil,
                    after.LeaseHeartbeatAt, after.CurrentStep, after.FailedStep,
                    after.AttemptCount, after.RecoveredAttemptCount, after.Version
                },
                // Proof that the resume runs under the ORIGINAL request identity. Recorded on both
                // sides so a reader does not have to take the word of the side that changed.
                idempotencyPreserved = string.Equals(
                    before.IdempotencyKey, after.IdempotencyKey, StringComparison.Ordinal),
                fingerprintPreserved = string.Equals(
                    before.RequestFingerprint, after.RequestFingerprint, StringComparison.Ordinal),
                resumesAtStep = assessment.FirstIncompleteStep,
                completedSteps = assessment.CompletedSteps,
                findings = assessment.Findings
            },
            actAsTenantId: execution.TenantId,
            httpContext: null,
            result: command.ActorPlatformUserId is > 0
                ? PlatformAuditResults.Success
                : PlatformAuditResults.Failure,
            ct: ct);
    }
}

/// <summary>The audit action names this module writes, so a query can find the whole story.</summary>
public static class ProvisioningAuditActions
{
    /// <summary>Ownership taken from an abandoned attempt. Carries before/after ownership state.</summary>
    public const string LeaseRecovered = "provisioning.lease.recovered";

    /// <summary>A step probed and found already done, so the resume did not re-run it.</summary>
    public const string StepReconciled = "provisioning.step.reconciled";
}

internal static class ProvisioningLeaseStalenessExtensions
{
    public static bool IsRecoverableStaleness(this ProvisioningLeaseStaleness staleness)
        => staleness is ProvisioningLeaseStaleness.Abandoned
            or ProvisioningLeaseStaleness.OwnershipResidue;
}

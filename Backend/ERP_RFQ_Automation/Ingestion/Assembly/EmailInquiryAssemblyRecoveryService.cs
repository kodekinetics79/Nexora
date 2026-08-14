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
    TimeSpan Duration)
{
    public static readonly EmailInquiryRecoverySweepResult Skipped =
        new(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);
}

public interface IEmailInquiryAssemblyRecoveryService
{
    Task<EmailInquiryRecoverySweepResult> SweepOnceAsync(CancellationToken ct = default);
}

/// <summary>
/// Finishes messages whose parts all completed but whose Lead was never built.
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
        var tenants = await ResolveTenantsWithStrandedMessagesAsync(ct);

        var candidates = 0;
        var recovered = 0;
        var alreadyCompleted = 0;
        var heldForReview = 0;
        var stillRecoverable = 0;
        var unexpected = 0;
        var failed = 0;

        foreach (var businessUnitId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tenantResult = await SweepTenantAsync(businessUnitId, ct);
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
            stillRecoverable, unexpected, failed, duration);

        // Logged at Information only when it did something, so an idle platform does not emit a
        // line every two minutes that operators learn to scroll past.
        if (candidates > 0 || failed > 0)
        {
            _log.LogInformation(
                "Email inquiry assembly recovery swept {Tenants} tenant(s) in {DurationMs}ms: "
                + "{Candidates} candidate(s), {Recovered} recovered, {AlreadyCompleted} already "
                + "complete, {HeldForReview} held for review, {StillRecoverable} still "
                + "recoverable, {Unexpected} in an unexpected state, {Failed} failed.",
                result.TenantsSwept, (long)duration.TotalMilliseconds, result.Candidates,
                result.Recovered, result.AlreadyCompleted, result.HeldForReview,
                result.StillRecoverable, result.Unexpected, result.Failed);
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
    private async Task<IReadOnlyList<long>> ResolveTenantsWithStrandedMessagesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var cutoff = _time.GetUtcNow() - _options.ValidatedMinimumAge;

        var tenants = await context.EmailInquiryAssemblies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Status == EmailInquiryAssemblyStatus.ReadyForAssembly
                        && a.AssembledLeadId == null
                        && a.UpdatedAtUtc <= cutoff)
            .Select(a => a.BusinessUnitId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        if (tenants.Count == 0) return tenants;

        // Resolved from this scope rather than injected: the sweep is consumed by a singleton
        // worker and the gate is scoped, so a constructor dependency would fail scope validation
        // at startup.
        var gate = scope.ServiceProvider.GetService<ITenantWorkGate>();
        return gate is null ? tenants : await gate.FilterServiceableAsync(tenants, ct);
    }

    private async Task<EmailInquiryRecoverySweepResult> SweepTenantAsync(
        long businessUnitId, CancellationToken ct)
    {
        // The push precedes the scope: ITenantContext captures the ambient tenant in its
        // CONSTRUCTOR, so a scope created first resolves a DbContext that believes in no tenant
        // whatever is pushed afterwards.
        using var tenant = _tenantScope.Push(businessUnitId);
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // FAIL CLOSED. Without a resolved tenant the connection routes to the BYPASSRLS pipeline
        // role AND the EF filters (`CurrentTenantId == null || ...`) become no-ops — both
        // isolation layers off at once, on a sweep that enumerates every tenant on the platform.
        // Refusing this tenant is caught by the per-tenant handler and the sweep continues.
        if (context.ScopedTenantId != businessUnitId)
        {
            throw new InvalidOperationException(
                $"Email inquiry assembly recovery refused to run for business unit {businessUnitId}: "
                + $"the DbContext resolved tenant {context.ScopedTenantId?.ToString() ?? "<none>"}. "
                + "Tenant scope is mandatory for this sweep.");
        }

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
                        && a.UpdatedAtUtc <= cutoff)
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

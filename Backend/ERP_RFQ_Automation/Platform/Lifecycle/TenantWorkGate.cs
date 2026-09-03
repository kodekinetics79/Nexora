using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// Answers, for a background path, the question the HTTP pipeline already answers for a request:
/// <i>may this business unit consume platform resources right now?</i>
///
/// <para><b>Why this exists.</b> Suspension was enforced by
/// <c>TenantStatusGuardMiddleware</c> and <c>AuthRepository</c> — both on the request path, and
/// neither of which a timer loop passes through. The consequence was that "suspended" meant
/// "cannot sign in", not "costs nothing": a suspended tenant's mailbox kept being polled and its
/// documents kept reaching an inference endpoint that bills per token, its quote PDFs kept being
/// emailed to ITS customers, and its SLA alerts kept going out. Non-payment is the commonest
/// reason for suspension, so the tenant least likely to pay was the one still spending.</para>
///
/// <para><b>Why a gate rather than a rule in each worker.</b> Every worker selects its tenants
/// differently — distinct business units on Leads, directory names under <c>Uploads/Tenants</c>,
/// every row in <c>BusinessUnits</c>, whatever has an unclaimed outbox row. A status predicate
/// written into each of those queries is six chances to write it differently, and the one written
/// differently is the one nobody notices.</para>
///
/// <para><b>Deliberately the same predicate as everywhere else.</b>
/// <see cref="TenantAccessSnapshot.IsAccessDenied"/> — Provisioning, PastDue, Suspended or Archived. Not a second opinion
/// about what suspension means: a background path blocking a different set of tenants from the
/// request path produces a tenant that can use the API and whose jobs never run, or the reverse.
/// Offboarding tenants need no further clause because they stay <see cref="TenantStatus.Archived"/>
/// for the whole of the offboarding path — see <see cref="TenantOffboardingStage"/>.</para>
///
/// <para><b>Fail open, on purpose.</b> A business unit with no platform tenant row (legacy) and an
/// unreadable platform plane both mean "carry on". A background sweep that stops working because
/// the control plane hiccuped is a worse outage than the leak it was closing, and this inherits
/// that contract from <see cref="ITenantAccessService"/> rather than restating it.</para>
///
/// <para><b>CALL THIS OUTSIDE A PUSHED TENANT SCOPE.</b> Not a style note — a correctness one, and
/// the one way this component fails silently. <see cref="ITenantAccessService"/> resolves the
/// tenant by reading <c>platform."Tenants"</c> through the ambient DbContext, and
/// <c>TenantRlsCommandInterceptor</c> routes a scoped context to <c>nexora_tenant_app</c>, which
/// holds only COLUMN-level SELECT there. The read then fails with 42501, lands in the service's
/// fail-open catch, and every tenant is reported serviceable — the gate would be off, and nothing
/// would say so. Called with no ambient tenant the same context routes to
/// <c>nexora_pipeline_app</c>, which can read the platform schema. Every call site therefore sits
/// in the worker's "resolve the tenant list" step, before the per-tenant
/// <see cref="ITenantScopeAccessor.Push"/>, and <see cref="TenantWorkGate"/> logs loudly if it is
/// ever called anywhere else.</para>
/// </summary>
public interface ITenantWorkGate
{
    /// <summary>
    /// Whether background work may run for one business unit. True for a business unit the
    /// platform plane cannot resolve — the contracted fail-open.
    /// </summary>
    Task<bool> MayConsumeResourcesAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// The subset of a worker's candidate business units that may be worked. The shape most
    /// workers need: they already build a list of tenants to sweep, and the fix is to filter it
    /// once rather than re-derive the predicate inside their selection query.
    /// </summary>
    Task<IReadOnlyList<long>> FilterServiceableAsync(
        IEnumerable<long> businessUnitIds, CancellationToken ct = default);
}

/// <summary>
/// The stricter lifecycle boundary for inbound mailboxes.
///
/// <para>The general worker gate deliberately admits legacy business units that have no platform
/// Tenant row. A mailbox cannot use that compatibility rule: an orphaned mailbox still contains
/// a live external credential and will keep authenticating forever after its former tenant has
/// been retired. Only a business unit linked to a serviceable platform Tenant is eligible for
/// network polling. An orphan stays in the database for evidence/audit and operator remediation,
/// but it cannot make an external connection or poison readiness.</para>
/// </summary>
public interface IMailboxTenantWorkGate
{
    Task<IReadOnlyList<long>> FilterPollableAsync(
        IEnumerable<long> businessUnitIds, CancellationToken ct = default);

    /// <summary>
    /// Authoritative single-tenant fence for the instant immediately before external mailbox
    /// authentication. This deliberately bypasses the ordinary lifecycle cache.
    /// </summary>
    Task<bool> MayPollFreshAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Authoritative eligibility refresh for process-local readiness ledgers on instances that
    /// may be standing by rather than producing their own poll report.
    /// </summary>
    Task<IReadOnlyList<long>> FilterPollableFreshAsync(
        IEnumerable<long> businessUnitIds, CancellationToken ct = default);
}

/// <inheritdoc cref="IMailboxTenantWorkGate"/>
public sealed class MailboxTenantWorkGate(
    ITenantAccessService access,
    ILogger<MailboxTenantWorkGate> logger) : IMailboxTenantWorkGate
{
    /// <summary>
    /// Orphan business units this process has already warned about. The refusal is re-evaluated
    /// every poll (once a minute per mailbox) and the situation does not change on its own, so
    /// the warning is stated once per unit per process and thereafter at Debug: an operator
    /// still finds it, and it stops being the loudest line in the log.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> OrphansAnnounced = new();

    public async Task<IReadOnlyList<long>> FilterPollableAsync(
        IEnumerable<long> businessUnitIds, CancellationToken ct = default)
        => await FilterAsync(businessUnitIds, fresh: false, ct);

    public async Task<bool> MayPollFreshAsync(long businessUnitId, CancellationToken ct = default)
        => IsPollable(await access.GetFreshAccessAsync(businessUnitId, ct), businessUnitId);

    public async Task<IReadOnlyList<long>> FilterPollableFreshAsync(
        IEnumerable<long> businessUnitIds, CancellationToken ct = default)
        => await FilterAsync(businessUnitIds, fresh: true, ct);

    private async Task<IReadOnlyList<long>> FilterAsync(
        IEnumerable<long> businessUnitIds, bool fresh, CancellationToken ct)
    {
        var candidates = businessUnitIds.Distinct().ToList();
        var admitted = new List<long>(candidates.Count);
        foreach (var businessUnitId in candidates)
        {
            var snapshot = fresh
                ? await access.GetFreshAccessAsync(businessUnitId, ct)
                : await access.GetAccessAsync(businessUnitId, ct);
            if (IsPollable(snapshot, businessUnitId))
                admitted.Add(businessUnitId);
        }

        return admitted;
    }

    private bool IsPollable(TenantAccessSnapshot snapshot, long businessUnitId)
    {
        if (snapshot.HasTenant && !snapshot.IsAccessDenied)
            return true;

        if (!snapshot.HasTenant && !snapshot.IsUnresolvable)
        {
            logger.Log(
                OrphansAnnounced.TryAdd(businessUnitId, 0) ? LogLevel.Warning : LogLevel.Debug,
                "Mailbox polling refused for orphan business unit {BusinessUnitId}: no platform "
                + "tenant owns it. Deactivate the stale mailbox or deliberately link the "
                + "business unit to an active tenant before polling can resume.",
                businessUnitId);
        }
        else
        {
            logger.LogInformation(
                "Mailbox polling deferred for business unit {BusinessUnitId}: tenant access is {Status}.",
                businessUnitId,
                snapshot.IsUnresolvable ? "unresolvable" : snapshot.Status?.ToString() ?? "unavailable");
        }

        return false;
    }
}

/// <inheritdoc cref="ITenantWorkGate"/>
public sealed class TenantWorkGate(
    ITenantAccessService access,
    ILogger<TenantWorkGate> logger,
    ITenantScopeAccessor? tenantScope = null)
    : ITenantWorkGate
{
    public async Task<bool> MayConsumeResourcesAsync(long businessUnitId, CancellationToken ct = default)
    {
        WarnIfCalledInsideATenantScope();

        var snapshot = await access.GetAccessAsync(businessUnitId, ct);
        if (!snapshot.IsAccessDenied) return true;

        // Information, not Warning: a suspended tenant being skipped is the system working. It is
        // logged at all because "why did this tenant stop being processed" would otherwise be
        // answerable only by reading source.
        logger.LogInformation(
            "Skipping background work for business unit {BusinessUnitId}: its tenant is {Status}.",
            businessUnitId, snapshot.Status);
        return false;
    }

    public async Task<IReadOnlyList<long>> FilterServiceableAsync(
        IEnumerable<long> businessUnitIds, CancellationToken ct = default)
    {
        WarnIfCalledInsideATenantScope();

        var candidates = businessUnitIds.Distinct().ToList();
        var serviceable = new List<long>(candidates.Count);
        foreach (var businessUnitId in candidates)
            if (await MayConsumeResourcesAsync(businessUnitId, ct))
                serviceable.Add(businessUnitId);

        if (serviceable.Count != candidates.Count)
            logger.LogInformation(
                "Tenant work gate admitted {Serviceable} of {Candidates} business unit(s) this cycle.",
                serviceable.Count, candidates.Count);

        return serviceable;
    }

    /// <summary>
    /// An ambient tenant scope means the platform read below will run as
    /// <c>nexora_tenant_app</c>, be refused at column level, and fail OPEN — the gate would be
    /// disabled and the only symptom would be that nothing was ever skipped. Logged at Error
    /// rather than thrown: this component exists to stop a cost leak, and turning a misplaced
    /// call into a crashed background worker would trade a leak for an outage.
    /// </summary>
    private void WarnIfCalledInsideATenantScope()
    {
        if (tenantScope?.BusinessUnitId is not long ambient) return;

        logger.LogError(
            "The tenant work gate was consulted inside a pushed tenant scope (business unit "
            + "{BusinessUnitId}). The platform lookup will run as the RLS tenant role, which holds "
            + "only column-level SELECT on platform.\"Tenants\", so it will fail open and admit "
            + "every tenant. Move the call above the tenant push.",
            ambient);
    }
}

using System.Collections.Concurrent;
using System.Data.Common;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Default <see cref="ITenantAccessService"/>: one platform-schema lookup per
/// BusinessUnit per cache window (~60s), shared process-wide through
/// <see cref="IMemoryCache"/> so per-request middleware never issues a query on the
/// hot path. Registered scoped (it reads through the scoped DbContext) while the
/// cache itself is the singleton.
///
/// <para><b>Every query here is a COLUMN-LEVEL projection, never an entity load.</b>
/// This service runs on the tenant plane: <c>TenantStatusGuardMiddleware</c> executes it
/// as <c>nexora_tenant_app</c> and <c>/api/Auth/Login</c> as <c>nexora_identity_app</c>,
/// and 20260805105320_HardenPlatformGrantsAndBillingImmutability deliberately narrowed
/// both roles from table-level SELECT to a handful of columns so a filter bug on the
/// tenant plane cannot enumerate the customer directory. Loading the <c>Tenant</c> entity
/// selects all 42 of its columns, so PostgreSQL answers 42501 (insufficient privilege)
/// for columns the role was never granted.
///
/// That failure is invisible in two directions at once, which is why it is called out
/// here rather than left to the reader: the catch below is a CONTRACTED FAIL-OPEN, so a
/// 42501 does not surface as an error — it silently disables tenant-suspension
/// enforcement and every plan limit for that business unit; and SQLite has neither roles
/// nor column privileges, so the whole test suite stays green while production runs
/// unenforced. Keep these queries projections. Adding a property to
/// <see cref="TenantAccessSnapshot"/> means adding its column to the grant, not widening
/// the SELECT and hoping.</para>
/// </summary>
public sealed class TenantAccessService : ITenantAccessService
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// PostgreSQL <c>insufficient_privilege</c>. Matched on SqlState so the check does not
    /// take a compile-time dependency on the Npgsql exception type (same approach as the
    /// unique-violation detection in the SLA sweep).
    /// </summary>
    private const string InsufficientPrivilege = "42501";

    /// <summary>
    /// How long a refusal is remembered before the column is tried again.
    ///
    /// <para>The latch exists so that a column this deployment will never grant does not cost
    /// a failed round trip on every cache miss forever. It is BOUNDED because the alternative
    /// — remembering a refusal for the life of the process — turns one transient 42501 into a
    /// permanently disabled capacity floor, recoverable only by a restart, with nothing in the
    /// product saying so. Five minutes: long enough that a permanently ungranted column costs
    /// one failed probe per scope every five minutes (nothing), short enough that a grant
    /// applied by a migration takes effect without anyone redeploying to make it work.</para>
    /// </summary>
    internal static readonly TimeSpan PrivilegeRefusalTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Refusals remembered per (column group, privilege scope), each until its own expiry.
    ///
    /// <para><b>Two column groups, because the two grants are separate decisions.</b>
    /// <c>BillingMode</c> and <c>CreatedOn</c> are an enum label and a timestamp of the same
    /// class as the already-granted <c>Status</c> and <c>PlanId</c>, and 20260807002456 grants
    /// them. <c>BillingModeReason</c> is internal commercial commentary about why a customer is
    /// not charged, of the same class as the <c>StatusReason</c> that 20260805105320
    /// deliberately hid from the tenant plane, and it is deliberately NOT granted. Probing them
    /// together would let the ungranted one veto the granted one: a single 42501 would silence
    /// both and the capacity floor would stay off for plan-missing tenants — the dominant case
    /// — because of a column that only ever mattered to a handful of exempt ones.</para>
    ///
    /// <para><b>Keyed by scope, because a privilege belongs to a role and not to a process.</b>
    /// The tenant plane genuinely cannot read <c>BillingModeReason</c> and refuses every time.
    /// A single process-wide latch would let that permanent, expected refusal also silence the
    /// PIPELINE role — which holds table-level SELECT and can read the column perfectly well —
    /// so the extraction worker's quota checks would lose the floor for unrecorded exemptions
    /// in exactly the configuration the platform ships. One role's answer is not evidence about
    /// another's.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<(PrivilegeProbe Probe, string Scope), DateTimeOffset>
        RefusedUntil = new();

    private enum PrivilegeProbe
    {
        BillingTerms,
        ExemptionReason
    }

    private readonly ErpRfqAutomationContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantAccessService> _log;
    private readonly Hardening.NexoraMetrics? _metrics;
    private readonly TimeProvider _time;

    public TenantAccessService(
        ErpRfqAutomationContext context,
        IMemoryCache cache,
        ILogger<TenantAccessService> log,
        Hardening.NexoraMetrics? metrics = null,
        TimeProvider? time = null)
    {
        _context = context;
        _cache = cache;
        _log = log;
        _metrics = metrics;
        _time = time ?? TimeProvider.System;
    }

    internal static string CacheKey(long businessUnitId) => $"nexora:tenant-access:{businessUnitId}";

    /// <summary>
    /// Teardown seam for fixtures that change grants mid-process. Deliberately NOT the
    /// production recovery path — <see cref="PrivilegeRefusalTtl"/> is. A test that has to
    /// call this to see a restored grant is testing the reset, not the recovery.
    /// </summary>
    internal static void ResetPrivilegeProbes() => RefusedUntil.Clear();

    /// <summary>
    /// Which execution role's privileges this resolution is subject to.
    ///
    /// <para>A conservative proxy for <c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c>,
    /// derived from the one signal available here: a context carrying a tenant scope executes
    /// as <c>nexora_tenant_app</c> (or <c>nexora_identity_app</c> on the login paths, which
    /// holds the same narrowed column grants), and a context without one executes as
    /// <c>nexora_pipeline_app</c> or the owner, both of which hold table-level SELECT. The two
    /// classes are exactly the two privilege surfaces that exist for these columns.</para>
    ///
    /// <para>Being a proxy rather than the role name itself is affordable: a mis-classification
    /// costs at most one extra failed probe per <see cref="PrivilegeRefusalTtl"/>, and reading
    /// the true effective role would mean a <c>current_user</c> round trip on a path whose
    /// whole purpose is to avoid round trips.</para>
    /// </summary>
    private string PrivilegeScope => _context.ScopedTenantId.HasValue ? "tenant-scoped" : "platform";

    private bool IsRefused(PrivilegeProbe probe)
        => RefusedUntil.TryGetValue((probe, PrivilegeScope), out var until)
           && _time.GetUtcNow() < until;

    /// <summary>Records a refusal. Returns true the first time this scope has seen one, for log volume.</summary>
    private bool RememberRefusal(PrivilegeProbe probe)
    {
        var key = (probe, PrivilegeScope);
        var first = !RefusedUntil.ContainsKey(key);
        RefusedUntil[key] = _time.GetUtcNow() + PrivilegeRefusalTtl;
        return first;
    }

    public async Task<TenantAccessSnapshot> GetAccessAsync(long businessUnitId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(businessUnitId), out TenantAccessSnapshot? cached) && cached is not null)
            return cached;

        TenantAccessSnapshot snapshot;
        TimeSpan ttl;
        try
        {
            snapshot = await ResolveAsync(businessUnitId, ct);
            ttl = CacheTtl;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Contracted fail-open: an unavailable platform plane (missing grant,
            // reduced test model, transient outage) must never take the tenant
            // product down. Briefly cached so a broken plane is not hammered.
            // Sec2: logged AND counted — a sustained fail-open rate means status
            // enforcement and plan limits are silently disabled; alert on the metric.
            _log.LogWarning(ex,
                "Tenant access resolution FAILED OPEN for BusinessUnit {BusinessUnitId}: the platform "
                + "plane could not be read, so tenant-status enforcement and plan limits are "
                + "disabled for this BU for {FailureCacheTtlSeconds}s.",
                businessUnitId, FailureCacheTtl.TotalSeconds);
            _metrics?.TenantAccessFailOpen(businessUnitId);
            snapshot = new TenantAccessSnapshot(businessUnitId, null, null, null);
            ttl = FailureCacheTtl;
        }

        _cache.Set(CacheKey(businessUnitId), snapshot, ttl);
        return snapshot;
    }

    /// <summary>
    /// Resolves status + plan (the enforcement-critical half, readable by every execution
    /// role), then the commercial terms and, only where it can change an answer, the
    /// exemption flag.
    ///
    /// <para>The layering is deliberate. Status and plan limits are what actually keep a
    /// suspended customer out and a paid customer inside their quota, and they must never
    /// be held hostage to a privilege the deployment may not have granted. Each further
    /// layer only sharpens the unconfigured-tenant floor, so when a layer is unreadable the
    /// snapshot leaves its fields null and
    /// <see cref="UnplannedTenantAllowance.AppliesTo"/> — which answers "no" to every
    /// unknown — degrades to the previous behaviour instead of the whole resolution
    /// collapsing into a fail-open.</para>
    /// </summary>
    private async Task<TenantAccessSnapshot> ResolveAsync(long businessUnitId, CancellationToken ct)
    {
        var core = await CoreQuery(businessUnitId).FirstOrDefaultAsync(ct);
        if (core is null)
            return new TenantAccessSnapshot(businessUnitId, null, null, null); // legacy BU → fail open

        var snapshot = new TenantAccessSnapshot(businessUnitId, core.TenantId, core.Status, core.Plan);
        snapshot = await AddBillingTermsAsync(snapshot, core.TenantId, ct);

        // The exemption flag decides nothing for a mode that IS charged: for Billable and
        // Trial the floor turns on plan absence alone. Asking anyway would spend a round trip
        // — and a privilege — on an answer CommercialConfigurationRequired never reads, on
        // the overwhelming majority of tenants. A null billing mode also skips it: with the
        // mode unknown there is no state this could resolve.
        if (snapshot.BillingMode is TenantBillingMode.Internal or TenantBillingMode.Partner)
            snapshot = await AddExemptionRecordedAsync(snapshot, core.TenantId, ct);

        return snapshot;
    }

    /// <summary>
    /// Billing mode + provisioning date: what turns the capacity floor on for a tenant with
    /// no plan. Granted by 20260807002456 — an enum label and a timestamp, the same class as
    /// the Status and PlanId already readable here.
    /// </summary>
    private async Task<TenantAccessSnapshot> AddBillingTermsAsync(
        TenantAccessSnapshot snapshot, long tenantId, CancellationToken ct)
    {
        if (IsRefused(PrivilegeProbe.BillingTerms))
            return snapshot;

        try
        {
            var terms = await _context.Set<Tenant>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new BillingTermsProjection(t.BillingMode, t.CreatedOn))
                .FirstOrDefaultAsync(ct);

            return terms is null
                ? snapshot
                : snapshot with { BillingMode = terms.BillingMode, CreatedOn = terms.CreatedOn };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsInsufficientPrivilege(ex))
        {
            // These two ARE granted (20260807002456), so a refusal here is unexpected and
            // stays at Warning — but only the first time per scope, because repeating it every
            // five minutes forever is how a real warning gets filtered out of a log pipeline.
            if (RememberRefusal(PrivilegeProbe.BillingTerms))
                _log.LogWarning(ex,
                    "platform.\"Tenants\".\"BillingMode\"/\"CreatedOn\" are not readable in the {Scope} privilege "
                    + "scope, so the unconfigured-tenant capacity floor is inactive there and a tenant with no plan "
                    + "keeps unlimited seats and documents. Tenant status enforcement and plan limits are UNAFFECTED. "
                    + "The column will be retried in {RetryMinutes} minute(s); grant SELECT on those two columns to "
                    + "nexora_tenant_app and nexora_identity_app to activate the floor.",
                    PrivilegeScope, PrivilegeRefusalTtl.TotalMinutes);
            else
                _log.LogDebug(ex, "Billing-term columns still unreadable in the {Scope} privilege scope.", PrivilegeScope);
            return snapshot;
        }
    }

    /// <summary>
    /// Whether a non-Billable tenant's exemption was written down — as a BOOLEAN, reduced
    /// inside the database so the internal commercial text never crosses the wire.
    ///
    /// <para>The predicate still needs SELECT on <c>BillingModeReason</c>, and that grant is
    /// deliberately withheld: the column is operator commentary about why a customer is not
    /// charged, and the tenant plane has no business reading it. A deployment that decides
    /// otherwise gets the floor for unrecorded exemptions by granting it; one that does not
    /// keeps those tenants uncapped, visible on the platform-plane revenue board and in the
    /// billing run's log, which run under a role that can read the column. That is a bounded
    /// and reported gap, which is the trade this split exists to make possible.</para>
    /// </summary>
    private async Task<TenantAccessSnapshot> AddExemptionRecordedAsync(
        TenantAccessSnapshot snapshot, long tenantId, CancellationToken ct)
    {
        if (IsRefused(PrivilegeProbe.ExemptionReason))
            return snapshot;

        try
        {
            var recorded = await _context.Set<Tenant>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => (bool?)(t.BillingModeReason != null && t.BillingModeReason != ""))
                .FirstOrDefaultAsync(ct);

            return recorded is null ? snapshot : snapshot with { ExemptionRecorded = recorded };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsInsufficientPrivilege(ex))
        {
            // Ungranted on the tenant plane by design, so this is Information rather than
            // Warning — and once per scope, not once per retry window.
            if (RememberRefusal(PrivilegeProbe.ExemptionReason))
                _log.LogInformation(ex,
                    "platform.\"Tenants\".\"BillingModeReason\" is not readable in the {Scope} privilege scope (the "
                    + "expected configuration there). Internal, Partner and other non-Billable tenants whose exemption "
                    + "was never written down keep unlimited seats and documents in that scope; they remain reported by "
                    + "the platform-plane revenue board and the billing run as commercial-configuration-required. "
                    + "Every other limit is unaffected.",
                    PrivilegeScope);
            else
                _log.LogDebug(ex, "Exemption reason still unreadable in the {Scope} privilege scope.", PrivilegeScope);
            return snapshot;
        }
    }

    /// <summary>
    /// Exactly the columns 20260805105320 grants: Tenants(Id, PrimaryBusinessUnitId,
    /// Status, PlanId) and Plans(Id, Code, Weight, MaxConcurrentExtractionJobs,
    /// MaxDocsPerMonth, MaxSeats). Plans.Name is NOT among them, which is why
    /// <see cref="PlanSnapshot"/> identifies a plan by its Code.
    /// </summary>
    private IQueryable<CoreProjection> CoreQuery(long businessUnitId)
        => _context.Set<Tenant>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.PrimaryBusinessUnitId == businessUnitId)
            .OrderBy(t => t.Id)
            .Select(t => new CoreProjection(
                t.Id,
                t.Status,
                t.Plan == null
                    ? null
                    : new PlanSnapshot(
                        t.Plan.Id,
                        t.Plan.Code,
                        t.Plan.Weight,
                        t.Plan.MaxConcurrentExtractionJobs,
                        t.Plan.MaxDocsPerMonth,
                        t.Plan.MaxSeats)));

    internal static bool IsInsufficientPrivilege(Exception exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            if (error is DbException dbError
                && string.Equals(dbError.SqlState, InsufficientPrivilege, StringComparison.Ordinal))
                return true;
            if (error.Message.Contains("permission denied for", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Evict(long businessUnitId) => _cache.Remove(CacheKey(businessUnitId));

    private sealed record CoreProjection(long TenantId, TenantStatus Status, PlanSnapshot? Plan);

    private sealed record BillingTermsProjection(TenantBillingMode BillingMode, DateTime CreatedOn);
}

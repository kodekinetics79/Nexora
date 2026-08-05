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
/// PostgreSQL note: under RLS role routing this query executes as the ambient role.
/// The merged migration must grant SELECT on platform."Tenants"/"Plans" to
/// nexora_tenant_app and nexora_identity_app (Integration Owner contract); until it
/// does, a permission failure lands in the fail-open catch below.
/// </summary>
public sealed class TenantAccessService : ITenantAccessService
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(10);

    private readonly ErpRfqAutomationContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantAccessService> _log;
    private readonly Hardening.NexoraMetrics? _metrics;

    public TenantAccessService(
        ErpRfqAutomationContext context,
        IMemoryCache cache,
        ILogger<TenantAccessService> log,
        Hardening.NexoraMetrics? metrics = null)
    {
        _context = context;
        _cache = cache;
        _log = log;
        _metrics = metrics;
    }

    internal static string CacheKey(long businessUnitId) => $"nexora:tenant-access:{businessUnitId}";

    public async Task<TenantAccessSnapshot> GetAccessAsync(long businessUnitId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(businessUnitId), out TenantAccessSnapshot? cached) && cached is not null)
            return cached;

        TenantAccessSnapshot snapshot;
        TimeSpan ttl;
        try
        {
            var tenant = await _context.Set<Tenant>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(t => t.Plan)
                .Where(t => t.PrimaryBusinessUnitId == businessUnitId)
                .OrderBy(t => t.Id)
                .FirstOrDefaultAsync(ct);

            snapshot = tenant is null
                ? new TenantAccessSnapshot(businessUnitId, null, null, null) // legacy BU → fail open
                : new TenantAccessSnapshot(
                    businessUnitId,
                    tenant.Id,
                    tenant.Status,
                    tenant.Plan is null
                        ? null
                        : new PlanSnapshot(
                            tenant.Plan.Id,
                            tenant.Plan.Code,
                            tenant.Plan.Name,
                            tenant.Plan.Weight,
                            tenant.Plan.MaxConcurrentExtractionJobs,
                            tenant.Plan.MaxDocsPerMonth,
                            tenant.Plan.MaxSeats));
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

    public void Evict(long businessUnitId) => _cache.Remove(CacheKey(businessUnitId));
}

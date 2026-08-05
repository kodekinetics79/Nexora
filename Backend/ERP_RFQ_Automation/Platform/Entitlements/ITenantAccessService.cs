using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Immutable copy of the entitlement-relevant <see cref="Plan"/> fields, so cached
/// snapshots never hold live EF entities.
/// </summary>
public sealed record PlanSnapshot(
    long Id,
    string Code,
    string Name,
    int Weight,
    int MaxConcurrentExtractionJobs,
    int MaxDocsPerMonth,
    int MaxSeats);

/// <summary>
/// The resolved platform-plane view of one BusinessUnit: the owning
/// <see cref="Tenant"/> (matched via <c>PrimaryBusinessUnitId</c>), its lifecycle
/// status and its plan. <see cref="TenantId"/> is null for a legacy BusinessUnit
/// without a platform Tenant row — the contracted fail-open case (no status
/// enforcement, no plan limits).
/// </summary>
public sealed record TenantAccessSnapshot(
    long BusinessUnitId,
    long? TenantId,
    TenantStatus? Status,
    PlanSnapshot? Plan)
{
    public bool HasTenant => TenantId.HasValue;

    /// <summary>Suspended and Archived tenants are denied login + tenant-plane API use.</summary>
    public bool IsAccessDenied => Status is TenantStatus.Suspended or TenantStatus.Archived;
}

/// <summary>
/// Resolves (and memory-caches for ~60s) the platform Tenant + Plan that owns a
/// BusinessUnit. Contracted fail modes (LEDGER):
/// - legacy BU without a platform Tenant row → fail OPEN (no tenant, no limits);
/// - resolution infrastructure failure (missing grant/table on a reduced model) →
///   fail OPEN, logged, briefly cached;
/// - Suspended/Archived tenant → callers must deny.
/// </summary>
public interface ITenantAccessService
{
    Task<TenantAccessSnapshot> GetAccessAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached snapshot for one BusinessUnit so the next resolution re-reads
    /// the platform plane. Called by tenant lifecycle mutations (suspend/resume/
    /// archive/restore, plan change) so enforcement is immediate on the mutating
    /// node; other instances converge within the ~60s TTL (documented bound).
    /// </summary>
    void Evict(long businessUnitId) { }
}

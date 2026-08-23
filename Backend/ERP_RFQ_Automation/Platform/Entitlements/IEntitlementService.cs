namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Outcome of a plan-limit check. <see cref="Limit"/> is null when the tenant has no
/// plan (contracted: no plan → no limit) or no platform Tenant row (fail open).
/// </summary>
public sealed record EntitlementDecision(bool Allowed, int? Limit, int Current, string? Reason)
{
    public static EntitlementDecision Unlimited { get; } = new(true, null, 0, null);

    public static EntitlementDecision Permit(int limit, int current) => new(true, limit, current, null);

    public static EntitlementDecision Deny(int limit, int current, string reason)
        => new(false, limit, current, reason);
}

/// <summary>
/// Plan entitlement checks (seats, monthly document quota, extraction concurrency,
/// WFQ weight), all backed by the same ~60s-cached tenant+plan resolution from
/// <see cref="ITenantAccessService"/>. A BusinessUnit with no Tenant row or a Tenant
/// with no Plan is unlimited (fail open per LEDGER contract).
/// </summary>
public interface IEntitlementService
{
    /// <summary>Deny when the BU's active-user count is already at Plan.MaxSeats.</summary>
    Task<EntitlementDecision> CheckSeatAvailabilityAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Deny when the BU's ExtractionJobs created since the first of the current UTC
    /// month already reach Plan.MaxDocsPerMonth.
    /// </summary>
    Task<EntitlementDecision> CheckDocumentQuotaAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Checks one key from <see cref="TypedEntitlementCatalog"/>. Known capabilities are denied
    /// unless the effective plan explicitly enables them; unknown keys are always denied.
    /// </summary>
    Task<EntitlementDecision> CheckFeatureAsync(
        long businessUnitId, string entitlement, CancellationToken ct = default);

    /// <summary>
    /// The runtime-available catalogue keys this BU's tenant is granted, in catalogue order.
    /// Empty — never a throw — when the platform plane is unresolvable, the BU is ungoverned, or
    /// tenant access is denied: the session bootstrap carries this list, and an outage that only
    /// affects surface presentation must not take login down with it. The client treats an empty
    /// list as "no optional surface", which is the floor, not a wrong answer.
    /// </summary>
    Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(long businessUnitId, CancellationToken ct = default);

    // NOTE (P2-A6): GetConcurrencyCapAsync was removed as dead code — the per-tenant
    // concurrency cap is enforced atomically inside ExtractionQueue.ClaimSql
    // (platform.Tenants → platform.Plans join), never through this service.

    /// <summary>Plan.Weight when a plan exists, else <paramref name="fallbackWeight"/>.</summary>
    Task<double> GetQueueWeightAsync(long businessUnitId, double fallbackWeight, CancellationToken ct = default);
}

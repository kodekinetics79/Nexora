using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Immutable copy of the entitlement-relevant <see cref="Plan"/> fields, so cached
/// snapshots never hold live EF entities.
///
/// <para>A plan is identified here by <see cref="Code"/> and not by its display name.
/// The tenant and identity execution roles hold column-level SELECT on
/// platform."Plans" covering Id/Code/Weight/limits and NOT Name
/// (20260805105320_HardenPlatformGrantsAndBillingImmutability), so reading the name to
/// print it in a quota message would make the whole resolution fail with 42501 — and
/// that failure is swallowed by the contracted fail-open, disabling every limit it was
/// trying to describe. Code is the stable machine identifier and is granted.</para>
/// </summary>
public sealed record PlanSnapshot(
    long Id,
    string Code,
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

    /// <summary>
    /// How the tenant is charged. Init-only rather than positional so existing snapshot
    /// constructions — including the fail-open ones that carry no tenant at all — keep
    /// compiling and keep meaning exactly what they meant.
    ///
    /// <para>Quota enforcement needs it because "no plan" is not one situation but two: a
    /// tenant the operator deliberately exempted, and a tenant somebody forgot to price.
    /// Treating both as unlimited is what let the second kind consume without limit.</para>
    /// </summary>
    public TenantBillingMode? BillingMode { get; init; }

    /// <summary>
    /// When the tenant row was created. Carried because the grace window for a plan-less
    /// tenant is measured from provisioning: a tenant created a minute ago is mid-setup,
    /// a tenant created last quarter with no plan is revenue on the floor.
    /// </summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>
    /// Whether a non-Billable tenant has a written <c>BillingModeReason</c> — a BOOLEAN, never
    /// the text. An exemption nobody wrote down is an exemption nobody decided, and it should
    /// not carry unlimited capacity; but the reason itself is internal commercial free text of
    /// the same class as <c>StatusReason</c>, which the grant hardening deliberately hid from
    /// the tenant plane, so the predicate is evaluated in the database and only its result
    /// crosses the wire.
    ///
    /// <para>Null means "not resolvable under this execution role's grants" and, like every
    /// other unknown here, is treated as no-limit rather than as a denial.</para>
    /// </summary>
    public bool? ExemptionRecorded { get; init; }

    /// <summary>
    /// True when this tenant's commercial configuration is incomplete: Billable or Trial with
    /// no plan, or a non-Billable exemption with nothing written down. Both shapes end in the
    /// same place — a tenant consuming the platform under terms nobody set — which is why they
    /// share one state rather than two adjacent booleans nobody checks together.
    ///
    /// <para>False whenever the inputs are unknown: this predicate can restrict capacity, and
    /// restricting a live customer on the strength of a value we could not read would turn a
    /// missing grant into an outage. That is also the shape of the one deliberate gap — with
    /// <c>BillingModeReason</c> ungranted, <see cref="ExemptionRecorded"/> is null and an
    /// unrecorded exemption is not capped here; it is reported instead by the platform-plane
    /// revenue board, which runs under a role that can read the column.</para>
    ///
    /// <para>The same rule is expressed on the platform plane by
    /// <c>Billing.CommercialConfigurationStates.For(Tenant)</c>, which reads the tenant entity
    /// directly. Two expressions because the two planes have different inputs, not different
    /// rules — change one and the other has to move with it.</para>
    /// </summary>
    public bool CommercialConfigurationRequired
        => BillingMode switch
        {
            TenantBillingMode.Billable or TenantBillingMode.Trial => Plan is null,
            TenantBillingMode.Internal or TenantBillingMode.Partner => ExemptionRecorded == false,
            _ => false
        };
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

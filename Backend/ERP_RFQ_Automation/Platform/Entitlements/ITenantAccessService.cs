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
    int MaxSeats,
    string Features = "{}");

/// <summary>
/// Whether the platform plane actually ANSWERED the question, as distinct from what it answered.
///
/// <para>These are not two shades of the same thing. <see cref="Resolved"/> with a null
/// <see cref="TenantAccessSnapshot.TenantId"/> is a fact: the platform plane was read and this
/// BusinessUnit has no Tenant row (a legacy BU), so there is no status to enforce and no plan to
/// limit. <see cref="Unresolvable"/> is the ABSENCE of a fact: the read failed — a missing
/// column grant, a dropped connection, a reduced model — and NOTHING is known about this
/// tenant's status or plan.</para>
///
/// <para>Sec-D1: the two used to be the same value. A <c>catch (Exception)</c> answered every
/// failure with the legacy-BU snapshot, so a single <c>42501</c> on the platform read — exactly
/// what a deployment sitting between 20260805105320 (which narrowed the tenant plane to
/// column-level SELECT) and 20260808163605 (which granted <c>Plans."Features"</c>, projected by
/// <c>CoreQuery</c>) answers on every request — read as "no suspension, no plan limits" for every
/// tenant, re-decided every 10 seconds, invisible to a SQLite test lane that has neither roles
/// nor column privileges. An unknown is now a denial (503), not a permission.</para>
/// </summary>
public enum TenantAccessResolution
{
    /// <summary>The platform plane answered. The snapshot's fields are the answer.</summary>
    Resolved,

    /// <summary>The platform plane could not be read. Nothing in the snapshot is known.</summary>
    Unresolvable
}

/// <summary>
/// The resolved platform-plane view of one BusinessUnit: the owning
/// <see cref="Tenant"/> (matched via <c>PrimaryBusinessUnitId</c>), its lifecycle
/// status and its plan. <see cref="TenantId"/> is null for a legacy BusinessUnit
/// without a platform Tenant row — resolved, but with no tenant to enforce against.
///
/// <para>A snapshot whose <see cref="Resolution"/> is
/// <see cref="TenantAccessResolution.Unresolvable"/> carries NO facts at all and denies
/// (see <see cref="IsAccessDenied"/> and <see cref="IsUnresolvable"/>).</para>
/// </summary>
public sealed record TenantAccessSnapshot(
    long BusinessUnitId,
    long? TenantId,
    TenantStatus? Status,
    PlanSnapshot? Plan)
{
    public bool HasTenant => TenantId.HasValue;

    /// <summary>
    /// Whether the platform plane answered at all. Defaults to
    /// <see cref="TenantAccessResolution.Resolved"/> so every existing construction — including
    /// the legacy-BU one and every test double — keeps meaning exactly what it meant; only
    /// <see cref="Unresolved"/> produces the other value.
    /// </summary>
    public TenantAccessResolution Resolution { get; init; } = TenantAccessResolution.Resolved;

    /// <summary>Why the platform plane could not be read. Null unless <see cref="IsUnresolvable"/>.
    /// Operator-facing text only — never the underlying exception, which stays in the log.</summary>
    public string? UnresolvedReason { get; init; }

    public bool IsUnresolvable => Resolution == TenantAccessResolution.Unresolvable;

    /// <summary>
    /// The snapshot for "the platform plane could not be read". Every field stays null because
    /// none of them is known; <see cref="IsAccessDenied"/> is true, so every caller that already
    /// asks that question denies without needing to learn a new one.
    /// </summary>
    public static TenantAccessSnapshot Unresolved(long businessUnitId, string reason)
        => new(businessUnitId, null, null, null)
        {
            Resolution = TenantAccessResolution.Unresolvable,
            UnresolvedReason = reason
        };

    /// <summary>
    /// Provisioning tenants have not passed the authoritative activation policy and therefore
    /// cannot receive a tenant token, call tenant APIs, or consume worker resources. Past-due,
    /// Suspended and Archived tenants are likewise restricted.
    ///
    /// <para>An UNRESOLVABLE snapshot denies too, and it is deliberately folded into this same
    /// predicate rather than added as a second question every caller has to remember to ask: the
    /// eight call sites that already gate on <c>IsAccessDenied</c> — the status guard middleware,
    /// login, the background work gate — become fail-closed by that fact alone. Callers that need
    /// to tell an unknown apart from a suspension (to answer 503 rather than 403) read
    /// <see cref="IsUnresolvable"/>.</para>
    /// </summary>
    public bool IsAccessDenied => IsUnresolvable
        || Status is TenantStatus.Provisioning or TenantStatus.PastDue
        or TenantStatus.Suspended or TenantStatus.Archived;

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
/// - legacy BU without a platform Tenant row → RESOLVED with no tenant: no status to
///   enforce and no plan to limit, which is a fact about the row and not an error.
///   This is the one remaining allowance and it is bounded by the platform's own
///   provisioning path, which creates a Tenant row for every BU it makes;
/// - resolution infrastructure failure (missing grant/table/connection) → UNRESOLVABLE:
///   fail CLOSED, logged, counted, briefly cached so a broken plane is not hammered and
///   so recovery needs no restart. Callers deny with 503;
/// - PastDue/Suspended/Archived tenant → callers must deny (403).
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

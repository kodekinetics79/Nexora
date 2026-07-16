namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// A customer organization — the first-class isolation + billing + lifecycle
/// boundary that sits ABOVE <c>BusinessUnit</c>. Lives in the (non-RLS'd)
/// platform schema. Provisioning creates the Tenant plus its primary
/// BusinessUnit transactionally. (ADR-0005 §1, §4)
/// </summary>
public class Tenant
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>URL/subdomain-safe identifier. Unique (see WIRING.md index).</summary>
    public string Slug { get; set; } = null!;

    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;

    /// <summary>FK to <see cref="Plan"/>. Nullable until a plan is assigned.</summary>
    public long? PlanId { get; set; }

    /// <summary>
    /// The primary intra-tenant division created during provisioning. Bridges the
    /// new Tenant to the existing <c>BusinessUnit</c> scope until the Phase 0
    /// TenantId backfill lands. (ADR-0005 §1)
    /// </summary>
    public long? PrimaryBusinessUnitId { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public string? ModifiedBy { get; set; }

    /// <summary>Reason captured on the last suspend/resume/archive action.</summary>
    public string? StatusReason { get; set; }

    public Plan? Plan { get; set; }
}

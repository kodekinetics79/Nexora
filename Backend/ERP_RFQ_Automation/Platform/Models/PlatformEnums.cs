namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// Role of a <see cref="PlatformUser"/> on the Platform-Owner control plane.
/// These are platform-plane roles and are entirely independent of tenant RBAC
/// (SetupMaster roles / RolePermission). Stored as a string in the platform
/// schema (see WIRING.md). (ADR-0005 §3)
/// </summary>
public enum PlatformRole
{
    /// <summary>Full control of the platform (tenants, plans, users, impersonation).</summary>
    Owner = 0,

    /// <summary>Support engineer: tenant lifecycle + read-only impersonation.</summary>
    SupportAdmin = 1,

    /// <summary>Billing/plan management and usage/cost visibility.</summary>
    BillingAdmin = 2,

    /// <summary>Cross-tenant read-only observability. No mutations.</summary>
    ReadOnlyOps = 3
}

/// <summary>
/// Lifecycle state of a <see cref="Tenant"/> (customer organization). (ADR-0005 §4)
/// </summary>
public enum TenantStatus
{
    /// <summary>Being created; not yet serviceable.</summary>
    Provisioning = 0,

    /// <summary>Live and serviceable.</summary>
    Active = 1,

    /// <summary>
    /// Commercially delinquent. Tenant-plane access and background consumption are restricted,
    /// but the tenant remains billable and no deletion/offboarding state is created implicitly.
    /// </summary>
    PastDue = 4,

    /// <summary>Access blocked (non-payment / abuse); data retained.</summary>
    Suspended = 2,

    /// <summary>Offboarded; retained for the contractual window before purge.</summary>
    Archived = 3
}

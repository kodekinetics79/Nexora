namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Constants for the hard Platform/Tenant security boundary. (ADR-0005 §3)
/// </summary>
public static class PlatformAuthConstants
{
    /// <summary>Name of the SECOND JWT bearer scheme (distinct from the default tenant scheme).</summary>
    public const string Scheme = "Platform";

    /// <summary>Audience that a platform token MUST carry. A tenant token (audience "RFQ")
    /// fails validation here, and vice-versa — the first of the five gates.</summary>
    public const string Audience = "nexora-platform";

    /// <summary>Value of the <see cref="ScopeClaim"/> on a platform token.</summary>
    public const string PlatformScopeValue = "platform";

    /// <summary>Value of the scope claim on a tenant token (incl. impersonation tokens).</summary>
    public const string TenantScopeValue = "tenant";

    // Claim types
    public const string ScopeClaim = "scope";
    public const string PlatformRoleClaim = "platformRole";
    public const string SessionGenerationClaim = "platformSessionGeneration";

    // Impersonation stamps (carried on the short-lived TENANT token that is minted)
    public const string ActSubClaim = "act_sub";           // acting platform user id
    public const string ImpersonatedClaim = "impersonated"; // "true"
    public const string ReadOnlyClaim = "impersonation_readonly"; // "true"
    public const string ImpersonationReasonClaim = "impersonation_reason";
}

/// <summary>
/// Authorization policy names for the platform plane. <see cref="PlatformScope"/>
/// is the default-deny gate applied to every <c>/api/platform/*</c> endpoint; the
/// others are role sub-policies. (ADR-0005 §3)
/// </summary>
public static class PlatformPolicies
{
    public const string PlatformScope = "PlatformScope";
    public const string Owner = "Platform.Owner";
    public const string TenantAdmin = "Platform.TenantAdmin";   // Owner or SupportAdmin
    public const string Billing = "Platform.Billing";           // Owner or BillingAdmin
    public const string Impersonate = "Platform.Impersonate";   // Owner or SupportAdmin
}

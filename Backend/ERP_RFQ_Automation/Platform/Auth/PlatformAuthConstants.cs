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
    public const string AuthenticationMethodClaim = "amr";
    public const string MfaAuthenticationMethod = "mfa";
    public const string MfaAuthenticatedAtClaim = "mfa_auth_time";

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
    /// <summary>
    /// The gate on every platform endpoint that does actual control-plane work. Since Sec-D2 it
    /// requires <c>amr=mfa</c> as well as <c>scope=platform</c>: a password-only session used to
    /// satisfy this and therefore reached every tenant's record, the entire cross-tenant
    /// privileged audit trail, and per-tenant queue and job rows — all executing under BYPASSRLS.
    /// Mutations always required MFA, so the exposure was disclosure, but it was disclosure of the
    /// whole platform.
    /// </summary>
    public const string PlatformScope = "PlatformScope";

    /// <summary>
    /// The ONLY policy a password-only (not-yet-MFA) platform session satisfies. It exists so that
    /// tightening <see cref="PlatformScope"/> does not lock an operator out of the very endpoints
    /// they need to become MFA-authenticated: read own MFA status, start enrollment, confirm
    /// enrollment, and sign out. It grants nothing else — no tenant data, no audit trail, no
    /// operations.
    ///
    /// <para>An operator who HAS enrolled also satisfies this (it requires no absence of
    /// <c>amr</c>), which is what makes "show me my recovery-code count" work from an ordinary
    /// session.</para>
    /// </summary>
    public const string Enrollment = "Platform.Enrollment";

    public const string Owner = "Platform.Owner";
    public const string TenantAdmin = "Platform.TenantAdmin";   // Owner or SupportAdmin
    public const string Billing = "Platform.Billing";           // Owner or BillingAdmin
    public const string Impersonate = "Platform.Impersonate";   // Owner or SupportAdmin
    public const string Mfa = "Platform.Mfa";
}

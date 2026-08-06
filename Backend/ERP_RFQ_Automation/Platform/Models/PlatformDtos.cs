using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Models;

// ---- Auth ----------------------------------------------------------------

public class PlatformLoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;
}

public class PlatformLoginResponse
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public string PlatformRole { get; set; } = null!;
    public string Token { get; set; } = null!;
    public DateTime ExpiresAtUtc { get; set; }
}

// ---- Tenants -------------------------------------------------------------

public class TenantSummaryDto
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long? PlanId { get; set; }
    public string? PlanCode { get; set; }
    public long? PrimaryBusinessUnitId { get; set; }
    public DateTime CreatedOn { get; set; }
    public string? StatusReason { get; set; }
}

public class ProvisionTenantRequest
{
    [Required]
    public string Name { get; set; } = null!;

    /// <summary>Optional; derived from Name when omitted.</summary>
    public string? Slug { get; set; }

    /// <summary>Optional plan to assign at provisioning time.</summary>
    public long? PlanId { get; set; }

    // ---- founding administrator -------------------------------------------------------
    // Required, deliberately. A tenant without its founding Super Admin is a shell nobody
    // can log into: the customer journey is Platform Admin -> customer account -> that
    // customer's Super Admin -> sub accounts, and provisioning that stops at the shell
    // breaks the journey at its first step. If an operator truly needs an admin-less
    // tenant they are doing something this API should not encourage.

    [Required, EmailAddress, StringLength(320)]
    public string AdminEmail { get; set; } = null!;

    [Required, StringLength(100, MinimumLength = 1)]
    public string AdminFirstName { get; set; } = null!;

    [Required, StringLength(100, MinimumLength = 1)]
    public string AdminLastName { get; set; } = null!;

    /// <summary>
    /// Optional. When omitted, a strong password is GENERATED and returned exactly once in
    /// the provisioning response — it is stored only as a BCrypt hash and can never be
    /// retrieved again. Supplying one is for operators who agree a password with the
    /// customer beforehand.
    /// </summary>
    [StringLength(128, MinimumLength = 8)]
    public string? AdminPassword { get; set; }
}

/// <summary>
/// What provisioning returns. Distinct from <see cref="TenantSummaryDto"/> because it can carry a
/// ONE-TIME generated credential that must never appear on any list/get endpoint.
/// </summary>
public class ProvisionTenantResponse
{
    public TenantSummaryDto Tenant { get; set; } = null!;

    public FoundingAdminDto FoundingAdmin { get; set; } = null!;
}

public class FoundingAdminDto
{
    public long UserId { get; set; }
    public string Email { get; set; } = null!;
    public string RoleName { get; set; } = null!;

    /// <summary>
    /// Present ONLY when the password was generated server-side, and only in this response.
    /// The operator hands it to the customer through a secure channel; the customer changes
    /// it on first login. Null when the operator supplied the password themselves.
    /// </summary>
    public string? GeneratedPassword { get; set; }
}

public class TenantStatusChangeRequest
{
    [Required]
    public string Reason { get; set; } = null!;
}

public class TenantAiPolicyDto
{
    public long BusinessUnitId { get; set; }
    public bool IsEnabled { get; set; }
    public bool ExternalProcessingAllowed { get; set; }
    public string[] AllowedPurposes { get; set; } = [];
    public string? AllowedProvider { get; set; }
    public string? AllowedModel { get; set; }
    public long? MonthlySoftTokenLimit { get; set; }
    public long? MonthlyHardTokenLimit { get; set; }
    public long Version { get; set; }
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

public class UpdateTenantAiPolicyRequest
{
    public bool IsEnabled { get; set; } = true;
    public bool ExternalProcessingAllowed { get; set; }
    public string[] AllowedPurposes { get; set; } = [];
    public string? AllowedProvider { get; set; }
    public string? AllowedModel { get; set; }
    public long? MonthlySoftTokenLimit { get; set; }
    public long? MonthlyHardTokenLimit { get; set; }
    public long Version { get; set; }

    [Required]
    public string Reason { get; set; } = null!;
}

// ---- Impersonation -------------------------------------------------------

public class ImpersonationRequest
{
    [Required]
    public string Reason { get; set; } = null!;
}

public class ImpersonationResponse
{
    public long TenantId { get; set; }
    public long BusinessUnitId { get; set; }
    public string Token { get; set; } = null!;
    public bool ReadOnly { get; set; } = true;
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// The revocable session id — pass to POST /api/platform/impersonation/{jti}/revoke.
    /// Surfaced directly so the console never has to decode the JWT to end a session.
    /// </summary>
    public string Jti { get; set; } = null!;
}

public class ImpersonationSessionDto
{
    public string Jti { get; set; } = null!;
    public long TenantId { get; set; }
    public string? TenantName { get; set; }
    public long ActorPlatformUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Reason { get; set; } = null!;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>"active" | "expired" | "revoked".</summary>
    public string Status { get; set; } = null!;
}

// ---- Tenant plan assignment ----------------------------------------------

public class ChangeTenantPlanRequest
{
    [Required]
    public long PlanId { get; set; }

    public string? Reason { get; set; }
}

// ---- Platform users ------------------------------------------------------

public class PlatformUserDto
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public string PlatformRole { get; set; } = null!;
    public bool IsActive { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime CreatedOn { get; set; }
}

public class CreatePlatformUserRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = null!;

    [Required, MinLength(12)]
    public string Password { get; set; } = null!;

    /// <summary>One of the <see cref="Models.PlatformRole"/> names.</summary>
    [Required]
    public string Role { get; set; } = null!;

    public string? DisplayName { get; set; }
}

public class ChangePlatformUserRoleRequest
{
    /// <summary>One of the <see cref="Models.PlatformRole"/> names.</summary>
    [Required]
    public string Role { get; set; } = null!;
}

public class ResetPlatformUserPasswordRequest
{
    [Required, MinLength(12)]
    public string NewPassword { get; set; } = null!;
}

// ---- Plans ---------------------------------------------------------------

public class UpsertPlanRequest
{
    [Required]
    public string Code { get; set; } = null!;

    [Required]
    public string Name { get; set; } = null!;

    [Range(1, 1000)]
    public int Weight { get; set; } = 1;

    [Range(1, 1000)]
    public int MaxConcurrentExtractionJobs { get; set; } = 2;

    [Range(0, int.MaxValue)]
    public int MaxDocsPerMonth { get; set; } = 1000;

    [Range(0, int.MaxValue)]
    public int MaxSeats { get; set; } = 5;

    [Range(0, 99999999.99)]
    public decimal? MonthlyPriceUsd { get; set; }

    /// <summary>JSON object of feature entitlements; defaults to "{}".</summary>
    public string? Features { get; set; }

    public bool IsActive { get; set; } = true;
}

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
}

public class TenantStatusChangeRequest
{
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
}

using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

public sealed class PlaceTenantLegalHoldRequest
{
    [Required, MinLength(3), MaxLength(64)]
    public string Scope { get; set; } = null!;

    [Required, MinLength(3), MaxLength(128)]
    public string Authority { get; set; } = null!;

    [Required, MinLength(TenantOffboardingService.MinimumDestructionReasonLength), MaxLength(1000)]
    public string Reason { get; set; } = null!;

    [Required, MinLength(3), MaxLength(512)]
    public string EvidenceReference { get; set; } = null!;
}

public sealed class ReleaseTenantLegalHoldRequest
{
    [Required, MinLength(TenantOffboardingService.MinimumDestructionReasonLength), MaxLength(1000)]
    public string Reason { get; set; } = null!;
}

public sealed record TenantLegalHoldDto(
    long Id,
    long TenantId,
    string Scope,
    string Authority,
    string Reason,
    string EvidenceReference,
    DateTime PlacedOn,
    string PlacedBy,
    bool IsActive,
    DateTime? ReleasedOn,
    string? ReleasedBy,
    string? ReleaseReason);


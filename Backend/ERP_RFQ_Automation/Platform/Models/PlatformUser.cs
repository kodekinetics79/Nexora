namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// A Platform-Owner control-plane operator. Deliberately a SEPARATE table and a
/// SEPARATE token from tenant <c>Users</c> — never a super-row in the tenant
/// identity store, and it carries NO tenantId. Authenticated on the dedicated
/// "Platform" JWT scheme (audience <c>nexora-platform</c>). (ADR-0005 §3)
/// </summary>
public class PlatformUser
{
    public long Id { get; set; }

    /// <summary>Login identity. Unique (see WIRING.md index).</summary>
    public string Email { get; set; } = null!;

    /// <summary>BCrypt hash — same hashing scheme as tenant users.</summary>
    public string PasswordHash { get; set; } = null!;

    public PlatformRole PlatformRole { get; set; } = PlatformRole.ReadOnlyOps;

    public bool IsActive { get; set; } = true;

    public string? DisplayName { get; set; }

    public DateTime? LastLogin { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }
}

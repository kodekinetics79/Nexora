namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// One issued support-impersonation ticket, keyed by the token's <c>jti</c>. Written
/// on issue and consulted by <c>ReadOnlyImpersonationMiddleware</c> on every request
/// carrying <c>impersonated=true</c>, which makes individual impersonation tokens
/// revocable before their JWT expiry. Lives in the (non-tenant) "platform" schema.
/// (ADR-0005 §3)
/// </summary>
public class ImpersonationSession
{
    public long Id { get; set; }

    /// <summary>JWT ID (<c>jti</c>) of the minted impersonation token. Unique.</summary>
    public string Jti { get; set; } = null!;

    /// <summary>Tenant the session impersonates into.</summary>
    public long TenantId { get; set; }

    /// <summary>The platform operator who requested the session.</summary>
    public long ActorPlatformUserId { get; set; }

    /// <summary>Mandatory business reason captured at issue time.</summary>
    public string Reason { get; set; } = null!;

    public DateTime IssuedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Set when the session was revoked before its natural expiry.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Email of the platform operator who revoked the session.</summary>
    public string? RevokedBy { get; set; }
}

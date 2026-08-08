namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// Revocable normal control-plane session. This is deliberately separate from
/// <see cref="ImpersonationSession"/>, whose tokens authenticate on the tenant
/// scheme and have a different security boundary.
/// </summary>
public sealed class PlatformSession
{
    public long Id { get; set; }

    public string Jti { get; set; } = null!;

    public long PlatformUserId { get; set; }

    public long SessionGeneration { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Server-side proof backing the token's <c>amr=mfa</c> claim.</summary>
    public DateTime? MfaAuthenticatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? RevokedBy { get; set; }

    public string? RevocationReason { get; set; }

    public PlatformUser PlatformUser { get; set; } = null!;
}

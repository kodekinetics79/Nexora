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

    /// <summary>
    /// The remembered browser this session's second factor came from, when the operator was not
    /// challenged because they had already been challenged on this browser inside its trust window.
    ///
    /// <para>Carried on the session so revoking a browser trust ends the sessions it minted, not
    /// merely the future ones. Revocation that leaves a live privileged session behind is
    /// revocation the operator believes in and the platform has not performed.</para>
    /// </summary>
    public long? BrowserTrustId { get; set; }

    /// <summary>
    /// When the operator last re-entered their password ON THIS SESSION.
    ///
    /// <para>This is the step-up marker for high-risk operations. It lives on the session row rather
    /// than in the token because the token is minted once and this has to move: a re-authentication
    /// two minutes ago must count and one from this morning must not, and only the server can tell
    /// the difference.</para>
    /// </summary>
    public DateTime? LastPasswordReauthAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? RevokedBy { get; set; }

    public string? RevocationReason { get; set; }

    public PlatformUser PlatformUser { get; set; } = null!;
}

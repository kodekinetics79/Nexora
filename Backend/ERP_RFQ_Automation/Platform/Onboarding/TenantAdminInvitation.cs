namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// One issued activation ticket for a tenant's founding Super Administrator.
///
/// <para><b>The handover this replaces.</b> Provisioning used to mint the founding admin with
/// either an operator-typed password or a server-generated one returned in the HTTP response
/// and read aloud to the customer. Three things were wrong with that at once: the operator
/// knew the customer's credential, <c>Models/User.cs</c> carries no must-change-password flag
/// so it was never rotated, and nothing was ever emailed — the credential lived in whatever
/// chat window or notebook the handover happened in. An invitation moves the secret out of
/// the operator's hands entirely: the operator can cause an email to be sent, and can revoke
/// it, but at no point holds anything that logs in.</para>
///
/// <para><b>Only the hash is stored.</b> <see cref="TokenHash"/> is SHA-256 of the cleartext
/// token, which exists in exactly one place for exactly one moment: the return value of
/// <c>ITenantAdminInvitationService.IssueAsync</c>. A dump of this table yields no usable
/// activation link — the same property the <c>Users.PasswordHash</c> column has, for the same
/// reason.</para>
///
/// <para><b>Why the platform schema.</b> Like <c>platform.ImpersonationSessions</c>, this is a
/// control-plane record ABOUT a tenant rather than a record BELONGING to one: it is written
/// during provisioning (before the tenant has a usable identity) and read on a fully anonymous
/// request (the invitee has no token, no session, and no business unit yet). Being outside the
/// public schema and carrying no global query filter, it is deliberately outside the RLS-policy
/// expectation asserted by <c>PostgreSqlProductionDialectTests</c> — the same exemption
/// <c>LoginAttempts</c> takes, and for the same pre-authentication reason.</para>
/// </summary>
public class TenantAdminInvitation
{
    public long Id { get; set; }

    /// <summary>The tenant whose founding administrator this invitation activates.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// The <c>Users</c> row created by provisioning. Redemption sets THIS user's password
    /// hash; the invitation never creates an account, so a stolen link cannot mint one.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// Address the invitation was sent to, recorded as it was at issue time. Kept for the
    /// operator's audit trail and for the masked echo on the activation page — never used to
    /// look an invitation up, because the token is the only accepted credential.
    /// </summary>
    public string Email { get; set; } = null!;

    /// <summary>
    /// The company name as it stood when the invitation was issued, denormalised onto the row.
    ///
    /// <para><b>Why a copy rather than a join.</b> The activation page greets the recipient with
    /// the company that invited them, and it runs as <c>nexora_identity_app</c> — a role whose
    /// access to <c>platform."Tenants"</c> was deliberately narrowed to the handful of columns the
    /// tenant plane needs to enforce suspension and entitlements, so that customer names, slugs
    /// and status reasons stopped being readable from the identity plane at all. Reading the name
    /// at activation time would mean granting that column back and eroding a control that
    /// <c>PlatformControlPlaneHardeningPostgreSqlTests</c> exists to hold.</para>
    ///
    /// <para>Copying it costs one column and is arguably more correct: this is the name that
    /// appeared in the invitation email, so a tenant renamed mid-flight cannot leave the
    /// recipient looking at a company they have never heard of.</para>
    /// </summary>
    public string TenantName { get; set; } = null!;

    /// <summary>
    /// Lowercase hex SHA-256 of the cleartext token. Unique, so the redemption lookup is a
    /// single indexed exact match and two tokens can never collide onto one invitation.
    ///
    /// <para>SHA-256 rather than BCrypt deliberately: the input is 256 bits of CSPRNG output,
    /// not a human-chosen password, so there is no dictionary to slow down — and a work factor
    /// here would make the lookup a table scan.</para>
    /// </summary>
    public string TokenHash { get; set; } = null!;

    public DateTime IssuedAtUtc { get; set; }

    /// <summary>
    /// Hard expiry. A link that never expires is a permanent skeleton key sitting in a mailbox
    /// the customer may not control a year from now.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Set by the single atomic claim that spends the invitation. Null while live.</summary>
    public DateTime? RedeemedAtUtc { get; set; }

    /// <summary>
    /// Client address the redemption came from. The only forensic trace of who actually turned
    /// the invitation into a credential — redemption is anonymous by construction, so there is
    /// no authenticated actor to attribute it to.
    /// </summary>
    public string? RedeemedFromIp { get; set; }

    /// <summary>Set when the invitation was withdrawn before it was used, or superseded by a reissue.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Email of the platform operator who revoked it; the system when superseded.</summary>
    public string? RevokedBy { get; set; }

    public string? RevocationReason { get; set; }

    /// <summary>Platform operator (or "platform") who caused the invitation to be issued.</summary>
    public string IssuedBy { get; set; } = null!;

    /// <summary>
    /// When the invite email was last dispatched, and how many times. Issue and delivery are
    /// separate events — a console-provider deployment issues invitations that were never
    /// actually sent — so an operator debugging "the customer says they got nothing" can tell
    /// the two apart instead of guessing.
    /// </summary>
    public DateTime? LastSentAtUtc { get; set; }

    public int SendCount { get; set; }

    /// <summary>
    /// Convenience for read paths and tests. NOT the redemption guard: redemption re-states
    /// these three conditions inside the WHERE clause of a single UPDATE, because evaluating
    /// them in memory would leave a read-then-write window two concurrent redeems could both
    /// pass through.
    /// </summary>
    public bool IsLiveAt(DateTime utcNow)
        => RedeemedAtUtc is null && RevokedAtUtc is null && ExpiresAtUtc > utcNow;
}

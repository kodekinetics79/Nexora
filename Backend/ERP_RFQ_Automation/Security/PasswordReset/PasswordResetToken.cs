namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// One issued password-reset ticket for a tenant user.
///
/// <para><b>The gap this closes.</b> A tenant user who forgot their password had exactly one
/// route back into the product: ask somebody with database access to overwrite
/// <c>public."Users"."Password_Hash"</c> for them. That is the same defect
/// <see cref="Platform.Onboarding.TenantAdminInvitation"/> was built to end for the FIRST
/// credential — an operator holding a working credential for a customer's account — reappearing
/// at every subsequent one. <c>ActivateAccountPage</c> has been telling users to "use forgot
/// password on the sign-in page" since it shipped; until this table existed, that sentence was
/// not true.</para>
///
/// <para><b>Only the hash is stored.</b> <see cref="TokenHash"/> is SHA-256 of the cleartext
/// token, which exists in exactly one place for exactly one moment: the return value of the
/// mint inside <c>PasswordResetService.RequestResetAsync</c>, on its way into one email. A dump
/// of this table yields no usable reset link.</para>
///
/// <para><b>Why the platform schema.</b> Identical reasoning to
/// <c>platform.TenantAdminInvitations</c>, and it has to be, because it is the identical
/// situation: the row is written on a fully anonymous pre-authentication request (the person
/// asking has no session — that is the whole point) and read on another one, so there is no
/// tenant scope to filter by and no <c>nexora.business_unit_id</c> for an RLS policy to key on.
/// Being outside the public schema and carrying no global query filter keeps it deliberately
/// outside the RLS-policy expectation asserted by <c>PostgreSqlProductionDialectTests</c> — the
/// same exemption <c>LoginAttempts</c> and <c>TenantAdminInvitations</c> take.</para>
///
/// <para><b>What is deliberately NOT on this row.</b> No email address, unlike an invitation.
/// An invitation stores one because an operator's audit screen lists invitations by recipient
/// and because the row outlives nothing else that knows the address. A reset needs the address
/// only while the message is being composed, and the redeem-time password check reads it from
/// <c>public."Users"</c> — the account the token is bound to. The difference matters because
/// <b>an anonymous caller can cause rows here</b>: a table an unauthenticated stranger can
/// append to must not double as a ledger of which addresses were typed into a public form.</para>
/// </summary>
public class PasswordResetToken
{
    public long Id { get; set; }

    /// <summary>
    /// The account this token can set a password for, resolved once at mint time from the
    /// address that was submitted. Redemption sets THIS user's hash and reads the address to
    /// check against from THIS user's row, so a token can never be steered at another account
    /// by anything the redeeming request carries.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// The tenant the account belongs to, when one can be resolved, so an offboarding purge can
    /// find these rows. Nullable, and not a foreign key, for two separate reasons.
    ///
    /// <para>Nullable: it is derived from <c>platform."Tenants".PrimaryBusinessUnitId</c> matching
    /// the user's business unit, which is the shape provisioning creates but not a shape anything
    /// enforces — a seeded demo account or a business unit that predates the control plane
    /// resolves to nothing. A reset must not fail because a tenant row is missing; the account
    /// still exists and its owner still cannot sign in.</para>
    ///
    /// <para>Not a foreign key: <c>TenantPurgeExecutor</c> runs under
    /// <c>session_replication_role = 'replica'</c>, which suspends foreign-key triggers along
    /// with the append-only guards — so a cascade here would not fire and would only create the
    /// illusion of cleanup. The real mechanism is the explicit entry in
    /// <c>PlatformTenantDataMap</c>, which is what makes the deletion visible and reviewable.</para>
    /// </summary>
    public long? TenantId { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the cleartext token. Unique, so the lookup is a single indexed
    /// exact match and two tokens can never collide onto one row.
    ///
    /// <para>SHA-256 rather than BCrypt, for the reason stated on
    /// <c>TenantAdminInvitation.TokenHash</c> and no other: the input is 256 bits of CSPRNG
    /// output, so there is no dictionary for a work factor to slow down — and a work factor here
    /// would turn the lookup into a table scan.</para>
    /// </summary>
    public string TokenHash { get; set; } = null!;

    public DateTime IssuedAtUtc { get; set; }

    /// <summary>
    /// Hard expiry, and short — see <c>TenantOnboardingOptions.ResetLifetime</c> for why an hour
    /// rather than the invitation's three days.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Client address the reset was ASKED FOR from. Anyone can cause this row, so this is the
    /// only trace of who did — the one thing an operator investigating "my customer got a reset
    /// email they did not request" can actually read.
    /// </summary>
    public string? RequestedFromIp { get; set; }

    /// <summary>Set by the single atomic claim that spends the token. Null while live.</summary>
    public DateTime? RedeemedAtUtc { get; set; }

    /// <summary>
    /// Client address the password was actually changed from. Different from
    /// <see cref="RequestedFromIp"/> in the ordinary case (asked for on a phone, opened on a
    /// laptop) and, when it matters, the pair is the evidence that they were not the same person.
    /// </summary>
    public string? RedeemedFromIp { get; set; }

    /// <summary>Set when the token was superseded by a newer request or spent by a sibling.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>The system component that superseded it. There is no operator here to name.</summary>
    public string? RevokedBy { get; set; }

    public string? RevocationReason { get; set; }

    /// <summary>
    /// When the reset email was dispatched, and how many times. Issue and delivery are separate
    /// facts — the default console provider issues resets that were never actually sent — so an
    /// operator debugging "the customer says nothing arrived" can tell the two apart.
    /// </summary>
    public DateTime? LastSentAtUtc { get; set; }

    public int SendCount { get; set; }

    /// <summary>
    /// Convenience for read paths and tests. NOT the guard: completion re-states these three
    /// conditions inside the WHERE clause of a single UPDATE, because evaluating them in memory
    /// would leave a read-then-write window two concurrent completions could both pass through.
    /// </summary>
    public bool IsLiveAt(DateTime utcNow)
        => RedeemedAtUtc is null && RevokedAtUtc is null && ExpiresAtUtc > utcNow;
}

/// <summary>
/// What a presented reset token is worth.
///
/// <para>The vocabulary is deliberately identical to <c>ActivationTokenState</c>, down to the
/// spelling, because both are rendered by pages that speak the same wire contract — a body
/// <c>status</c> plus a status code — and a customer who follows a reset link should not get a
/// differently-worded explanation from the one an activation link gives for the same situation.
/// It is a separate enum rather than a shared one so the reset module does not take a dependency
/// on the onboarding module's public surface for the sake of five names.</para>
/// </summary>
public enum PasswordResetTokenState
{
    /// <summary>Live, unspent, unrevoked, and the account it names still exists.</summary>
    Valid,

    /// <summary>Malformed, unknown, or naming an account that no longer exists. See ResolveAsync.</summary>
    Invalid,

    /// <summary>Genuinely issued, and past its expiry.</summary>
    Expired,

    /// <summary>Genuinely issued, and already spent. A recipient who did not spend it should escalate.</summary>
    Used,

    /// <summary>Genuinely issued, and superseded by a newer request for the same account.</summary>
    Revoked
}

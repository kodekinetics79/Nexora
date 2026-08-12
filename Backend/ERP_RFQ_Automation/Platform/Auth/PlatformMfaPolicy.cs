namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// How hard the platform plane insists on a second factor.
///
/// <para><b>The member names are SCREAMING_SNAKE on purpose.</b> This value is persisted as its
/// NAME, returned on the API, typed into a confirmation box by an operator, and read back out of
/// an audit row years later. Keeping the C# member, the stored string and the wire contract
/// character-identical means a rename cannot silently reclassify a deployment — which is the
/// entire failure mode this enum exists to prevent. The same reasoning already governs
/// <c>PlatformEmailSettings.OutboundGuardMode</c>; this one goes further because the value in
/// question is "is MFA on".</para>
/// </summary>
public enum PlatformMfaMode
{
    /// <summary>Every platform operator must present a second factor. The only mode production
    /// accepts, and the mode every other one decays back into.</summary>
    REQUIRED = 0,

    /// <summary>An operator who HAS enrolled is still challenged; one who has not is not forced to.
    /// Intended for a staging cutover window, never for production.</summary>
    OPTIONAL = 1,

    /// <summary>No second factor is enforced at all. Reachable only on isolated test
    /// infrastructure, never in production, and never without an expiry.</summary>
    DISABLED_TEST_ONLY = 2
}

/// <summary>
/// What KIND of deployment this process is, decided by the backend from
/// <see cref="IHostEnvironment"/> plus one explicit configuration key — never from a request, a
/// header, or anything the console can set.
/// </summary>
public enum PlatformMfaEnvironmentClass
{
    /// <summary>Real customers, real data. <see cref="PlatformMfaMode.REQUIRED"/> only.</summary>
    Production = 0,

    /// <summary>Staging / UAT. REQUIRED or OPTIONAL; DISABLED_TEST_ONLY only when the deployment
    /// has been explicitly classified as isolated test infrastructure.</summary>
    StagingOrUat = 1,

    /// <summary>A developer machine or the automated test lane. All three modes.</summary>
    LocalOrTest = 2
}

/// <summary>
/// The platform's MFA enforcement policy: one row, versioned, audited, and authoritative.
///
/// <para><b>Why it is a row and not a setting.</b> An enforcement switch that lives in
/// <c>appsettings</c> is changed by whoever can edit a file and redeploy, leaves no record of who
/// or why, and — the part that actually bites — is trivially forgotten in the "on" position after
/// a test window closes. Every field below exists to close one of those: <see cref="ChangedBy"/>
/// and <see cref="ChangeReason"/> so the change has an author and a purpose,
/// <see cref="ExpiresAtUtc"/> so it ends without anyone remembering to end it, and
/// <see cref="EnvironmentClass"/> so an audit row read a year later still says which kind of
/// deployment this was.</para>
///
/// <para><b>The row is a DECLARATION, not the effective state.</b> Reading it is not enough:
/// <see cref="PlatformMfaPolicyService"/> computes the effective mode from this row plus the clock
/// plus the environment, so an expired bypass is REQUIRED again on the very next request with no
/// job having run and no human having acted. See <c>PlatformEffectiveMfaPolicy</c>.</para>
///
/// <para><b>Single row by construction</b>, on the same terms as
/// <c>PlatformEmailSettings</c>: the primary key is pinned by a check constraint rather than by
/// convention, because a second row would not be a duplicate — it would be two answers to "is MFA
/// enforced", resolved differently by whichever query ran first.</para>
/// </summary>
public sealed class PlatformMfaPolicy
{
    /// <summary>The only permitted primary key. Enforced by a database check constraint.</summary>
    public const long SingletonId = 1;

    public long Id { get; set; } = SingletonId;

    /// <summary>The DECLARED mode, as the <see cref="PlatformMfaMode"/> name. Never read this
    /// directly to make an authorization decision — read the effective policy, which also applies
    /// the expiry and the environment ceiling.</summary>
    public string Mode { get; set; } = nameof(PlatformMfaMode.REQUIRED);

    /// <summary>When the declared mode took effect. Display and forensics; the effective-mode
    /// computation treats a future date as not yet in force.</summary>
    public DateTime EffectiveFromUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the declared mode stops applying. MANDATORY for anything other than
    /// <see cref="PlatformMfaMode.REQUIRED"/> and bounded by the configured maximum bypass
    /// duration.
    ///
    /// <para>A bypass with no expiry is the defect. It is not that somebody disables MFA — that is
    /// a legitimate thing to do on a test rig — it is that nobody ever turns it back on, and the
    /// only signal is silence.</para>
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The <see cref="PlatformMfaEnvironmentClass"/> name as classified by the BACKEND at
    /// the moment of the change. Stored rather than recomputed so an audit reading of this row does
    /// not depend on how the process happens to be configured today.</summary>
    public string EnvironmentClass { get; set; } = nameof(PlatformMfaEnvironmentClass.Production);

    /// <summary>Email of the platform Owner who last changed this. The full trail is in
    /// <c>platform."PlatformAuditLogs"</c>; this is the copy the screen renders without a second
    /// query, exactly as <c>PlatformEmailSettings.UpdatedBy</c> does.</summary>
    public string? ChangedBy { get; set; }

    public long? ChangedByPlatformUserId { get; set; }

    /// <summary>Why. Required, and length-checked, for every change away from REQUIRED.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// Bumped on every save and used as the optimistic-concurrency token, so two Owners relaxing
    /// enforcement from two tabs cannot silently overwrite each other — the shape that would let
    /// the shorter expiry win invisibly.
    /// </summary>
    public long Version { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set the first time a READ observes that <see cref="ExpiresAtUtc"/> has passed, so the
    /// "MFA bypass expired" audit event is emitted exactly once rather than on every request for
    /// the rest of the deployment's life.
    ///
    /// <para>It is deliberately NOT the thing that makes the expiry take effect. The effective mode
    /// is computed from the clock; this column only debounces the evidence. If it were load-bearing,
    /// a failure to write it would keep a bypass alive.</para>
    /// </summary>
    public DateTime? ExpiryRecordedAtUtc { get; set; }

    /// <summary>
    /// Whether "remember this browser" is offered at all on this platform.
    ///
    /// <para><b>It is enforced at REDEMPTION, not only at issuance.</b> Gating only the minting of new
    /// trusts would mean an Owner who turns this off has turned it off for people who have not used it
    /// yet, and left it running for everyone who has — for up to the full window. Turning it off has
    /// to mean the trusts that already exist stop counting on the very next sign-in, which is what
    /// <c>PlatformBrowserTrustService.RedeemAsync</c> does with it.</para>
    ///
    /// <para>It does not un-trust anything: the rows stay, unrevoked, and switching the control back on
    /// restores whatever window they had left. Deleting them on a policy flip would make an Owner's
    /// half-hour experiment cost every operator a fresh challenge cycle, which is the cost that makes
    /// people leave a control alone.</para>
    /// </summary>
    public bool BrowserTrustEnabled { get; set; } = true;

    /// <summary>
    /// How many hours a remembered browser suppresses a repeat challenge for, between
    /// <see cref="PlatformMfaPolicyOptions.MinBrowserTrustHours"/> and
    /// <see cref="PlatformMfaPolicyOptions.MaxBrowserTrustHours"/> — a database check constraint, not
    /// only a service-layer rule, so a hand-written UPDATE cannot install "remember forever".
    ///
    /// <para><b>Why it moved out of appsettings.</b> It is a customer-set security parameter, and the
    /// same argument that put <c>Mode</c> in a row applies to it unchanged: a value in a deployment
    /// file is changed by whoever can edit that file, leaves no record of who or why, and cannot be
    /// changed at all by the Owner it belongs to. <c>Platform:Mfa:BrowserTrustHours</c> survives as the
    /// SEED for a deployment that has never had a policy row — see
    /// <c>PlatformMfaPolicyOptions.ResolveBrowserTrust</c> for the precedence.</para>
    /// </summary>
    public int BrowserTrustHours { get; set; } = PlatformMfaPolicyOptions.DefaultBrowserTrustHours;
}

/// <summary>
/// The resolved answer to "are remembered browsers honoured here, and for how long" — the row's
/// values, or the configured seed when no row has ever been written.
/// </summary>
/// <param name="FromPolicyRow">True when a stored policy row decided this, false when it came from
/// <c>Platform:Mfa:BrowserTrustHours</c>. Surfaced so the screen can say which, rather than showing a
/// number that an operator will assume they set.</param>
public sealed record PlatformBrowserTrustSettings(bool Enabled, int Hours, bool FromPolicyRow)
{
    public TimeSpan Duration => TimeSpan.FromHours(Hours);
}

/// <summary>
/// A browser an operator has told the platform to remember, so an ordinary day of navigation costs
/// one MFA challenge instead of one per session.
///
/// <para><b>What is stored is a HASH.</b> The raw token is generated once, returned once, and never
/// written down here. Storing the same secret the client holds would mean a read of this table —
/// by a backup, by a support query, by anything with BYPASSRLS — hands out working second factors
/// for every operator at once. <see cref="TokenHash"/> is SHA-256 of the raw token; verification
/// hashes the presented value and looks THAT up, so a stolen table row proves nothing.</para>
///
/// <para><b>Revocation is a column, not a cache eviction.</b> <see cref="RevokedAtUtc"/> is checked
/// on every use, inside the same query that finds the row, so revoking takes effect on the next
/// request rather than on the next cache expiry.</para>
/// </summary>
public sealed class PlatformBrowserTrust
{
    public long Id { get; set; }

    public long PlatformUserId { get; set; }

    /// <summary>SHA-256, hex, of the raw token handed to the browser. 64 characters, fixed.</summary>
    public string TokenHash { get; set; } = null!;

    /// <summary>Operator-readable label for the trust list ("Chrome on macOS"). Derived from the
    /// User-Agent and truncated — never the raw header, which is a fingerprinting surface and long
    /// enough to be a storage problem.</summary>
    public string? Label { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Stamped at issuance from the window in force at that moment — the policy row's
    /// <see cref="PlatformMfaPolicy.BrowserTrustHours"/>, or the configured seed when no row exists.
    /// Bounded to 8–720 hours by the same check constraint the policy column carries.
    ///
    /// <para>Deliberately NOT recomputed when the policy window changes. Shortening the platform
    /// window does not retroactively shorten trusts already granted; the control for "these trusts
    /// end now" is revocation, which is immediate and reaches the live sessions they minted.</para></summary>
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? RevokedBy { get; set; }

    public string? RevocationReason { get; set; }

    public Models.PlatformUser PlatformUser { get; set; } = null!;
}

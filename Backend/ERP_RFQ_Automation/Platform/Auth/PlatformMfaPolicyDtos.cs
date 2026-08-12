using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// The EFFECTIVE policy: what the platform is actually enforcing right now, after the declared row,
/// the clock and the environment ceiling have all been applied.
///
/// <para>Every authorization decision and every screen reads this, never
/// <see cref="PlatformMfaPolicy"/> directly. That is what makes an expired bypass revert with no
/// job having run: the expiry is applied on READ, so the first request after
/// <c>ExpiresAtUtc</c> is already enforced.</para>
/// </summary>
public sealed record PlatformEffectiveMfaPolicy(
    PlatformMfaMode Mode,
    PlatformMfaMode DeclaredMode,
    PlatformMfaEnvironmentClass EnvironmentClass,
    string EnvironmentName,
    DateTime EffectiveFromUtc,
    DateTime? ExpiresAtUtc,
    string? ChangedBy,
    string? ChangeReason,
    long Version,
    DateTime? UpdatedAtUtc,
    bool IsolatedTestInfrastructure,
    bool BrowserTrustEnabled,
    int BrowserTrustHours)
{
    /// <summary>
    /// True when a session that never presented a second factor may reach the privileged control
    /// plane. This — not <see cref="EnforcementDisabled"/> — is what the authorization layer asks,
    /// because OPTIONAL and DISABLED_TEST_ONLY differ in how login behaves, not in what a
    /// password-only session is allowed to do once it exists.
    /// </summary>
    public bool PasswordOnlySessionsPermitted => Mode != PlatformMfaMode.REQUIRED;

    /// <summary>True when no second factor is enforced ANYWHERE, including at login for an operator
    /// who has enrolled. This is the state the console must announce with a persistent banner; a
    /// test environment that quietly stopped asking for codes is one nobody notices has stopped.</summary>
    public bool EnforcementDisabled => Mode == PlatformMfaMode.DISABLED_TEST_ONLY;

    /// <summary>True when the declared mode has lapsed and REQUIRED has been restored by the clock
    /// alone. Surfaced so the screen can say "this expired at …" rather than showing REQUIRED with
    /// no explanation for the audit row the operator remembers writing.</summary>
    public bool BypassExpired =>
        DeclaredMode != PlatformMfaMode.REQUIRED && Mode == PlatformMfaMode.REQUIRED;
}

/// <summary>The Owner-facing read model for Platform Admin → Security → Platform Authentication.</summary>
public sealed class PlatformMfaPolicyDto
{
    public string Mode { get; set; } = nameof(PlatformMfaMode.REQUIRED);

    /// <summary>The mode as stored, which differs from <see cref="Mode"/> exactly when a bypass has
    /// expired. Showing both is what lets the screen explain itself.</summary>
    public string DeclaredMode { get; set; } = nameof(PlatformMfaMode.REQUIRED);

    public string EnvironmentClass { get; set; } = nameof(PlatformMfaEnvironmentClass.Production);

    public string EnvironmentName { get; set; } = string.Empty;

    public bool IsolatedTestInfrastructure { get; set; }

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public bool BypassExpired { get; set; }

    public bool EnforcementDisabled { get; set; }

    /// <summary>True when a password-only session reaches the privileged control plane — OPTIONAL as
    /// well as DISABLED_TEST_ONLY.</summary>
    public bool PasswordOnlySessionsPermitted { get; set; }

    public string? ChangedBy { get; set; }

    public string? ChangeReason { get; set; }

    public long Version { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Platform operators with an enrolled second factor — who a change to enforcement
    /// actually affects. "Are you sure" is meaningless without a number attached to it.</summary>
    public int EnrolledOperatorCount { get; set; }

    public int ActiveOperatorCount { get; set; }

    /// <summary>Live sessions whose <c>amr=mfa</c> is backed by a server-side MFA timestamp. These
    /// are the sessions that stay privileged regardless of what the policy becomes.</summary>
    public int ActiveMfaBoundSessionCount { get; set; }

    /// <summary>Browser trusts that have neither expired nor been revoked.</summary>
    public int ActiveBrowserTrustCount { get; set; }

    /// <summary>Which modes THIS deployment may select, decided by the backend. The console renders
    /// only these; the backend refuses anything else regardless of what the console sends.</summary>
    public List<string> AvailableModes { get; set; } = new();

    /// <summary>Per-mode confirmation phrase, so the screen can show the operator what to type
    /// without hard-coding a string that could drift from the one the server checks.</summary>
    public Dictionary<string, string> ConfirmationPhrases { get; set; } = new();

    public int MaxBypassHours { get; set; }

    public int MinimumReasonLength { get; set; }

    /// <summary>Whether the platform honours remembered browsers at all. False means an existing
    /// trust is refused at the next sign-in, not merely that no new ones are minted.</summary>
    public bool BrowserTrustEnabled { get; set; }

    /// <summary>The window in force, in hours. It comes from the policy row once one exists; until
    /// then it is the <c>Platform:Mfa:BrowserTrustHours</c> seed — see
    /// <see cref="BrowserTrustFromPolicyRow"/>.</summary>
    public int BrowserTrustHours { get; set; }

    /// <summary>True when an Owner set the window, false when it is still the deployment default.
    /// The screen says which, because a number nobody chose looks exactly like a number somebody
    /// did.</summary>
    public bool BrowserTrustFromPolicyRow { get; set; }

    /// <summary>The bounds the server will accept, sent rather than hard-coded in the console so the
    /// two cannot drift into a screen that offers a value the API refuses.</summary>
    public int MinBrowserTrustHours { get; set; }

    public int MaxBrowserTrustHours { get; set; }
}

/// <summary>
/// A change to platform MFA enforcement. Every field on it is a control, and the service refuses
/// the request if any one is missing — see <c>PlatformMfaPolicyService.ChangeAsync</c> for which
/// failure each one prevents.
/// </summary>
public sealed class ChangePlatformMfaPolicyRequest
{
    /// <summary><see cref="PlatformMfaMode"/> name.</summary>
    [Required]
    public string Mode { get; set; } = null!;

    /// <summary>The caller's CURRENT password. Re-authentication, not identification: the session
    /// is already authenticated, and the point is to prove the person at the keyboard is still the
    /// operator who signed in and not whoever sat down at their unlocked machine.</summary>
    [Required]
    public string CurrentPassword { get; set; } = null!;

    [Required]
    public string Reason { get; set; } = null!;

    /// <summary>Mandatory for any mode other than REQUIRED, and bounded by
    /// <c>Platform:Mfa:MaxBypassHours</c>.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Alternative to <see cref="ExpiresAtUtc"/> for the console, which thinks in "four
    /// hours" rather than in timestamps. Whichever arrives is validated against the same ceiling.</summary>
    public double? DurationHours { get; set; }

    /// <summary>The exact phrase from <c>PlatformMfaPolicyOptions.ConfirmationPhraseFor</c>.</summary>
    [Required]
    public string Confirmation { get; set; } = null!;

    /// <summary>Optimistic concurrency, on the same terms as the email settings screen.</summary>
    public long? ExpectedVersion { get; set; }

    /// <summary>
    /// Turn "remember this browser" on or off for the whole platform. NULL keeps what is stored —
    /// the same null-keeps rule <c>PlatformEmailSettingsService.ResolveSecret</c> uses — so a caller
    /// changing only the enforcement mode cannot silently reset a control it never mentioned.
    ///
    /// <para>It rides on this request, and therefore through this request's ceremony, deliberately.
    /// Disabling browser trust is a tightening and could arguably be cheap; SETTING IT BACK ON, or
    /// widening the window to a month, is a relaxation of the second factor for every operator at
    /// once, and it must not be reachable by a lighter path than the one that relaxes the mode.</para>
    /// </summary>
    public bool? BrowserTrustEnabled { get; set; }

    /// <summary>How long a remembered browser suppresses a repeat challenge, in hours. NULL keeps
    /// what is stored. Bounded by <c>PlatformMfaPolicyOptions.Min/MaxBrowserTrustHours</c> — the
    /// service refuses anything outside it with a 400 that names both bounds, and a check constraint
    /// refuses it again at the table.</summary>
    public int? BrowserTrustHours { get; set; }
}

/// <summary>Outcome of a policy change. A conflict is distinguished from a refusal because they
/// need different words in front of an operator: one means "somebody else changed this", the other
/// "this change is not permitted here".</summary>
public sealed record PlatformMfaPolicyChangeResult(
    bool Succeeded,
    PlatformMfaPolicyDto? Policy,
    string? Error,
    bool Conflict = false,
    bool Forbidden = false);

/// <summary>Re-authentication for a high-risk operation. Deliberately its own endpoint rather than
/// a password field bolted onto every destructive request body, so adding the control to a new
/// operation is an attribute and not a contract change.</summary>
public sealed class PlatformReauthenticationRequest
{
    [Required]
    public string CurrentPassword { get; set; } = null!;
}

public sealed record PlatformReauthenticationResponse(DateTime ValidUntilUtc, int WindowMinutes);

/// <summary>A remembered browser, as shown in the operator's own trust list.</summary>
public sealed record PlatformBrowserTrustDto(
    long Id,
    string? Label,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? LastUsedAtUtc);

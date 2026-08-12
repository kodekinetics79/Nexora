using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// The "I forgot my password" submission.
///
/// <para>The address is the ONLY field, and nothing about the response depends on it. See
/// <c>PasswordResetController.RequestReset</c> for the enumeration rule that governs it.</para>
/// </summary>
public sealed class ForgotPasswordRequest
{
    /// <summary>
    /// Bounded, not shape-validated. <c>[EmailAddress]</c> would produce a SECOND response
    /// shape from this endpoint — 400 with a ModelState body for "sara@" and 202 for
    /// "sara@nowhere.invalid" — and every extra shape is another thing a prober can measure and
    /// another thing a future edit can accidentally make depend on the account. A malformed
    /// address simply matches no account and gets the one answer everybody gets.
    /// </summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Email { get; set; } = null!;
}

/// <summary>
/// The reset page's view of a token.
///
/// <para>Field names and the <see cref="Status"/> vocabulary are the contract
/// <c>Frontend/src/pages/PasswordReset/passwordResetApi.ts</c> reads: it switches on
/// <c>status</c> when present and falls back to the HTTP status code, so the two must agree.
/// A rejected token carries the status and NOTHING else — no address, no expiry — because a
/// link the holder cannot use must not describe the account it belonged to.</para>
/// </summary>
public sealed class PasswordResetChallengeResponse
{
    /// <summary>"valid" | "expired" | "used" | "revoked" | "invalid".</summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// Always masked ("s***a@acme.com"), with no opt-out, which is where this deliberately
    /// diverges from activation's <c>TenantOnboarding:MaskActivationEmail</c>.
    ///
    /// <para>An invitation is sent to somebody an operator chose, so a deployment can reasonably
    /// decide the invitee may see the exact string they will type at sign-in. A reset link is
    /// caused by whoever typed an address into a public form — which may not be the account's
    /// owner at all — so echoing the address in full would let a stranger who intercepts one
    /// message confirm the exact mailbox. The masked form still lets the real owner recognise
    /// which of their addresses this is, which is the only thing it is there for.</para>
    /// </summary>
    public string? Email { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Null unless valid. Lets the page greet the person by name.</summary>
    public string? FirstName { get; set; }

    /// <summary>
    /// So the page can state the rule up front instead of after a failed submit — and so the
    /// client-side checklist cannot silently drift from what the server actually enforces.
    /// </summary>
    public int? MinimumPasswordLength { get; set; }
}

/// <summary>
/// The completion POST body. The token is not in it: it travels in the route, so the page can
/// submit exactly what it was opened with and no request can carry a token and a password that
/// disagree about which account is being changed.
/// </summary>
public sealed class SetNewPasswordRequest
{
    /// <summary>
    /// Length is bounded here only to reject obvious abuse cheaply; the real rule lives in
    /// <c>ActivationPasswordPolicy</c>, which can explain itself to the person typing.
    /// </summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Password { get; set; } = null!;
}

/// <summary>What the reset page needs in order to render the form, once the token checks out.</summary>
public sealed class PasswordResetPreview
{
    public string Email { get; set; } = null!;
    public string RecipientFirstName { get; set; } = null!;
    public DateTime ExpiresAtUtc { get; set; }
    public int MinimumPasswordLength { get; set; }
}

/// <summary>A verdict on a presented token, plus the preview when — and only when — it is valid.</summary>
public sealed record PasswordResetChallenge(PasswordResetTokenState State, PasswordResetPreview? Preview)
{
    public static PasswordResetChallenge Rejected(PasswordResetTokenState state) => new(state, null);
}

public enum PasswordResetStatus
{
    /// <summary>The password was changed.</summary>
    Completed,

    /// <summary>The token was not usable. <see cref="PasswordResetResult.TokenState"/> says how.</summary>
    TokenRejected,

    /// <summary>
    /// The token was fine; the chosen password was not. Kept separate from
    /// <see cref="TokenRejected"/> because the token is NOT spent by this outcome and the page
    /// must not tell the person their link is dead when it is still perfectly good.
    /// </summary>
    PasswordRejected
}

/// <summary>The outcome of spending a reset token.</summary>
public sealed record PasswordResetResult(
    PasswordResetStatus Status,
    PasswordResetTokenState TokenState,
    IReadOnlyList<string> PasswordFailures)
{
    public static PasswordResetResult Completed() =>
        new(PasswordResetStatus.Completed, PasswordResetTokenState.Valid, Array.Empty<string>());

    public static PasswordResetResult TokenRejected(PasswordResetTokenState state) =>
        new(PasswordResetStatus.TokenRejected, state, Array.Empty<string>());

    public static PasswordResetResult PasswordRejected(IReadOnlyList<string> failures) =>
        new(PasswordResetStatus.PasswordRejected, PasswordResetTokenState.Valid, failures);
}

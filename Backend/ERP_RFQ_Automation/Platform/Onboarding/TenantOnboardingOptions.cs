using System.Text;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// Configuration for founding-administrator activation, bound from the
/// <c>TenantOnboarding</c> section. Every value has a defensible default so the flow works on
/// a deployment that configures nothing; the knobs exist for customers whose security team
/// wants a shorter window, not because the defaults are placeholders.
/// </summary>
public sealed class TenantOnboardingOptions
{
    public const string SectionName = "TenantOnboarding";

    /// <summary>
    /// How long an activation link stays usable. 72 hours covers a customer who is invited on
    /// a Friday afternoon and gets to it on Monday morning, which is the ordinary case — a
    /// 1-hour window would turn every real handover into a resend request, and operators who
    /// have to resend constantly stop treating expiry as meaningful.
    /// </summary>
    public int InvitationLifetimeHours { get; set; } = 72;

    /// <summary>
    /// Frontend route the emailed link points at, relative to
    /// <c>Notifications:AppBaseUrl</c>. The token rides as the <c>token</c> query parameter.
    /// </summary>
    public string ActivationPath { get; set; } = "activate";

    /// <summary>
    /// How long a self-service password-reset link stays usable, in MINUTES.
    ///
    /// <para><b>Why an hour and not the invitation's 72.</b> The two links look alike and are
    /// not alike. An invitation is caused by an operator, addressed to somebody who is expecting
    /// it, and its long window buys the one thing that actually matters there: a customer invited
    /// on a Friday can still activate on Monday without a resend. A reset is caused by ANYONE who
    /// can type an address into a public form, and the person who wants it is, by definition,
    /// sitting at the product right now. There is no Monday-morning case to serve — so the window
    /// buys nothing and costs a bearer credential sitting in a mailbox for three days, obtainable
    /// by anyone who reaches that mailbox in the meantime. Sixty minutes is the conventional
    /// figure for exactly this reason, and re-requesting is one click.</para>
    ///
    /// <para>Minutes rather than hours so a security team can actually tighten it. An
    /// hours-granular knob makes "fifteen minutes" unexpressible, and the deployments that ask
    /// for a shorter reset window are the ones that ask for fifteen.</para>
    /// </summary>
    public int ResetLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// Configuration cannot raise the reset window past this. The clamp exists in the same
    /// spirit as <see cref="AbsoluteMinimumPasswordLength"/>: the setting is here for teams who
    /// want it SHORTER, and a deployment that sets it to a fortnight — by typo, or because
    /// somebody was tired of resending — must not be able to turn a recovery link into a
    /// standing key to every account whose mailbox is ever compromised.
    /// </summary>
    public const int AbsoluteMaximumResetLifetimeMinutes = 24 * 60;

    /// <summary>
    /// Frontend route the emailed reset link points at, relative to
    /// <c>Notifications:AppBaseUrl</c>. Sibling of <see cref="ActivationPath"/> and resolved the
    /// same way; the token rides as a path segment, matching what <c>App.tsx</c> declares.
    /// </summary>
    public string ResetPasswordPath { get; set; } = "reset-password";

    /// <summary>
    /// Floor for the password chosen at activation. This is the credential for a tenant's
    /// most privileged account (RoleRank.Owner — every module check passes for it), so the
    /// default is well above the eight characters most products still accept.
    /// </summary>
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>
    /// Configuration cannot lower the floor below this. A deployment that sets
    /// <c>MinimumPasswordLength: 4</c> — by typo or by pressure from an impatient pilot — must
    /// not be able to weaken the founding admin's credential; the setting is clamped and the
    /// clamp is reported by <see cref="Validate"/> rather than applied silently.
    /// </summary>
    public const int AbsoluteMinimumPasswordLength = 12;

    /// <summary>
    /// BCrypt hashes at most 72 BYTES of input and silently discards the rest. Accepting a
    /// 100-character passphrase and then verifying only its first 72 bytes would be a lie
    /// about the strength of what is stored, so the limit is stated here in the same unit the
    /// algorithm actually uses.
    /// </summary>
    public const int MaximumPasswordBytes = 72;

    /// <summary>
    /// Address shown to a recipient whose link no longer works. Falls back to the notification
    /// sender when unset, because "contact support" with no address is not help.
    /// </summary>
    public string? SupportEmail { get; set; }

    /// <summary>
    /// Whether the activation page echoes the invitee's address masked ("s***a@acme.com") or in
    /// full.
    ///
    /// <para>Masked by default. Only the token holder can reach that response, so showing the
    /// address in full discloses nothing to a stranger — but invitations get forwarded into
    /// shared mailboxes and pasted into ticketing systems, and a masked echo still lets the
    /// intended person confirm which of their addresses this is. Deployments that would rather
    /// show the exact string the customer will type at sign-in set this to false.</para>
    /// </summary>
    public bool MaskActivationEmail { get; set; } = true;

    public TimeSpan InvitationLifetime =>
        TimeSpan.FromHours(InvitationLifetimeHours > 0 ? InvitationLifetimeHours : 72);

    /// <summary>Effective reset window after the clamp above. Never zero, never over a day.</summary>
    public TimeSpan ResetLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(
            ResetLifetimeMinutes > 0 ? ResetLifetimeMinutes : 60, 1, AbsoluteMaximumResetLifetimeMinutes));

    /// <summary>Effective floor after the clamp above.</summary>
    public int EffectiveMinimumPasswordLength =>
        Math.Max(AbsoluteMinimumPasswordLength, MinimumPasswordLength);

    /// <summary>
    /// Non-fatal configuration warnings, logged once at startup in the shape
    /// <see cref="Notifications.NotificationsOptions.Validate"/> established. Nothing here
    /// stops the host: a misconfigured lifetime must not prevent tenants from being created.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var warnings = new List<string>();

        if (InvitationLifetimeHours <= 0)
            warnings.Add(
                $"TenantOnboarding:InvitationLifetimeHours is {InvitationLifetimeHours}; " +
                "falling back to 72 hours.");
        else if (InvitationLifetimeHours > 24 * 14)
            warnings.Add(
                $"TenantOnboarding:InvitationLifetimeHours is {InvitationLifetimeHours} " +
                "(over two weeks). An activation link is a bearer credential sitting in a mailbox; " +
                "consider a shorter window plus resend.");

        if (MinimumPasswordLength < AbsoluteMinimumPasswordLength)
            warnings.Add(
                $"TenantOnboarding:MinimumPasswordLength is {MinimumPasswordLength}, below the " +
                $"hard floor of {AbsoluteMinimumPasswordLength}; the floor is enforced instead.");

        if (string.IsNullOrWhiteSpace(ActivationPath))
            warnings.Add("TenantOnboarding:ActivationPath is empty; falling back to 'activate'.");

        if (ResetLifetimeMinutes <= 0)
            warnings.Add(
                $"TenantOnboarding:ResetLifetimeMinutes is {ResetLifetimeMinutes}; " +
                "falling back to 60 minutes.");
        else if (ResetLifetimeMinutes > AbsoluteMaximumResetLifetimeMinutes)
            warnings.Add(
                $"TenantOnboarding:ResetLifetimeMinutes is {ResetLifetimeMinutes}, above the " +
                $"ceiling of {AbsoluteMaximumResetLifetimeMinutes}; the ceiling is enforced instead. " +
                "A password-reset link is a bearer credential in a mailbox — the knob is there to " +
                "make the window shorter, not longer.");

        if (string.IsNullOrWhiteSpace(ResetPasswordPath))
            warnings.Add(
                "TenantOnboarding:ResetPasswordPath is empty; falling back to 'reset-password'.");

        return warnings;
    }
}

/// <summary>Outcome of checking a proposed activation password against the policy.</summary>
public sealed record PasswordPolicyResult(bool IsAcceptable, IReadOnlyList<string> Failures)
{
    public static readonly PasswordPolicyResult Accepted = new(true, Array.Empty<string>());
}

/// <summary>
/// The password rules applied at activation.
///
/// <para>Deliberately explicit about WHY each rule exists, because a password policy nobody can
/// justify is a policy that gets watered down the first time a customer complains. Failures are
/// returned in full: unlike a login, telling the caller exactly what is wrong with a password
/// THEY just chose leaks nothing — they already know the value.</para>
/// </summary>
public static class ActivationPasswordPolicy
{
    public static PasswordPolicyResult Validate(string? password, string? email, int minimumLength)
    {
        var failures = new List<string>();

        if (string.IsNullOrEmpty(password))
            return new PasswordPolicyResult(false, ["A password is required."]);

        if (password.Length < minimumLength)
            failures.Add($"Use at least {minimumLength} characters.");

        // Counted in bytes, not characters: BCrypt truncates at 72 bytes, and a passphrase of
        // emoji or Arabic script reaches that limit far sooner than 72 keystrokes suggests.
        if (Encoding.UTF8.GetByteCount(password) > TenantOnboardingOptions.MaximumPasswordBytes)
            failures.Add(
                $"Use at most {TenantOnboardingOptions.MaximumPasswordBytes} bytes " +
                "(roughly 72 plain characters).");

        // Leading/trailing whitespace survives a copy-paste and then fails to survive the next
        // one, producing a credential the owner cannot reproduce and cannot explain.
        if (password != password.Trim())
            failures.Add("Remove the leading or trailing spaces.");

        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;
        if (classes < 3)
            failures.Add(
                "Mix at least three of: lowercase letters, uppercase letters, digits, symbols.");

        // The invitee's own address is the first thing an attacker who has seen the invitation
        // will try, and it is the first thing a hurried person reaches for.
        var localPart = LocalPartOf(email);
        if (localPart is { Length: >= 3 }
            && password.Contains(localPart, StringComparison.OrdinalIgnoreCase))
            failures.Add("Do not include your email address in the password.");

        return failures.Count == 0
            ? PasswordPolicyResult.Accepted
            : new PasswordPolicyResult(false, failures);
    }

    private static string? LocalPartOf(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        return at > 0 ? email[..at] : email;
    }
}

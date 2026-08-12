using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>What the provisioning transaction knows about the administrator it just created.</summary>
public sealed record TenantAdminInvitationRequest
{
    public required long TenantId { get; init; }

    /// <summary>The <c>Users</c> row provisioning created. Nothing is invited into thin air.</summary>
    public required long UserId { get; init; }

    public required string Email { get; init; }

    /// <summary>Greeting name for the email. The first name is enough and is what a person expects.</summary>
    public required string RecipientName { get; init; }

    /// <summary>Company name the recipient will recognise — the reason they trust the email.</summary>
    public required string TenantName { get; init; }

    /// <summary>Platform operator who caused the issue, for the invitation's own audit trail.</summary>
    public required string IssuedBy { get; init; }
}

/// <summary>
/// The one and only moment the cleartext token exists. Everything else in the system — the
/// database row, the audit record, every log line — holds only its SHA-256.
/// </summary>
public sealed record IssuedTenantAdminInvitation
{
    public required long InvitationId { get; init; }
    public required long TenantId { get; init; }
    public required long UserId { get; init; }
    public required string Email { get; init; }
    public required string RecipientName { get; init; }
    public required string TenantName { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>Cleartext activation token. Put it in the email; never in a log, audit row, or response body.</summary>
    public required string Token { get; init; }

    /// <summary>The link the recipient clicks, already combined with <c>Notifications:AppBaseUrl</c>.</summary>
    public required string ActivationUrl { get; init; }

    /// <summary>
    /// A record's compiler-generated ToString prints EVERY property, so a single
    /// <c>_logger.LogInformation("issued {Invitation}", issued)</c> — the most natural line
    /// anyone would write — would put a live activation token in the log file, and log files
    /// go to places the token was specifically kept out of. Redacted at the source instead of
    /// trusting every future call site to remember.
    /// </summary>
    public override string ToString() =>
        $"IssuedTenantAdminInvitation {{ InvitationId = {InvitationId}, TenantId = {TenantId}, " +
        $"UserId = {UserId}, Email = {Email}, ExpiresAtUtc = {ExpiresAtUtc:O}, Token = [redacted], " +
        "ActivationUrl = [redacted] }";
}

/// <summary>
/// Everything the activation page may know. Deliberately thin: it is served to an
/// unauthenticated caller, so by default it carries a MASKED address rather than the real one,
/// and it is returned only for a token that is currently redeemable.
/// </summary>
public sealed record TenantActivationPreview
{
    public required string Email { get; init; }
    public required string CompanyName { get; init; }
    public required string RecipientFirstName { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>So the page can state the rule before the user types, instead of after.</summary>
    public required int MinimumPasswordLength { get; init; }
}

/// <summary>
/// Why a presented token is or is not usable.
///
/// <para><b>Why these are named rather than collapsed into one refusal.</b> Every state here is
/// keyed on a 256-bit token the caller must already possess. Someone guessing gets
/// <see cref="Invalid"/> and only <see cref="Invalid"/>, forever — the other three are
/// unreachable without holding a link that was genuinely issued, so naming them tells an
/// attacker nothing they did not supply themselves. What it buys is real: "this link was
/// already used" is the sentence that tells a customer who never used it that somebody ELSE
/// activated their account, and "expired" is the difference between a customer asking for a
/// resend and a customer concluding the product is broken.</para>
///
/// <para>The property that IS load-bearing for privacy is elsewhere and unchanged: nothing is
/// ever looked up by email address, and the address echoed back is masked. There is no request
/// anyone can make here that answers "does this person have an account?".</para>
/// </summary>
public enum ActivationTokenState
{
    Valid,

    /// <summary>Malformed, or no invitation carries that hash. The only state a guesser can reach.</summary>
    Invalid,

    Expired,

    /// <summary>Already redeemed. A password has been set with it and cannot be set again.</summary>
    Used,

    /// <summary>Withdrawn by an operator, or superseded by a reissue.</summary>
    Revoked
}

/// <summary>The token's verdict plus, when it is <see cref="ActivationTokenState.Valid"/>, the page's content.</summary>
public sealed record TenantActivationChallenge(
    ActivationTokenState State,
    TenantActivationPreview? Preview)
{
    public static TenantActivationChallenge Rejected(ActivationTokenState state) => new(state, null);
}

public enum TenantActivationStatus
{
    /// <summary>The password was set and the account is usable.</summary>
    Activated,

    /// <summary>The token was good; the chosen password was not. The invitation stays live.</summary>
    PasswordRejected,

    /// <summary>The token could not be spent. <see cref="TenantActivationResult.TokenState"/> says why.</summary>
    TokenRejected
}

public sealed record TenantActivationResult(
    TenantActivationStatus Status,
    ActivationTokenState TokenState,
    IReadOnlyList<string> PasswordFailures)
{
    /// <summary>
    /// Whether the account this token belongs to can actually SIGN IN yet.
    ///
    /// <para><b>Why redeeming a link is not the same as being able to use the product.</b> The
    /// invitation is issued by the LAST step of provisioning, while the tenant is still
    /// <c>TenantStatus.Provisioning</c>. Going live is a separate, deliberately governed act — an
    /// operator pressing "Activate tenant" on the authoritative activation policy — and until it
    /// happens <c>AuthRepository</c> refuses the login with "This organization's workspace is
    /// still provisioning and has not passed authoritative activation"
    /// (<c>TenantAccessSnapshot.IsAccessDenied</c>).</para>
    ///
    /// <para>So the two ends of the journey have different owners and the seam between them used
    /// to lie: the invitation email said "choose your password and sign in for the first time",
    /// this endpoint answered "Your password is set. You can now sign in.", and the page then
    /// pushed the customer at a sign-in screen that answered 403. The customer's very first
    /// interaction with the product was a refusal they could not self-repair. This flag is what
    /// lets both surfaces say the true thing instead.</para>
    ///
    /// <para>Defaults to <c>true</c>: every construction other than a real redemption (token
    /// rejected, password rejected, and every existing test double) is a path where the question
    /// is not asked, and the value is only ever READ on the <see cref="TenantActivationStatus.Activated"/>
    /// branch.</para>
    /// </summary>
    public bool SignInAvailable { get; init; } = true;

    public static readonly TenantActivationResult Activated =
        new(TenantActivationStatus.Activated, ActivationTokenState.Valid, Array.Empty<string>());

    /// <summary>Activated, saying plainly whether sign-in is open yet. See <see cref="SignInAvailable"/>.</summary>
    public static TenantActivationResult ActivatedWith(bool signInAvailable) =>
        Activated with { SignInAvailable = signInAvailable };

    public static TenantActivationResult TokenRejected(ActivationTokenState state) =>
        new(TenantActivationStatus.TokenRejected, state, Array.Empty<string>());

    public static TenantActivationResult PasswordRejected(IReadOnlyList<string> failures) =>
        new(TenantActivationStatus.PasswordRejected, ActivationTokenState.Valid, failures);
}

/// <summary>Operator-facing view of one invitation. Carries no token and no hash.</summary>
public sealed record TenantAdminInvitationSummary
{
    public required long Id { get; init; }
    public required long UserId { get; init; }
    public required string Email { get; init; }
    public required string Status { get; init; }
    public required DateTime IssuedAtUtc { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public DateTime? RedeemedAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
    public string? RevokedBy { get; init; }
    public string? RevocationReason { get; init; }
    public DateTime? LastSentAtUtc { get; init; }
    public required int SendCount { get; init; }
    public required string IssuedBy { get; init; }
}

/// <summary>
/// Issue, deliver, redeem, reissue and revoke founding-administrator activation links.
///
/// <para><b>Issue runs inside somebody else's transaction; redeem owns its own.</b> Issuing is
/// half of provisioning — an invitation for a tenant whose creation rolled back is a link to
/// nothing — so <see cref="IssueAsync"/> takes the caller's unit of work and never begins,
/// commits, or rolls back anything. Redemption arrives on a separate, anonymous request with
/// no ambient transaction to join, so it opens and owns one.</para>
/// </summary>
public interface ITenantAdminInvitationService
{
    /// <summary>
    /// Mints a token, stores only its hash on <paramref name="unitOfWork"/>, and returns the
    /// cleartext exactly once.
    ///
    /// <para>Call this INSIDE the provisioning transaction, after the <c>User</c> row has an
    /// id. It calls <c>SaveChangesAsync</c> on the supplied context — which enlists in whatever
    /// transaction that context already has open — and opens no transaction of its own, so a
    /// failed provision takes the invitation down with it.</para>
    ///
    /// <para>Under an execution strategy each retry attempt must call this again and overwrite
    /// the captured result: the token belongs to the attempt that committed, and a token whose
    /// row rolled back activates nothing. This is safe to repeat — unlike a BCrypt salt, the
    /// stored hash is a pure function of the token.</para>
    ///
    /// <para>Do NOT send the email from inside the transaction. A committed email cannot be
    /// rolled back, and a live activation link for a tenant that never existed is worse than
    /// no email at all.</para>
    /// </summary>
    Task<IssuedTenantAdminInvitation> IssueAsync(
        ErpRfqAutomationContext unitOfWork,
        TenantAdminInvitationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Sends the invite email and records the dispatch. Call AFTER the provisioning transaction
    /// commits. Never throws: a mail outage must not turn a successful provision into a 500,
    /// and the operator can always resend.
    /// </summary>
    /// <returns>True when the configured provider accepted the message.</returns>
    Task<bool> SendInvitationEmailAsync(
        IssuedTenantAdminInvitation issued, CancellationToken ct = default);

    /// <summary>Combines a cleartext token with the configured base URL and activation path.</summary>
    string BuildActivationUrl(string token);

    /// <summary>
    /// Reads a presented token without spending it: the verdict, plus what the page should
    /// render when that verdict is <see cref="ActivationTokenState.Valid"/>.
    /// </summary>
    Task<TenantActivationChallenge> InspectAsync(string? token, CancellationToken ct = default);

    /// <summary>
    /// Spends the invitation and sets the account's password, atomically. Two concurrent calls
    /// with the same token produce exactly one <see cref="TenantActivationStatus.Activated"/>.
    /// </summary>
    Task<TenantActivationResult> RedeemAsync(
        string? token, string? newPassword, string? clientIp, CancellationToken ct = default);

    /// <summary>Invitation history for one tenant, newest first. Tokens are not retrievable.</summary>
    Task<IReadOnlyList<TenantAdminInvitationSummary>> ListAsync(
        long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Revokes every live invitation for the target administrator and issues a fresh one, so a
    /// reissue can never leave two working links in two different mailboxes. Null when the
    /// tenant has no administrator to invite.
    /// </summary>
    Task<IssuedTenantAdminInvitation?> ReissueAsync(
        long tenantId, long? userId, string issuedBy, CancellationToken ct = default);

    /// <summary>Withdraws one live invitation. False when it does not exist or was already spent.</summary>
    Task<bool> RevokeAsync(
        long tenantId, long invitationId, string revokedBy, string reason, CancellationToken ct = default);

    /// <summary>
    /// Withdraws EVERY live invitation held by one account, on the caller's unit of work.
    ///
    /// <para><b>The invariant this exists to protect.</b> Redemption sets
    /// <c>IsActive = true</c> and clears <c>DeactivatedAtUtc</c> — that is the whole point of it.
    /// So an account that is deactivated while an activation link is still in flight can be
    /// switched back on by whoever holds that link, silently, with no operator involved. Any
    /// mutation that takes an account out of service therefore has to kill its outstanding links
    /// in the SAME transaction, and this is the one place that knows what "outstanding" means.</para>
    ///
    /// <para>Takes the caller's context for the same reason <see cref="IssueAsync"/> does: the
    /// revocation is half of somebody else's atomic change, so it must join their transaction and
    /// must never begin, commit, or roll back one of its own.</para>
    /// </summary>
    /// <returns>How many live invitations were withdrawn.</returns>
    Task<int> RevokeOutstandingForUserAsync(
        ErpRfqAutomationContext unitOfWork,
        long tenantId,
        long userId,
        string revokedBy,
        string reason,
        CancellationToken ct = default);
}

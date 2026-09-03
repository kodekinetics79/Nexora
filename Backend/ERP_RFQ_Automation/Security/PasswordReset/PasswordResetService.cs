using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// Default <see cref="IPasswordResetService"/>.
///
/// <para><b>Three invariants everything here serves.</b></para>
///
/// <para><i>One: the request path tells the caller nothing.</i> Whether the address is known,
/// whether the account is active, whether a tenant could be resolved, whether the mail provider
/// accepted the message — none of it reaches the caller, and none of it can, because
/// <see cref="RequestResetAsync"/> returns <c>Task</c>. Everything the flow learns about the
/// address is written to the log, where an operator can read it and a stranger cannot. This is
/// the property the activation flow never had to think about, because activation is never asked
/// to look anything up by email; reset exists precisely to do that, so the rule has to be
/// written down rather than inherited.</para>
///
/// <para><i>Two: the cleartext token exists only in flight.</i> It is a local in
/// <see cref="RequestResetAsync"/> and text inside one email. The database holds SHA-256, and
/// nothing logs, audits or serialises the original — the log lines below name a row id and a
/// user id, never a token or an address.</para>
///
/// <para><i>Three: a token is spent by exactly one caller, ever.</i> The spend is a single UPDATE
/// whose WHERE clause IS the single-use rule. Reading the row, deciding it looks live, and then
/// writing would leave a window; two clicks of the same link from two devices — ordinary user
/// behaviour, not an attack — would both pass the check, and the second would silently overwrite
/// the first person's password.</para>
/// </summary>
public sealed class PasswordResetService : IPasswordResetService
{
    /// <summary>
    /// 256 bits from a CSPRNG — the same size the activation token uses, and for the same
    /// reason: large enough that guessing is not a threat model anyone has to reason about
    /// again, and the reason a plain SHA-256 (rather than a work-factored hash) is the right
    /// thing to store, because there is no dictionary to defend against.
    /// </summary>
    private const int TokenBytes = 32;

    // Cheap shape guard so an obviously malformed token costs no database round trip. The
    // bounds are generous; correctness comes from the hash lookup, not from this.
    private const int MinimumTokenLength = 32;
    private const int MaximumTokenLength = 256;

    /// <summary>Written into RevokedBy / ModifiedBy. There is no human actor to name here.</summary>
    private const string Actor = "password-reset";

    private readonly ErpRfqAutomationContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ILoginAttemptThrottle _loginThrottle;
    private readonly NotificationsOptions _notifications;
    private readonly TenantOnboardingOptions _options;
    private readonly ILogger<PasswordResetService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ITenantSessionCache? _sessions;

    public PasswordResetService(
        ErpRfqAutomationContext db,
        IEmailSender emailSender,
        ILoginAttemptThrottle loginThrottle,
        IOptions<NotificationsOptions> notifications,
        IOptions<TenantOnboardingOptions> options,
        ILogger<PasswordResetService> logger,
        TimeProvider? timeProvider = null,
        ITenantSessionCache? sessions = null)
    {
        _sessions = sessions;
        _db = db;
        _emailSender = emailSender;
        _loginThrottle = loginThrottle;
        _notifications = notifications.Value;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ==== request ================================================================================

    /// <inheritdoc />
    public async Task RequestResetAsync(string? email, string? clientIp, CancellationToken ct = default)
    {
        var address = email?.Trim();
        if (string.IsNullOrEmpty(address) || address.Length > 256)
        {
            // Deliberately silent, and deliberately not an exception. A blank or absurd address
            // is a form that was submitted badly, not an event worth a log line per keystroke —
            // and the caller must not be able to tell this branch from the one below.
            return;
        }

        var account = await FindResettableAccountAsync(address, ct);
        if (account is null)
        {
            // The whole enumeration story in one place. The address was not found, or the account
            // it names cannot sign in — either way the caller gets the identical answer, and the
            // fact that there was nothing to do lives here, in the log, where an operator
            // debugging "the customer says no email arrived" can read it.
            //
            // The address IS logged, on purpose. It was just submitted in cleartext by whoever is
            // asking; the log is not where it leaks, and without it this line answers no question.
            _logger.LogInformation(
                "Password reset requested for {Email} from {ClientIp}, which matches no account that " +
                "can use one. No token minted and no email sent; the caller is not told this.",
                address, clientIp ?? "unknown");
            return;
        }

        var now = UtcNow();
        var token = NewToken();
        var expiresAt = now.Add(_options.ResetLifetime);
        var requestedFrom = Truncate(clientIp, 64);
        var accountId = account.UserId;
        long rowId = 0;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Supersede first, then mint — in ONE transaction, so there is never an instant where
            // two reset links work. Two live links is not a theoretical state: double-clicking the
            // button produces it, and it is the state in which a customer who reports "I got a
            // reset email I did not ask for" cannot be made safe by asking them to request their
            // own, because the stranger's link would still be live.
            await _db.Set<PasswordResetToken>()
                .Where(t => t.UserId == accountId
                            && t.RedeemedAtUtc == null
                            && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAtUtc, (DateTime?)now)
                    .SetProperty(t => t.RevokedBy, Actor)
                    .SetProperty(t => t.RevocationReason, "Superseded by a newer reset request."), ct);

            var row = new PasswordResetToken
            {
                UserId = accountId,
                TenantId = account.TenantId,
                TokenHash = HashOf(token),
                IssuedAtUtc = now,
                ExpiresAtUtc = expiresAt,
                RequestedFromIp = requestedFrom
            };
            _db.Set<PasswordResetToken>().Add(row);
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            rowId = row.Id;
        });

        // No token, not even a prefix: a log aggregator is exactly the kind of place a live reset
        // link must never be recoverable from.
        _logger.LogInformation(
            "Issued password reset {ResetId} for user {UserId} from {ClientIp}, expiring {ExpiresAtUtc:O}.",
            rowId, accountId, clientIp ?? "unknown", expiresAt);

        await SendResetEmailAsync(rowId, account, token, expiresAt, ct);
    }

    /// <summary>
    /// The lookup by email address that makes this module need an enumeration rule at all.
    ///
    /// <para><c>AuthRepository.LoginAsync</c> does the same lookup and does not need one, because
    /// a caller there has to present a password to learn anything and every failure answers
    /// "Invalid email or password." Here there is no password to present, so the ONLY thing
    /// standing between an address list and a customer directory is that the response never varies.
    /// That is enforced by the caller returning <c>Task</c>; this method is where the fact worth
    /// hiding is actually computed.</para>
    ///
    /// <para><c>IgnoreQueryFilters</c> because the request is anonymous: there is no tenant scope
    /// to filter by, and relying on the filter being a no-op would make the result depend on how
    /// the caller happened to resolve the context.</para>
    ///
    /// <para><b>Active accounts only.</b> A deactivated user must not be able to bring themselves
    /// back with a link they mailed to themselves — deactivation is an administrative decision and
    /// self-service recovery is not an appeal against it. This is also the one behavioural
    /// difference from activation, which deliberately DOES set <c>IsActive</c>, because there the
    /// account is dormant-by-design and the invitation is the operator's decision to wake it.</para>
    /// </summary>
    private async Task<ResettableAccount?> FindResettableAccountAsync(string address, CancellationToken ct)
    {
        // Ordered rather than Single: public."Users" carries a unique index on Email
        // (UQ__Users__A9D10534A3A2A11E, over a citext column), so at most one row can match today.
        // AuthRepository nonetheless contemplates the same address in several business units, and
        // if that uniqueness is ever relaxed this must degrade to "the oldest active account"
        // rather than to an exception thrown at an anonymous caller — which would be both a 500
        // and, because only real addresses could cause it, an enumeration oracle of its own.
        //
        // ToLower on both sides, matching AuthRepository exactly rather than leaning on the column
        // being citext. The two lookups have to agree: an address that signs in as "Sara@acme.com"
        // and does not match here would produce a reset request that silently does nothing, and
        // the enumeration rule guarantees the user is never told why.
        var normalised = address.ToLowerInvariant();
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Email.ToLower() == normalised && u.IsActive == true)
            .OrderBy(u => u.CreatedOn).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.Email, u.FirstName, u.Buid })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        // Resolved by projection, not by materialising the entity: nexora_identity_app's SELECT on
        // platform."Tenants" is column-scoped by 20260805105320 to Id, PrimaryBusinessUnitId,
        // Status and PlanId, so a query that reads the whole row is answered with 42501 on
        // PostgreSQL and passes silently on SQLite. Reading exactly the two granted columns is
        // what keeps this working in production.
        //
        // A miss is not a failure. TenantId is only there so an offboarding purge can find these
        // rows; a user whose business unit is not any tenant's primary one still deserves to be
        // able to get back into their account.
        long? tenantId = user.Buid is null
            ? null
            : await _db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.PrimaryBusinessUnitId == user.Buid)
                .Select(t => (long?)t.Id)
                .FirstOrDefaultAsync(ct);

        return new ResettableAccount(user.Id, tenantId, user.Email, user.FirstName);
    }

    private sealed record ResettableAccount(long UserId, long? TenantId, string Email, string? FirstName);

    // ==== deliver ================================================================================

    /// <summary>
    /// Composes and dispatches the reset email, and never lets a mail problem reach the caller.
    ///
    /// <para>Returning a bool would be a leak: the only way to observe it would be for
    /// <see cref="RequestResetAsync"/> to branch on it, and the whole module is built so that
    /// there is nothing there to branch on. A failure is a log line and a
    /// <see cref="PasswordResetToken.SendCount"/> that stayed at zero.</para>
    /// </summary>
    private async Task SendResetEmailAsync(
        long resetId, ResettableAccount account, string token, DateTime expiresAt, CancellationToken ct)
    {
        try
        {
            var message = ComposeResetEmail(account, token, expiresAt);
            var receipt = await _emailSender.SendAsync(message, ct);

            if (receipt is null)
            {
                // A NULL receipt is how IEmailSender says "the provider did not accept this" —
                // it is not an exception, and treating it as success is how a deployment
                // discovers months later that its default console provider was only ever
                // logging the messages it claimed to send.
                _logger.LogWarning(
                    "The email provider did not accept password reset {ResetId} for user {UserId}. " +
                    "The link is valid but unreachable; the user must request another once mail works.",
                    resetId, account.UserId);
                return;
            }

            // Issue and delivery are separate facts, recorded separately.
            var sentAt = UtcNow();
            await _db.Set<PasswordResetToken>()
                .Where(t => t.Id == resetId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.LastSentAtUtc, (DateTime?)sentAt)
                    .SetProperty(t => t.SendCount, t => t.SendCount + 1), ct);

            _logger.LogInformation(
                "Dispatched password reset {ResetId} for user {UserId} via {Provider}.",
                resetId, account.UserId, receipt.Provider);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never rethrow. The token row is already committed, and turning a mail outage into a
            // 500 would do two damaging things at once: hand the caller a response that DIFFERS
            // from the one an unknown address gets — which is the enumeration oracle this module
            // exists to close — and tell an honest user that the product is broken when the only
            // thing wrong is that one message did not leave the building.
            _logger.LogError(exception,
                "Failed to send password reset {ResetId} for user {UserId}. The link remains valid.",
                resetId, account.UserId);
        }
    }

    private EmailMessage ComposeResetEmail(ResettableAccount account, string token, DateTime expiresAt)
    {
        var expiresOn = expiresAt.ToString("dddd d MMMM yyyy 'at' HH:mm 'UTC'");
        var validityWindow = DescribeWindow(_options.ResetLifetime);
        var supportEmail = string.IsNullOrWhiteSpace(_options.SupportEmail)
            ? _notifications.ReplyToAddress
            : _options.SupportEmail;
        var supportLine = string.IsNullOrWhiteSpace(supportEmail)
            ? "If the link has expired, ask for another from the sign-in page."
            : $"If the link has expired, request another from the sign-in page or contact {supportEmail}.";

        // Two models over the same values. The recipient's name is customer-supplied free text
        // that lands inside an HTML document, so the HTML model is encoded; the plain-text part
        // must stay literal or a person called "O'Neill & Sons" reads as "O&#39;Neill &amp; Sons"
        // in every text-only client.
        var raw = new Dictionary<string, string?>
        {
            ["recipientName"] = FirstNameOf(account.FirstName, account.Email),
            ["resetUrl"] = BuildResetUrl(token),
            ["expiresOn"] = expiresOn,
            ["validityWindow"] = validityWindow,
            ["supportLine"] = supportLine
        };
        var encoded = raw.ToDictionary(
            pair => pair.Key,
            pair => (string?)System.Net.WebUtility.HtmlEncode(pair.Value ?? string.Empty));

        var html = PasswordResetEmailTemplate.Render(encoded);
        var text = PasswordResetEmailTemplate.Render(raw);

        var message = new EmailMessage
        {
            Subject = text.Subject,
            HtmlBody = html.HtmlBody,
            TextBody = text.TextBody,
            // Metadata for the outbound-email diagnostics, when a tenant could be resolved. Null
            // is a legitimate value here — see PasswordResetToken.TenantId — and the guard treats
            // it as untagged rather than as an error.
            TenantId = account.TenantId?.ToString()
        };
        message.AddTo(account.Email, FirstNameOf(account.FirstName, account.Email));
        return message;
    }

    /// <inheritdoc />
    public string BuildResetUrl(string token)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_notifications.AppBaseUrl)
            ? "https://app.nexora.local"
            : _notifications.AppBaseUrl.TrimEnd('/');

        var path = (string.IsNullOrWhiteSpace(_options.ResetPasswordPath)
                ? "reset-password"
                : _options.ResetPasswordPath)
            .Trim('/');

        // Path segment, not a query parameter, because that is the route the reset page declares
        // (/reset-password/:token). The token is unpadded base64url precisely so it survives this
        // position untouched by any URL parser between the mail client and the browser.
        return $"{baseUrl}/{path}/{Uri.EscapeDataString(token)}";
    }

    // ==== preview ================================================================================

    /// <inheritdoc />
    public async Task<PasswordResetChallenge> InspectAsync(string? token, CancellationToken ct = default)
    {
        var (row, state) = await ResolveAsync(token, ct);
        if (row is null || state != PasswordResetTokenState.Valid)
            return PasswordResetChallenge.Rejected(state);

        var user = await LoadAccountAsync(row.UserId, ct);
        if (user is null)
        {
            // The token outlived the account, or the account was deactivated after the link was
            // sent. Reported as Invalid rather than as its own state: the second case is an
            // administrative decision that the person holding the link is not entitled to learn
            // about from an anonymous endpoint, and in both cases their useful next step is the
            // same one an unrecognised link gives them.
            _logger.LogWarning(
                "Password reset {ResetId} references user {UserId}, which no longer exists or is no " +
                "longer active. Reported as an unrecognised link.",
                row.Id, row.UserId);
            return PasswordResetChallenge.Rejected(PasswordResetTokenState.Invalid);
        }

        return new PasswordResetChallenge(PasswordResetTokenState.Valid, new PasswordResetPreview
        {
            // The SAME masking rule the activation preview uses, called rather than reimplemented.
            // Two independent maskers would drift, and the drift would be invisible until one of
            // them started showing more of an address than the other.
            Email = TenantAdminInvitationService.MaskEmail(user.Email),
            RecipientFirstName = FirstNameOf(user.FirstName, user.Email),
            ExpiresAtUtc = row.ExpiresAtUtc,
            MinimumPasswordLength = _options.EffectiveMinimumPasswordLength
        });
    }

    // ==== complete ===============================================================================

    /// <inheritdoc />
    public async Task<PasswordResetResult> CompleteAsync(
        string? token, string? newPassword, string? clientIp, CancellationToken ct = default)
    {
        var (row, state) = await ResolveAsync(token, ct);
        if (row is null || state != PasswordResetTokenState.Valid)
            return PasswordResetResult.TokenRejected(state);

        var user = await LoadAccountAsync(row.UserId, ct);
        if (user is null) return PasswordResetResult.TokenRejected(PasswordResetTokenState.Invalid);

        // The identical policy the activation flow applies, including the same hard floor. A
        // second, laxer rule for the recovery path would mean the strength of an account's
        // credential depended on which door it was set through — and the recovery door is the one
        // a hurried person uses.
        var policy = ActivationPasswordPolicy.Validate(
            newPassword, user.Email, _options.EffectiveMinimumPasswordLength);
        if (!policy.IsAcceptable)
            // The token is NOT spent by a rejected password. Someone who mistypes must not have
            // to go back to the sign-in page and start the whole flow again.
            return PasswordResetResult.PasswordRejected(policy.Failures);

        // Hashed OUTSIDE the transaction: BCrypt mints a fresh salt per call, and a retried
        // execution-strategy attempt must not store a different hash from the one the caller was
        // told about.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(newPassword!);
        var now = UtcNow();
        var redeemedFrom = Truncate(clientIp, 64);
        var resetId = row.Id;
        var accountId = row.UserId;

        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // THE claim. Everything that makes a reset token spendable is restated inside this
            // WHERE clause, so the database — not this process — decides who wins. Two
            // simultaneous completions both issue this statement; exactly one matches a row.
            var claimed = await _db.Set<PasswordResetToken>()
                .Where(t => t.Id == resetId
                            && t.RedeemedAtUtc == null
                            && t.RevokedAtUtc == null
                            && t.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RedeemedAtUtc, (DateTime?)now)
                    .SetProperty(t => t.RedeemedFromIp, redeemedFrom), ct);

            if (claimed != 1)
            {
                await tx.RollbackAsync(ct);
                // Somebody else won the race between the read above and this statement. Reported
                // as Used because that is what it now is, and because "already used" is the
                // sentence that makes a customer look for who else has their link.
                return PasswordResetResult.TokenRejected(PasswordResetTokenState.Used);
            }

            // IgnoreQueryFilters because this request is anonymous: there is no tenant scope to
            // filter by, and relying on the filter being a no-op would make the write depend on
            // how the caller happened to resolve the context.
            //
            // IsActive is deliberately absent from this SetProperty list, and the omission is the
            // control: 20260807002456 grants nexora_identity_app UPDATE on it for activation, so
            // the privilege exists and only this code declines to use it. A reset may change what
            // an account's credential IS; it may not change whether the account is allowed to
            // exist. The predicate also re-states IsActive, so an account deactivated in the
            // seconds between the read and this write matches nothing and is left alone.
            var updated = await _db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == accountId && u.IsActive == true)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PasswordHash, passwordHash)
                    // A reset ends every session minted under the old credential. Runs as
                    // nexora_identity_app, which holds UPDATE("SecurityStamp") since
                    // 20260902120000_UserSecurityStamp for exactly this statement.
                    .SetProperty(u => u.SecurityStamp, SecurityStamps.NewStamp())
                    .SetProperty(u => u.ModifiedOn, (DateTime?)now)
                    .SetProperty(u => u.ModifiedBy, Actor), ct);
            _sessions?.Evict(accountId);

            if (updated != 1)
            {
                await tx.RollbackAsync(ct);
                return PasswordResetResult.TokenRejected(PasswordResetTokenState.Invalid);
            }

            // Any OTHER live reset for the same account dies with this one. Without it, a person
            // who clicked "forgot password" three times would leave two working links able to
            // re-set their password after they had already chosen it — and if any of those three
            // requests was made by somebody else, that is a silent account takeover.
            await _db.Set<PasswordResetToken>()
                .Where(t => t.UserId == accountId
                            && t.Id != resetId
                            && t.RedeemedAtUtc == null
                            && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAtUtc, (DateTime?)now)
                    .SetProperty(t => t.RevokedBy, Actor)
                    .SetProperty(t => t.RevocationReason, "Superseded when the password was reset."), ct);

            await tx.CommitAsync(ct);
            return PasswordResetResult.Completed();
        });

        if (result.Status != PasswordResetStatus.Completed) return result;

        // AFTER the commit, and never able to fail it.
        //
        // The lockout this clears is the one the person just tripped: five wrong guesses at their
        // own password is EXACTLY the sequence that precedes clicking "forgot password", and
        // LoginAttemptThrottle's progressive window would then refuse the new credential for up to
        // an hour. A recovery flow that ends with "your password is set — now wait" is a recovery
        // flow that produces a support call, which is the call this whole feature exists to end.
        //
        // It is not a weakening. Reaching this line requires having spent a live single-use token
        // that was mailed to the account's own address, which is a stronger demonstration of
        // control than the password the counter was protecting. The counter is keyed by address on
        // LoginPlane.Tenant, so this clears one account's budget and nobody else's.
        await _loginThrottle.RegisterSuccessAsync(LoginPlane.Tenant, user.Email, ct);

        _logger.LogInformation(
            "Password reset {ResetId} completed for user {UserId} from {ClientIp}.",
            resetId, accountId, clientIp ?? "unknown");

        return result;
    }

    // ==== internals ==============================================================================

    /// <summary>
    /// Turns a presented token into the row it unlocks, plus a verdict.
    ///
    /// <para>Everything a caller who does NOT hold a real token can reach collapses to
    /// <see cref="PasswordResetTokenState.Invalid"/>: malformed, wrong length, no such hash, and a
    /// token whose account has been deleted or deactivated all answer the same way. The three
    /// states that say more — expired, used, revoked — require having been sent a genuine link, so
    /// they disclose nothing the holder did not already have, and each one is the difference
    /// between a customer who knows what to do next and a customer who telephones support.</para>
    /// </summary>
    private async Task<(PasswordResetToken? Row, PasswordResetTokenState State)> ResolveAsync(
        string? token, CancellationToken ct)
    {
        var presented = token?.Trim();
        if (string.IsNullOrEmpty(presented)
            || presented.Length < MinimumTokenLength
            || presented.Length > MaximumTokenLength)
            return (null, PasswordResetTokenState.Invalid);

        var hash = HashOf(presented);

        // Exact match on a UNIQUE index over a hash: there is no prefix scan and no per-character
        // comparison for a timing side channel to live in, which is what makes the lookup itself
        // safe against enumeration.
        var row = await _db.Set<PasswordResetToken>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null) return (null, PasswordResetTokenState.Invalid);

        // Belt and braces on top of that: the final comparison is fixed-time, so if this lookup is
        // ever rewritten to fetch candidates and compare in memory, the rewrite does not quietly
        // reintroduce a byte-at-a-time oracle.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(row.TokenHash),
                Encoding.ASCII.GetBytes(hash)))
            return (null, PasswordResetTokenState.Invalid);

        // Redeemed outranks revoked: completing a reset revokes the account's other outstanding
        // links, so the winning row is momentarily both, and "already used" is the true and more
        // useful of the two.
        if (row.RedeemedAtUtc is not null) return (row, PasswordResetTokenState.Used);
        if (row.RevokedAtUtc is not null) return (row, PasswordResetTokenState.Revoked);
        if (row.ExpiresAtUtc <= UtcNow()) return (row, PasswordResetTokenState.Expired);

        return (row, PasswordResetTokenState.Valid);
    }

    /// <summary>
    /// The account a live token names, or null if it is gone or has been deactivated since.
    ///
    /// <para>Projected rather than materialised so the password hash is never loaded into this
    /// process at all — there is no reason for a reset to read the credential it is replacing,
    /// and a value that is never in memory cannot be logged, serialised or compared by accident.</para>
    /// </summary>
    private async Task<ResettableAccount?> LoadAccountAsync(long userId, CancellationToken ct)
    {
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive == true)
            .Select(u => new { u.Id, u.Email, u.FirstName })
            .FirstOrDefaultAsync(ct);

        return user is null ? null : new ResettableAccount(user.Id, null, user.Email, user.FirstName);
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        // URL-safe base64 without padding: the token travels as a path segment and gets copied
        // out of an email by hand when the button does not work, so '+', '/' and '=' — each of
        // which is mangled by some mail client or URL parser — are all excluded.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A greeting name that is never blank and never the whole address. Falls back to the local
    /// part of the email so a record with an empty FirstName still produces "Hi sara," rather
    /// than "Hi ,".
    /// </summary>
    private static string FirstNameOf(string? name, string email)
    {
        var trimmed = name?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
            return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var at = email.IndexOf('@');
        return at > 0 ? email[..at] : "there";
    }

    private static string DescribeWindow(TimeSpan window) =>
        window.TotalMinutes < 60
            ? $"{Math.Round(window.TotalMinutes)} minutes"
            : $"{Math.Round(window.TotalHours)} hour{(Math.Round(window.TotalHours) == 1 ? "" : "s")}";

    private static string? Truncate(string? value, int maximum) =>
        value is null || value.Length <= maximum ? value : value[..maximum];
}

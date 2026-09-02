using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// Default <see cref="ITenantAdminInvitationService"/>.
///
/// <para><b>The two invariants everything else here serves.</b> First: the cleartext token
/// exists only as the return value of <see cref="IssueAsync"/> and as text inside one email —
/// the database holds SHA-256, and nothing logs, audits, or serialises the original. Second:
/// an invitation is spent by exactly one caller, ever, because the spend is a single UPDATE
/// whose WHERE clause IS the single-use rule. Reading the row, deciding it looks live, and then
/// writing would leave a window; under two clicks of the same link from two devices — which is
/// ordinary user behaviour, not an attack — both would pass the check and the second would
/// silently overwrite the first person's password.</para>
/// </summary>
public sealed class TenantAdminInvitationService : ITenantAdminInvitationService
{
    /// <summary>
    /// 256 bits from a CSPRNG. Large enough that guessing is not a threat model anyone has to
    /// reason about again, and the reason a plain SHA-256 (rather than a work-factored hash) is
    /// the right thing to store: there is no dictionary to defend against.
    /// </summary>
    private const int TokenBytes = 32;

    // Cheap shape guard so an obviously malformed token costs no database round trip. The
    // bounds are generous; correctness comes from the hash lookup, not from this.
    private const int MinimumTokenLength = 32;
    private const int MaximumTokenLength = 256;

    private readonly ErpRfqAutomationContext _db;
    private readonly IEmailSender _emailSender;
    private readonly NotificationsOptions _notifications;
    private readonly TenantOnboardingOptions _options;
    private readonly ILogger<TenantAdminInvitationService> _logger;
    private readonly TimeProvider _timeProvider;

    public TenantAdminInvitationService(
        ErpRfqAutomationContext db,
        IEmailSender emailSender,
        IOptions<NotificationsOptions> notifications,
        IOptions<TenantOnboardingOptions> options,
        ILogger<TenantAdminInvitationService> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _emailSender = emailSender;
        _notifications = notifications.Value;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ==== issue ==================================================================================

    public async Task<IssuedTenantAdminInvitation> IssueAsync(
        ErpRfqAutomationContext unitOfWork,
        TenantAdminInvitationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(request);

        var now = UtcNow();
        var token = NewToken();

        var invitation = new TenantAdminInvitation
        {
            TenantId = request.TenantId,
            UserId = request.UserId,
            Email = Truncate(request.Email.Trim(), 256)!,
            TenantName = Truncate(
                string.IsNullOrWhiteSpace(request.TenantName)
                    ? "your organisation"
                    : request.TenantName.Trim(), 256)!,
            TokenHash = HashOf(token),
            IssuedAtUtc = now,
            ExpiresAtUtc = now.Add(_options.InvitationLifetime),
            IssuedBy = Truncate(
                string.IsNullOrWhiteSpace(request.IssuedBy) ? "platform" : request.IssuedBy.Trim(), 256)!
        };

        // The caller's context, deliberately: SaveChanges here joins whatever transaction the
        // caller already opened, so a provision that rolls back takes the invitation with it.
        // No BeginTransaction, no execution strategy — both belong to the caller, and nesting
        // either inside theirs is exactly how EF throws on a retrying context.
        unitOfWork.Set<TenantAdminInvitation>().Add(invitation);
        await unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Issued tenant administrator invitation {InvitationId} for tenant {TenantId}, user {UserId}, " +
            "expiring {ExpiresAtUtc:O}.",
            invitation.Id, invitation.TenantId, invitation.UserId, invitation.ExpiresAtUtc);

        return new IssuedTenantAdminInvitation
        {
            InvitationId = invitation.Id,
            TenantId = invitation.TenantId,
            UserId = invitation.UserId,
            Email = invitation.Email,
            RecipientName = FirstNameOf(request.RecipientName, invitation.Email),
            TenantName = request.TenantName,
            ExpiresAtUtc = invitation.ExpiresAtUtc,
            Token = token,
            ActivationUrl = BuildActivationUrl(token)
        };
    }

    public string BuildActivationUrl(string token)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_notifications.AppBaseUrl)
            ? "https://app.nexora.local"
            : _notifications.AppBaseUrl.TrimEnd('/');

        var path = (string.IsNullOrWhiteSpace(_options.ActivationPath)
                ? "activate"
                : _options.ActivationPath)
            .Trim('/');

        // Path segment, not a query parameter, because that is the route the activation page
        // declares (/activate/:token). The token is unpadded base64url precisely so it survives
        // this position untouched by any URL parser between the mail client and the browser.
        return $"{baseUrl}/{path}/{Uri.EscapeDataString(token)}";
    }

    // ==== deliver ================================================================================

    public async Task<bool> SendInvitationEmailAsync(
        IssuedTenantAdminInvitation issued, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issued);

        try
        {
            var message = ComposeInvitationEmail(issued);
            var receipt = await _emailSender.SendAsync(message, ct);

            if (receipt is null)
            {
                _logger.LogWarning(
                    "The email provider did not accept the activation invitation {InvitationId} for tenant {TenantId}. " +
                    "The invitation is still valid; resend it once mail delivery is working.",
                    issued.InvitationId, issued.TenantId);
                return false;
            }

            // Issue and delivery are separate facts. Recording the dispatch is what lets an
            // operator answer "did we actually send it?" without reading provider logs — on a
            // console-provider deployment (the default) the answer is always "no, it was logged".
            var sentAt = UtcNow();
            await _db.Set<TenantAdminInvitation>()
                .Where(i => i.Id == issued.InvitationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.LastSentAtUtc, (DateTime?)sentAt)
                    .SetProperty(i => i.SendCount, i => i.SendCount + 1), ct);

            _logger.LogInformation(
                "Dispatched activation invitation {InvitationId} for tenant {TenantId} via {Provider}.",
                issued.InvitationId, issued.TenantId, receipt.Provider);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never rethrow. Provisioning has already committed by the time this runs; turning a
            // mail outage into a 500 would tell the operator the tenant was not created when it
            // was, and the fix (resend) is one endpoint away.
            _logger.LogError(exception,
                "Failed to send the activation invitation {InvitationId} for tenant {TenantId}. " +
                "The invitation remains valid and can be resent.",
                issued.InvitationId, issued.TenantId);
            return false;
        }
    }

    private EmailMessage ComposeInvitationEmail(IssuedTenantAdminInvitation issued)
    {
        var expiresOn = issued.ExpiresAtUtc.ToString("dddd d MMMM yyyy 'at' HH:mm 'UTC'");
        var validityWindow = DescribeWindow(_options.InvitationLifetime);
        var supportEmail = string.IsNullOrWhiteSpace(_options.SupportEmail)
            ? _notifications.ReplyToAddress
            : _options.SupportEmail;
        var supportLine = string.IsNullOrWhiteSpace(supportEmail)
            ? "If the link has expired, ask whoever set up your workspace to send a new one."
            : $"If the link has expired, contact {supportEmail} for a new one.";

        // Two models over the same values. The tenant name and the administrator's name are
        // operator-supplied free text that lands inside an HTML document, so the HTML model is
        // encoded; the plain-text part must stay literal or a company called "Smith & Sons"
        // reads as "Smith &amp; Sons" in every text-only client.
        var raw = new Dictionary<string, string?>
        {
            ["recipientName"] = issued.RecipientName,
            ["companyName"] = issued.TenantName,
            ["activationUrl"] = issued.ActivationUrl,
            ["expiresOn"] = expiresOn,
            ["validityWindow"] = validityWindow,
            ["supportLine"] = supportLine
        };
        var encoded = raw.ToDictionary(
            pair => pair.Key,
            pair => (string?)System.Net.WebUtility.HtmlEncode(pair.Value ?? string.Empty));

        var html = TenantActivationEmailTemplate.Render(encoded);
        var text = TenantActivationEmailTemplate.Render(raw);

        var message = new EmailMessage
        {
            Subject = text.Subject,
            HtmlBody = html.HtmlBody,
            TextBody = text.TextBody,
            TenantId = issued.TenantId.ToString()
        };
        message.AddTo(issued.Email, issued.RecipientName);
        return message;
    }

    // ==== preview ================================================================================

    public async Task<TenantActivationChallenge> InspectAsync(
        string? token, CancellationToken ct = default)
    {
        var (invitation, state) = await ResolveAsync(token, ct);
        if (invitation is null || state != ActivationTokenState.Valid)
            return TenantActivationChallenge.Rejected(state);

        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == invitation.UserId, ct);
        if (user is null)
        {
            // The invitation outlived the account it belonged to. Reported as Invalid rather
            // than as a distinct state: this is a data-integrity fault on our side, and the
            // recipient's only useful next step is the same one an unrecognised link gives them.
            _logger.LogWarning(
                "Activation invitation {InvitationId} references user {UserId}, which no longer exists.",
                invitation.Id, invitation.UserId);
            return TenantActivationChallenge.Rejected(ActivationTokenState.Invalid);
        }

        // The company name comes off the INVITATION, not from platform."Tenants".
        //
        // Activation runs as nexora_identity_app, and 20260805105320 deliberately narrowed that
        // role's access to the Tenants table to the few columns the tenant plane needs for
        // suspension and entitlement checks — customer names, slugs and status reasons stopped
        // being readable from the identity plane on purpose, and
        // PlatformControlPlaneHardeningPostgreSqlTests asserts column by column that they stay
        // that way. Reading the name here would mean granting one of them back so that an
        // activation page could render a greeting, which is not a trade worth making.
        //
        // The denormalised copy is also the more truthful value: it is the name that appeared in
        // the invitation email the recipient is holding.
        return new TenantActivationChallenge(ActivationTokenState.Valid, new TenantActivationPreview
        {
            // Masked by default, even though only the token holder can get here: an invitation
            // forwarded into a shared mailbox should not hand the whole address to everyone who
            // reads it. Deployments that would rather show the customer the exact string they
            // will type at the sign-in page set TenantOnboarding:MaskActivationEmail to false.
            Email = _options.MaskActivationEmail ? MaskEmail(invitation.Email) : invitation.Email,
            CompanyName = invitation.TenantName,
            RecipientFirstName = FirstNameOf(user.FirstName, invitation.Email),
            ExpiresAtUtc = invitation.ExpiresAtUtc,
            MinimumPasswordLength = _options.EffectiveMinimumPasswordLength
        });
    }

    // ==== redeem =================================================================================

    public async Task<TenantActivationResult> RedeemAsync(
        string? token, string? newPassword, string? clientIp, CancellationToken ct = default)
    {
        var (invitation, state) = await ResolveAsync(token, ct);
        if (invitation is null || state != ActivationTokenState.Valid)
            return TenantActivationResult.TokenRejected(state);

        var policy = ActivationPasswordPolicy.Validate(
            newPassword, invitation.Email, _options.EffectiveMinimumPasswordLength);
        if (!policy.IsAcceptable)
            // The invitation is NOT spent by a rejected password. A person who mistypes their
            // new password must not have to ask the operator for a fresh link.
            return TenantActivationResult.PasswordRejected(policy.Failures);

        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == invitation.UserId, ct);
        if (user is null) return TenantActivationResult.TokenRejected(ActivationTokenState.Invalid);

        // Hashed OUTSIDE the transaction for the same reason TenantsController hashes outside
        // its execution strategy: BCrypt mints a fresh salt per call, and a retried attempt
        // must not store a different hash from the one the caller was told about.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(newPassword!);
        var now = UtcNow();
        var redeemedFrom = Truncate(clientIp, 64);
        var invitationId = invitation.Id;
        var accountId = invitation.UserId;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // THE claim. Everything that makes an invitation redeemable is restated inside this
            // WHERE clause, so the database — not this process — decides who wins. Two
            // simultaneous redemptions both issue this statement; exactly one matches a row.
            var claimed = await _db.Set<TenantAdminInvitation>()
                .Where(i => i.Id == invitationId
                            && i.RedeemedAtUtc == null
                            && i.RevokedAtUtc == null
                            && i.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.RedeemedAtUtc, (DateTime?)now)
                    .SetProperty(i => i.RedeemedFromIp, redeemedFrom), ct);

            if (claimed != 1)
            {
                await tx.RollbackAsync(ct);
                // Somebody else won the race between the read above and this statement. Reported
                // as Used because that is what it now is, and because "already used" is the
                // sentence that makes a customer look for who else has their link.
                return TenantActivationResult.TokenRejected(ActivationTokenState.Used);
            }

            // IgnoreQueryFilters because this request is anonymous: there is no tenant scope to
            // filter by, and relying on the filter being a no-op would make the write depend on
            // how the caller happened to resolve the context.
            var updated = await _db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == accountId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PasswordHash, passwordHash)
                    // New credential ⇒ new revocation handle (docs/design/token-revocation.md).
                    .SetProperty(u => u.SecurityStamp, ERP_RFQ_Automation.Security.SecurityStamps.NewStamp())
                    .SetProperty(u => u.IsActive, (bool?)true)
                    .SetProperty(u => u.DeactivatedAtUtc, (DateTime?)null)
                    .SetProperty(u => u.ModifiedOn, (DateTime?)now)
                    .SetProperty(u => u.ModifiedBy, "tenant-activation"), ct);

            if (updated != 1)
            {
                await tx.RollbackAsync(ct);
                return TenantActivationResult.TokenRejected(ActivationTokenState.Invalid);
            }

            // Any OTHER live invitation for the same account dies with this one. Without it, an
            // operator who resent twice would leave a second working link able to re-set the
            // administrator's password after they had already chosen it — a silent account
            // takeover by whoever still has the older email.
            await _db.Set<TenantAdminInvitation>()
                .Where(i => i.UserId == accountId
                            && i.Id != invitationId
                            && i.RedeemedAtUtc == null
                            && i.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.RevokedAtUtc, (DateTime?)now)
                    .SetProperty(i => i.RevokedBy, "tenant-activation")
                    .SetProperty(i => i.RevocationReason, "Superseded when the account was activated."), ct);

            await tx.CommitAsync(ct);

            // Redemption is anonymous by construction, so there is no platform actor to attribute
            // it to and IPlatformAuditService would (correctly) refuse to write a success row for
            // one. The durable record is the invitation itself — RedeemedAtUtc plus the client
            // address — and this line is the operational breadcrumb beside it. No token.
            _logger.LogInformation(
                "Activation invitation {InvitationId} redeemed for tenant {TenantId}, user {UserId} from {ClientIp}.",
                invitationId, invitation.TenantId, accountId, clientIp ?? "unknown");

            // Can this person actually sign in now? The invitation is issued by the LAST step of
            // provisioning, so the ordinary case at this moment is a tenant still in
            // TenantStatus.Provisioning, whose login AuthRepository refuses until an operator
            // takes the governed activation decision. Read AFTER the commit — the password is set
            // either way, and a status read must never be able to fail the redemption itself.
            //
            // IgnoreQueryFilters because this request is anonymous and carries no tenant scope,
            // exactly as the user update above does.
            var signInAvailable = await _db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == invitation.TenantId)
                .Select(t => (TenantStatus?)t.Status)
                .FirstOrDefaultAsync(ct) == TenantStatus.Active;

            return TenantActivationResult.ActivatedWith(signInAvailable);
        });
    }

    // ==== operator surface =======================================================================

    public async Task<IReadOnlyList<TenantAdminInvitationSummary>> ListAsync(
        long tenantId, CancellationToken ct = default)
    {
        var now = UtcNow();
        var rows = await _db.Set<TenantAdminInvitation>().AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.IssuedAtUtc).ThenByDescending(i => i.Id)
            .ToListAsync(ct);

        return rows.Select(i => new TenantAdminInvitationSummary
        {
            Id = i.Id,
            UserId = i.UserId,
            Email = i.Email,
            Status = DescribeStatus(i, now),
            IssuedAtUtc = i.IssuedAtUtc,
            ExpiresAtUtc = i.ExpiresAtUtc,
            RedeemedAtUtc = i.RedeemedAtUtc,
            RevokedAtUtc = i.RevokedAtUtc,
            RevokedBy = i.RevokedBy,
            RevocationReason = i.RevocationReason,
            LastSentAtUtc = i.LastSentAtUtc,
            SendCount = i.SendCount,
            IssuedBy = i.IssuedBy
        }).ToList();
    }

    public async Task<IssuedTenantAdminInvitation?> ReissueAsync(
        long tenantId, long? userId, string issuedBy, CancellationToken ct = default)
    {
        var tenant = await _db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return null;

        var targetUserId = userId ?? await ResolveFoundingAdminIdAsync(tenant, ct);
        if (targetUserId is null) return null;

        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        // Refuse to invite a user who does not belong to this tenant's primary business unit.
        // Without the check, an operator with a tenant id and a guessed user id could mail an
        // activation link for somebody ELSE's account to an address of their choosing.
        if (user is null || user.Buid != tenant.PrimaryBusinessUnitId) return null;

        var now = UtcNow();
        var supersededBy = Truncate(issuedBy, 256);
        var supersededUserId = targetUserId.Value;
        IssuedTenantAdminInvitation issued = null!;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Supersede first, then issue — in one transaction, so there is never an instant
            // where two links work. "Reissue" that left the old token alive would mean a
            // customer who reports a leaked invite cannot actually be made safe by resending.
            await RevokeOutstandingForUserAsync(_db, tenantId, supersededUserId,
                supersededBy ?? "platform", "Superseded by a reissued invitation.", ct);

            issued = await IssueAsync(_db, new TenantAdminInvitationRequest
            {
                TenantId = tenantId,
                UserId = supersededUserId,
                Email = user.Email,
                RecipientName = user.FirstName,
                TenantName = tenant.Name,
                IssuedBy = issuedBy
            }, ct);

            await tx.CommitAsync(ct);
        });

        return issued;
    }

    public async Task<bool> RevokeAsync(
        long tenantId, long invitationId, string revokedBy, string reason, CancellationToken ct = default)
    {
        var now = UtcNow();
        var actor = Truncate(revokedBy, 256);
        var why = Truncate(reason, 1000);

        // Same conditional-update discipline as redemption: revoking is only meaningful against
        // a live invitation, and "was it live?" has to be answered by the statement that changes
        // it. TenantId is in the predicate so an operator cannot revoke across tenants by id.
        var revoked = await _db.Set<TenantAdminInvitation>()
            .Where(i => i.Id == invitationId
                        && i.TenantId == tenantId
                        && i.RedeemedAtUtc == null
                        && i.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.RevokedAtUtc, (DateTime?)now)
                .SetProperty(i => i.RevokedBy, actor)
                .SetProperty(i => i.RevocationReason, why), ct);

        return revoked == 1;
    }

    public Task<int> RevokeOutstandingForUserAsync(
        ErpRfqAutomationContext unitOfWork,
        long tenantId,
        long userId,
        string revokedBy,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var now = UtcNow();
        var actor = Truncate(revokedBy, 256);
        var why = Truncate(reason, 1000);

        // TenantId is in the predicate for the same reason it is in RevokeAsync's: the caller
        // supplies both ids, and an account id that does not belong to this tenant must change
        // nothing rather than reach across the boundary. Conditional update, on the caller's
        // context, so this enlists in their transaction and no separate read can race it.
        return unitOfWork.Set<TenantAdminInvitation>()
            .Where(i => i.TenantId == tenantId
                        && i.UserId == userId
                        && i.RedeemedAtUtc == null
                        && i.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.RevokedAtUtc, (DateTime?)now)
                .SetProperty(i => i.RevokedBy, actor)
                .SetProperty(i => i.RevocationReason, why), ct);
    }

    // ==== internals ==============================================================================

    /// <summary>
    /// Turns a presented token into the invitation it unlocks, plus a verdict.
    ///
    /// <para>Everything a caller who does NOT hold a real token can reach collapses to
    /// <see cref="ActivationTokenState.Invalid"/>: malformed, wrong length, no such hash, and an
    /// invitation whose account has been deleted all answer the same way. The three states that
    /// say more — expired, used, revoked — require having been sent a genuine link, so they
    /// disclose nothing the holder did not already have, and each one is the difference between
    /// a customer who knows what to do next and a customer who telephones the operator.</para>
    /// </summary>
    private async Task<(TenantAdminInvitation? Invitation, ActivationTokenState State)> ResolveAsync(
        string? token, CancellationToken ct)
    {
        var presented = token?.Trim();
        if (string.IsNullOrEmpty(presented)
            || presented.Length < MinimumTokenLength
            || presented.Length > MaximumTokenLength)
            return (null, ActivationTokenState.Invalid);

        var hash = HashOf(presented);

        // Exact match on a UNIQUE index over a hash: there is no prefix scan and no per-character
        // comparison for a timing side channel to live in, which is what makes the lookup itself
        // safe against enumeration.
        var invitation = await _db.Set<TenantAdminInvitation>().AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null) return (null, ActivationTokenState.Invalid);

        // Belt and braces on top of that: the final comparison is fixed-time, so if this lookup
        // is ever rewritten to fetch candidates and compare in memory, the rewrite does not
        // quietly reintroduce a byte-at-a-time oracle.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(invitation.TokenHash),
                Encoding.ASCII.GetBytes(hash)))
            return (null, ActivationTokenState.Invalid);

        // Redeemed outranks revoked: activation revokes the account's other outstanding links,
        // so the winning invitation is momentarily both, and "already used" is the true and more
        // useful of the two.
        if (invitation.RedeemedAtUtc is not null) return (invitation, ActivationTokenState.Used);
        if (invitation.RevokedAtUtc is not null) return (invitation, ActivationTokenState.Revoked);
        if (invitation.ExpiresAtUtc <= UtcNow()) return (invitation, ActivationTokenState.Expired);

        return (invitation, ActivationTokenState.Valid);
    }

    /// <summary>
    /// The founding administrator of a tenant provisioned before invitations existed: the
    /// earliest-created user in the tenant's primary business unit holding an Owner-rank role.
    /// That is precisely the shape provisioning creates, so it identifies the right account
    /// without needing an invitation to already exist.
    /// </summary>
    private async Task<long?> ResolveFoundingAdminIdAsync(Tenant tenant, CancellationToken ct)
    {
        var latest = await _db.Set<TenantAdminInvitation>().AsNoTracking()
            .Where(i => i.TenantId == tenant.Id)
            .OrderByDescending(i => i.IssuedAtUtc).ThenByDescending(i => i.Id)
            .Select(i => (long?)i.UserId)
            .FirstOrDefaultAsync(ct);
        if (latest is not null) return latest;

        if (tenant.PrimaryBusinessUnitId is not long businessUnitId) return null;

        // Materialised rather than left as a subquery: IgnoreQueryFilters is a query-wide flag,
        // and composing one filtered source into another is the kind of construct that quietly
        // changes meaning between EF versions. Two small reads are worth the certainty.
        var ownerRoleIds = await _db.SetupMasters.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId
                        && s.SetupType == "Role"
                        && s.RoleRank >= RoleRanks.Owner)
            .Select(s => (long?)s.SetupId)
            .ToListAsync(ct);
        if (ownerRoleIds.Count == 0) return null;

        return await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Buid == businessUnitId && ownerRoleIds.Contains(u.RoleId))
            .OrderBy(u => u.CreatedOn).ThenBy(u => u.Id)
            .Select(u => (long?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        // URL-safe base64 without padding: the token travels as a query parameter and gets
        // copied out of an email by hand when the button does not work, so '+', '/' and '='
        // — each of which is mangled by some mail client or URL parser — are all excluded.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string DescribeStatus(TenantAdminInvitation invitation, DateTime now)
    {
        if (invitation.RedeemedAtUtc is not null) return "Redeemed";
        if (invitation.RevokedAtUtc is not null) return "Revoked";
        return invitation.ExpiresAtUtc <= now ? "Expired" : "Pending";
    }

    /// <summary>
    /// Keeps the domain intact and hides most of the mailbox. The recipient needs to recognise
    /// which of their addresses this is; nobody else needs the address itself.
    /// </summary>
    internal static string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "***";

        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];
        var head = local[..1];
        var tail = local.Length >= 4 ? local[^1..] : string.Empty;
        var hidden = new string('*', Math.Max(3, local.Length - head.Length - tail.Length));

        return $"{head}{hidden}{tail}@{domain}";
    }

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
        window.TotalHours >= 48
            ? $"{Math.Round(window.TotalDays)} days"
            : $"{Math.Round(window.TotalHours)} hours";

    private static string? Truncate(string? value, int maximum) =>
        value is null || value.Length <= maximum ? value : value[..maximum];
}

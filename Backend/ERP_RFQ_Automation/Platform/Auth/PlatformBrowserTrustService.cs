using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>Issued once, shown once, never stored. The raw token goes to the browser and the hash
/// stays here.</summary>
public sealed record PlatformBrowserTrustGrant(long Id, string Token, DateTime ExpiresAtUtc);

public interface IPlatformBrowserTrustService
{
    /// <summary>The window in force right now, and whether the control is switched on at all — from
    /// the policy row, falling back to configuration when no row exists. The login screen reads this
    /// so it can name the real duration instead of offering "remember this browser" with no number
    /// attached, and so it can decline to offer it when the platform is not honouring it.</summary>
    Task<PlatformBrowserTrustSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Remember this browser for the window the policy currently sets. Returns the raw token
    /// exactly once, or NULL when browser trust is disabled — the caller must treat that as "no trust
    /// was granted", never as a failure to report.</summary>
    Task<PlatformBrowserTrustGrant?> IssueAsync(
        long platformUserId, string? userAgent, HttpContext? httpContext, CancellationToken ct = default);

    /// <summary>Is this browser currently trusted for this operator? Returns the row and stamps
    /// <c>LastUsedAtUtc</c>, or null — which means "challenge them", never "let them in".</summary>
    Task<PlatformBrowserTrust?> RedeemAsync(
        long platformUserId, string? rawToken, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformBrowserTrustDto>> ListAsync(
        long platformUserId, CancellationToken ct = default);

    /// <summary>Revoke one trust. Takes effect on the next request, because the revocation is a
    /// column that <see cref="RedeemAsync"/> reads, not an entry in a cache that has to expire.</summary>
    Task<bool> RevokeAsync(
        long platformUserId, long trustId, ClaimsPrincipal actor, HttpContext? httpContext,
        CancellationToken ct = default);

    Task<int> RevokeAllAsync(
        long platformUserId, string reason, ClaimsPrincipal actor, HttpContext? httpContext,
        CancellationToken ct = default);
}

/// <summary>
/// "Remember this browser" — one MFA challenge per trusted browser per window, instead of one per
/// session.
///
/// <para><b>Why the control exists.</b> A platform token lives 30 minutes. Without this, an operator
/// working a normal day is challenged every half hour, which does not make them more secure — it
/// trains them to approve prompts reflexively, which is precisely the reflex that MFA fatigue
/// attacks are built on. One challenge, then a bounded window of quiet navigation, with step-up
/// re-authentication reserved for the operations that deserve an interruption.</para>
///
/// <para><b>What is stored is a hash, and this is not a formality.</b> The row holds SHA-256 of the
/// token the browser keeps. A backup, a support query, or anything reading this table under
/// BYPASSRLS therefore gets a list of digests, not a set of working second factors for every
/// operator at once. It is the same reasoning that keeps recovery codes hashed in
/// <c>PlatformMfaRecoveryCodes</c> — a "remember me" secret is a bearer credential and has to be
/// treated as one.</para>
///
/// <para><b>Bounded, always.</b> The window comes from the singleton policy row — an Owner-set,
/// audited, versioned column — falling back to <c>Platform:Mfa:BrowserTrustHours</c> only when no row
/// exists. Both are refused outside 8–720 hours, by the service AND by a database check constraint.
/// There is no "remember forever".</para>
///
/// <para><b>And it can be switched off entirely.</b> <c>BrowserTrustEnabled</c> is checked on BOTH
/// paths below. Checking it only at issuance would mean an Owner who turns the control off has turned
/// it off for the operators who never used it and left it running for everyone who did — for up to a
/// month, with the console reporting it as off. The redemption check is what makes the switch honest.</para>
/// </summary>
public sealed class PlatformBrowserTrustService : IPlatformBrowserTrustService
{
    public const string CreatedAction = "platform.mfa.browser-trust.created";
    public const string RevokedAction = "platform.mfa.browser-trust.revoked";
    public const string TargetType = nameof(PlatformBrowserTrust);

    /// <summary>The revocation reason stamped on a "revoke every remembered browser" sweep. A
    /// constant rather than a literal at the call site so the string in the row, the string in the
    /// audit metadata and the string a test asserts on cannot drift apart.</summary>
    public const string OperatorRevokedAllReason = "operator-revoked-all";

    /// <summary>Bytes of entropy in the raw token. 32 bytes is the same order as the JWT it
    /// substitutes a challenge for; anything guessable here is a second factor that can be
    /// brute-forced offline against a login endpoint.</summary>
    private const int TokenBytes = 32;

    private readonly DbContext _db;
    private readonly PlatformMfaPolicyOptions _options;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<PlatformBrowserTrustService> _log;
    private readonly TimeProvider _time;

    public PlatformBrowserTrustService(
        DbContext db,
        PlatformMfaPolicyOptions options,
        IPlatformAuditService audit,
        ILogger<PlatformBrowserTrustService> log,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _options = options;
        _audit = audit;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<PlatformBrowserTrustSettings> GetSettingsAsync(CancellationToken ct = default) =>
        _options.ResolveBrowserTrust(await LoadPolicyAsync(ct));

    public async Task<PlatformBrowserTrustGrant?> IssueAsync(
        long platformUserId, string? userAgent, HttpContext? httpContext, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.Enabled)
        {
            // Not an error and not audited: the operator ticked a box that the platform no longer
            // honours, and the honest outcome is that no trust exists. The caller returns a normal
            // session with no token on it, so the browser stores nothing and is challenged next time.
            _log.LogInformation(
                "[PlatformMfa] Browser trust is disabled by policy; no trust was issued for platform user " +
                "{PlatformUserId}.", platformUserId);
            return null;
        }

        var now = UtcNow();
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        var trust = new PlatformBrowserTrust
        {
            PlatformUserId = platformUserId,
            TokenHash = Hash(raw),
            Label = Label(userAgent),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(settings.Duration)
        };

        _db.Set<PlatformBrowserTrust>().Add(trust);
        await _db.SaveChangesAsync(ct);

        // Audited under the operator's own identity: this is them granting a second factor a longer
        // life, and it is the record that explains why the next twelve hours of their sessions carry
        // amr=mfa without a challenge in the log.
        await _audit.WriteAsync(PrincipalFor(platformUserId), CreatedAction, TargetType,
            trust.Id.ToString(),
            new
            {
                label = trust.Label,
                createdAtUtc = trust.CreatedAtUtc,
                expiresAtUtc = trust.ExpiresAtUtc,
                trustHours = settings.Hours,
                // Which authority set that number. A reviewer reading this row a month later needs to
                // know whether an Owner chose the window or the deployment default did.
                trustWindowFrom = settings.FromPolicyRow ? "policy-row" : "configuration",
                environment = _options.EnvironmentName,
                correlationId = PlatformMfaPolicyService.CorrelationId(httpContext)
            },
            actAsTenantId: null, httpContext, ct);

        return new PlatformBrowserTrustGrant(trust.Id, raw, trust.ExpiresAtUtc);
    }

    public async Task<PlatformBrowserTrust?> RedeemAsync(
        long platformUserId, string? rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || !IsMapped) return null;

        // The switch, checked HERE and not only where trusts are minted. This is the line that makes
        // "browser trust is off" true for the trusts that already exist: an Owner who disables it at
        // 10am has disabled it at 10am, rather than at some point over the following month as each
        // outstanding trust happens to expire. The rows are left alone — see
        // PlatformMfaPolicy.BrowserTrustEnabled for why switching back on restores them.
        if (!(await GetSettingsAsync(ct)).Enabled) return null;

        var now = UtcNow();
        var hash = Hash(rawToken.Trim());

        // Revocation and expiry are IN the lookup, not applied afterwards. That is what makes
        // revocation immediate: there is no cached decision to invalidate, and no window in which a
        // revoked browser is still trusted because something had already loaded the row.
        var trust = await _db.Set<PlatformBrowserTrust>()
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == hash
                                               && candidate.PlatformUserId == platformUserId
                                               && candidate.RevokedAtUtc == null
                                               && candidate.ExpiresAtUtc > now, ct);
        if (trust is null) return null;

        trust.LastUsedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return trust;
    }

    public async Task<IReadOnlyList<PlatformBrowserTrustDto>> ListAsync(
        long platformUserId, CancellationToken ct = default)
    {
        if (!IsMapped) return Array.Empty<PlatformBrowserTrustDto>();

        var now = UtcNow();
        return await _db.Set<PlatformBrowserTrust>().AsNoTracking()
            .Where(trust => trust.PlatformUserId == platformUserId
                            && trust.RevokedAtUtc == null && trust.ExpiresAtUtc > now)
            .OrderByDescending(trust => trust.CreatedAtUtc)
            .Select(trust => new PlatformBrowserTrustDto(
                trust.Id, trust.Label, trust.CreatedAtUtc, trust.ExpiresAtUtc, trust.LastUsedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeAsync(
        long platformUserId, long trustId, ClaimsPrincipal actor, HttpContext? httpContext,
        CancellationToken ct = default)
    {
        if (!IsMapped) return false;

        var now = UtcNow();
        var actorLabel = actor.FindFirst("email")?.Value ?? actor.Identity?.Name ?? "platform";

        // Scoped to the owning operator. A trust id is a small integer, and without the ownership
        // predicate the endpoint would let any authenticated operator revoke — or enumerate —
        // somebody else's remembered browsers by counting upwards.
        var revoked = await _db.Set<PlatformBrowserTrust>()
            .Where(trust => trust.Id == trustId
                            && trust.PlatformUserId == platformUserId
                            && trust.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(trust => trust.RevokedAtUtc, now)
                .SetProperty(trust => trust.RevokedBy, actorLabel)
                .SetProperty(trust => trust.RevocationReason, "operator-revoked"), ct);

        if (revoked == 0) return false;

        await _audit.WriteAsync(actor, RevokedAction, TargetType, trustId.ToString(),
            new
            {
                revokedAtUtc = now,
                revokedBy = actorLabel,
                reason = "operator-revoked",
                environment = _options.EnvironmentName,
                sessionId = PlatformMfaPolicyService.SessionId(actor),
                correlationId = PlatformMfaPolicyService.CorrelationId(httpContext)
            },
            actAsTenantId: null, httpContext, ct);

        _log.LogInformation(
            "[PlatformMfa] Browser trust {TrustId} revoked for platform user {PlatformUserId} by {Actor}.",
            trustId, platformUserId, actorLabel);
        return true;
    }

    public async Task<int> RevokeAllAsync(
        long platformUserId, string reason, ClaimsPrincipal actor, HttpContext? httpContext,
        CancellationToken ct = default)
    {
        if (!IsMapped) return 0;

        var now = UtcNow();
        var actorLabel = actor.FindFirst("email")?.Value ?? actor.Identity?.Name ?? "platform";
        var revoked = await _db.Set<PlatformBrowserTrust>()
            .Where(trust => trust.PlatformUserId == platformUserId && trust.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(trust => trust.RevokedAtUtc, now)
                .SetProperty(trust => trust.RevokedBy, actorLabel)
                .SetProperty(trust => trust.RevocationReason, reason), ct);

        if (revoked == 0) return 0;

        await _audit.WriteAsync(actor, RevokedAction, TargetType, platformUserId.ToString(),
            new
            {
                revokedCount = revoked,
                revokedAtUtc = now,
                revokedBy = actorLabel,
                reason,
                environment = _options.EnvironmentName,
                sessionId = PlatformMfaPolicyService.SessionId(actor),
                correlationId = PlatformMfaPolicyService.CorrelationId(httpContext)
            },
            actAsTenantId: null, httpContext, ct);

        return revoked;
    }

    private bool IsMapped => _db.Model.FindEntityType(typeof(PlatformBrowserTrust)) is not null;

    /// <summary>
    /// The singleton policy row, or null when there is none — which
    /// <see cref="PlatformMfaPolicyOptions.ResolveBrowserTrust"/> reads as "use the configured seed".
    ///
    /// <para>Read on every issuance and every redemption rather than cached, on the same terms as
    /// <c>PlatformMfaPolicyService.GetEffectiveAsync</c>: a cached copy is a window in which the
    /// control is switched off and this process has not noticed, and the cost is one indexed read on
    /// a single-row table on a path that is already doing a hash lookup.</para>
    ///
    /// <para>Projected rather than materialised so this never tracks the policy row into a change
    /// tracker that is about to save a browser trust.</para>
    /// </summary>
    private async Task<PlatformMfaPolicy?> LoadPolicyAsync(CancellationToken ct)
    {
        if (_db.Model.FindEntityType(typeof(PlatformMfaPolicy)) is null) return null;

        return await _db.Set<PlatformMfaPolicy>().AsNoTracking()
            .Where(policy => policy.Id == PlatformMfaPolicy.SingletonId)
            .Select(policy => new PlatformMfaPolicy
            {
                BrowserTrustEnabled = policy.BrowserTrustEnabled,
                BrowserTrustHours = policy.BrowserTrustHours
            })
            .FirstOrDefaultAsync(ct);
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    /// <summary>SHA-256, hex, 64 characters — the width the column is fixed at. No salt and no
    /// iteration count on purpose: the input is 256 bits of cryptographic randomness, so there is no
    /// dictionary to slow down, and a per-row salt would make the lookup a table scan.</summary>
    internal static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A short, human-recognisable name for the trust list. The raw User-Agent is neither —
    /// it is long, it is a fingerprint, and no operator has ever recognised their own laptop from
    /// one.</summary>
    private static string? Label(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        var browser = userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "Browser";
        var platform = userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "unknown platform";
        return $"{browser} on {platform}";
    }

    private static ClaimsPrincipal PrincipalFor(long platformUserId) =>
        new(new ClaimsIdentity(
        [
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, platformUserId.ToString())
        ], PlatformAuthConstants.Scheme));
}

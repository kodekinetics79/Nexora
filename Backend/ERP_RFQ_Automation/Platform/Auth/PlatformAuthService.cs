using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ERP_RFQ_Automation.Platform.Auth;

public interface IPlatformAuthService
{
    Task<PlatformLoginResponse> LoginAsync(PlatformLoginRequest request);
    Task<PlatformLoginResponse> CompleteMfaChallengeAsync(
        PlatformMfaChallengeRequest request, string? userAgent = null,
        HttpContext? httpContext = null, CancellationToken ct = default);

    /// <summary>
    /// Re-verify the caller's CURRENT password and stamp their session with the moment they did it.
    /// The stamp is what <c>PlatformHighRiskOperationFilter</c> reads, so a step-up is proved
    /// server-side and is bounded by <c>Platform:Mfa:PasswordReauthWindowMinutes</c>.
    /// </summary>
    Task<DateTime?> ReauthenticateAsync(
        long platformUserId, string? jti, string? currentPassword, CancellationToken ct = default);
    Task<PlatformMfaStatusResponse> GetMfaStatusAsync(long platformUserId, CancellationToken ct = default);
    Task<PlatformMfaEnrollmentStartResponse> BeginMfaEnrollmentAsync(
        long platformUserId, CancellationToken ct = default);
    Task<PlatformMfaEnrollmentConfirmResponse> ConfirmMfaEnrollmentAsync(
        long platformUserId, PlatformMfaEnrollmentConfirmRequest request, CancellationToken ct = default);
    Task<bool> RevokeSessionAsync(string jti, string revokedBy, CancellationToken ct = default);

    /// <summary>
    /// Mint the short-lived, read-only TENANT token used for support impersonation.
    /// It is signed/validated by the tenant scheme (audience "RFQ") — NOT the
    /// platform scheme — and is stamped act_sub / impersonated / read-only. The
    /// returned <c>Jti</c> keys the revocable <see cref="ImpersonationSession"/> row
    /// that the caller MUST persist alongside issuance.
    /// </summary>
    (string Token, DateTime ExpiresAtUtc, string Jti) IssueImpersonationToken(
        long actingPlatformUserId, Tenant tenant, long businessUnitId, string reason);
}

/// <summary>
/// Issues platform tokens (audience <c>nexora-platform</c>, <c>scope=platform</c>,
/// <c>platformRole</c>, and NO tenant claim) and the short-lived read-only tenant
/// token for impersonation. Mirrors the JWT construction style of
/// <c>AuthRepository</c>. (ADR-0005 §3)
/// </summary>
public class PlatformAuthService : IPlatformAuthService
{
    internal static readonly TimeSpan MfaChallengeLifetime = TimeSpan.FromMinutes(5);
    internal const int MfaChallengeMaxAttempts = 5;
    internal const int RecoveryCodeCount = 10;

    private readonly ErpRfqAutomationContext _context;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformAuthService> _logger;
    private readonly IPlatformMfaPolicyProvider? _mfaPolicy;
    private readonly IPlatformBrowserTrustService? _browserTrust;

    /// <param name="mfaPolicy">
    /// The server-authoritative enforcement policy. OPTIONAL in the constructor and REQUIRED in
    /// behaviour: when it is absent — a unit test constructing this service directly — enforcement
    /// is treated as <see cref="PlatformMfaMode.REQUIRED"/>, which is the strict path this class has
    /// always taken. A missing dependency must never be the thing that relaxes a security control.
    /// </param>
    /// <param name="browserTrust">
    /// Absent means "remember this browser" is simply not offered, and every sign-in is challenged.
    /// Same fail-closed reasoning.
    /// </param>
    public PlatformAuthService(
        ErpRfqAutomationContext context,
        IConfiguration config,
        ILogger<PlatformAuthService> logger,
        IPlatformMfaPolicyProvider? mfaPolicy = null,
        IPlatformBrowserTrustService? browserTrust = null)
    {
        _context = context;
        _config = config;
        _logger = logger;
        _mfaPolicy = mfaPolicy;
        _browserTrust = browserTrust;
    }

    public async Task<PlatformLoginResponse> LoginAsync(PlatformLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new UnauthorizedAccessException("Email and password are required.");

        var user = await _context.Set<PlatformUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower());

        if (user == null)
        {
            _logger.LogWarning("Platform login: user not found for {Email}", request.Email);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Platform login: bad password for {Email}", request.Email);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Platform login: inactive account {Email}", request.Email);
            throw new UnauthorizedAccessException("Platform account is not active.");
        }

        var issuedAt = DateTime.UtcNow;

        // The enforcement decision is taken HERE, on the server, from a persisted policy row and the
        // process's own environment classification. Nothing on the request participates in it — a
        // client that sends "mfaRequired: false" changes nothing at all.
        var effective = _mfaPolicy is null
            ? null
            : await _mfaPolicy.GetEffectiveAsync();
        var enforcementDisabled = effective?.EnforcementDisabled ?? false;
        var relaxed = effective?.PasswordOnlySessionsPermitted ?? false;

        var mfa = await _context.Set<PlatformMfaCredential>().AsNoTracking()
            .SingleOrDefaultAsync(credential => credential.PlatformUserId == user.Id);
        if (mfa?.EnabledAtUtc is not null && !enforcementDisabled)
        {
            // "Remember this browser": the second factor was presented on this machine inside its
            // trust window and the server still remembers it, so challenging again buys nothing
            // except the habit of approving prompts. Redemption is a hash lookup that carries the
            // revocation and expiry predicates with it, so a revoked trust falls straight through to
            // the challenge below on the very next attempt.
            var trust = _browserTrust is null
                ? null
                : await _browserTrust.RedeemAsync(user.Id, request.BrowserTrustToken);
            if (trust is null)
                return withChallenge(await GetOrCreateChallengeAsync(user.Id, issuedAt));

            var trusted = IssuePlatformSession(user, issuedAt, issuedAt, trust.Id);
            trusted.BrowserTrustUsed = true;
            trusted.BrowserTrustExpiresAtUtc = trust.ExpiresAtUtc;
            await _context.SaveChangesAsync();
            await UpdateLastLoginAsync(user.Id, issuedAt);
            _logger.LogInformation(
                "Platform login for {Email} satisfied MFA from remembered browser trust {TrustId}.",
                user.Email, trust.Id);
            return trusted;
        }

        // Sec-D2: an operator who has never enrolled still gets a token — refusing one outright
        // would make first-run enrollment impossible and would lock out the bootstrap owner on a
        // fresh deployment. What changed is what that token is WORTH: with no amr=mfa claim it
        // now satisfies PlatformPolicies.Enrollment only, so it can read its own MFA status,
        // enrol, and log out, and nothing else. It used to satisfy PlatformScope and therefore
        // every read on the control plane.
        //
        // ...unless the server-authoritative policy has relaxed enforcement, in which case the same
        // password-only token satisfies the full scope — a decision made by the assertion in
        // AddPlatformPolicies from the same policy row, never from the claim below, which stays
        // honestly absent. An enrolled operator's TOTP secret is untouched by any of this and is
        // waiting exactly where it was when enforcement returns.
        var response = IssuePlatformSession(user, issuedAt, mfaAuthenticatedAtUtc: null);
        response.MfaEnforcementRelaxed = relaxed;
        response.MfaEnrollmentRequired = !relaxed;
        await _context.SaveChangesAsync();

        await UpdateLastLoginAsync(user.Id, issuedAt);
        _logger.LogInformation("Platform login successful for {Email} ({Role})", user.Email, user.PlatformRole);
        return response;

        PlatformLoginResponse withChallenge(PlatformMfaChallenge challenge) => new()
        {
            Id = user.Id,
            Email = user.Email,
            PlatformRole = user.PlatformRole.ToString(),
            MfaRequired = true,
            MfaChallengeId = challenge.Id,
            MfaChallengeExpiresAtUtc = challenge.ExpiresAtUtc
        };
    }

    public async Task<PlatformLoginResponse> CompleteMfaChallengeAsync(
        PlatformMfaChallengeRequest request, string? userAgent = null,
        HttpContext? httpContext = null, CancellationToken ct = default)
    {
        if (request.ChallengeId == Guid.Empty
            || (string.IsNullOrWhiteSpace(request.TotpCode) == string.IsNullOrWhiteSpace(request.RecoveryCode)))
            throw new UnauthorizedAccessException("A valid MFA challenge and exactly one verification code are required.");

        var now = DateTime.UtcNow;
        var challenge = await _context.Set<PlatformMfaChallenge>()
            .Include(value => value.PlatformUser)
            .SingleOrDefaultAsync(value => value.Id == request.ChallengeId, ct)
            ?? throw new UnauthorizedAccessException("The MFA challenge is invalid or expired.");
        if (challenge.ConsumedAtUtc is not null || challenge.ExpiresAtUtc <= now
            || challenge.FailedAttempts >= MfaChallengeMaxAttempts || !challenge.PlatformUser.IsActive)
            throw new UnauthorizedAccessException("The MFA challenge is invalid or expired.");

        var credential = await _context.Set<PlatformMfaCredential>()
            .SingleOrDefaultAsync(value => value.PlatformUserId == challenge.PlatformUserId, ct);
        if (credential?.EnabledAtUtc is null)
            throw new UnauthorizedAccessException("MFA is not enabled for this account.");

        PlatformMfaRecoveryCode? recovery = null;
        var recoveryUsed = false;
        if (!string.IsNullOrWhiteSpace(request.TotpCode))
        {
            if (!PlatformTotp.TryVerify(credential.TotpSecret, request.TotpCode, now,
                    credential.LastAcceptedTotpStep, out var acceptedStep))
                return await RejectChallengeAsync(challenge, ct);
            credential.LastAcceptedTotpStep = acceptedStep;
        }
        else
        {
            var hash = HashRecoveryCode(request.RecoveryCode!);
            recovery = await _context.Set<PlatformMfaRecoveryCode>().SingleOrDefaultAsync(value =>
                value.PlatformUserId == challenge.PlatformUserId && value.CodeHash == hash, ct);
            if (recovery?.UsedAtUtc is not null || recovery is null)
                return await RejectChallengeAsync(challenge, ct);
            recovery.UsedAtUtc = now;
            recoveryUsed = true;
        }

        challenge.ConsumedAtUtc = now;
        challenge.PlatformUser.LastLogin = now;
        var response = IssuePlatformSession(challenge.PlatformUser, now, now);
        response.RecoveryCodeUsed = recoveryUsed;
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new UnauthorizedAccessException("The MFA code was already used.");
        }

        // AFTER the challenge has committed, never before: a trust minted alongside a challenge that
        // then failed to consume would be a second factor granted for a code that was never accepted.
        if (request.RememberBrowser && _browserTrust is not null)
        {
            var grant = await _browserTrust.IssueAsync(challenge.PlatformUserId, userAgent, httpContext, ct);
            // The one and only time this value leaves the server. The row holds its SHA-256.
            response.BrowserTrustToken = grant.Token;
            response.BrowserTrustExpiresAtUtc = grant.ExpiresAtUtc;
        }

        return response;
    }

    /// <summary>
    /// Step-up: prove the person at the keyboard is still the operator who signed in.
    ///
    /// <para>Returns the moment of proof, or null if the password was wrong — and stamps it on the
    /// SESSION row rather than handing back a token, so the window cannot be extended by replaying a
    /// response and cannot outlive the session it was granted on. Revoking the session revokes the
    /// step-up with it, in the same UPDATE, for free.</para>
    /// </summary>
    public async Task<DateTime?> ReauthenticateAsync(
        long platformUserId, string? jti, string? currentPassword, CancellationToken ct = default)
    {
        if (platformUserId <= 0 || string.IsNullOrWhiteSpace(jti) || string.IsNullOrEmpty(currentPassword))
            return null;

        var user = await _context.Set<PlatformUser>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == platformUserId, ct);
        if (user is null || !user.IsActive || !BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return null;

        var now = DateTime.UtcNow;
        var stamped = await _context.Set<PlatformSession>()
            .Where(session => session.Jti == jti
                              && session.PlatformUserId == platformUserId
                              && session.RevokedAtUtc == null
                              && session.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.LastPasswordReauthAtUtc, now), ct);

        // A correct password on a session that is already revoked or expired proves nothing useful:
        // there is nothing left to step up.
        return stamped == 1 ? now : null;
    }

    public async Task<PlatformMfaStatusResponse> GetMfaStatusAsync(
        long platformUserId, CancellationToken ct = default)
    {
        var credential = await _context.Set<PlatformMfaCredential>().AsNoTracking()
            .SingleOrDefaultAsync(value => value.PlatformUserId == platformUserId, ct);
        var remaining = credential?.EnabledAtUtc is null ? 0 : await _context.Set<PlatformMfaRecoveryCode>()
            .CountAsync(value => value.PlatformUserId == platformUserId && value.UsedAtUtc == null, ct);
        return new(credential?.EnabledAtUtc is not null, credential?.EnabledAtUtc, remaining);
    }

    public async Task<PlatformMfaEnrollmentStartResponse> BeginMfaEnrollmentAsync(
        long platformUserId, CancellationToken ct = default)
    {
        var user = await _context.Set<PlatformUser>().SingleOrDefaultAsync(value => value.Id == platformUserId, ct)
            ?? throw new UnauthorizedAccessException("The platform account is invalid.");
        if (!user.IsActive) throw new UnauthorizedAccessException("Platform account is not active.");

        var credential = await _context.Set<PlatformMfaCredential>()
            .SingleOrDefaultAsync(value => value.PlatformUserId == platformUserId, ct);
        if (credential?.EnabledAtUtc is not null)
            throw new InvalidOperationException("MFA is already enabled for this account.");

        var secret = PlatformTotp.GenerateSecret();
        if (credential is null)
        {
            credential = new PlatformMfaCredential { PlatformUserId = platformUserId };
            _context.Set<PlatformMfaCredential>().Add(credential);
        }
        credential.TotpSecret = secret;
        credential.LastAcceptedTotpStep = null;
        credential.UpdatedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        var label = Uri.EscapeDataString($"Nexora Platform:{user.Email}");
        var issuer = Uri.EscapeDataString("Nexora Platform");
        return new(secret, $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6&period=30");
    }

    public async Task<PlatformMfaEnrollmentConfirmResponse> ConfirmMfaEnrollmentAsync(
        long platformUserId, PlatformMfaEnrollmentConfirmRequest request, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry must not reuse generated recovery-code entities or stale concurrency
            // snapshots left in the tracker by a rolled-back attempt.
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);
            var now = DateTime.UtcNow;
            var credential = await _context.Set<PlatformMfaCredential>()
                .SingleOrDefaultAsync(value => value.PlatformUserId == platformUserId, ct)
                ?? throw new InvalidOperationException("Start MFA enrollment before confirming it.");
            if (credential.EnabledAtUtc is not null)
                throw new InvalidOperationException("MFA is already enabled for this account.");
            if (!PlatformTotp.TryVerify(credential.TotpSecret, request.TotpCode, now, null, out var acceptedStep))
                throw new UnauthorizedAccessException("The authenticator code is invalid.");

            var user = await _context.Set<PlatformUser>().SingleAsync(value => value.Id == platformUserId, ct);
            credential.EnabledAtUtc = now;
            credential.LastAcceptedTotpStep = acceptedStep;
            credential.UpdatedAtUtc = now;
            user.SessionGeneration = checked(user.SessionGeneration + 1);

            var rawCodes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => GenerateRecoveryCode()).ToArray();
            _context.Set<PlatformMfaRecoveryCode>().AddRange(rawCodes.Select(code => new PlatformMfaRecoveryCode
            {
                PlatformUserId = platformUserId,
                CodeHash = HashRecoveryCode(code),
                CreatedAtUtc = now
            }));
            await _context.SaveChangesAsync(ct);
            await _context.Set<PlatformSession>()
                .Where(session => session.PlatformUserId == platformUserId && session.RevokedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(session => session.RevokedAtUtc, now)
                    .SetProperty(session => session.RevokedBy, user.Email)
                    .SetProperty(session => session.RevocationReason, "platform-mfa-enabled"), ct);
            await transaction.CommitAsync(ct);
            return new PlatformMfaEnrollmentConfirmResponse(now, rawCodes);
        });
    }

    private async Task<PlatformLoginResponse> RejectChallengeAsync(
        PlatformMfaChallenge challenge, CancellationToken ct)
    {
        if (_context.Database.IsRelational())
        {
            // One SQL statement counts every parallel guess. A stale EF snapshot must never make
            // concurrent failures collapse into a single attempt.
            await _context.Set<PlatformMfaChallenge>()
                .Where(value => value.Id == challenge.Id && value.ConsumedAtUtc == null
                                && value.ExpiresAtUtc > DateTime.UtcNow
                                && value.FailedAttempts < MfaChallengeMaxAttempts)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    value => value.FailedAttempts, value => value.FailedAttempts + 1), ct);
        }
        else
        {
            challenge.FailedAttempts++;
            try { await _context.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { }
        }
        throw new UnauthorizedAccessException("The MFA code is invalid.");
    }

    private async Task<PlatformMfaChallenge> GetOrCreateChallengeAsync(long platformUserId, DateTime now)
    {
        if (!_context.Database.IsNpgsql())
        {
            var existing = await FindActiveChallengeAsync(platformUserId, now);
            if (existing is not null) return existing;
            var created = NewChallenge(platformUserId, now);
            _context.Add(created);
            await _context.SaveChangesAsync();
            return created;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync();
            var lockKey = unchecked(0x4E584D4600000000L ^ platformUserId); // "NXMF" namespace
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})");
            var existing = await FindActiveChallengeAsync(platformUserId, now);
            if (existing is not null)
            {
                await transaction.CommitAsync();
                return existing;
            }

            await _context.Set<PlatformMfaChallenge>()
                .Where(value => value.PlatformUserId == platformUserId
                                && value.ConsumedAtUtc == null && value.ExpiresAtUtc <= now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ConsumedAtUtc, now));
            var created = NewChallenge(platformUserId, now);
            _context.Add(created);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return created;
        });
    }

    private Task<PlatformMfaChallenge?> FindActiveChallengeAsync(long platformUserId, DateTime now) =>
        _context.Set<PlatformMfaChallenge>().AsNoTracking()
            .Where(value => value.PlatformUserId == platformUserId
                            && value.ConsumedAtUtc == null
                            && value.ExpiresAtUtc > now)
            .OrderByDescending(value => value.CreatedAtUtc)
            .FirstOrDefaultAsync();

    private static PlatformMfaChallenge NewChallenge(long platformUserId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), PlatformUserId = platformUserId, CreatedAtUtc = now,
        ExpiresAtUtc = now.Add(MfaChallengeLifetime)
    };

    private static string GenerateRecoveryCode()
    {
        var compact = PlatformTotp.Base32Encode(RandomNumberGenerator.GetBytes(15));
        return string.Join('-', Enumerable.Range(0, 4).Select(index => compact.Substring(index * 6, 6)));
    }

    private static string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private async Task UpdateLastLoginAsync(long userId, DateTime issuedAt)
    {
        // LastLogin is display metadata, not part of the security decision.
        try
        {
            await _context.Set<PlatformUser>().Where(u => u.Id == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastLogin, issuedAt));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform login: could not update LastLogin for user {PlatformUserId}", userId);
        }
    }

    private PlatformLoginResponse IssuePlatformSession(
        PlatformUser user, DateTime issuedAt, DateTime? mfaAuthenticatedAtUtc, long? browserTrustId = null)
    {
        var (token, expires, jti) = GeneratePlatformToken(user, issuedAt, mfaAuthenticatedAtUtc);

        // Session persistence is mandatory: a token that is not represented in
        // the revocation ledger must never be returned to the caller.
        _context.Set<PlatformSession>().Add(new PlatformSession
        {
            Jti = jti,
            PlatformUserId = user.Id,
            SessionGeneration = user.SessionGeneration,
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = expires,
            MfaAuthenticatedAtUtc = mfaAuthenticatedAtUtc,
            // Pinned so revoking the browser trust ends this session too — PlatformSessionValidator
            // re-checks it on every request.
            BrowserTrustId = browserTrustId
        });
        return new PlatformLoginResponse
        {
            Id = user.Id,
            Email = user.Email,
            PlatformRole = user.PlatformRole.ToString(),
            Token = token,
            ExpiresAtUtc = expires,
            MfaRequired = false,
            // Sec-D2: a session minted without a second factor carries no amr=mfa claim and
            // therefore satisfies only PlatformPolicies.Enrollment. Say so on the response
            // rather than leaving the operator to discover it as a wall of 403s.
            MfaEnrollmentRequired = mfaAuthenticatedAtUtc is null
        };
    }

    public async Task<bool> RevokeSessionAsync(
        string jti, string revokedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;
        var now = DateTime.UtcNow;
        return await _context.Set<PlatformSession>()
            .Where(session => session.Jti == jti && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAtUtc, now)
                .SetProperty(session => session.RevokedBy, revokedBy)
                .SetProperty(session => session.RevocationReason, "platform-logout"), ct) == 1;
    }

    private (string Token, DateTime ExpiresAtUtc, string Jti) GeneratePlatformToken(
        PlatformUser user, DateTime issuedAtUtc, DateTime? mfaAuthenticatedAtUtc)
    {
        var signingKey = _config["Jwt:PlatformKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            signingKey = _config["Jwt:Key"];

        var issuer = _config["Jwt:PlatformIssuer"] ?? _config["Jwt:Issuer"];
        var audience = _config["Jwt:PlatformAudience"] ?? PlatformAuthConstants.Audience;
        var minutes = double.TryParse(_config["Jwt:PlatformExpiryMinutes"], out var m) ? m : 30;

        // NOTE: deliberately NO businessUnitId / tenantId / roleId claim — a platform
        // token must never satisfy the tenant scope or tenant RBAC handlers.
        var jti = Guid.NewGuid().ToString();
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
            new Claim(PlatformAuthConstants.PlatformRoleClaim, user.PlatformRole.ToString()),
            new Claim(PlatformAuthConstants.SessionGenerationClaim, user.SessionGeneration.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jti)
        };
        if (mfaAuthenticatedAtUtc is DateTime mfaAt)
        {
            claims.Add(new Claim(PlatformAuthConstants.AuthenticationMethodClaim,
                PlatformAuthConstants.MfaAuthenticationMethod));
            claims.Add(new Claim(PlatformAuthConstants.MfaAuthenticatedAtClaim,
                new DateTimeOffset(mfaAt).ToUnixTimeSeconds().ToString()));
        }

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)), SecurityAlgorithms.HmacSha256);
        var expires = issuedAtUtc.AddMinutes(minutes);

        var token = new JwtSecurityToken(
            issuer: issuer, audience: audience, claims: claims, expires: expires, signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires, jti);
    }

    /// <summary>Inclusive bounds for the impersonation-token lifetime, in minutes.</summary>
    public const double MinImpersonationExpiryMinutes = 5;
    public const double MaxImpersonationExpiryMinutes = 60;

    public (string Token, DateTime ExpiresAtUtc, string Jti) IssueImpersonationToken(
        long actingPlatformUserId, Tenant tenant, long businessUnitId, string reason)
    {
        // Impersonation mints a TENANT token: tenant signing key + tenant audience
        // so it is accepted by the DEFAULT scheme and scoped by the tenant query
        // filters (businessUnitId claim). It carries NO platform scope, and NO
        // roleId — so tenant RBAC permission checks (which require roleId) fail,
        // making the session effectively read-only. (ADR-0005 §3)
        var signingKey = _config["Jwt:Key"];
        var issuer = _config["Jwt:Issuer"];
        var audience = _config["Jwt:Audience"];
        var minutes = double.TryParse(_config["Jwt:ImpersonationExpiryMinutes"], out var m) ? m : 15;

        // Clamp to [5, 60] minutes: a misconfigured (or hostile) value can neither
        // mint an effectively non-expiring support token nor one too short to use.
        minutes = Math.Clamp(minutes, MinImpersonationExpiryMinutes, MaxImpersonationExpiryMinutes);

        var jti = Guid.NewGuid().ToString();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, $"impersonation:{tenant.Id}"),
            new Claim("businessUnitId", businessUnitId.ToString()),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.TenantScopeValue),
            new Claim(PlatformAuthConstants.ActSubClaim, actingPlatformUserId.ToString()),
            new Claim(PlatformAuthConstants.ImpersonatedClaim, "true"),
            new Claim(PlatformAuthConstants.ReadOnlyClaim, "true"),
            new Claim(PlatformAuthConstants.ImpersonationReasonClaim, reason),
            new Claim(JwtRegisteredClaimNames.Jti, jti)
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)), SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(minutes);

        var token = new JwtSecurityToken(
            issuer: issuer, audience: audience, claims: claims, expires: expires, signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires, jti);
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// The read seam the AUTHORIZATION layer depends on. Deliberately one method: the policy pipeline
/// must be able to ask "is a second factor being enforced" without acquiring the ability to change
/// the answer.
/// </summary>
public interface IPlatformMfaPolicyProvider
{
    Task<PlatformEffectiveMfaPolicy> GetEffectiveAsync(CancellationToken ct = default);
}

public interface IPlatformMfaPolicyService : IPlatformMfaPolicyProvider
{
    /// <summary>The Owner-facing read model, including the counts that make a confirmation dialog
    /// mean something.</summary>
    Task<PlatformMfaPolicyDto> GetAsync(CancellationToken ct = default);

    Task<PlatformMfaPolicyChangeResult> ChangeAsync(
        ChangePlatformMfaPolicyRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct = default);
}

/// <summary>
/// Server-authoritative platform MFA enforcement.
///
/// <para><b>The rule this class exists to make unbreakable:</b> the mode is decided here, from a
/// persisted row, the clock and the backend's own environment classification. No request field, no
/// header and no frontend flag participates. The console can ask; it cannot decide. Every refusal
/// below is checked on the WRITE path and again on the READ path, because a control enforced only
/// where the UI makes the change is a control that a direct <c>UPDATE</c> against the table — or a
/// database restored from a staging snapshot — walks straight past.</para>
///
/// <para><b>Expiry is computed on read.</b> <see cref="GetEffectiveAsync"/> compares
/// <c>ExpiresAtUtc</c> to now on every call, so the first request after a bypass window closes is
/// already enforced. There is no sweeper to fail, no job to be disabled, and no window in which the
/// row says DISABLED and the platform agrees. The only thing the once-per-bypass write below does
/// is record the EVIDENCE that it happened.</para>
///
/// <para><b>What this class never touches:</b> <c>PlatformMfaCredential</c>. Enrolment is the
/// operator's property, not a consequence of enforcement. Deleting a TOTP secret when enforcement
/// is switched off would mean every operator has to re-enrol — with a QR code, an authenticator app
/// and a new set of recovery codes — the moment it is switched back on, which is precisely the cost
/// that makes people leave it off.</para>
/// </summary>
public sealed class PlatformMfaPolicyService : IPlatformMfaPolicyService
{
    public const string PolicyChangedAction = "platform.mfa.policy.changed";
    public const string BypassStartedAction = "platform.mfa.bypass.started";
    public const string BypassExpiredAction = "platform.mfa.bypass.expired";
    public const string BypassEndedAction = "platform.mfa.bypass.ended";
    public const string PolicyChangeRefusedAction = "platform.mfa.policy.change.refused";
    public const string TargetType = nameof(PlatformMfaPolicy);

    private readonly DbContext _db;
    private readonly PlatformMfaPolicyOptions _options;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<PlatformMfaPolicyService> _log;
    private readonly TimeProvider _time;

    public PlatformMfaPolicyService(
        DbContext db,
        PlatformMfaPolicyOptions options,
        IPlatformAuditService audit,
        ILogger<PlatformMfaPolicyService> log,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _options = options;
        _audit = audit;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ==== read ======================================================================================

    public async Task<PlatformEffectiveMfaPolicy> GetEffectiveAsync(CancellationToken ct = default)
    {
        var now = UtcNow();
        var row = IsMapped ? await LoadAsync(track: false, ct) : null;

        // No row, no model, no policy — REQUIRED. A deployment that has never been configured must
        // behave like the strictest one, not the loosest: the absence of a decision is not consent.
        if (row is null) return Enforced(now);

        var declared = ParseMode(row.Mode);
        var effective = declared;

        // 1. The clock. A bypass whose window has closed is over, whether or not anything ran.
        var expired = declared != PlatformMfaMode.REQUIRED
                      && row.ExpiresAtUtc is DateTime expiry && expiry <= now;
        if (expired) effective = PlatformMfaMode.REQUIRED;

        // 2. A future EffectiveFromUtc has not started yet.
        if (row.EffectiveFromUtc > now) effective = PlatformMfaMode.REQUIRED;

        // 3. The environment ceiling, applied AGAIN here and not only on the write path. This is the
        //    line that survives a production database restored from a staging dump, a hand-edited
        //    row, and a deployment whose ASPNETCORE_ENVIRONMENT changed under a policy written when
        //    it was something else. It is loud because a policy row that production is ignoring is a
        //    thing an operator needs to know exists.
        if (effective != PlatformMfaMode.REQUIRED && !_options.Permits(effective))
        {
            _log.LogError(
                "[PlatformMfa] The stored MFA policy is {Declared} but {Environment} ({Class}) does not permit it. " +
                "MFA enforcement is REQUIRED. Correct the policy row or the environment classification.",
                declared, _options.EnvironmentName, _options.EnvironmentClass);
            effective = PlatformMfaMode.REQUIRED;
        }

        if (expired) await RecordExpiryOnceAsync(row, now, ct);

        return new PlatformEffectiveMfaPolicy(
            effective, declared, _options.EnvironmentClass, _options.EnvironmentName,
            row.EffectiveFromUtc, row.ExpiresAtUtc, row.ChangedBy, row.ChangeReason,
            row.Version, row.UpdatedAtUtc, _options.IsolatedTestInfrastructure);
    }

    public async Task<PlatformMfaPolicyDto> GetAsync(CancellationToken ct = default)
    {
        var effective = await GetEffectiveAsync(ct);
        var now = UtcNow();

        var dto = ToDto(effective);

        // The numbers that turn "are you sure?" into a decision. An Owner about to disable
        // enforcement should be told how many people it affects and how many privileged sessions
        // are already MFA-bound and will therefore be unaffected either way.
        dto.EnrolledOperatorCount = await _db.Set<PlatformMfaCredential>()
            .CountAsync(credential => credential.EnabledAtUtc != null, ct);
        dto.ActiveOperatorCount = await _db.Set<PlatformUser>().CountAsync(user => user.IsActive, ct);
        dto.ActiveMfaBoundSessionCount = await _db.Set<PlatformSession>()
            .CountAsync(session => session.RevokedAtUtc == null
                                   && session.ExpiresAtUtc > now
                                   && session.MfaAuthenticatedAtUtc != null, ct);
        dto.ActiveBrowserTrustCount = IsBrowserTrustMapped
            ? await _db.Set<PlatformBrowserTrust>()
                .CountAsync(trust => trust.RevokedAtUtc == null && trust.ExpiresAtUtc > now, ct)
            : 0;

        return dto;
    }

    /// <summary>
    /// Emits the "MFA bypass expired" evidence exactly once, and never lets its failure matter.
    ///
    /// <para>The stamp is written with <c>ExecuteUpdate</c> under a <c>WHERE … IS NULL</c> guard, so
    /// two concurrent requests observing the same expiry produce one audit row rather than two — and
    /// so the change tracker is never involved on what is otherwise a pure read path.</para>
    ///
    /// <para><b>Whose name is on it.</b> There is no human actor: the window closed by itself. It is
    /// attributed to the operator who DECLARED the bypass, because the record is about their window
    /// ending, and the metadata says <c>endedBy: automatic-expiry</c> so it can never be misread as
    /// them having acted. Attributing it to whichever operator's request happened to observe the
    /// expiry would be an outright falsehood in an append-only table.</para>
    /// </summary>
    private async Task RecordExpiryOnceAsync(PlatformMfaPolicy row, DateTime now, CancellationToken ct)
    {
        if (row.ExpiryRecordedAtUtc is not null || row.ChangedByPlatformUserId is not > 0) return;

        try
        {
            var claimed = await _db.Set<PlatformMfaPolicy>()
                .Where(policy => policy.Id == PlatformMfaPolicy.SingletonId
                                 && policy.Version == row.Version
                                 && policy.ExpiryRecordedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(policy => policy.ExpiryRecordedAtUtc, now), ct);
            if (claimed != 1) return;

            await _audit.WriteAsync(
                SystemActorFor(row.ChangedByPlatformUserId.Value, row.ChangedBy),
                BypassExpiredAction, TargetType, PlatformMfaPolicy.SingletonId.ToString(),
                new
                {
                    declaredMode = row.Mode,
                    effectiveMode = nameof(PlatformMfaMode.REQUIRED),
                    expiredAtUtc = row.ExpiresAtUtc,
                    observedAtUtc = now,
                    startedBy = row.ChangedBy,
                    reason = row.ChangeReason,
                    endedBy = "automatic-expiry",
                    environment = _options.EnvironmentName,
                    environmentClass = _options.EnvironmentClass.ToString(),
                    version = row.Version
                },
                actAsTenantId: null, httpContext: null, ct: ct);

            _log.LogWarning(
                "[PlatformMfa] The MFA bypass declared by {Actor} expired at {ExpiredAt:O}. Enforcement is " +
                "REQUIRED again.", row.ChangedBy, row.ExpiresAtUtc);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The reversion already happened — it is computed from the clock above and does not
            // depend on this write. Failing the caller's request because the EVIDENCE could not be
            // written would turn a security control that worked into an outage.
            _log.LogError(exception,
                "[PlatformMfa] Could not record the MFA bypass expiry audit event. Enforcement is REQUIRED " +
                "regardless; the evidence is missing.");
        }
    }

    // ==== write =====================================================================================

    public async Task<PlatformMfaPolicyChangeResult> ChangeAsync(
        ChangePlatformMfaPolicyRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (!IsMapped)
            return Refused("The platform MFA policy table is not present in this deployment yet. Apply the " +
                           "PlatformMfaPolicyAndBrowserTrust migration, then try again. Enforcement is REQUIRED " +
                           "until it is.");

        if (!Enum.TryParse<PlatformMfaMode>((request.Mode ?? string.Empty).Trim(), ignoreCase: false, out var mode))
            return Refused(
                $"'{request.Mode}' is not a platform MFA mode. Use REQUIRED, OPTIONAL or DISABLED_TEST_ONLY.");

        // ---- the environment ceiling, before anything about the operator is considered ------------
        // Checked first so a production Owner who types a perfect reason, a perfect confirmation and
        // their correct password is still refused, and the refusal names the environment rather than
        // pretending one of their inputs was wrong.
        if (!_options.Permits(mode))
        {
            await AuditRefusalAsync(actor, mode, _options.ExplainRefusal(mode), httpContext, ct);
            return new PlatformMfaPolicyChangeResult(false, null, _options.ExplainRefusal(mode), Forbidden: true);
        }

        var actorId = ActorId(actor);
        var user = actorId > 0
            ? await _db.Set<PlatformUser>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == actorId, ct)
            : null;
        if (user is null || !user.IsActive)
            return new PlatformMfaPolicyChangeResult(false, null,
                "The acting platform account could not be verified.", Forbidden: true);

        // ---- re-authentication --------------------------------------------------------------------
        // The session is already authenticated; this proves the PERSON is. Without it, an unattended
        // console — or a stolen bearer token — is enough to turn platform MFA off, which is the one
        // change that makes every other stolen credential more valuable.
        if (string.IsNullOrEmpty(request.CurrentPassword)
            || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            await AuditRefusalAsync(actor, mode, "re-authentication failed", httpContext, ct);
            return new PlatformMfaPolicyChangeResult(false, null,
                "That password is not correct. Changing MFA enforcement requires you to re-enter your current " +
                "platform password.", Forbidden: true);
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length < PlatformMfaPolicyOptions.MinimumReasonLength)
            return Refused(
                $"Give a reason of at least {PlatformMfaPolicyOptions.MinimumReasonLength} characters. It is " +
                "recorded in the audit log and is what a reviewer reads months from now.");

        var expectedPhrase = PlatformMfaPolicyOptions.ConfirmationPhraseFor(mode);
        if (!string.Equals((request.Confirmation ?? string.Empty).Trim(), expectedPhrase, StringComparison.Ordinal))
            return Refused($"Type {expectedPhrase} exactly to confirm.");

        var now = UtcNow();
        DateTime? expiresAtUtc = null;
        if (mode != PlatformMfaMode.REQUIRED)
        {
            // ---- MANDATORY expiry -----------------------------------------------------------------
            // The defect is never the disabling. It is that nobody turns it back on, and nothing
            // anywhere complains. A bypass without an end time is the only variant of this change
            // that can outlive the reason for it.
            expiresAtUtc = request.ExpiresAtUtc
                           ?? (request.DurationHours is double hours && hours > 0
                               ? now.AddHours(hours)
                               : null);

            if (expiresAtUtc is null)
                return Refused("An expiry is required. MFA enforcement may be relaxed for a window, never " +
                               "indefinitely.");

            if (expiresAtUtc <= now)
                return Refused("The expiry must be in the future.");

            var ceiling = now.Add(_options.MaxBypassDuration);
            if (expiresAtUtc > ceiling)
                return Refused(
                    $"The maximum bypass duration is {_options.MaxBypassHours} hours " +
                    $"(until {ceiling:yyyy-MM-dd HH:mm} UTC). Re-apply the change if you still need it then.");
        }

        var row = await LoadAsync(track: true, ct);
        if (row is not null && request.ExpectedVersion is { } expected && expected != row.Version)
            return new PlatformMfaPolicyChangeResult(false, null,
                "This policy was changed by someone else while you were editing. Reload and reapply your change.",
                Conflict: true);

        var isNew = row is null;
        var previousMode = row is null ? PlatformMfaMode.REQUIRED : ParseMode(row.Mode);
        var previousExpiry = row?.ExpiresAtUtc;
        if (row is null)
        {
            row = new PlatformMfaPolicy { Id = PlatformMfaPolicy.SingletonId, CreatedAtUtc = now };
            _db.Set<PlatformMfaPolicy>().Add(row);
        }

        row.Mode = mode.ToString();
        row.EffectiveFromUtc = now;
        row.ExpiresAtUtc = expiresAtUtc;
        row.EnvironmentClass = _options.EnvironmentClass.ToString();
        row.ChangedBy = user.Email;
        row.ChangedByPlatformUserId = user.Id;
        row.ChangeReason = reason;
        row.UpdatedAtUtc = now;
        row.Version = isNew ? 1 : row.Version + 1;
        // A new window gets a new right to one expiry record. Leaving the old stamp in place would
        // silently suppress the evidence for every bypass after the first.
        row.ExpiryRecordedAtUtc = null;

        // NOTE, and this is the point of requirement 6: nothing here touches PlatformMfaCredential,
        // PlatformMfaRecoveryCode or PlatformUser.SessionGeneration. Turning enforcement off does not
        // un-enrol anybody, and turning it back on costs no operator a re-enrolment.

        var metadata = new
        {
            mode = row.Mode,
            previousMode = previousMode.ToString(),
            effectiveFromUtc = row.EffectiveFromUtc,
            expiresAtUtc = row.ExpiresAtUtc,
            previousExpiresAtUtc = previousExpiry,
            reason = row.ChangeReason,
            environment = _options.EnvironmentName,
            environmentClass = row.EnvironmentClass,
            isolatedTestInfrastructure = _options.IsolatedTestInfrastructure,
            maxBypassHours = _options.MaxBypassHours,
            actorEmail = user.Email,
            sessionId = SessionId(actor),
            correlationId = CorrelationId(httpContext),
            version = row.Version
        };

        // ONE batch, therefore one transaction: the enforcement change and the record of it commit
        // together or neither does. PlatformAuditService adds to this same scoped context and flushes
        // it, so enlisting the audit rows here — before any flush of our own — is what makes that
        // true. The email settings screen learned this the expensive way (see PlatformEmailSettings
        // Service.SaveAsync): flushing first and auditing second returned 500 for a change that had
        // already committed, and every retry then 409'd.
        try
        {
            await _audit.WriteAsync(actor, PolicyChangedAction, TargetType,
                PlatformMfaPolicy.SingletonId.ToString(), metadata, null, httpContext, ct);

            if (mode != PlatformMfaMode.REQUIRED)
                await _audit.WriteAsync(actor, BypassStartedAction, TargetType,
                    PlatformMfaPolicy.SingletonId.ToString(), metadata, null, httpContext, ct);
            else if (previousMode != PlatformMfaMode.REQUIRED)
                // Manually ended, as distinct from expired. Both restore REQUIRED; only one of them
                // has a person's name on it, and a reviewer needs to be able to tell which.
                await _audit.WriteAsync(actor, BypassEndedAction, TargetType,
                    PlatformMfaPolicy.SingletonId.ToString(),
                    new
                    {
                        previousMode = previousMode.ToString(),
                        previousExpiresAtUtc = previousExpiry,
                        endedBy = user.Email,
                        endedAtUtc = now,
                        reason = row.ChangeReason,
                        environment = _options.EnvironmentName,
                        sessionId = SessionId(actor),
                        correlationId = CorrelationId(httpContext)
                    },
                    null, httpContext, ct);

            if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new PlatformMfaPolicyChangeResult(false, null,
                "This policy was changed by someone else while you were editing. Reload and reapply your change.",
                Conflict: true);
        }

        // Error level for a relaxation even though the request succeeded: this is the line an
        // operator greps for when they want to know why a test environment stopped asking for a code.
        if (mode == PlatformMfaMode.REQUIRED)
            _log.LogWarning(
                "[PlatformMfa] MFA enforcement set to REQUIRED in {Environment} by {Actor}. Reason: {Reason}",
                _options.EnvironmentName, user.Email, reason);
        else
            _log.LogError(
                "[PlatformMfa] MFA enforcement RELAXED to {Mode} in {Environment} by {Actor} until {Expiry:O}. " +
                "Reason: {Reason}",
                row.Mode, _options.EnvironmentName, user.Email, row.ExpiresAtUtc, reason);

        return new PlatformMfaPolicyChangeResult(true, await GetAsync(ct), null);
    }

    // ==== internals =================================================================================

    /// <summary>True once the context has been spliced with <c>ApplyPlatformMfaPolicyModel</c>. Until
    /// the splice and the migration land, the effective policy is REQUIRED and changes are refused
    /// with a sentence an operator can act on — rather than an unmapped-entity exception they
    /// cannot.</summary>
    private bool IsMapped => _db.Model.FindEntityType(typeof(PlatformMfaPolicy)) is not null;

    private bool IsBrowserTrustMapped => _db.Model.FindEntityType(typeof(PlatformBrowserTrust)) is not null;

    private Task<PlatformMfaPolicy?> LoadAsync(bool track, CancellationToken ct)
    {
        var query = _db.Set<PlatformMfaPolicy>().AsQueryable();
        if (!track) query = query.AsNoTracking();
        return query.FirstOrDefaultAsync(x => x.Id == PlatformMfaPolicy.SingletonId, ct);
    }

    private PlatformEffectiveMfaPolicy Enforced(DateTime now) => new(
        PlatformMfaMode.REQUIRED, PlatformMfaMode.REQUIRED, _options.EnvironmentClass,
        _options.EnvironmentName, now, null, null, null, 0, null, _options.IsolatedTestInfrastructure);

    /// <summary>Anything unparseable is REQUIRED. A corrupted or hand-edited mode string must not be
    /// the thing that turns enforcement off.</summary>
    private static PlatformMfaMode ParseMode(string? value) =>
        Enum.TryParse<PlatformMfaMode>((value ?? string.Empty).Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : PlatformMfaMode.REQUIRED;

    private static PlatformMfaPolicyChangeResult Refused(string error) => new(false, null, error);

    private async Task AuditRefusalAsync(
        ClaimsPrincipal actor, PlatformMfaMode mode, string why, HttpContext? httpContext, CancellationToken ct)
    {
        // A refused attempt to disable MFA is more interesting than a successful one, and it is the
        // record that exists only if it is written at the moment of refusal.
        try
        {
            await _audit.WriteAsync(actor, PolicyChangeRefusedAction, TargetType,
                PlatformMfaPolicy.SingletonId.ToString(),
                new
                {
                    requestedMode = mode.ToString(),
                    refusal = why,
                    environment = _options.EnvironmentName,
                    environmentClass = _options.EnvironmentClass.ToString(),
                    sessionId = SessionId(actor),
                    correlationId = CorrelationId(httpContext)
                },
                null, httpContext, PlatformAuditResults.Failure, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.LogError(exception, "[PlatformMfa] Could not audit a refused MFA policy change.");
        }
    }

    private PlatformMfaPolicyDto ToDto(PlatformEffectiveMfaPolicy effective)
    {
        var available = Enum.GetValues<PlatformMfaMode>().Where(_options.Permits).ToList();
        return new PlatformMfaPolicyDto
        {
            Mode = effective.Mode.ToString(),
            DeclaredMode = effective.DeclaredMode.ToString(),
            EnvironmentClass = effective.EnvironmentClass.ToString(),
            EnvironmentName = effective.EnvironmentName,
            IsolatedTestInfrastructure = effective.IsolatedTestInfrastructure,
            EffectiveFromUtc = effective.EffectiveFromUtc,
            ExpiresAtUtc = effective.ExpiresAtUtc,
            BypassExpired = effective.BypassExpired,
            EnforcementDisabled = effective.EnforcementDisabled,
            PasswordOnlySessionsPermitted = effective.PasswordOnlySessionsPermitted,
            ChangedBy = effective.ChangedBy,
            ChangeReason = effective.ChangeReason,
            Version = effective.Version,
            UpdatedAtUtc = effective.UpdatedAtUtc,
            AvailableModes = available.Select(mode => mode.ToString()).ToList(),
            ConfirmationPhrases = available.ToDictionary(
                mode => mode.ToString(), PlatformMfaPolicyOptions.ConfirmationPhraseFor),
            MaxBypassHours = _options.MaxBypassHours,
            MinimumReasonLength = PlatformMfaPolicyOptions.MinimumReasonLength,
            BrowserTrustHours = _options.BrowserTrustHours
        };
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    internal static long ActorId(ClaimsPrincipal actor)
    {
        var value = actor.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : 0;
    }

    internal static string? SessionId(ClaimsPrincipal actor) =>
        actor.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

    internal static string? CorrelationId(HttpContext? httpContext) =>
        httpContext?.TraceIdentifier;

    /// <summary>Materialises the declaring operator as a principal so the once-only expiry record
    /// satisfies <c>PlatformAuditService</c>'s positive-actor invariant. Same technique
    /// <c>PlatformAuthController.Login</c> uses when the request principal is anonymous.</summary>
    private static ClaimsPrincipal SystemActorFor(long platformUserId, string? email) =>
        new(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, platformUserId.ToString()),
            new Claim("email", email ?? string.Empty)
        ], PlatformAuthConstants.Scheme));
}

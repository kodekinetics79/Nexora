using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Platform-plane login. Issues the dedicated platform token (audience
/// <c>nexora-platform</c>). Anonymous — this is the entry point. Every attempt
/// is audited: <c>platform.login</c> (success) and <c>platform.login.failed</c>
/// (result=failure, attributed to the reserved system actor because no
/// authenticated actor exists pre-auth). (ADR-0005 §3)
/// </summary>
[ApiController]
[Route("api/platform/auth")]
public class PlatformAuthController : ControllerBase
{
    private readonly IPlatformAuthService _authService;
    private readonly ILoginAttemptThrottle _loginThrottle;
    private readonly IPlatformAuditService _audit;

    public PlatformAuthController(
        IPlatformAuthService authService, ILoginAttemptThrottle loginThrottle, IPlatformAuditService audit)
    {
        _authService = authService;
        _loginThrottle = loginThrottle;
        _audit = audit;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<PlatformLoginResponse>> Login([FromBody] PlatformLoginRequest request)
    {
        // SEC-H6: same durable progressive lockout as the tenant login, but under the
        // "platform" key namespace so the two planes stay independent — a locked-out tenant
        // email can never affect a platform operator's ability to sign in, and vice versa.
        var lockout = await _loginThrottle.CheckAsync(
            LoginPlane.Platform, request?.Email, HttpContext.RequestAborted);

        // Sec5: an additional IP-keyed dimension. Beyond the per-IP failure threshold the
        // request 429s HERE — before credentials are checked and, critically, before any
        // platform.login.failed audit row could be appended, so an unauthenticated
        // flooder (rotating emails) cannot grow the append-only audit table unbounded.
        var remoteIp = HttpContext.Connection?.RemoteIpAddress?.ToString();
        var ipLockout = await _loginThrottle.CheckAsync(
            LoginPlane.PlatformIp, remoteIp, HttpContext.RequestAborted);

        if (lockout.IsLockedOut || ipLockout.IsLockedOut)
        {
            var retryAfter = lockout.RetryAfter > ipLockout.RetryAfter
                ? lockout.RetryAfter
                : ipLockout.RetryAfter;
            Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "Too many failed sign-in attempts. Please try again later."
            });
        }

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var response = await _authService.LoginAsync(request);
            // Password verification is only the first factor for enrolled users.
            // Do not record platform.login until the MFA challenge actually succeeds.
            if (response.MfaRequired)
                return Ok(response);

            await _loginThrottle.RegisterSuccessAsync(
                LoginPlane.Platform, request?.Email, HttpContext.RequestAborted);
            await _loginThrottle.RegisterSuccessAsync(
                LoginPlane.PlatformIp, remoteIp, HttpContext.RequestAborted);

            // The request principal is anonymous (this is the login endpoint), so the
            // authenticated actor is materialized from the login result itself.
            var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", response.Id.ToString()),
                new Claim("email", response.Email)
            ], PlatformAuthConstants.Scheme));
            await _audit.WriteAsync(actor, "platform.login", nameof(PlatformUser),
                response.Id.ToString(), new { email = response.Email }, null, HttpContext,
                PlatformAuditResults.Success, HttpContext.RequestAborted);

            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            await _loginThrottle.RegisterFailureAsync(
                LoginPlane.Platform, request?.Email, HttpContext.RequestAborted);
            // Sec5: advance the IP-keyed counter too, so email-rotating floods from
            // one address hit the 429 gate above and stop producing audit rows.
            await _loginThrottle.RegisterFailureAsync(
                LoginPlane.PlatformIp, remoteIp, HttpContext.RequestAborted);

            // Pre-auth failure: no platform actor exists, so the record is written
            // with result=failure under the reserved system actor (see
            // PlatformAuditService.SystemActorId).
            await _audit.WriteAsync(new ClaimsPrincipal(new ClaimsIdentity()),
                "platform.login.failed", nameof(PlatformUser), null,
                new { email = request?.Email }, null, HttpContext,
                PlatformAuditResults.Failure, HttpContext.RequestAborted);

            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPost("mfa/challenge")]
    [AllowAnonymous]
    public async Task<ActionResult<PlatformLoginResponse>> CompleteMfaChallenge(
        [FromBody] PlatformMfaChallengeRequest request, CancellationToken ct)
    {
        var remoteIp = HttpContext.Connection?.RemoteIpAddress?.ToString();
        var ipLockout = await _loginThrottle.CheckAsync(LoginPlane.PlatformIp, remoteIp, ct);
        if (ipLockout.IsLockedOut)
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(ipLockout.RetryAfter.TotalSeconds)).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "Too many failed sign-in attempts. Please try again later."
            });
        }

        try
        {
            // The User-Agent is passed for the trust LABEL only ("Chrome on macOS"), never as part of
            // the security decision: a header the client controls must not be able to make a browser
            // look trusted.
            var response = await _authService.CompleteMfaChallengeAsync(
                request, Request.Headers.UserAgent.ToString(), HttpContext, ct);
            await _loginThrottle.RegisterSuccessAsync(LoginPlane.Platform, response.Email, ct);
            await _loginThrottle.RegisterSuccessAsync(LoginPlane.PlatformIp, remoteIp, ct);
            var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", response.Id.ToString()),
                new Claim("email", response.Email)
            ], PlatformAuthConstants.Scheme));
            await _audit.WriteAsync(actor, "platform.login", nameof(PlatformUser),
                response.Id.ToString(),
                new { mfa = true, response.RecoveryCodeUsed, rememberBrowser = request.RememberBrowser },
                httpContext: HttpContext, ct: ct);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            await _loginThrottle.RegisterFailureAsync(
                LoginPlane.PlatformIp, remoteIp, ct);
            await _audit.WriteAsync(new ClaimsPrincipal(new ClaimsIdentity()),
                "platform.login.mfa.failed", nameof(PlatformUser), null, null, null,
                result: PlatformAuditResults.Failure,
                httpContext: HttpContext, ct: ct);
            return Unauthorized(new { error = ex.Message });
        }
    }

    // Sec-D2: the four endpoints below are the ONLY ones a password-only platform session may
    // reach. PlatformScope now requires amr=mfa, so an operator who has never enrolled would
    // otherwise be able to sign in and do nothing at all — including enrol. PlatformPolicies
    // .Enrollment still pins the platform scheme and still requires scope=platform; it only
    // omits the amr requirement, and it grants access to nothing but a person's own second
    // factor and their own session.
    [HttpGet("mfa")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public Task<PlatformMfaStatusResponse> GetMfaStatus(CancellationToken ct) =>
        _authService.GetMfaStatusAsync(ActorId(), ct);

    [HttpPost("mfa/enrollment")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<ActionResult<PlatformMfaEnrollmentStartResponse>> BeginMfaEnrollment(CancellationToken ct)
    {
        try
        {
            var result = await _authService.BeginMfaEnrollmentAsync(ActorId(), ct);
            await _audit.WriteAsync(User, "platform.mfa.enrollment.started", nameof(PlatformUser),
                ActorId().ToString(), httpContext: HttpContext, ct: ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("mfa/enrollment/confirm")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<ActionResult<PlatformMfaEnrollmentConfirmResponse>> ConfirmMfaEnrollment(
        [FromBody] PlatformMfaEnrollmentConfirmRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _authService.ConfirmMfaEnrollmentAsync(ActorId(), request, ct);
            await _audit.WriteAsync(User, "platform.mfa.enrollment.confirmed", nameof(PlatformUser),
                ActorId().ToString(), new { recoveryCodeCount = result.RecoveryCodes.Count },
                httpContext: HttpContext, ct: ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { error = ex.Message }); }
    }

    /// <summary>
    /// Step-up: re-enter the current platform password and stamp this session with the moment it
    /// was proved. <c>PlatformHighRiskOperationAttribute</c> reads that stamp, so a password-only
    /// session — reachable only while the server-authoritative policy has relaxed enforcement —
    /// still cannot purge a tenant, export its data, release a legal hold or finalise an invoice
    /// without the person at the keyboard proving they are still the operator who signed in.
    ///
    /// <para>On <see cref="PlatformPolicies.Enrollment"/> because that is the policy a password-only
    /// session satisfies, and a session that cannot reach this endpoint could never step up at all.
    /// It grants nothing by itself: the stamp is worthless without the Owner/TenantAdmin gate the
    /// high-risk endpoint already carries.</para>
    /// </summary>
    [HttpPost("reauthenticate")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<ActionResult<PlatformReauthenticationResponse>> Reauthenticate(
        [FromBody] PlatformReauthenticationRequest request,
        [FromServices] PlatformMfaPolicyOptions mfaOptions,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var remoteIp = HttpContext.Connection?.RemoteIpAddress?.ToString();
        var lockout = await _loginThrottle.CheckAsync(LoginPlane.PlatformIp, remoteIp, ct);
        if (lockout.IsLockedOut)
        {
            // Same durable, IP-keyed lockout the login endpoint uses. Without it this endpoint would
            // be an unthrottled password oracle for an already-authenticated session.
            Response.Headers.RetryAfter = ((int)Math.Ceiling(lockout.RetryAfter.TotalSeconds)).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "Too many failed attempts. Please try again later."
            });
        }

        var jti = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
        var provedAt = await _authService.ReauthenticateAsync(ActorId(), jti, request.CurrentPassword, ct);
        if (provedAt is null)
        {
            await _loginThrottle.RegisterFailureAsync(LoginPlane.PlatformIp, remoteIp, ct);
            await _audit.WriteAsync(User, "platform.security.reauth.failed", nameof(PlatformSession), jti,
                new { correlationId = HttpContext.TraceIdentifier }, null, HttpContext,
                PlatformAuditResults.Failure, ct);
            return Unauthorized(new { error = "That password is not correct." });
        }

        await _loginThrottle.RegisterSuccessAsync(LoginPlane.PlatformIp, remoteIp, ct);
        await _audit.WriteAsync(User, "platform.security.reauth", nameof(PlatformSession), jti,
            new
            {
                provedAtUtc = provedAt,
                windowMinutes = mfaOptions.PasswordReauthWindowMinutes,
                environment = mfaOptions.EnvironmentName,
                correlationId = HttpContext.TraceIdentifier
            },
            null, HttpContext, PlatformAuditResults.Success, ct);

        return Ok(new PlatformReauthenticationResponse(
            provedAt.Value.Add(mfaOptions.PasswordReauthWindow), mfaOptions.PasswordReauthWindowMinutes));
    }

    /// <summary>The browsers this operator has told the platform to remember. Own-account only —
    /// there is no route that reads or revokes somebody else's.</summary>
    [HttpGet("browser-trusts")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<ActionResult<IReadOnlyList<PlatformBrowserTrustDto>>> ListBrowserTrusts(
        [FromServices] IPlatformBrowserTrustService trusts, CancellationToken ct)
        => Ok(await trusts.ListAsync(ActorId(), ct));

    /// <summary>Revoke one remembered browser. Effective on the very next request: the revocation is
    /// a column that both the login redemption and <c>PlatformSessionValidator</c> read, so the
    /// session it minted stops being valid too rather than running out its remaining lifetime.</summary>
    [HttpDelete("browser-trusts/{id:long}")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<IActionResult> RevokeBrowserTrust(
        long id, [FromServices] IPlatformBrowserTrustService trusts, CancellationToken ct)
        => await trusts.RevokeAsync(ActorId(), id, User, HttpContext, ct) ? NoContent() : NotFound();

    /// <summary>
    /// Revoke every browser this operator has told the platform to remember — the "I have lost a
    /// device and I do not know which entry it is" control.
    ///
    /// <para>Scoped to the caller's own account by <c>ActorId()</c>, with no route or body parameter
    /// that could widen it. One operator revoking every OTHER operator's remembered browsers would be
    /// a denial-of-service on the whole plane dressed as a security action.</para>
    ///
    /// <para>POST rather than DELETE: it takes no identifier, it is a command, and it must not be
    /// something a stray link or a prefetch can perform. Idempotent either way — a second call
    /// revokes nothing and returns zero.</para>
    /// </summary>
    [HttpPost("browser-trusts/revoke-all")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<ActionResult<object>> RevokeAllBrowserTrusts(
        [FromServices] IPlatformBrowserTrustService trusts, CancellationToken ct)
    {
        var revoked = await trusts.RevokeAllAsync(
            ActorId(), PlatformBrowserTrustService.OperatorRevokedAllReason, User, HttpContext, ct);
        return Ok(new { revoked });
    }

    // Logout revokes only the caller's OWN jti and reads nothing. Refusing a password-only
    // session here would leave an unenrolled operator unable to end the very session that
    // cannot do anything else — a control that only makes the hole last longer.
    [HttpPost("logout")]
    [Authorize(Policy = PlatformPolicies.Enrollment)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var jti = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti)) return Unauthorized();

        var actorEmail = User.FindFirst("email")?.Value ?? "platform";
        if (await _authService.RevokeSessionAsync(jti, actorEmail, ct))
            await _audit.WriteAsync(User, "platform.logout", nameof(PlatformSession), jti,
                new { reason = "operator-logout" }, httpContext: HttpContext, ct: ct);

        // Idempotent: an already-revoked session is still logged out.
        return NoContent();
    }

    private long ActorId()
    {
        var value = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0
            ? id
            : throw new UnauthorizedAccessException("A valid platform actor is required.");
    }
}

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

    [HttpPost("logout")]
    [Authorize(Policy = PlatformPolicies.PlatformScope)]
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
}

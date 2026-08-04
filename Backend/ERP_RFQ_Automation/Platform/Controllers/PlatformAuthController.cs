using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Platform-plane login. Issues the dedicated platform token (audience
/// <c>nexora-platform</c>). Anonymous — this is the entry point. (ADR-0005 §3)
/// </summary>
[ApiController]
[Route("api/platform/auth")]
[AllowAnonymous]
public class PlatformAuthController : ControllerBase
{
    private readonly IPlatformAuthService _authService;
    private readonly ILoginAttemptThrottle _loginThrottle;

    public PlatformAuthController(IPlatformAuthService authService, ILoginAttemptThrottle loginThrottle)
    {
        _authService = authService;
        _loginThrottle = loginThrottle;
    }

    [HttpPost("login")]
    public async Task<ActionResult<PlatformLoginResponse>> Login([FromBody] PlatformLoginRequest request)
    {
        // SEC-H6: same durable progressive lockout as the tenant login, but under the
        // "platform" key namespace so the two planes stay independent — a locked-out tenant
        // email can never affect a platform operator's ability to sign in, and vice versa.
        var lockout = await _loginThrottle.CheckAsync(
            LoginPlane.Platform, request?.Email, HttpContext.RequestAborted);
        if (lockout.IsLockedOut)
        {
            Response.Headers.RetryAfter =
                ((int)Math.Ceiling(lockout.RetryAfter.TotalSeconds)).ToString();
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
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            await _loginThrottle.RegisterFailureAsync(
                LoginPlane.Platform, request?.Email, HttpContext.RequestAborted);
            return Unauthorized(new { error = ex.Message });
        }
    }
}

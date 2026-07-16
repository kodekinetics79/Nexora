using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
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

    public PlatformAuthController(IPlatformAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public async Task<ActionResult<PlatformLoginResponse>> Login([FromBody] PlatformLoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var response = await _authService.LoginAsync(request);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}

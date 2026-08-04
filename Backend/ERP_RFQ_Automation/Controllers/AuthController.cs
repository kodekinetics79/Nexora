using ERP_RFQ_Automation.DTOs.AuthDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
    public class AuthController : ControllerBase
    {
        private readonly IAuthRepository _authRepository;
        private readonly ILoginAttemptThrottle _loginThrottle;

        public AuthController(IAuthRepository authRepository, ILoginAttemptThrottle loginThrottle)
        {
            _authRepository = authRepository;
            _loginThrottle = loginThrottle;
        }

        // POST: api/Auth/Login
        [HttpPost("Login")]
        public async Task<ActionResult<LoginResponseDTO>> Login([FromBody] LoginRequestDTO request)
        {
            // SEC-H6: this endpoint previously had no lockout and no failed-attempt counter,
            // so credentials could be guessed at the full request-rate limit indefinitely.
            // The counter is persisted per (plane, email), so the lockout survives restarts
            // and is shared by every instance on the same database.
            var lockout = await _loginThrottle.CheckAsync(
                LoginPlane.Tenant, request?.Email, HttpContext.RequestAborted);
            if (lockout.IsLockedOut)
            {
                Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(lockout.RetryAfter.TotalSeconds)).ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    error = "Too many failed sign-in attempts. Please try again later."
                });
            }

            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var response = await _authRepository.LoginAsync(request);

                if (response.RequiresBusinessUnitSelection)
                {
                    // Same email+password is valid in multiple business units:
                    // no token yet — the client retries with businessUnitId set.
                    // The password was verified, so this counts as a success.
                    await _loginThrottle.RegisterSuccessAsync(
                        LoginPlane.Tenant, request?.Email, HttpContext.RequestAborted);

                    return Ok(new
                    {
                        requiresBusinessUnitSelection = true,
                        businessUnits = response.BusinessUnits
                    });
                }

                await _loginThrottle.RegisterSuccessAsync(
                    LoginPlane.Tenant, request?.Email, HttpContext.RequestAborted);
                return Ok(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                await _loginThrottle.RegisterFailureAsync(
                    LoginPlane.Tenant, request?.Email, HttpContext.RequestAborted);
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Error during login: {ex.Message}" });
            }
        }
    }
}

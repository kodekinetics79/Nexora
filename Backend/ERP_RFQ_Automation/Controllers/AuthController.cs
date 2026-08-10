using ERP_RFQ_Automation.DTOs.AuthDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Platform.Entitlements;
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
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            IAuthRepository authRepository,
            ILoginAttemptThrottle loginThrottle,
            ILogger<AuthController> logger)
        {
            _authRepository = authRepository;
            _loginThrottle = loginThrottle;
            _logger = logger;
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
            catch (EntitlementDeniedException ex)
            {
                // Credentials were valid — the ORGANIZATION is suspended/archived (403), or its
                // subscription could not be confirmed at all because the platform plane is
                // unreadable (503, Sec-D1). Neither is a credential failure, so neither may feed
                // the failed-attempt lockout counter. P2-A10: rendered by the single canonical
                // EntitlementProblemFilter shape. Caught as the BASE type rather than
                // TenantAccessDeniedException specifically, so a denial added later cannot fall
                // through to the catch-all below and be answered as a 500.
                if (ex is TenantAccessUnresolvableException)
                    Response.Headers.RetryAfter =
                        TenantAccessUnresolvableException.RetryAfterSeconds.ToString();
                return EntitlementProblemFilter.ToResult(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                await _loginThrottle.RegisterFailureAsync(
                    LoginPlane.Tenant, request?.Email, HttpContext.RequestAborted);
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                // Sec-A3: this is the product's ONE anonymous endpoint, so the caller here is by
                // definition unauthenticated — and it used to answer with ex.Message. An Npgsql or
                // EF exception names the schema, the table, the column and the constraint it
                // tripped on, which handed an unauthenticated caller a partial map of the database
                // for the price of a malformed login. The detail goes to the log, where it is
                // correlatable; the caller gets a fixed string. Same shape as
                // ProcurementController's catch-all, which already did this correctly.
                _logger.LogError(ex, "Tenant login failed with an unhandled error.");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    error = "Sign-in could not be completed. Please try again."
                });
            }
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Platform Admin → Security → Platform Authentication.
///
/// <para><b>Authorization, and why it splits where it does.</b> The class carries
/// <see cref="PlatformPolicies.Enrollment"/> — the ONE policy a password-only session satisfies —
/// because the effective-policy read has to be reachable from a session that has not presented a
/// second factor. It has to be, or the control would be unreachable in exactly the state it exists
/// to describe: with enforcement relaxed, the console has to be able to ask "is it relaxed?" before
/// it can render the banner saying so, and after a bypass expires an operator needs to be told that
/// is why they are suddenly being challenged. The read carries no secret — it says which mode is in
/// force, when it lapses and who set it, all of which the banner shows anyway.</para>
///
/// <para>Writing is <see cref="PlatformPolicies.Owner"/> AND a password re-authentication AND a
/// typed confirmation AND a mandatory expiry — see <see cref="PlatformMfaPolicyService.ChangeAsync"/>
/// for which failure each of those prevents. The role gate alone is not enough here: the Owner
/// policy is satisfied by a bearer token, and this is the one change that devalues every other
/// control on the plane.</para>
/// </summary>
[ApiController]
[Route("api/platform/auth/policy")]
[Authorize(Policy = PlatformPolicies.Enrollment)]
public class PlatformMfaPolicyController : ControllerBase
{
    private readonly IPlatformMfaPolicyService _policy;

    public PlatformMfaPolicyController(IPlatformMfaPolicyService policy) => _policy = policy;

    /// <summary>
    /// The minimum an authenticated operator needs to render the console correctly: which mode is in
    /// force, whether a banner is owed, and when the window closes. Deliberately narrower than
    /// <see cref="Get"/>, which carries operator counts and the available-mode list.
    /// </summary>
    // GET /api/platform/auth/policy/effective
    [HttpGet("effective")]
    public async Task<ActionResult<object>> Effective(CancellationToken ct)
    {
        var effective = await _policy.GetEffectiveAsync(ct);
        return Ok(new
        {
            mode = effective.Mode.ToString(),
            declaredMode = effective.DeclaredMode.ToString(),
            environmentClass = effective.EnvironmentClass.ToString(),
            environmentName = effective.EnvironmentName,
            effectiveFromUtc = effective.EffectiveFromUtc,
            expiresAtUtc = effective.ExpiresAtUtc,
            enforcementDisabled = effective.EnforcementDisabled,
            passwordOnlySessionsPermitted = effective.PasswordOnlySessionsPermitted,
            bypassExpired = effective.BypassExpired,
            changedBy = effective.ChangedBy,
            version = effective.Version,
            // Carried on the NARROW read because the console needs it on screens an Owner never
            // opens: the trust list, and the copy that tells an operator how long "remember this
            // browser" actually lasts. Neither is a secret — the operator was offered the control.
            browserTrustEnabled = effective.BrowserTrustEnabled,
            browserTrustHours = effective.BrowserTrustHours
        });
    }

    /// <summary>The full Owner read model. Owner-gated because the operator counts and the
    /// available-mode list are a map of what could be turned off and how many people it would
    /// affect.</summary>
    // GET /api/platform/auth/policy
    [HttpGet]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<PlatformMfaPolicyDto>> Get(CancellationToken ct)
        => Ok(await _policy.GetAsync(ct));

    // PUT /api/platform/auth/policy
    [HttpPut]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<PlatformMfaPolicyDto>> Change(
        [FromBody] ChangePlatformMfaPolicyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var result = await _policy.ChangeAsync(request, User, HttpContext, ct);
        if (result.Succeeded) return Ok(result.Policy);

        // Three outcomes an operator has to be able to tell apart: "somebody else changed this"
        // (reload), "this deployment will never allow it" (stop asking), and "fix what you typed".
        if (result.Conflict) return Conflict(new { error = result.Error });
        if (result.Forbidden)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error });
        return BadRequest(new { error = result.Error });
    }
}

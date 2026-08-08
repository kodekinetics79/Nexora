using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Activation;

[ApiController]
[Route("api/platform/tenants/{tenantId:long}/activation")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public sealed class TenantActivationPolicyController(ITenantActivationPolicyService policy) : ControllerBase
{
    [HttpGet("decision")]
    public async Task<ActionResult<TenantActivationDecision>> Decision(long tenantId, CancellationToken ct)
    {
        var result = await policy.EvaluateAsync(tenantId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<ActionResult<TenantActivationDecision>> Activate(long tenantId, CancellationToken ct)
    {
        try { return Ok(await policy.ActivateAsync(tenantId, User, HttpContext, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (TenantActivationBlockedException blocked)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Type = "https://nexora.ai/problems/tenant-activation-blocked",
                Title = "Tenant activation is blocked",
                Detail = "The authoritative activation policy did not pass.",
                Extensions = { ["decision"] = blocked.Decision }
            });
        }
        catch (InvalidOperationException invalid) { return Conflict(new ProblemDetails
            { Status = 409, Title = "Invalid tenant activation transition", Detail = invalid.Message }); }
    }

    [HttpPost("controls/{controlCode}/evidence")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<ActionResult<ActivationControlEvidenceReceipt>> RecordEvidence(
        long tenantId, string controlCode, [FromBody] RecordActivationControlEvidenceRequest request,
        CancellationToken ct)
    {
        try { return Ok(await policy.RecordControlEvidenceAsync(tenantId, controlCode, request, User, HttpContext, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception invalid) when (invalid is ArgumentException or ArgumentOutOfRangeException)
        { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid activation evidence", Detail = invalid.Message }); }
    }
}

using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

[ApiController]
[Route("api/platform/tenants/{tenantId:long}/legal-holds")]
[Authorize(Policy = PlatformPolicies.Owner)]
public sealed class TenantLegalHoldsController(
    TenantLegalHoldService holds,
    ILogger<TenantLegalHoldsController> logger) : ControllerBase
{
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<TenantLegalHoldDto>>> List(long tenantId, CancellationToken ct) =>
        Execute(() => holds.ListAsync(tenantId, ct));

    [HttpPost]
    public Task<ActionResult<TenantLegalHoldDto>> Place(
        long tenantId, [FromBody] PlaceTenantLegalHoldRequest request, CancellationToken ct) =>
        Execute(() => holds.PlaceAsync(tenantId, request, User, HttpContext, ct));

    // Releasing a hold is the act that makes a tenant's data destroyable again, so it keeps the
    // second factor even on a deployment where MFA enforcement has been relaxed — as a password
    // re-authentication if there is no MFA-bound session to lean on. Placing one is additive and
    // needs no step-up.
    [HttpPost("{holdId:long}/release")]
    [PlatformHighRiskOperation("tenant.legal-hold.release")]
    public Task<ActionResult<TenantLegalHoldDto>> Release(
        long tenantId, long holdId, [FromBody] ReleaseTenantLegalHoldRequest request, CancellationToken ct) =>
        Execute(() => holds.ReleaseAsync(tenantId, holdId, request, User, HttpContext, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> work)
    {
        try { return Ok(await work()); }
        catch (TenantOffboardingNotFoundException) { return NotFound(); }
        catch (TenantOffboardingRefusedException refusal)
        {
            logger.LogWarning("Refused tenant legal-hold operation: {Message}", refusal.Message);
            return StatusCode(refusal.SuggestedStatusCode,
                new { error = refusal.Message, status = refusal.SuggestedStatusCode });
        }
    }
}

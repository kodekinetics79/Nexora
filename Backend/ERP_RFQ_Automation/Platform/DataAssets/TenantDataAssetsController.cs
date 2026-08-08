using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

[ApiController]
[Route("api/platform/tenants/{tenantId:long}/data-assets")]
[Authorize(Policy = PlatformPolicies.Owner)]
public sealed class TenantDataAssetsController(
    TenantDataAssetRegistryService registry,
    ILogger<TenantDataAssetsController> logger) : ControllerBase
{
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<TenantDataAssetDto>>> List(
        long tenantId, CancellationToken ct) => Execute(() => registry.ListAsync(tenantId, ct));

    [HttpPost]
    public Task<ActionResult<TenantDataAssetDto>> Register(
        long tenantId, [FromBody] RegisterTenantDataAssetRequest request, CancellationToken ct) =>
        Execute(() => registry.RegisterAsync(tenantId, request, User, HttpContext, ct));

    [HttpPost("{assetId:long}/verify")]
    public Task<ActionResult<TenantDataAssetDto>> Verify(
        long tenantId, long assetId, [FromBody] VerifyTenantDataAssetRequest request, CancellationToken ct) =>
        Execute(() => registry.VerifyAsync(tenantId, assetId, request, User, HttpContext, ct));

    [HttpGet("activation-data-decision")]
    public Task<ActionResult<TenantActivationDataDecisionDto>> ActivationDataDecision(
        long tenantId, CancellationToken ct) =>
        Execute(() => registry.ActivationDataDecisionAsync(tenantId, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> work)
    {
        try { return Ok(await work()); }
        catch (TenantDataAssetNotFoundException) { return NotFound(); }
        catch (TenantDataAssetValidationException exception)
        {
            logger.LogWarning("Refused tenant data-asset request: {Message}", exception.Message);
            return BadRequest(new { error = exception.Message });
        }
        catch (TenantDataAssetConflictException exception)
        {
            logger.LogWarning("Conflicting tenant data-asset request: {Message}", exception.Message);
            return Conflict(new { error = exception.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "The data asset changed; reload it and retry." });
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Database constraint refused tenant data-asset request.");
            return Conflict(new { error = "The data asset conflicts with an existing registry record." });
        }
    }
}

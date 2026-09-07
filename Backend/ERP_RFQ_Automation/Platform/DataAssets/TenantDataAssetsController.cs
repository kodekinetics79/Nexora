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
    IPlatformDataBoundaryApplier applier,
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

    /// <summary>
    /// Registers and verifies this tenant's data boundaries from what the DEPLOYMENT declares about
    /// its own infrastructure, instead of asking an operator to retype it.
    ///
    /// <para>No request body: there is nothing here for a human to decide. The provider reference,
    /// region and backup policy come from <c>Platform:DataBoundaries</c>, the verification comes
    /// from a live probe of the running database, and a deployment that has declared nothing gets a
    /// 400 naming the keys it has to set rather than an invented answer.</para>
    /// </summary>
    [HttpPost("apply-platform-manifest")]
    public Task<ActionResult<ApplyPlatformDataBoundariesResult>> ApplyPlatformManifest(
        long tenantId, CancellationToken ct) =>
        Execute(() => applier.ApplyAsync(tenantId, User, HttpContext, ct));

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

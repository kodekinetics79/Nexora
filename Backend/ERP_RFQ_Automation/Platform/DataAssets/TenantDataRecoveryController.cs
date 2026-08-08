using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

[ApiController]
[Route("api/platform/tenants/{tenantId:long}/data-recovery")]
[Authorize(Policy = PlatformPolicies.Owner)]
public sealed class TenantDataRecoveryController(
    TenantDataRecoveryService recovery,
    ILogger<TenantDataRecoveryController> logger) : ControllerBase
{
    [HttpGet("evidence")]
    public Task<ActionResult<IReadOnlyList<TenantDataRecoveryEvidenceDto>>> Evidence(
        long tenantId, CancellationToken ct) => Execute(() => recovery.ListAsync(tenantId, ct));

    [HttpPost("evidence")]
    public Task<ActionResult<TenantDataRecoveryEvidenceDto>> Record(
        long tenantId, [FromBody] RecordTenantDataRecoveryEvidenceRequest request, CancellationToken ct) =>
        Execute(() => recovery.RecordAsync(tenantId, request, User, HttpContext, ct));

    [HttpGet("deletion-certification")]
    public Task<ActionResult<TenantDeletionCertificationDecisionDto>> Decision(
        long tenantId, CancellationToken ct) => Execute(() => recovery.DecisionAsync(tenantId, ct));

    [HttpPost("deletion-certification")]
    public Task<ActionResult<TenantDeletionCertificateDto>> Certify(
        long tenantId, [FromBody] CreateTenantDeletionCertificateRequest request, CancellationToken ct) =>
        Execute(() => recovery.CertifyAsync(tenantId, request, User, HttpContext, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> work)
    {
        try { return Ok(await work()); }
        catch (TenantDataAssetNotFoundException) { return NotFound(); }
        catch (TenantDataAssetValidationException exception) { return BadRequest(new { error = exception.Message }); }
        catch (TenantDataAssetConflictException exception) { return Conflict(new { error = exception.Message }); }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Database refused tenant recovery evidence.");
            return Conflict(new { error = "Recovery evidence conflicts with an existing immutable record." });
        }
    }
}

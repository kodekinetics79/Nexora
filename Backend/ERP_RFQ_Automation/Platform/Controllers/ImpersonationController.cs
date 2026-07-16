using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Support impersonation (stub). Mints a short-lived, read-only-by-default TENANT
/// token stamped <c>act_sub</c> / <c>impersonated=true</c>; fully audited with both
/// actors. Requires a reason and the tenant-admin role. The token is a tenant
/// token (audience "RFQ") so it grants NO platform-plane access. (ADR-0005 §3)
/// </summary>
[ApiController]
[Route("api/platform/tenants/{id:long}/impersonate")]
[Authorize(Policy = PlatformPolicies.Impersonate)]
public class ImpersonationController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IPlatformAuthService _authService;
    private readonly IPlatformAuditService _audit;

    public ImpersonationController(
        ErpRfqAutomationContext context, IPlatformAuthService authService, IPlatformAuditService audit)
    {
        _context = context;
        _authService = authService;
        _audit = audit;
    }

    // POST /api/platform/tenants/{id}/impersonate
    [HttpPost]
    public async Task<ActionResult<ImpersonationResponse>> Impersonate(
        long id, [FromBody] ImpersonationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return BadRequest(new { error = "A reason is required to impersonate." });

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
            return NotFound();

        if (tenant.Status != TenantStatus.Active)
            return Conflict(new { error = $"Cannot impersonate into a {tenant.Status} tenant." });

        if (tenant.PrimaryBusinessUnitId is not long buId)
            return Conflict(new { error = "Tenant has no primary business unit to scope the session to." });

        var subClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        long.TryParse(subClaim, out var actingPlatformUserId);

        var (token, expires) = _authService.IssueImpersonationToken(
            actingPlatformUserId, tenant, buId, request.Reason);

        await _audit.WriteAsync(User, "impersonate.issue", nameof(Tenant), tenant.Id.ToString(),
            new { tenant.Slug, businessUnitId = buId, reason = request.Reason, readOnly = true, expiresAtUtc = expires },
            actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

        return Ok(new ImpersonationResponse
        {
            TenantId = tenant.Id,
            BusinessUnitId = buId,
            Token = token,
            ReadOnly = true,
            ExpiresAtUtc = expires
        });
    }
}

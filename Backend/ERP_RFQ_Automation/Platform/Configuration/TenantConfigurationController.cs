using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Configuration;

/// <summary>
/// The single read behind the redesigned customer screen.
///
/// <para>Guarded by <see cref="PlatformPolicies.PlatformScope"/> only, and that is deliberate:
/// it discloses exactly what the existing per-tab reads already disclose to the same caller, and
/// the fields that are genuinely Owner-only — AI policy, egress, retention — are NOT in it. What
/// this endpoint adds over those reads is the <c>editable</c> flag per slice, so the console
/// learns from the server which slices this operator may write instead of guessing from a role
/// name and being told "403" after the operator has typed a reason.</para>
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:long}/configuration")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public sealed class TenantConfigurationController(ITenantConfigurationService configuration) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TenantConfigurationView>> Get(long tenantId, CancellationToken ct)
    {
        var view = await configuration.ReadAsync(tenantId, User, ct);
        if (view is null) return NotFound();

        // The concurrency token as an ETag, so a screen that merges several former tabs can echo
        // it back as If-Match. A merged screen makes a stale overwrite MORE likely, not less: one
        // commit now carries edits somebody may have started ten minutes and two interruptions ago.
        Response.Headers.ETag = $"W/\"{view.Version}\"";
        return Ok(view);
    }
}

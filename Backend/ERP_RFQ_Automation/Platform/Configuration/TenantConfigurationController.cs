using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Configuration;

/// <summary>
/// The single read behind the redesigned customer screen.
///
/// <para>Guarded by <see cref="PlatformPolicies.PlatformScope"/>, which is what the per-tab
/// reads it replaces are gated on. An earlier version of this comment claimed the endpoint
/// "discloses exactly what the existing per-tab reads already disclose" — that was false when
/// written: legal-hold state is Owner-only everywhere else (TenantLegalHoldsController is
/// class-level Owner, and the offboarding DTO carries no hold field), and this endpoint served
/// it to every PlatformScope caller. It is now suppressed for non-Owners in the service. The
/// lesson is worth keeping: a reviewer trusts a sentence like that instead of checking it, so it
/// has to be earned rather than asserted. Fields that are genuinely Owner-only — AI policy,
/// egress, retention — are NOT in it. What
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

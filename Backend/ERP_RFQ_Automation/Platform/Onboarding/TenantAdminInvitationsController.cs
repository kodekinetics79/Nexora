using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// The operator's half of activation: see what was sent, send it again, take it back.
///
/// <para>Guarded by the same default-deny <see cref="PlatformPolicies.PlatformScope"/> as every
/// other <c>/api/platform/*</c> endpoint, with mutations additionally requiring
/// <see cref="PlatformPolicies.TenantAdmin"/> — the identical gate <c>TenantsController</c>
/// puts on provision/suspend/resume, because reissuing an administrator's invitation is the
/// same class of authority as creating one.</para>
///
/// <para><b>What an operator can and cannot do here.</b> They can cause a link to be mailed to
/// the address on file and they can kill a link. They cannot read one: no response on this
/// controller carries a token, and no audit record does either. That asymmetry is the whole
/// improvement over reading a generated password down the phone.</para>
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:long}/admin-invitations")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class TenantAdminInvitationsController : ControllerBase
{
    private readonly ITenantAdminInvitationService _invitations;
    private readonly ErpRfqAutomationContext _context;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<TenantAdminInvitationsController> _logger;

    public TenantAdminInvitationsController(
        ITenantAdminInvitationService invitations,
        ErpRfqAutomationContext context,
        IPlatformAuditService audit,
        ILogger<TenantAdminInvitationsController> logger)
    {
        _invitations = invitations;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    // GET /api/platform/tenants/{tenantId}/admin-invitations
    [HttpGet]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<IEnumerable<TenantAdminInvitationSummary>>> List(
        long tenantId, CancellationToken ct)
    {
        if (!await TenantExistsAsync(tenantId, ct)) return NotFound();
        return Ok(await _invitations.ListAsync(tenantId, ct));
    }

    // POST /api/platform/tenants/{tenantId}/admin-invitations/resend
    [HttpPost("resend")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ResendTenantAdminInvitationResponse>> Resend(
        long tenantId, [FromBody] ResendTenantAdminInvitationRequest? request, CancellationToken ct)
    {
        if (!await TenantExistsAsync(tenantId, ct)) return NotFound();

        var actor = ActorEmail();
        var issued = await _invitations.ReissueAsync(tenantId, request?.UserId, actor, ct);
        if (issued is null)
            return Conflict(new
            {
                error = "This tenant has no administrator account to invite. " +
                        "Provision one before sending an activation link."
            });

        // Audited BEFORE the send, because the durable fact is "an operator caused a new link to
        // exist and invalidated the previous one" — that happened whether or not the mail server
        // was reachable a second later. The address is recorded, as tenant.provision already
        // records it; the token is not, here or anywhere.
        await _audit.WriteAsync(User, "tenant.admin-invitation.resend", nameof(TenantAdminInvitation),
            issued.InvitationId.ToString(),
            new
            {
                issued.UserId,
                Email = issued.Email,
                issued.ExpiresAtUtc,
                Reason = request?.Reason
            },
            actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);

        var dispatched = await _invitations.SendInvitationEmailAsync(issued, ct);
        if (!dispatched)
            _logger.LogWarning(
                "Reissued invitation {InvitationId} for tenant {TenantId} was not accepted by the email " +
                "provider. The link is valid; delivery needs attention.",
                issued.InvitationId, tenantId);

        var summary = (await _invitations.ListAsync(tenantId, ct))
            .First(i => i.Id == issued.InvitationId);

        return Ok(new ResendTenantAdminInvitationResponse
        {
            Invitation = summary,
            EmailDispatched = dispatched,
            ActivationUrl = dispatched ? null : issued.ActivationUrl
        });
    }

    // POST /api/platform/tenants/{tenantId}/admin-invitations/{invitationId}/revoke
    [HttpPost("{invitationId:long}/revoke")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<IActionResult> Revoke(
        long tenantId, long invitationId,
        [FromBody] RevokeTenantAdminInvitationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!await TenantExistsAsync(tenantId, ct)) return NotFound();

        var actor = ActorEmail();
        var revoked = await _invitations.RevokeAsync(tenantId, invitationId, actor, request.Reason, ct);
        if (!revoked)
            // Nothing was live to revoke: already redeemed, already revoked, or not this
            // tenant's. All three are "there is nothing here to withdraw", and the operator
            // needs the list to see which.
            return Conflict(new
            {
                error = "That invitation is not outstanding — it has already been used, revoked, " +
                        "or does not belong to this tenant."
            });

        await _audit.WriteAsync(User, "tenant.admin-invitation.revoke", nameof(TenantAdminInvitation),
            invitationId.ToString(),
            new { reason = request.Reason },
            actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);

        return NoContent();
    }

    private Task<bool> TenantExistsAsync(long tenantId, CancellationToken ct) =>
        _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking().AnyAsync(t => t.Id == tenantId, ct);

    private string ActorEmail() => User.FindFirst("email")?.Value ?? "platform";
}

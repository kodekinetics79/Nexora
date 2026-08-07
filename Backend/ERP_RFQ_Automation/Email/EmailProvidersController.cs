using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Email;

/// <summary>
/// The provider catalogue, as the tenant mailbox setup screen needs it.
///
/// <para><b>Why this is a separate surface from <c>/api/Mailbox/presets</c>.</b> That endpoint
/// returns one host/port pair per direction and a SINGLE encryption flag shared between them, which
/// cannot describe Microsoft 365 — 993 implicit TLS inbound, 587 STARTTLS outbound. It is kept,
/// serving the same catalogue, so the screen in production does not break; this is the shape the
/// screen should bind to, and the shape that can express a provider honestly.</para>
///
/// <para><b>View permission, not Edit.</b> Nothing here reads a tenant's data or opens a socket —
/// it is the same published table of hosts and ports the providers put on their own help pages.
/// Gating it behind Edit would mean an operator has to be granted the right to change mail settings
/// before they are allowed to find out what the settings should be.</para>
/// </summary>
[Route("api/email/providers")]
[ApiController]
[Authorize]
public sealed class EmailProvidersController : ControllerBase
{
    /// <summary>The RBAC module the mailbox and supplier-email screens already use. Reused rather
    /// than invented so a tenant that granted email administration does not have to discover a
    /// second permission meaning the same thing.</summary>
    private const string Module = "Email & SMTP";

    /// <summary>
    /// Providers a tenant mailbox can actually be configured against.
    ///
    /// <para>Send-only providers (SendGrid, SES, Postmark, Mailgun) are excluded by default:
    /// <c>Email_Configurations</c> stores a host, port and password, so listing a provider that
    /// needs an API key produces a row that saves cleanly and can never connect. Pass
    /// <c>includeSendOnly=true</c> for a surface that can represent them.</para>
    /// </summary>
    [HttpGet]
    [RequireModulePermission(Module, PermissionAction.View)]
    public ActionResult<IReadOnlyList<EmailProviderCapabilityDto>> GetAll([FromQuery] bool includeSendOnly = false)
        => Ok((includeSendOnly ? EmailProviderCatalog.All : EmailProviderCatalog.ForTenantMailbox)
            .Select(x => x.ToDto()).ToList());

    /// <summary>Capability and guidance for one provider, including which fields the operator must
    /// supply and what has to be enabled at the provider first.</summary>
    [HttpGet("{key}")]
    [RequireModulePermission(Module, PermissionAction.View)]
    public ActionResult<EmailProviderCapabilityDto> GetByKey(string key)
    {
        var provider = EmailProviderCatalog.Find(key);
        return provider is null ? NotFound($"No mail provider is registered under '{key}'.") : Ok(provider.ToDto());
    }
}

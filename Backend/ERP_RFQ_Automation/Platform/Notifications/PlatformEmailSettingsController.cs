using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// Platform-operator control over the product's outbound email identity.
///
/// <para><b>Authorization, and why it splits where it does.</b> The class carries the default-deny
/// <see cref="PlatformPolicies.PlatformScope"/> gate, so every platform role — including
/// ReadOnlyOps — can READ the status. That is deliberate: "is mail working?" is the question a
/// support engineer asks at 2am, and making them escalate to an Owner to find out that nothing has
/// been sent since Tuesday is how the silence lasts another day. The read model carries no secret,
/// so there is nothing to leak.</para>
///
/// <para>Writing is <see cref="PlatformPolicies.Owner"/>. So is the test send, which is not merely
/// a read dressed up: it transmits a real message from the platform's identity to an address the
/// caller chooses, and it will open a connection to an arbitrary operator-supplied host and present
/// credentials to it. Both are Owner-rank acts.</para>
/// </summary>
[ApiController]
[Route("api/platform/notifications/email")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class PlatformEmailSettingsController : ControllerBase
{
    private readonly IPlatformEmailSettingsService _settings;

    public PlatformEmailSettingsController(IPlatformEmailSettingsService settings) => _settings = settings;

    /// <summary>The configuration as stored, with secrets reported as set/not-set. When nothing has
    /// been stored this returns what the process is actually using, marked
    /// <c>origin: "Configuration"</c>.</summary>
    // GET /api/platform/notifications/email/settings
    [HttpGet("settings")]
    public async Task<ActionResult<PlatformEmailSettingsDto>> Get(CancellationToken ct)
        => Ok(await _settings.GetAsync(ct));

    /// <summary>"Is mail working?" in one response.</summary>
    // GET /api/platform/notifications/email/status
    [HttpGet("status")]
    public async Task<ActionResult<OutboundEmailStatusDto>> Status(CancellationToken ct)
        => Ok(await _settings.GetStatusAsync(ct));

    // PUT /api/platform/notifications/email/settings
    [HttpPut("settings")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<PlatformEmailSettingsDto>> Save(
        [FromBody] SavePlatformEmailSettingsRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var result = await _settings.SaveAsync(request, User, HttpContext, ct);
        if (result.Succeeded) return Ok(result.Settings);

        // 409 for a lost edit, 400 for a configuration that cannot work. The distinction matters to
        // the console: one is "reload and try again", the other "change what you typed".
        return result.Conflict
            ? Conflict(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Sends one real message using the supplied (or stored) settings and reports the classified
    /// outcome. Always 200 — a provider refusal is a successful diagnosis, not a failed request,
    /// and the console renders <c>succeeded</c> and <c>kind</c>.
    /// </summary>
    // POST /api/platform/notifications/email/test-send
    [HttpPost("test-send")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<TestOutboundEmailResponse>> TestSend(
        [FromBody] TestOutboundEmailRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        return Ok(await _settings.TestSendAsync(request, User, HttpContext, ct));
    }
}

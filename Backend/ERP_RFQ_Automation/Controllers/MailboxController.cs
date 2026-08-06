using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Tenant administration for the mailboxes Nexora reads leads from and sends quotes through.
///
/// <para><b>Why this controller exists.</b> <c>Email_Configurations</c> has driven lead ingestion
/// and quote delivery since the beginning, but there was no API and no screen for it — rows were
/// created by hand in the database. That makes onboarding a customer a developer task, makes a
/// credential rotation an unaudited direct write, and leaves an operator with no way to find out
/// why a mailbox stopped ingesting.</para>
///
/// <para><b>The credential never travels outbound.</b> <see cref="MailboxResponseDTO"/> has no
/// password property at all. <c>EmailConfiguration.Password</c> is decrypted transparently by the
/// value converter, so returning the entity — or any DTO carrying the field — would hand a live
/// customer mailbox credential to the browser. Passwords are write-only, and an update that omits
/// one keeps what is stored.</para>
///
/// <para><b>Every connection is policed.</b> Host and port are operator-supplied and this server
/// dials them, so <see cref="MailEndpointPolicy"/> gates every attempt: without it, the test
/// endpoint is a tenant-operable SSRF probe into the private network with timings and error
/// detail reported back in the response.</para>
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public sealed class MailboxController(
    ErpRfqAutomationContext context,
    IMailboxConnectionProbe probe,
    IIamAuditWriter audit,
    ILogger<MailboxController> logger) : ControllerBase
{
    /// <summary>The RBAC module already used by the supplier-email screen. Reused rather than
    /// invented so a tenant that has granted email administration does not have to discover a
    /// second permission that means the same thing.</summary>
    private const string Module = "Email & SMTP";

    /// <summary>Whole-request ceiling for a connection test. Six network stages against an
    /// unresponsive host must not hold a request thread indefinitely.</summary>
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(45);

    // ---- read ---------------------------------------------------------------------------

    [HttpGet]
    [RequireModulePermission(Module, PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<MailboxResponseDTO>>> GetAll()
    {
        if (!TryTenant(out var tenant)) return Forbid();

        var rows = await context.EmailConfigurations
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant)
            .OrderBy(x => x.Protocol).ThenBy(x => x.Id)
            .ToListAsync(HttpContext.RequestAborted);

        return Ok(rows.Select(ToResponse).ToList());
    }

    [HttpGet("{id:long}")]
    [RequireModulePermission(Module, PermissionAction.View)]
    public async Task<ActionResult<MailboxResponseDTO>> GetById(long id)
    {
        if (!TryTenant(out var tenant)) return Forbid();

        var row = await context.EmailConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == tenant, HttpContext.RequestAborted);

        return row is null ? NotFound("Mailbox not found.") : Ok(ToResponse(row));
    }

    /// <summary>
    /// Whether customer-facing mail can currently leave this tenant, and whether the configuration
    /// is ambiguous. Read by the screen's containment banner.
    /// </summary>
    [HttpGet("outbound-status")]
    [RequireModulePermission(Module, PermissionAction.View)]
    public async Task<ActionResult<OutboundMailStatusDTO>> GetOutboundStatus()
    {
        if (!TryTenant(out var tenant)) return Forbid();

        var active = await context.EmailConfigurations.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.IsActive)
            .Select(x => new { x.Protocol, x.Host })
            .ToListAsync(HttpContext.RequestAborted);

        var smtp = active.Where(x => IsSmtp(x.Protocol)).ToList();
        var imap = active.Count(x => !IsSmtp(x.Protocol));

        return Ok(new OutboundMailStatusDTO
        {
            CanSendToCustomers = smtp.Count > 0,
            ActiveSmtpCount = smtp.Count,
            ActiveSmtpHosts = smtp.Select(x => x.Host).Distinct().ToList(),
            ActiveImapCount = imap,
            HasAmbiguousOutbound = smtp.Count > 1,
            Summary = smtp.Count switch
            {
                0 => "Outbound email is contained. No SMTP mailbox is active, so quotes and supplier " +
                     "emails cannot reach anyone outside this system.",
                1 => $"Quotes and supplier emails WILL be delivered through {smtp[0].Host}.",
                _ => $"{smtp.Count} SMTP mailboxes are active. Mail is sent through only one of them — " +
                     "the oldest — so the others are not doing what they appear to be doing."
            }
        });
    }

    /// <summary>Known-good settings for the providers a Saudi industrial distributor actually
    /// uses, so onboarding is a choice rather than a hunt through help pages.</summary>
    [HttpGet("presets")]
    [RequireModulePermission(Module, PermissionAction.View)]
    public ActionResult<IReadOnlyList<MailboxPresetDTO>> GetPresets() => Ok(MailboxPresets.All);

    // ---- test ---------------------------------------------------------------------------

    /// <summary>
    /// Runs a staged connection test WITHOUT saving anything and WITHOUT sending any email.
    ///
    /// <para>Deliberately a POST on a read-shaped action: the settings — including a password —
    /// must travel in the body. A GET would put a live mailbox credential into the URL, and from
    /// there into browser history, proxy logs and the server's own request log.</para>
    /// </summary>
    [HttpPost("test")]
    [RequireModulePermission(Module, PermissionAction.Edit)]
    public async Task<ActionResult<MailboxProbeResult>> Test([FromBody] MailboxTestRequestDTO request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!TryTenant(out var tenant)) return Forbid();
        if (!TryProtocol(request.Protocol, out var protocol))
            return BadRequest("Protocol must be IMAP or SMTP.");

        var password = request.Password;
        if (string.IsNullOrEmpty(password))
        {
            // Re-testing a stored mailbox: the operator never had the password to retype.
            if (request.MailboxId is not { } id)
                return BadRequest("A password is required to test a mailbox that has not been saved yet.");

            var stored = await context.EmailConfigurations.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == tenant, HttpContext.RequestAborted);
            if (stored is null) return NotFound("Mailbox not found.");
            password = stored.Password;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TestDeadline);

        var result = await probe.ProbeAsync(
            new MailboxProbeRequest(protocol, request.Host, request.Port, request.Username, password, request.UseSsl),
            deadline.Token);

        // Audited because it makes this server open a socket to an operator-chosen host. The
        // outcome is recorded; the credential obviously is not.
        await audit.WriteAsync(User, new IamAuditEntry(
            IamAuditActions.MailboxTested, IamAuditTargets.Mailbox, request.MailboxId,
            $"{protocol} {request.Host}:{request.Port}",
            After: new { result.Succeeded, result.Summary }));
        await context.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(result);
    }

    // ---- write --------------------------------------------------------------------------

    [HttpPost]
    [RequireModulePermission(Module, PermissionAction.Create)]
    public async Task<ActionResult<MailboxResponseDTO>> Create([FromBody] MailboxCreateRequestDTO request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!TryTenant(out var tenant)) return Forbid();
        if (!TryProtocol(request.Protocol, out var protocol))
            return BadRequest("Protocol must be IMAP or SMTP.");
        if (!MailEndpointPolicy.IsAllowedEndpoint(request.Host, request.Port))
            return BadRequest("That mail host is not an address this server may connect to.");

        if (request.VerifyBeforeSave)
        {
            var verification = await VerifyAsync(protocol, request.Host, request.Port,
                request.Username, request.Password, request.UseSsl);
            if (verification is not null) return verification;
        }

        EmailConfiguration? created = null;
        await audit.ExecuteAtomicAsync(async () =>
        {
            // Constructed inside the delegate: ExecuteAtomicAsync is retriable, and an entity
            // built outside stays tracked as Added across a rolled-back attempt.
            created = new EmailConfiguration
            {
                BusinessUnitId = tenant,
                ConfigurationName = request.ConfigurationName.Trim(),
                EmailAddress = request.EmailAddress.Trim(),
                Protocol = protocol,
                Host = MailEndpointPolicy.Normalize(request.Host),
                Port = request.Port,
                Username = request.Username.Trim(),
                Password = request.Password,
                UseSsl = request.UseSsl,
                PollingInterval = request.PollingInterval,
                IsActive = request.IsActive,
                CreatedOn = DateTime.UtcNow
            };

            context.EmailConfigurations.Add(created);
            await context.SaveChangesAsync(HttpContext.RequestAborted);

            await audit.WriteAsync(User, new IamAuditEntry(
                IamAuditActions.MailboxCreated, IamAuditTargets.Mailbox, created.Id,
                $"{created.Protocol} {created.EmailAddress}", After: Snapshot(created)));
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }, HttpContext.RequestAborted);

        var row = created ?? throw new InvalidOperationException("Mailbox creation produced no entity.");
        logger.LogInformation("Mailbox {MailboxId} ({Protocol}) created for tenant {Tenant}.",
            row.Id, row.Protocol, tenant);

        return CreatedAtAction(nameof(GetById), new { id = row.Id }, ToResponse(row));
    }

    [HttpPut("{id:long}")]
    [RequireModulePermission(Module, PermissionAction.Edit)]
    public async Task<ActionResult<MailboxResponseDTO>> Update(long id, [FromBody] MailboxUpdateRequestDTO request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!TryTenant(out var tenant)) return Forbid();
        if (!MailEndpointPolicy.IsAllowedEndpoint(request.Host, request.Port))
            return BadRequest("That mail host is not an address this server may connect to.");

        var row = await context.EmailConfigurations
            .FirstOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == tenant, HttpContext.RequestAborted);
        if (row is null) return NotFound("Mailbox not found.");

        // Blank means "keep the stored credential" — the UI never had it to resend.
        var rotating = !string.IsNullOrEmpty(request.Password);
        var effectivePassword = rotating ? request.Password! : row.Password;

        if (request.VerifyBeforeSave)
        {
            var verification = await VerifyAsync(row.Protocol, request.Host, request.Port,
                request.Username, effectivePassword, request.UseSsl);
            if (verification is not null) return verification;
        }

        var before = Snapshot(row);

        await audit.ExecuteAtomicAsync(async () =>
        {
            row.ConfigurationName = request.ConfigurationName.Trim();
            row.EmailAddress = request.EmailAddress.Trim();
            row.Host = MailEndpointPolicy.Normalize(request.Host);
            row.Port = request.Port;
            row.Username = request.Username.Trim();
            row.UseSsl = request.UseSsl;
            row.PollingInterval = request.PollingInterval;
            row.IsActive = request.IsActive;
            if (rotating) row.Password = request.Password!;

            await context.SaveChangesAsync(HttpContext.RequestAborted);

            await audit.WriteAsync(User, new IamAuditEntry(
                IamAuditActions.MailboxUpdated, IamAuditTargets.Mailbox, row.Id,
                $"{row.Protocol} {row.EmailAddress}", Before: before, After: Snapshot(row)));

            // A credential rotation gets its own event so a reviewer can find it without
            // reading the diff of every unrelated host and port edit.
            if (rotating)
                await audit.WriteAsync(User, new IamAuditEntry(
                    IamAuditActions.MailboxCredentialChanged, IamAuditTargets.Mailbox, row.Id,
                    $"{row.Protocol} {row.EmailAddress}"));

            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }, HttpContext.RequestAborted);

        return Ok(ToResponse(row));
    }

    [HttpDelete("{id:long}")]
    [RequireModulePermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(long id)
    {
        if (!TryTenant(out var tenant)) return Forbid();

        var row = await context.EmailConfigurations
            .FirstOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == tenant, HttpContext.RequestAborted);
        if (row is null) return NotFound("Mailbox not found.");

        // Ingested mail references its source configuration. Deleting the row would orphan the
        // provenance of every lead that arrived through it, so a mailbox that has ever ingested
        // is deactivated instead — it stops polling, and the audit trail stays intact.
        var hasHistory = await context.EmailIngests
            .AnyAsync(x => x.EmailConfigurationId == row.Id, HttpContext.RequestAborted);

        var before = Snapshot(row);
        await audit.ExecuteAtomicAsync(async () =>
        {
            if (hasHistory)
                row.IsActive = false;
            else
                context.EmailConfigurations.Remove(row);

            await context.SaveChangesAsync(HttpContext.RequestAborted);
            await audit.WriteAsync(User, new IamAuditEntry(
                hasHistory ? IamAuditActions.MailboxUpdated : IamAuditActions.MailboxDeleted,
                IamAuditTargets.Mailbox, id, $"{before.Protocol} {before.EmailAddress}", Before: before));
            await context.SaveChangesAsync(HttpContext.RequestAborted);
        }, HttpContext.RequestAborted);

        return Ok(new
        {
            deactivated = hasHistory,
            message = hasHistory
                ? "This mailbox has ingested mail before, so it was deactivated rather than deleted. " +
                  "Polling has stopped and the history of what it ingested is preserved."
                : "Mailbox deleted."
        });
    }

    /// <summary>
    /// Deactivates every SMTP configuration for this tenant in one action, so nothing can reach a
    /// customer or supplier.
    ///
    /// <para><b>Why this is a first-class button and not a note in a runbook.</b> Before running a
    /// simulation against a mailbox of live customer correspondence, the operator needs outbound
    /// mail provably off. Configuration cannot do it — the transports read host and credentials
    /// straight from these rows and never consult the notification guard — so deactivating the
    /// rows IS the containment mechanism. Doing it by hand in SQL, under time pressure, against
    /// production, is exactly where mistakes happen.</para>
    ///
    /// <para>There is deliberately no matching "resume all". Containment should be one click;
    /// releasing it should be a per-mailbox decision.</para>
    /// </summary>
    [HttpPost("outbound/pause")]
    [RequireModulePermission(Module, PermissionAction.Edit)]
    public async Task<ActionResult<OutboundMailStatusDTO>> PauseOutbound()
    {
        if (!TryTenant(out var tenant)) return Forbid();

        var affected = await context.EmailConfigurations
            .Where(x => x.BusinessUnitId == tenant && x.IsActive)
            .ToListAsync(HttpContext.RequestAborted);
        var smtp = affected.Where(x => IsSmtp(x.Protocol)).ToList();

        if (smtp.Count > 0)
        {
            await audit.ExecuteAtomicAsync(async () =>
            {
                foreach (var row in smtp) row.IsActive = false;
                await context.SaveChangesAsync(HttpContext.RequestAborted);

                await audit.WriteAsync(User, new IamAuditEntry(
                    IamAuditActions.OutboundMailPaused, IamAuditTargets.Mailbox, null,
                    $"{smtp.Count} SMTP mailbox(es)",
                    After: new { PausedIds = smtp.Select(x => x.Id).ToArray() }));
                await context.SaveChangesAsync(HttpContext.RequestAborted);
            }, HttpContext.RequestAborted);

            logger.LogWarning(
                "Outbound email paused for tenant {Tenant}: {Count} SMTP configuration(s) deactivated.",
                tenant, smtp.Count);
        }

        return await GetOutboundStatus();
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>Runs the probe and returns a 422 describing the failed stage, or null when the
    /// settings are good. Saving a mailbox that cannot connect produces a screen that looks
    /// configured while silently ingesting nothing.</summary>
    private async Task<ActionResult?> VerifyAsync(
        string protocol, string host, int port, string username, string password, bool useSsl)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TestDeadline);

        var result = await probe.ProbeAsync(
            new MailboxProbeRequest(protocol, host, port, username, password, useSsl), deadline.Token);

        return result.Succeeded
            ? null
            : UnprocessableEntity(new
            {
                message = result.Summary,
                detail = "The mailbox was not saved because it could not connect. " +
                         "Correct the settings, or save without verification.",
                probe = result
            });
    }

    private bool TryTenant(out long tenant)
    {
        tenant = 0;
        var claim = User.FindFirst("businessUnitId")?.Value;
        return long.TryParse(claim, out tenant) && tenant > 0;
    }

    private static bool TryProtocol(string? value, out string protocol)
    {
        protocol = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return protocol is MailboxConnectionProbe.Imap or MailboxConnectionProbe.Smtp;
    }

    private static bool IsSmtp(string protocol) =>
        string.Equals(protocol?.Trim(), MailboxConnectionProbe.Smtp, StringComparison.OrdinalIgnoreCase);

    /// <summary>Audit payload. Never includes the password — the audit table is queryable by
    /// support, which would make it a second place a live credential leaks from.</summary>
    private static MailboxAuditSnapshot Snapshot(EmailConfiguration row) => new(
        row.ConfigurationName, row.EmailAddress, row.Protocol, row.Host,
        row.Port, row.Username, row.UseSsl, row.PollingInterval, row.IsActive);

    private sealed record MailboxAuditSnapshot(
        string ConfigurationName, string EmailAddress, string Protocol, string Host,
        int Port, string Username, bool UseSsl, int PollingInterval, bool IsActive);

    private static MailboxResponseDTO ToResponse(EmailConfiguration row)
    {
        var (state, detail) = Health(row);
        return new MailboxResponseDTO
        {
            Id = row.Id,
            ConfigurationName = row.ConfigurationName,
            EmailAddress = row.EmailAddress,
            Protocol = row.Protocol,
            Host = row.Host,
            Port = row.Port,
            Username = row.Username,
            UseSsl = row.UseSsl,
            PollingInterval = row.PollingInterval,
            IsActive = row.IsActive,
            CreatedOn = row.CreatedOn,
            LastSuccessfulPollOn = row.LastSuccessfulPollOn,
            LastPollAttemptOn = row.LastPollAttemptOn,
            LastPollError = row.LastPollError,
            ConsecutivePollFailures = row.ConsecutivePollFailures,
            HealthState = state,
            HealthDetail = detail,
            CredentialsSentInClear =
                MailboxConnectionProbe.SecurityFor(row.Protocol, row.UseSsl) == MailKit.Security.SecureSocketOptions.None
        };
    }

    /// <summary>
    /// Derives one operator-facing state from the poller's telemetry. Computed here rather than in
    /// the browser so every surface — screen, alert, support query — agrees on what "failing"
    /// means. SMTP rows are never polled, so asking whether they are polling is meaningless.
    /// </summary>
    private static (string State, string Detail) Health(EmailConfiguration row)
    {
        if (!row.IsActive)
            return ("Disabled", IsSmtp(row.Protocol)
                ? "Inactive. Mail cannot be sent through this mailbox."
                : "Inactive. This mailbox is not being polled, so no leads arrive from it.");

        if (IsSmtp(row.Protocol))
            return ("Ready", "Active. Quotes and supplier emails can be sent through this mailbox.");

        if (row.ConsecutivePollFailures > 0)
            return ("Failing",
                $"The last {row.ConsecutivePollFailures} poll(s) failed" +
                (string.IsNullOrWhiteSpace(row.LastPollError) ? "." : $": {row.LastPollError}") +
                (row.LastSuccessfulPollOn is { } last
                    ? $" Last successful read {last:u}. No mail since then has been ingested."
                    : " This mailbox has never been read successfully."));

        if (row.LastSuccessfulPollOn is null)
            return ("Never polled", row.LastPollAttemptOn is null
                ? "Active, but no poll has run yet. Allow one polling interval."
                : "Active and attempted, but never completed a successful read.");

        return ("Healthy", $"Last read {row.LastSuccessfulPollOn:u}. Polling every {row.PollingInterval} minute(s).");
    }
}

/// <summary>Connection settings for the mail providers customers in this market actually use.
/// Ports and TLS modes are the providers' documented values.</summary>
internal static class MailboxPresets
{
    public static readonly IReadOnlyList<MailboxPresetDTO> All =
    [
        new("microsoft365", "Microsoft 365 / Outlook", "outlook.office365.com", 993,
            "smtp.office365.com", 587, true,
            "Microsoft disables IMAP per-mailbox by default and rejects basic authentication when MFA " +
            "is on. Ask your administrator to enable IMAP for this mailbox, and use an app password."),

        new("google", "Google Workspace / Gmail", "imap.gmail.com", 993,
            "smtp.gmail.com", 465, true,
            "Requires an App Password when 2-Step Verification is enabled; the normal account password " +
            "is always rejected. IMAP must also be enabled in Gmail settings."),

        new("zoho", "Zoho Mail", "imap.zoho.com", 993, "smtp.zoho.com", 465, true,
            "Use an application-specific password generated in Zoho account security settings."),

        new("cpanel", "cPanel / company mail server", "mail.yourcompany.com", 993,
            "mail.yourcompany.com", 465, true,
            "Replace the host with your own mail server name. If your provider does not offer TLS on " +
            "993/465, use 143/587 with a secure connection turned off — the connection still upgrades " +
            "to TLS where the server supports it."),

        new("custom", "Something else", string.Empty, 993, string.Empty, 587, true,
            "Enter the hostname and port your mail provider documents for IMAP and SMTP.")
    ];
}

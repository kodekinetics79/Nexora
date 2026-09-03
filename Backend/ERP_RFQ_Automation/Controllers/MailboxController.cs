using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications.Runtime;
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
///
/// <para><b>Write failures are named, on the two actions that can produce one.</b>
/// <see cref="Create"/> and <see cref="Update"/> are the only actions that carry operator-typed
/// TEXT into a bounded column, so they are the only ones wrapped. The other three writes cannot
/// reach a <c>22001</c>: <see cref="Delete"/> and <see cref="PauseOutbound"/> assign booleans or
/// remove a row, <see cref="Test"/> persists nothing but an audit event, and every audit field
/// those three touch is either truncated by <c>IamAuditWriter</c> (<c>TargetLabel</c> at 256,
/// <c>Reason</c> at 512) or is unbounded <c>jsonb</c>. Adding a catch there would be an unreachable
/// claim on an exception type that only a genuine fault can raise.</para>
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.EmailIntake)]
public sealed class MailboxController(
    ErpRfqAutomationContext context,
    IMailConnectionTester tester,
    IIamAuditWriter audit,
    ILogger<MailboxController> logger,
    IOutboundSenderResolver? senders = null,
    OutboundEmailProbe? probe = null) : ControllerBase
{
    /// <summary>The RBAC module already used by the supplier-email screen. Reused rather than
    /// invented so a tenant that has granted email administration does not have to discover a
    /// second permission that means the same thing.</summary>
    private const string Module = "Email & SMTP";

    /// <summary>Whole-request ceiling for a connection test. Six network stages against an
    /// unresponsive host must not hold a request thread indefinitely.</summary>
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(45);

    /// <summary>
    /// RFC 7807 body carrying the request's trace identifier, so an operator reporting a failed
    /// save gives support an id that ties straight back to the server log entry. Same helper, same
    /// shape and same name as the ones on <c>ProductController</c> and
    /// <c>ProductCategoryController</c>; deliberately NOT called <c>Problem</c>, because
    /// <see cref="ControllerBase"/> already declares that and two same-named helpers with
    /// different return types in one file is a trap.
    /// </summary>
    private ProblemDetails TracedProblem(int status, string title, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return problem;
    }

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
    /// Which sender customer-facing mail will actually leave from, and whether the configuration
    /// is ambiguous. Read by the screen's banner.
    ///
    /// <para>The answer comes from <see cref="IOutboundSenderResolver"/> — the authority the quote
    /// sender and the supplier RFQ worker use — not from counting rows. Counting rows is how this
    /// endpoint used to promise "quotes WILL be delivered through smtpout.secureserver.net" while
    /// dispatch never read the table (issue #54).</para>
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

        if (senders is null)
        {
            // No resolver composed (unit harnesses): report the rows and say nothing about the
            // sender rather than guess.
            return Ok(new OutboundMailStatusDTO
            {
                CanSendToCustomers = smtp.Count > 0,
                ActiveSmtpCount = smtp.Count,
                ActiveSmtpHosts = smtp.Select(x => x.Host).Distinct().ToList(),
                ActiveImapCount = imap,
                HasAmbiguousOutbound = smtp.Count > 1,
                SenderOrigin = "unknown",
                Summary = smtp.Count > 0
                    ? $"{smtp.Count} SMTP mailbox(es) active; the sender authority is not available."
                    : "No SMTP mailbox is active."
            });
        }

        var sender = await senders.ResolveAsync(tenant, HttpContext.RequestAborted);
        var contained = sender.GuardMode != Notifications.OutboundEmailMode.Live;
        var origin = sender.Origin.ToString().ToLowerInvariant();
        var summary = sender.Origin switch
        {
            OutboundSenderOrigin.Tenant =>
                $"Quotes and supplier RFQs will be sent from {sender.FromAddress} through {sender.Host} " +
                $"(this tenant's mailbox \"{sender.MailboxLabel}\").",
            _ when sender.TransmitsMail =>
                $"No SMTP mailbox is active for this tenant, so quotes and supplier RFQs will be sent from the " +
                $"platform address {sender.FromAddress} via {sender.Provider}. Add an SMTP mailbox to send from your own address.",
            _ =>
                "Outbound email is contained. No SMTP mailbox is active for this tenant and the platform " +
                "provider does not transmit, so quotes and supplier RFQs cannot reach anyone."
        };
        if (contained)
            summary += $" The platform containment mode is {sender.GuardMode}: every send is intercepted before it leaves.";

        return Ok(new OutboundMailStatusDTO
        {
            CanSendToCustomers = sender.TransmitsMail,
            ActiveSmtpCount = smtp.Count,
            ActiveSmtpHosts = smtp.Select(x => x.Host).Distinct().ToList(),
            ActiveImapCount = imap,
            HasAmbiguousOutbound = smtp.Count > 1,
            SenderOrigin = origin,
            SenderAddress = sender.FromAddress,
            SenderName = sender.FromName,
            SenderHost = sender.Host,
            SenderMailboxId = sender.MailboxId,
            SenderMailboxName = sender.MailboxLabel,
            ContainmentMode = sender.GuardMode.ToString(),
            Summary = summary
        });
    }

    /// <summary>
    /// Sends ONE real message through this tenant's SMTP mailbox, built exactly as a live quote
    /// send would be built (issue #54), and reports what the provider said.
    ///
    /// <para>The connection test proves the server accepts the login; only a send proves the
    /// mailbox is allowed to send as its address and that the message leaves. The recipient is
    /// restricted to the signed-in user or the mailbox's own address — this proves a channel, it
    /// is not a relay a tenant can aim at arbitrary inboxes.</para>
    /// </summary>
    [HttpPost("{id:long}/send-test")]
    [RequireModulePermission(Module, PermissionAction.Edit)]
    public async Task<ActionResult<OutboundEmailProbeResult>> SendTest(long id, [FromBody] MailboxSendTestRequestDTO request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!TryTenant(out var tenant)) return Forbid();
        if (senders is null || probe is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The outbound sender is not available.");

        var row = await context.EmailConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == tenant, HttpContext.RequestAborted);
        if (row is null) return NotFound("Mailbox not found.");
        if (!IsSmtp(row.Protocol)) return BadRequest("Only an SMTP mailbox can send a test message.");
        if (!MailEndpointPolicy.IsAllowedEndpoint(row.Host, row.Port))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The mailbox host is not an address this server may connect to.");

        var recipient = request.Recipient.Trim();
        var callerEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value;
        var permitted = string.Equals(recipient, row.EmailAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(callerEmail)
                && string.Equals(recipient, callerEmail.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!permitted)
            return BadRequest("A test message can only be sent to your own address or to the mailbox's own address.");

        var companyName = await context.BusinessUnits.AsNoTracking()
            .Where(x => x.Id == tenant).Select(x => x.BusinessUnitName)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        var platform = await senders.ResolveAsync(null, HttpContext.RequestAborted);
        var mailbox = new TenantOutboundSender(
            tenant, row.Id, row.ConfigurationName, row.EmailAddress,
            string.IsNullOrWhiteSpace(companyName) ? row.ConfigurationName : companyName!, row);
        var sender = senders.ForMailbox(mailbox, platform.PlatformSettings);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TestDeadline);
        var result = await probe.SendAsync(sender, recipient, callerEmail, deadline.Token);

        // Audited for the same reason the connection test is: this server opened a socket to an
        // operator-chosen host and, this time, put a message on the wire.
        await audit.WriteAsync(User, new IamAuditEntry(
            IamAuditActions.MailboxTested, IamAuditTargets.Mailbox, row.Id,
            $"SMTP {row.Host}:{row.Port} send-test",
            After: new { result.Succeeded, result.Transmitted, result.Kind, result.EffectiveRecipient }));
        await context.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(result);
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
    public async Task<ActionResult<MailConnectionTestResult>> Test([FromBody] MailboxTestRequestDTO request)
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

        var result = await tester.TestAsync(
            TestRequestFor(protocol, request.Host, request.Port, request.EmailAddress ?? string.Empty, request.Username, password, request.UseSsl),
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
                request.EmailAddress, request.Username, request.Password, request.UseSsl);
            if (verification is not null) return verification;
        }

        EmailConfiguration? created = null;
        try
        {
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
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "22001" })
        {
            // 22001 ONLY — "value too long for type character varying(n)". Deliberately not a
            // blanket catch: a bare catch(DbUpdateException) would also swallow the foreign-key
            // violation on BusinessUnitID (23503), unique violations (23505), RLS denials
            // (42501 — this codebase is deny-by-default under nexora_tenant_isolation) and
            // serialization failures from the retriable transaction ExecuteAtomicAsync opens,
            // and would report every one of them to the operator as "shorten the address" while
            // removing the log entry that says what actually happened. Everything else escapes
            // to the global handler, on purpose.
            //
            // The DTO caps now mirror the columns, so this should be unreachable for the fields
            // this screen writes. It stays as the backstop for the ones it does not.
            return BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Mailbox not created",
                "One of the values is too long for the field it is stored in. Shorten it and try again."));
        }
        catch (ArgumentException ex) when (ex is not (ArgumentNullException or ArgumentOutOfRangeException))
        {
            // The subclasses are EXCLUDED, and that exclusion is the point of the filter.
            // ArgumentNullException and ArgumentOutOfRangeException both derive from
            // ArgumentException, and neither is ever a message to the operator — each is a bug
            // in this process. There is a real one on this exact call: IamAuditWriter opens
            // ExecuteAtomicAsync with ArgumentNullException.ThrowIfNull(work). Caught here it
            // would be reported to the operator as a mailbox that already exists: a sentence
            // about their data describing a fault in ours, sending them to change an address
            // that was never wrong. Excluded, it reaches the global handler and stays logged.
            //
            // The message text is the only thing that separates a conflict from a bad request.
            // This IS message-sniffing and it is knowingly accepted for now; a reworded message
            // degrades to 400 rather than 409, which is wrong but not harmful. Nothing is
            // logged from here — the message could be echoed back, and this module's exception
            // paths handle mailbox credentials.
            var duplicate = ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
            return duplicate
                ? Conflict(TracedProblem(StatusCodes.Status409Conflict, "Mailbox not created", ex.Message))
                : BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Mailbox not created", ex.Message));
        }

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
                request.EmailAddress, request.Username, effectivePassword, request.UseSsl);
            if (verification is not null) return verification;
        }

        var before = Snapshot(row);

        try
        {
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
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "22001" })
        {
            // 22001 ONLY — see the identical catch on Create for why this is not a blanket
            // catch(DbUpdateException). Everything else escapes to the global handler, on purpose.
            //
            // The DTO caps now mirror the columns, so this should be unreachable for the fields
            // this screen writes. It stays as the backstop for the ones it does not.
            return BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Mailbox not saved",
                "One of the values is too long for the field it is stored in. Shorten it and try again."));
        }
        catch (ArgumentException ex) when (ex is not (ArgumentNullException or ArgumentOutOfRangeException))
        {
            // The subclasses are EXCLUDED — see the identical catch on Create. Nothing is logged
            // from here: the row being edited carries a live customer mailbox credential, and this
            // catch sits directly on the call that assigns it.
            var duplicate = ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
            return duplicate
                ? Conflict(TracedProblem(StatusCodes.Status409Conflict, "Mailbox not saved", ex.Message))
                : BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Mailbox not saved", ex.Message));
        }

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
        string protocol, string host, int port, string emailAddress, string username,
        string password, bool useSsl)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TestDeadline);

        var result = await tester.TestAsync(
            TestRequestFor(protocol, host, port, emailAddress, username, password, useSsl), deadline.Token);

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

    /// <summary>
    /// Translates a stored mailbox row into the shared tester's vocabulary.
    ///
    /// <para>The stored <c>UseSsl</c> flag is a boolean whose meaning depends on the protocol, and
    /// this is the one place in this controller that knows it: for IMAP it is implicit TLS or no
    /// encryption at all, for SMTP it is implicit TLS or STARTTLS. The tester takes an explicit
    /// mode so that ambiguity stops at this boundary, and the provider is inferred from the host so
    /// a mailbox configured long before the catalogue existed still gets provider-specific
    /// remedies when it fails.</para>
    /// </summary>
    private static MailConnectionTestRequest TestRequestFor(
        string protocol, string host, int port, string emailAddress, string username,
        string password, bool useSsl)
    {
        var smtp = IsSmtp(protocol);
        var tls = useSsl
            ? MailTlsMode.Implicit
            : smtp ? MailTlsMode.StartTls : MailTlsMode.None;

        // THE login identity, resolved by the same rule the runtime uses — not by whichever
        // column this screen happens to collect.
        //
        // This endpoint used to send Username for both directions while the INBOUND poller
        // signed in as EmailAddress. Those are independent columns, so wherever a tenant's UPN
        // differs from the mailbox address — the normal case for shared and enterprise
        // mailboxes — a green "Test Connection" proved nothing about the poller at all. It is
        // the exact failure EmailBackgroundService records having already happened: the door
        // shut for seven days with every human-facing surface reporting healthy.
        var login = smtp
            ? username
            : (string.IsNullOrWhiteSpace(emailAddress) ? username : emailAddress);

        return new MailConnectionTestRequest(
            smtp ? MailDirection.Outbound : MailDirection.Inbound,
            smtp ? MailTransport.Smtp : MailTransport.Imap,
            host, port, tls, login, password,
            EmailProviderCatalog.InferKeyFromHost(host));
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

        // MINUTES, and now true: this line said "minute(s)" while EmailBackgroundService read
        // the same column as SECONDS. The unit of record is documented on
        // EmailBackgroundService.MinimumPollIntervalMinutes; every surface states minutes and
        // the poller now means it.
        return ("Healthy", $"Last read {row.LastSuccessfulPollOn:u}. Polling every {row.PollingInterval} minute(s).");
    }
}

/// <summary>
/// The legacy shape of the provider presets, projected from <see cref="EmailProviderCatalog"/>.
///
/// <para>This list is no longer the source of truth — the catalogue is, and it is asserted by
/// <c>EmailProviderCatalogTests</c>. The projection is kept because the mailbox screen in
/// production binds to it, and because removing an endpoint to tidy up a shape is how a working
/// screen becomes a blank dropdown.</para>
///
/// <para><b>What this shape cannot say.</b> There is ONE encryption flag for both directions, and
/// Microsoft 365 needs two different ones — 993 implicit TLS to read, 587 STARTTLS to send. The
/// flag here is the INBOUND one, and for any provider whose outbound differs the guidance says so
/// in its first sentence, because that is the only channel this DTO leaves open. The screen should
/// move to <c>GET /api/email/providers</c>, which carries a TLS mode and a <c>useSsl</c> value per
/// direction and needs no such warning.</para>
/// </summary>
internal static class MailboxPresets
{
    public static readonly IReadOnlyList<MailboxPresetDTO> All =
        EmailProviderCatalog.ForTenantMailbox.Select(provider => new MailboxPresetDTO(
            provider.Key,
            provider.DisplayName,
            provider.Inbound?.Host ?? string.Empty,
            provider.Inbound?.Port ?? 993,
            provider.OutboundSmtp?.Host ?? string.Empty,
            provider.OutboundSmtp?.Port ?? 587,
            provider.Inbound?.UseSsl ?? provider.OutboundSmtp?.UseSsl ?? true,
            Guidance(provider))).ToList();

    /// <summary>Leads with the direction mismatch when there is one, then the provider's own
    /// advice. An operator who applies a preset and then switches to SMTP has to be told that the
    /// encryption toggle no longer matches, or they save a row that cannot connect.</summary>
    private static string Guidance(EmailProviderDefinition provider)
    {
        var mismatch = provider.Inbound is { } inbound && provider.OutboundSmtp is { } outbound &&
                       inbound.UseSsl != outbound.UseSsl;

        var prefix = mismatch
            ? $"For SENDING, use port {provider.OutboundSmtp!.Port} with 'use a secure connection' " +
              $"{(provider.OutboundSmtp.UseSsl ? "ON" : "OFF")} — the value filled in above is the one " +
              $"for READING mail on port {provider.Inbound!.Port}. "
            : string.Empty;

        var limit = provider.SendingLimit is { Length: > 0 } cap ? " " + cap : string.Empty;
        var enablement = provider.InboundEnablementNote is { Length: > 0 } note ? " " + note : string.Empty;

        return prefix + provider.Guidance + enablement + limit;
    }
}

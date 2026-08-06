using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using System.Net;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SmtpController(
    ILogger<SmtpController> logger,
    ErpRfqAutomationContext context,
    TenantSmtpConcurrencyGate concurrencyGate,
    IOutboundSmtpTransport transport) : ControllerBase
{
    private const long MaximumAttachmentBytes = 10 * 1024 * 1024;
    private const long MaximumRequestBytes = 11 * 1024 * 1024;
    private const int MaximumAttachmentCount = 10;
    private readonly ILogger<SmtpController> _logger = logger;
    private readonly ErpRfqAutomationContext _context = context;
    private readonly TenantSmtpConcurrencyGate _concurrencyGate = concurrencyGate;
    private readonly IOutboundSmtpTransport _transport = transport;

    [HttpPost("send")]
    [RequireModulePermission("Email & SMTP", PermissionAction.Create)]
    [EnableRateLimiting(RateLimitingExtensions.SmtpPolicy)]
    [RequestSizeLimit(MaximumRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestBytes)]
    public async Task<IActionResult> SendEmail([FromForm] SendSupplierEmailRequestDTO request)
    {
        var businessUnitClaim = User.FindFirst("businessUnitId")?.Value;
        if (!long.TryParse(businessUnitClaim, out var businessUnitId) || businessUnitId <= 0)
            return Forbid();
        if (request is null || request.SupplierId <= 0 || string.IsNullOrWhiteSpace(request.ToEmail) ||
            string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body))
            return BadRequest("Supplier, recipient, subject, and body are required.");
        if (ContainsNewline(request.ToEmail) || ContainsNewline(request.Subject))
            return BadRequest("Email headers contain invalid characters.");
        if (request.Subject.Length > 300 || request.Body.Length > 500_000)
            return BadRequest("Email content exceeds the allowed size.");
        if (request.Attachments is { Count: > MaximumAttachmentCount } ||
            request.Attachments?.Sum(x => x.Length) > MaximumAttachmentBytes)
            return BadRequest("Attachments exceed the allowed count or total size.");
        if (request.Attachments?.Any(x => x.Length > 0) == true)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Attachment delivery is unavailable until malware scanning is configured.");

        await using var sendLease = await _concurrencyGate.TryAcquireAsync(
            businessUnitId, HttpContext.RequestAborted);
        if (sendLease is null)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Too many email deliveries are already in progress for this tenant.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;

        MailboxAddress recipient;
        try { recipient = MailboxAddress.Parse(request.ToEmail.Trim()); }
        catch (ParseException) { return BadRequest("The recipient email address is invalid."); }

        var authorizedRecipient = await _context.Suppliers.AnyAsync(s =>
            s.Id == request.SupplierId && s.Buid == businessUnitId && s.IsActive != false &&
            (s.ContactEmail == recipient.Address || s.Contacts.Any(c =>
                c.IsActive != false && c.Email == recipient.Address)), cancellationToken);
        if (!authorizedRecipient)
            return BadRequest("The recipient is not an active contact for this tenant supplier.");

        var configuration = await _context.EmailConfigurations
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive && x.Protocol.ToUpper() == "SMTP")
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (configuration is null)
            return Conflict("No active SMTP configuration is available for this tenant.");
        if (!IsValidConfiguredEndpoint(configuration.Host, configuration.Port))
        {
            _logger.LogError("Rejected invalid SMTP endpoint configuration for tenant {BusinessUnitId}.", businessUnitId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "The tenant SMTP configuration is not permitted.");
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(configuration.ConfigurationName, configuration.EmailAddress));
            message.To.Add(recipient);
            message.Subject = request.Subject.Trim();
            var builder = new BodyBuilder { HtmlBody = request.Body };

            message.Body = builder.ToMessageBody();

            _logger.LogInformation("Connecting to configured SMTP endpoint for tenant {BusinessUnitId}.", businessUnitId);
            await _transport.SendAsync(configuration, message, cancellationToken);
            return Ok(new { message = "Email sent successfully" });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Configured SMTP delivery timed out for tenant {BusinessUnitId}.", businessUnitId);
            return StatusCode(StatusCodes.Status504GatewayTimeout, "SMTP delivery timed out.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Configured SMTP delivery failed for tenant {BusinessUnitId}.", businessUnitId);
            return StatusCode(StatusCodes.Status502BadGateway, "SMTP delivery failed.");
        }
    }

    // Delegates to the shared SSRF policy; kept here under the original name because
    // AdministrativeSecurityTests binds it by reflection.
    internal static bool IsValidConfiguredEndpoint(string? host, int port)
        => MailEndpointPolicy.IsAllowedEndpoint(host, port);

    internal static bool IsPublicAddress(IPAddress address)
        => MailKitOutboundSmtpTransport.IsPublicAddress(address);

    private static bool ContainsNewline(string value)
        => value.Contains('\r') || value.Contains('\n');

}

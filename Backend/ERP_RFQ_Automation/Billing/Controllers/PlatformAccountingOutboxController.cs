using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Controllers;

[ApiController]
[Route("api/platform/billing/accounting-outbox")]
[Authorize(Policy = PlatformPolicies.Billing)]
public sealed class PlatformAccountingOutboxController(
    ErpRfqAutomationContext db, AccountingOutboxService outbox, IPlatformAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] AccountingOutboxStatus? status, CancellationToken ct)
    {
        var query = db.Set<AccountingOutboxMessage>().AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        // Payload intentionally excluded: operator visibility must not disclose legal/tax snapshots.
        return Ok((await query.OrderByDescending(x => x.CreatedAtUtc).Take(500).ToListAsync(ct)).Select(ToDto));
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct) => Ok(await outbox.HealthAsync(ct));

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> Acknowledge(Guid id, [FromBody] AcknowledgeAccountingRequest request, CancellationToken ct)
    {
        try
        {
            await InAuditedTransactionAsync(async () =>
            {
                await outbox.AcknowledgeAsync(id, request.LeaseToken, request.ExternalReference,
                    request.ReceiptSha256, Actor(), ct);
                await audit.WriteAsync(User, "billing.accounting-outbox.acknowledge",
                    nameof(AccountingOutboxMessage), id.ToString(),
                    new { receiptSha256 = request.ReceiptSha256.ToLowerInvariant() },
                    httpContext: HttpContext, ct: ct);
            }, ct);
            return NoContent();
        }
        catch (AccountingOutboxException exception) { return Conflict(new { error = exception.Message }); }
    }

    [HttpPost("{id:guid}/redrive")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> Redrive(Guid id, [FromBody] RedriveAccountingRequest request, CancellationToken ct)
    {
        try
        {
            await InAuditedTransactionAsync(async () =>
            {
                await outbox.RedriveAsync(id, Actor(), request.Reason, ct);
                await audit.WriteAsync(User, "billing.accounting-outbox.redrive",
                    nameof(AccountingOutboxMessage), id.ToString(), new { request.Reason },
                    httpContext: HttpContext, ct: ct);
            }, ct);
            return NoContent();
        }
        catch (AccountingOutboxException exception) { return Conflict(new { error = exception.Message }); }
    }

    private static object ToDto(AccountingOutboxMessage x) => new
    {
        x.Id, x.TenantId, x.SubscriptionInvoiceId, x.MessageType, x.Status, x.ReconciliationStatus,
        x.AttemptCount, x.MaxAttempts, x.CreatedAtUtc, x.AvailableAtUtc, x.LastAttemptAtUtc,
        x.LeaseExpiresAtUtc, x.LastFailureCode, x.ExternalReference, x.ExternalReceiptSha256,
        x.AcknowledgedAtUtc, x.RedrivenAtUtc, x.RedrivenBy, x.RedriveReason
    };

    private string Actor() => User.FindFirst("email")?.Value
                              ?? User.Identity?.Name
                              ?? throw new UnauthorizedAccessException("A valid platform actor is required.");

    private async Task InAuditedTransactionAsync(Func<Task> operation, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await operation();
            return;
        }
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await operation();
            await transaction.CommitAsync(ct);
        });
    }
}

public sealed record AcknowledgeAccountingRequest(
    Guid LeaseToken,
    [property: Required, StringLength(256)] string ExternalReference,
    [property: Required, StringLength(64, MinimumLength = 64)] string ReceiptSha256);

public sealed record RedriveAccountingRequest(
    [property: Required, StringLength(1000, MinimumLength = 10)] string Reason);

using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Controllers;

[ApiController]
[Route("api/platform/billing/invoices")]
[Authorize(Policy = PlatformPolicies.Billing)]
public sealed class PlatformSubscriptionInvoicesController(
    ErpRfqAutomationContext db,
    SubscriptionInvoiceService invoices,
    IPlatformAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] long? tenantId, CancellationToken ct)
    {
        var query = db.Set<SubscriptionInvoice>().AsNoTracking()
            .Include(i => i.Credits).Include(i => i.Payments).AsQueryable();
        if (tenantId is long id) query = query.Where(i => i.TenantId == id);
        return Ok((await query.OrderByDescending(i => i.IssuedAtUtc).Take(500).ToListAsync(ct)).Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInvoiceRequest request, CancellationToken ct)
    {
        try
        {
            var invoice = await InAuditedTransactionAsync(async () =>
            {
                var value = await invoices.CreateDraftAsync(new CreateSubscriptionInvoice(
                    request.StatementId, request.TaxRatePercent, request.TaxTreatment,
                    request.SellerLegalName, request.SellerTaxNumber), Actor(), ct);
                await audit.WriteAsync(User, "billing.invoice.create", nameof(SubscriptionInvoice),
                    value.Id.ToString(), new { value.TenantId, value.BillingStatementId, value.TotalAmount },
                    value.TenantId, HttpContext, ct);
                return value;
            }, ct);
            return Ok(ToDto(invoice));
        }
        catch (BillingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:long}/finalize")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> Finalize(long id, CancellationToken ct)
    {
        try
        {
            var invoice = await InAuditedTransactionAsync(async () =>
            {
                var value = await invoices.FinalizeAsync(id, Actor(), ct);
                await audit.WriteAsync(User, "billing.invoice.finalize", nameof(SubscriptionInvoice),
                    id.ToString(), new { value.TenantId, value.InvoiceNumber, value.TotalAmount },
                    value.TenantId, HttpContext, ct);
                return value;
            }, ct);
            return Ok(ToDto(invoice));
        }
        catch (BillingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:long}/credits")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> Credit(long id, [FromBody] CreditInvoiceRequest request, CancellationToken ct)
    {
        try
        {
            var credit = await InAuditedTransactionAsync(async () =>
            {
                var value = await invoices.CreditAsync(
                    id, request.Amount, request.Reason, Actor(), IdempotencyKey(), ct);
                var tenantId = await TenantIdAsync(id, ct);
                await audit.WriteAsync(User, "billing.invoice.credit", nameof(SubscriptionCreditNote),
                    value.Id.ToString(), new { invoiceId = id, value.Amount, value.Reason },
                    actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);
                return value;
            }, ct);
            return Ok(credit);
        }
        catch (BillingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:long}/payments")]
    public async Task<IActionResult> Payment(long id, [FromBody] RecordInvoicePaymentRequest request, CancellationToken ct)
    {
        try
        {
            var payment = await InAuditedTransactionAsync(async () =>
            {
                var value = await invoices.RecordPaymentAsync(
                    id, request.Amount, request.ExternalReference, request.ReceivedAtUtc, Actor(), ct);
                var tenantId = await TenantIdAsync(id, ct);
                await audit.WriteAsync(User, "billing.invoice.payment", nameof(SubscriptionPayment),
                    value.Id.ToString(), new { invoiceId = id, value.Amount, value.ExternalReference },
                    actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);
                return value;
            }, ct);
            return Ok(payment);
        }
        catch (BillingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    private string Actor() => User.FindFirst("email")?.Value
                              ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                              ?? throw new UnauthorizedAccessException("A valid platform actor is required.");

    private Task<long> TenantIdAsync(long invoiceId, CancellationToken ct) =>
        db.Set<SubscriptionInvoice>().AsNoTracking()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.TenantId)
            .SingleAsync(ct);

    private string IdempotencyKey() =>
        Request.Headers.TryGetValue("Idempotency-Key", out var value)
            ? value.ToString()
            : string.Empty;

    private async Task<T> InAuditedTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return await operation();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private static object ToDto(SubscriptionInvoice i) => new
    {
        id = i.Id.ToString(), tenantId = i.TenantId.ToString(), statementId = i.BillingStatementId.ToString(),
        i.InvoiceNumber, status = i.Status.ToString(), i.Currency, i.Subtotal, i.TaxRatePercent,
        i.TaxAmount, i.TotalAmount, i.CreditedAmount, i.PaidAmount,
        outstandingAmount = i.TotalAmount - i.CreditedAmount - i.PaidAmount,
        i.IssuedAtUtc, i.DueAtUtc, i.TaxTreatment, i.SourceEvidenceSha256,
        i.CreatedBy, i.CreatedAtUtc, i.FinalizedBy, i.FinalizedAtUtc, i.Version,
        credits = i.Credits.Select(c => new { id = c.Id.ToString(), c.CreditNumber, c.Amount, c.Reason, c.CreatedBy, c.CreatedAtUtc }),
        payments = i.Payments.Select(p => new { id = p.Id.ToString(), p.ExternalReference, p.Amount, p.ReceivedAtUtc, p.RecordedBy })
    };
}

public sealed record CreateInvoiceRequest(
    long StatementId,
    [property: Range(0, 100)] decimal TaxRatePercent,
    [property: Required, StringLength(128)] string TaxTreatment,
    [property: Required, StringLength(256)] string SellerLegalName,
    [property: Required, StringLength(64)] string SellerTaxNumber);

public sealed record CreditInvoiceRequest(
    [property: Range(typeof(decimal), "0.01", "999999999999.99")] decimal Amount,
    [property: Required, StringLength(1000, MinimumLength = 5)] string Reason);

public sealed record RecordInvoicePaymentRequest(
    [property: Range(typeof(decimal), "0.01", "999999999999.99")] decimal Amount,
    [property: Required, StringLength(128)] string ExternalReference,
    DateTime ReceivedAtUtc);

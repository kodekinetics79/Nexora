using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialFinance;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/commercial-finance")]
[Authorize]
public sealed class CommercialFinanceController(ICommercialFinanceApplicationService service) : ControllerBase
{
    private readonly ICommercialFinanceApplicationService _service = service;

    [HttpPost("orders/{orderId:long}/invoices")]
    [RequireModulePermission("Accounts Receivable", PermissionAction.Create)]
    public Task<IActionResult> CreateInvoice(long orderId, [FromBody] CreateInvoiceRequest request)
        => ExecuteMutation(async () =>
        {
            var result = await _service.CreateInvoiceAsync(TenantId(), orderId, IdempotencyKey(), request, Actor());
            return CreatedAtAction(nameof(GetDocument), new { documentId = result.Id }, result);
        });

    [HttpPost("documents/{invoiceId:long}/adjustments")]
    [RequireModulePermission("Receivable Adjustments", PermissionAction.Create)]
    public Task<IActionResult> CreateAdjustment(long invoiceId, [FromBody] CreateAdjustmentRequest request)
        => ExecuteMutation(async () =>
        {
            var result = await _service.CreateAdjustmentAsync(TenantId(), invoiceId, IdempotencyKey(), request, Actor());
            return CreatedAtAction(nameof(GetDocument), new { documentId = result.Id }, result);
        });

    [HttpPost("documents/{documentId:long}/issue")]
    [RequireModulePermission("Accounts Receivable", PermissionAction.Edit)]
    public Task<IActionResult> Issue(long documentId, [FromBody] IssueDocumentRequest request)
        => ExecuteMutation(async () => Ok(await _service.IssueAsync(TenantId(), documentId, request, Actor())));

    [HttpPost("documents/{documentId:long}/issue-adjustment")]
    [RequireModulePermission("Receivable Adjustments", PermissionAction.Edit)]
    public Task<IActionResult> IssueAdjustment(long documentId, [FromBody] IssueDocumentRequest request)
        => ExecuteMutation(async () => Ok(await _service.IssueAdjustmentAsync(TenantId(), documentId, request, Actor())));

    [HttpPost("documents/{documentId:long}/cancel")]
    [RequireModulePermission("Accounts Receivable", PermissionAction.Edit)]
    public Task<IActionResult> Cancel(long documentId, [FromBody] CancelDocumentRequest request)
        => ExecuteMutation(async () => Ok(await _service.CancelAsync(TenantId(), documentId, request, Actor())));

    [HttpPost("documents/{documentId:long}/cancel-adjustment")]
    [RequireModulePermission("Receivable Adjustments", PermissionAction.Edit)]
    public Task<IActionResult> CancelAdjustment(long documentId, [FromBody] CancelDocumentRequest request)
        => ExecuteMutation(async () => Ok(await _service.CancelAdjustmentAsync(TenantId(), documentId, request, Actor())));

    [HttpGet("documents/{documentId:long}")]
    [RequireModulePermission("Accounts Receivable", PermissionAction.View)]
    public async Task<IActionResult> GetDocument(long documentId)
    {
        var result = await _service.GetDocumentAsync(TenantId(), documentId);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("documents")]
    [RequireModulePermission("Accounts Receivable", PermissionAction.View)]
    public async Task<IActionResult> GetDocuments([FromQuery] long? customerId, [FromQuery] string? status)
        => Ok(await _service.GetDocumentsAsync(TenantId(), customerId, status));

    [HttpPost("payments")]
    [RequireModulePermission("Customer Payments", PermissionAction.Create)]
    public Task<IActionResult> PostPayment([FromBody] PostPaymentRequest request)
        => ExecuteMutation(async () =>
        {
            var result = await _service.PostPaymentAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"api/commercial-finance/payments/{result.Id}", result);
        });

    [HttpGet("payments")]
    [RequireModulePermission("Customer Payments", PermissionAction.View)]
    public async Task<IActionResult> GetPayments([FromQuery] long? customerId, [FromQuery] string? status)
        => Ok(await _service.GetPaymentsAsync(TenantId(), customerId, status));

    [HttpPost("payments/{paymentId:long}/reverse")]
    [RequireModulePermission("Customer Payments", PermissionAction.Edit)]
    public Task<IActionResult> ReversePayment(long paymentId, [FromBody] ReversePaymentRequest request)
        => ExecuteMutation(async () => Ok(await _service.ReversePaymentAsync(TenantId(), paymentId, request, Actor())));

    [HttpGet("ar/open-items")]
    [RequireModulePermission("Accounts Receivable", PermissionAction.View)]
    public async Task<IActionResult> GetOpenItems([FromQuery] DateTime? asOf)
        => Ok(await _service.GetOpenItemsAsync(TenantId(), asOf));

    [HttpGet("documents/{documentId:long}/write-off-eligibility")]
    [RequireModulePermission("Receivable Write-offs", PermissionAction.View)]
    public async Task<IActionResult> GetWriteOffEligibility(long documentId)
        => Ok(await _service.GetWriteOffEligibilityAsync(TenantId(), documentId));

    [HttpPost("write-offs")]
    [RequireModulePermission("Receivable Write-offs", PermissionAction.Create)]
    public Task<IActionResult> CreateWriteOff([FromBody] CreateWriteOffRequest request)
        => ExecuteMutation(async () =>
        {
            var result = await _service.CreateWriteOffAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"api/commercial-finance/write-offs/{result.Id}", result);
        });

    [HttpGet("write-offs")]
    [RequireModulePermission("Receivable Write-offs", PermissionAction.View)]
    public async Task<IActionResult> GetWriteOffs([FromQuery] long? customerId, [FromQuery] string? status)
        => Ok(await _service.GetWriteOffsAsync(TenantId(), customerId, status));

    [HttpPost("write-offs/{writeOffId:long}/post")]
    [RequireModulePermission("Receivable Write-offs", PermissionAction.Edit)]
    public Task<IActionResult> PostWriteOff(long writeOffId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.PostWriteOffAsync(TenantId(), writeOffId, request, Actor())));

    [HttpPost("write-offs/{writeOffId:long}/cancel")]
    [RequireModulePermission("Receivable Write-offs", PermissionAction.Edit)]
    public Task<IActionResult> CancelWriteOff(long writeOffId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.CancelWriteOffAsync(TenantId(), writeOffId, request, Actor())));

    [HttpPost("write-offs/{writeOffId:long}/reverse")]
    [RequireModulePermission("Receivable Write-offs", PermissionAction.Edit)]
    public Task<IActionResult> ReverseWriteOff(long writeOffId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.ReverseWriteOffAsync(TenantId(), writeOffId, request, Actor())));

    [HttpGet("payments/{paymentId:long}/refund-eligibility")]
    [RequireModulePermission("Customer Refunds", PermissionAction.View)]
    public async Task<IActionResult> GetRefundEligibility(long paymentId)
        => Ok(await _service.GetRefundEligibilityAsync(TenantId(), paymentId));

    [HttpPost("refunds")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Create)]
    public Task<IActionResult> CreateRefund([FromBody] CreateRefundRequest request)
        => ExecuteMutation(async () =>
        {
            var result = await _service.CreateRefundAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"api/commercial-finance/refunds/{result.Id}", result);
        });

    [HttpGet("refunds")]
    [RequireModulePermission("Customer Refunds", PermissionAction.View)]
    public async Task<IActionResult> GetRefunds([FromQuery] long? customerId, [FromQuery] string? status)
        => Ok(await _service.GetRefundsAsync(TenantId(), customerId, status));

    [HttpPost("refunds/{refundId:long}/approve")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Edit)]
    public Task<IActionResult> ApproveRefund(long refundId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.ApproveRefundAsync(TenantId(), refundId, request, Actor())));

    [HttpPost("refunds/{refundId:long}/release")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Edit)]
    public Task<IActionResult> ReleaseRefund(long refundId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.ReleaseRefundAsync(TenantId(), refundId, request, Actor())));

    [HttpPost("refunds/{refundId:long}/confirm-disbursement")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Edit)]
    public Task<IActionResult> ConfirmRefundDisbursement(long refundId, [FromBody] RefundDisbursementRequest request)
        => ExecuteMutation(async () => Ok(await _service.ConfirmRefundDisbursementAsync(TenantId(), refundId, request, Actor())));

    [HttpPost("refunds/{refundId:long}/fail-disbursement")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Edit)]
    public Task<IActionResult> FailRefundDisbursement(long refundId, [FromBody] RefundDisbursementRequest request)
        => ExecuteMutation(async () => Ok(await _service.FailRefundDisbursementAsync(TenantId(), refundId, request, Actor())));

    [HttpPost("refunds/{refundId:long}/cancel")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Edit)]
    public Task<IActionResult> CancelRefund(long refundId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.CancelRefundAsync(TenantId(), refundId, request, Actor())));

    [HttpPost("refunds/{refundId:long}/reverse")]
    [RequireModulePermission("Customer Refunds", PermissionAction.Edit)]
    public Task<IActionResult> ReverseRefund(long refundId, [FromBody] FinanceExceptionActionRequest request)
        => ExecuteMutation(async () => Ok(await _service.ReverseRefundAsync(TenantId(), refundId, request, Actor())));

    private async Task<IActionResult> ExecuteMutation(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (FinanceConflictException exception)
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "Commercial finance conflict", Detail = exception.Message });
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.SerializationFailure)
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "Commercial finance conflict",
                Detail = "The request conflicts with a concurrent or existing financial record. Reload and try again." });
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres &&
            postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation
                or PostgresErrorCodes.SerializationFailure)
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "Commercial finance conflict",
                Detail = "The request conflicts with a concurrent or existing financial record. Reload and try again." });
        }
        catch (DbUpdateException)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid commercial finance request",
                Detail = "The request violates a financial data constraint." });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new ProblemDetails { Status = 404, Title = "Financial record not found", Detail = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid commercial finance request", Detail = exception.Message });
        }
    }

    private long TenantId()
    {
        if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenantId) || tenantId <= 0)
            throw new ArgumentException("A valid tenant claim is required.");
        return tenantId;
    }

    private string Actor()
        => User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? User.Identity?.Name
            ?? throw new ArgumentException("An authenticated actor claim is required.");

    private string IdempotencyKey()
        => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString().Trim()
            : throw new ArgumentException("Idempotency-Key header is required.");
}

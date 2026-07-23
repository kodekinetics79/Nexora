using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.OrderToCash;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/customer-awards")]
[Authorize]
public sealed class CustomerAwardsController(ICustomerAwardApplicationService service) : ControllerBase
{
    [HttpGet("quote/{quoteId:long}")]
    [RequireModulePermission("Customer Awards", PermissionAction.View)]
    public Task<IActionResult> GetByQuote(long quoteId)
        => ExecuteAsync(async () => Ok(await service.GetByQuoteAsync(TenantId(), quoteId, HttpContext.RequestAborted)));

    [HttpPost("purchase-orders")]
    [RequireModulePermission("Customer Awards", PermissionAction.Create)]
    public Task<IActionResult> CreatePurchaseOrder([FromBody] CreateCustomerPurchaseOrderCommand command)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreatePurchaseOrderAsync(TenantId(), IdempotencyKey(), CorrelationId(),
                command, Actor(), HttpContext.RequestAborted);
            return Created($"/api/customer-awards/purchase-orders/{result.Id}", result);
        });

    [HttpPost]
    [RequireModulePermission("Customer Awards", PermissionAction.Create)]
    public Task<IActionResult> CreateAward([FromBody] CreateCustomerAwardCommand command)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateAwardAsync(TenantId(), IdempotencyKey(), CorrelationId(),
                command, Actor(), HttpContext.RequestAborted);
            return Created($"/api/customer-awards/{result.Id}", result);
        });

    [HttpPost("{awardId:long}/confirm")]
    [RequireModulePermission("Customer Awards", PermissionAction.Edit)]
    public Task<IActionResult> Confirm(long awardId, [FromBody] VersionedCustomerAwardCommand command)
        => ExecuteAsync(async () => Ok(await service.ConfirmAwardAsync(TenantId(), awardId, IdempotencyKey(),
            CorrelationId(), command, Actor(), HttpContext.RequestAborted)));

    [HttpPost("{awardId:long}/cancel")]
    [RequireModulePermission("Customer Awards", PermissionAction.Edit)]
    public Task<IActionResult> Cancel(long awardId, [FromBody] CancelCustomerAwardCommand command)
        => ExecuteAsync(async () => Ok(await service.CancelAwardAsync(TenantId(), awardId, IdempotencyKey(),
            CorrelationId(), command, Actor(), HttpContext.RequestAborted)));

    [HttpPost("{awardId:long}/convert-to-order")]
    [RequireModulePermission("Customer Awards", PermissionAction.Edit)]
    [RequireModulePermission("Orders", PermissionAction.Create)]
    public Task<IActionResult> ConvertToOrder(long awardId, [FromBody] VersionedCustomerAwardCommand command)
        => ExecuteAsync(async () => Ok(await service.ConvertToOrderAsync(TenantId(), awardId, IdempotencyKey(),
            CorrelationId(), command, Actor(), HttpContext.RequestAborted)));

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (CustomerAwardConflictException exception)
        {
            return Conflict(Problem(409, "Customer award conflict", exception.Message));
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Conflict(Problem(409, "Customer award conflict", exception.Message));
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.SerializationFailure)
        {
            return Conflict(Problem(409, "Customer award conflict",
                "The request conflicts with a concurrent or existing customer award record. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation
                or PostgresErrorCodes.SerializationFailure)
        {
            return Conflict(Problem(409, "Customer award conflict",
                "The request conflicts with a concurrent or existing customer award record. Reload and try again."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(400, "Invalid customer award request",
                "The customer award request violates a data constraint."));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(404, "Customer award record not found", exception.Message));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(400, "Invalid customer award request", exception.Message));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };

    private long TenantId()
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
            ? value
            : throw new ArgumentException("A valid tenant claim is required.");

    private string Actor() => User.FindFirst("email")?.Value ?? User.Identity?.Name ?? "system";

    private string IdempotencyKey()
        => Header("Idempotency-Key", required: true)!;

    private string CorrelationId()
        => Header("X-Correlation-ID", required: false) ?? HttpContext.TraceIdentifier;

    private string? Header(string name, bool required)
    {
        if (Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.ToString().Trim();
        if (required) throw new ArgumentException($"{name} header is required.");
        return null;
    }
}

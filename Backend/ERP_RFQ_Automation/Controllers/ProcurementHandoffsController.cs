using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Procurement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/procurement-handoffs")]
public sealed class ProcurementHandoffsController(IProcurementHandoffService service) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> Search([FromQuery] long? customerOrderId = null,
        [FromQuery] string? search = null, [FromQuery] int limit = 100)
        => ExecuteAsync(async () => Ok(await service.SearchAsync(TenantId(), customerOrderId, search, limit,
            HttpContext.RequestAborted)));

    [HttpGet("{id:long}")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> Get(long id)
        => ExecuteAsync(async () => Ok(await service.GetAsync(TenantId(), id, HttpContext.RequestAborted)));

    [HttpGet("candidates")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> Candidates()
        => ExecuteAsync(async () => Ok(await service.CandidatesAsync(TenantId(), HttpContext.RequestAborted)));

    [HttpPost]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> Create([FromBody] CreateProcurementHandoffCommand command)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateAsync(TenantId(), IdempotencyKey(), CorrelationId(), Actor(),
                command, HttpContext.RequestAborted);
            return Created($"/api/procurement-handoffs/{result.Id}", result);
        });

    [HttpPost("{id:long}/synchronize")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> Synchronize(long id, [FromBody] SynchronizeProcurementHandoffCommand command)
        => ExecuteAsync(async () => Ok(await service.SynchronizeAsync(TenantId(), id, IdempotencyKey(),
            CorrelationId(), Actor(), command, HttpContext.RequestAborted)));

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException exception) { return NotFound(Problem(404, "Handoff not found", exception.Message)); }
        catch (ArgumentException exception) { return BadRequest(Problem(400, "Invalid handoff request", exception.Message)); }
        catch (InvalidOperationException exception) { return Conflict(Problem(409, "Handoff conflict", exception.Message)); }
        catch (DbUpdateConcurrencyException) { return Conflict(Problem(409, "Handoff conflict", "The handoff changed; reload and retry.")); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation)
        {
            return Conflict(Problem(409, "Handoff conflict", "The handoff conflicts with an existing governed record."));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };
    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
        ? value : throw new ArgumentException("A valid tenant claim is required.");
    private string Actor() => User.FindFirst("email")?.Value ?? User.Identity?.Name ?? "system";
    private string IdempotencyKey() => Request.Headers.TryGetValue("Idempotency-Key", out var value)
        && !string.IsNullOrWhiteSpace(value) ? value.ToString().Trim()
        : throw new ArgumentException("Idempotency-Key header is required.");
    private string CorrelationId() => Request.Headers.TryGetValue("X-Correlation-ID", out var value)
        && !string.IsNullOrWhiteSpace(value) ? value.ToString().Trim() : HttpContext.TraceIdentifier;
}

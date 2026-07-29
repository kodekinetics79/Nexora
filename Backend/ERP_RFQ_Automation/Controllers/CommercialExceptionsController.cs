using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-exceptions")]
public sealed class CommercialExceptionsController(
    ICommercialExceptionApplicationService exceptions,
    IRoleGate roleGate) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<ActionResult<CommercialExceptionPage>> Query(
        [FromQuery] CommercialExceptionQuery query,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () => await exceptions.QueryAsync(
            TenantId(), query, await AccessScopeAsync(), cancellationToken));

    [HttpPost("refresh")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<RefreshCommercialExceptionsResult>> Refresh(
        [FromBody] RefreshCommercialExceptionsRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = TenantId();
        if (!await IsManagerAsync(tenant)) return Forbid();
        return await ExecuteAsync(() => exceptions.RefreshAsync(
            tenant,
            new RefreshCommercialExceptionsCommand(
                First(request.CorrelationId, Request.Headers["X-Correlation-ID"].FirstOrDefault(), HttpContext.TraceIdentifier),
                First(request.IdempotencyKey, Request.Headers["Idempotency-Key"].FirstOrDefault()),
                ActorId()),
            cancellationToken));
    }

    [HttpPost("{id:long}/transition")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<CommercialExceptionItem>> Transition(
        long id,
        [FromBody] TransitionCommercialExceptionRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () => await exceptions.TransitionAsync(
            TenantId(),
            id,
            new TransitionCommercialExceptionCommand(
                request.ExpectedVersion,
                request.TargetStatus,
                request.ActionCode,
                request.Reason ?? string.Empty,
                First(request.CorrelationId, Request.Headers["X-Correlation-ID"].FirstOrDefault(), HttpContext.TraceIdentifier),
                First(request.IdempotencyKey, Request.Headers["Idempotency-Key"].FirstOrDefault()),
                ActorId()),
            await AccessScopeAsync(),
            cancellationToken));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (CommercialExceptionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CommercialExceptionConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<CommercialExceptionAccessScope> AccessScopeAsync()
    {
        var tenant = TenantId();
        if (await IsManagerAsync(tenant))
            return CommercialExceptionAccessScope.ForTenant();
        var userId = UserId();
        if (!userId.HasValue || userId.Value <= 0)
            throw new UnauthorizedAccessException("Authenticated user ID is required.");
        return CommercialExceptionAccessScope.ForOwner(userId.Value);
    }

    private Task<bool> IsManagerAsync(long businessUnitId)
        => roleGate.IsManagerOrAdminAsync(RoleId(), businessUnitId);

    private long TenantId()
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0
            ? id
            : throw new UnauthorizedAccessException("Business Unit ID is required.");

    private long? UserId()
        => long.TryParse(
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value,
            out var id)
            ? id
            : null;

    private long RoleId()
        => long.TryParse(User.FindFirst("roleId")?.Value, out var id) && id > 0 ? id : 0;

    private string ActorId()
    {
        var actorId = First(
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            User.FindFirst("sub")?.Value);
        if (string.IsNullOrWhiteSpace(actorId))
            throw new UnauthorizedAccessException("A stable authenticated actor ID is required.");
        return actorId;
    }

    private static string First(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
}

public sealed record RefreshCommercialExceptionsRequest(
    string? CorrelationId,
    string? IdempotencyKey);

public sealed record TransitionCommercialExceptionRequest(
    long ExpectedVersion,
    CommercialExceptionStatus TargetStatus,
    string ActionCode,
    string? Reason,
    string? CorrelationId,
    string? IdempotencyKey);

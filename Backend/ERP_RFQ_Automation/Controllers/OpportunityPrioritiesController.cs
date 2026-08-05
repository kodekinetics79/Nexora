using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/opportunity-priorities")]
public sealed class OpportunityPrioritiesController(
    IOpportunityPriorityApplicationService priorities,
    IRoleGate roleGate) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<ActionResult<OpportunityPriorityPage>> Query(
        [FromQuery] OpportunityPriorityQuery query,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () => await priorities.QueryAsync(
            TenantId(), query, await AccessScopeAsync(), cancellationToken));

    [HttpPost("reconcile")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<ReconcileOpportunityPrioritiesResult>> Reconcile(
        [FromBody] ReconcileOpportunityPrioritiesRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = TenantId();
        if (!await IsManagerAsync(tenantId)) return Forbid();
        return await ExecuteAsync(() => priorities.ReconcileAsync(
            tenantId,
            new ReconcileOpportunityPrioritiesCommand(
                First(request.CorrelationId, Request.Headers["X-Correlation-ID"].FirstOrDefault(), HttpContext.TraceIdentifier),
                First(request.IdempotencyKey, Request.Headers["Idempotency-Key"].FirstOrDefault()),
                ActorId(),
                request.AfterCommercialCaseId,
                request.BatchSize ?? 100),
            cancellationToken));
    }

    [HttpGet("commercial-cases/{commercialCaseId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<ActionResult<OpportunityPriorityItem>> GetForCommercialCase(
        long commercialCaseId,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () => await priorities.GetForCommercialCaseAsync(
            TenantId(), commercialCaseId, await AccessScopeAsync(), cancellationToken));

    [HttpPost("{recommendationId:long}/feedback")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<OpportunityFeedbackItem>> RecordFeedback(
        long recommendationId,
        [FromBody] RecordOpportunityFeedbackRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var tenantId = TenantId();
            return await priorities.RecordFeedbackAsync(
                tenantId,
                recommendationId,
                new RecordOpportunityFeedbackCommand(
                    request.ExpectedRecommendationId,
                    request.Decision,
                    request.ReplacementActionCode,
                    request.Reason,
                    request.SupersedesFeedbackId,
                    First(request.CorrelationId, Request.Headers["X-Correlation-ID"].FirstOrDefault(), HttpContext.TraceIdentifier),
                    First(request.IdempotencyKey, Request.Headers["Idempotency-Key"].FirstOrDefault()),
                    ActorId(),
                    await IsManagerAsync(tenantId)),
                await AccessScopeAsync(),
                cancellationToken);
        });

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (OpportunityPriorityNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (OpportunityPriorityConflictException ex)
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

    private async Task<OpportunityPriorityAccessScope> AccessScopeAsync()
    {
        var tenantId = TenantId();
        if (await IsManagerAsync(tenantId))
            return OpportunityPriorityAccessScope.ForTenant();
        var userId = UserId();
        if (!userId.HasValue || userId.Value <= 0)
            throw new UnauthorizedAccessException("Authenticated user ID is required.");
        return OpportunityPriorityAccessScope.ForOwner(userId.Value);
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

public sealed record ReconcileOpportunityPrioritiesRequest(
    string? CorrelationId,
    string? IdempotencyKey,
    long? AfterCommercialCaseId = null,
    int? BatchSize = null);

public sealed record RecordOpportunityFeedbackRequest(
    long ExpectedRecommendationId,
    string Decision,
    string? ReplacementActionCode,
    string Reason,
    long? SupersedesFeedbackId,
    string? CorrelationId,
    string? IdempotencyKey);

using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Sla;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-cases")]
public sealed class CommercialLifecycleController : ControllerBase
{
    private readonly ILifecycleApplicationService _lifecycle;
    private readonly IRoleGate _roleGate;
    private readonly ILeadOutcomeReasons _leadOutcomeReasons;

    public CommercialLifecycleController(
        ILifecycleApplicationService lifecycle, IRoleGate roleGate, ILeadOutcomeReasons leadOutcomeReasons)
    {
        _lifecycle = lifecycle;
        _roleGate = roleGate;
        _leadOutcomeReasons = leadOutcomeReasons;
    }

    /// <summary>
    /// The governed outcome-reason picklist a lead loss must choose from. Identical rows to
    /// <c>GET /api/Quote/outcome-reasons</c> — one vocabulary for the whole commercial cycle.
    /// </summary>
    [HttpGet("leads/outcome-reasons")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<ActionResult<IReadOnlyList<OutcomeReasonDto>>> GetLeadOutcomeReasons(CancellationToken ct)
        => Execute(() => _leadOutcomeReasons.GetAsync(TenantId(), ct));

    [HttpGet("leads/{id:long}/lifecycle")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<ActionResult<LifecycleStateView>> GetLeadState(long id, CancellationToken ct)
        => Execute(() => _lifecycle.GetLeadStateAsync(TenantId(), id, ct));

    [HttpPost("leads/{id:long}/transition")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<LifecycleTransitionResult>> TransitionLead(long id, [FromBody] LifecycleTransitionRequest request, CancellationToken ct)
    {
        if (await RequiresManagerAsync("Lead", request.TargetStatusCode)) return Forbid();
        return await Execute(() => _lifecycle.TransitionLeadAsync(
            TenantId(), id, Actor(), Command(request, request.TargetStatusCode ?? string.Empty), false, ct));
    }

    [HttpPost("leads/{id:long}/reopen")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    [RequireManagerRole]
    public Task<ActionResult<LifecycleTransitionResult>> ReopenLead(long id, [FromBody] LifecycleTransitionRequest request, CancellationToken ct)
        => Execute(() => _lifecycle.TransitionLeadAsync(TenantId(), id, Actor(), Command(request, LifecyclePolicy.ReopenTarget("Lead")), true, ct));

    [HttpGet("rfqs/{id:long}/lifecycle")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public Task<ActionResult<LifecycleStateView>> GetRfqState(long id, CancellationToken ct)
        => Execute(() => _lifecycle.GetRfqStateAsync(TenantId(), id, ct));

    [HttpPost("rfqs/{id:long}/transition")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    public async Task<ActionResult<LifecycleTransitionResult>> TransitionRfq(long id, [FromBody] LifecycleTransitionRequest request, CancellationToken ct)
    {
        if (await RequiresManagerAsync("Rfq", request.TargetStatusCode)) return Forbid();
        return await Execute(() => _lifecycle.TransitionRfqAsync(
            TenantId(), id, Actor(), Command(request, request.TargetStatusCode ?? string.Empty), false, ct));
    }

    [HttpPost("rfqs/{id:long}/reopen")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireManagerRole]
    public Task<ActionResult<LifecycleTransitionResult>> ReopenRfq(long id, [FromBody] LifecycleTransitionRequest request, CancellationToken ct)
        => Execute(() => _lifecycle.TransitionRfqAsync(TenantId(), id, Actor(), Command(request, LifecyclePolicy.ReopenTarget("Rfq")), true, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (LifecycleNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (LifecycleValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (LifecycleConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    private LifecycleTransitionCommand Command(LifecycleTransitionRequest request, string target)
    {
        var correlationId = First(request.CorrelationId, Request.Headers["X-Correlation-ID"].FirstOrDefault(), HttpContext.TraceIdentifier);
        var requestReference = First(request.RequestReference, Request.Headers["X-Request-ID"].FirstOrDefault(), HttpContext.TraceIdentifier);
        var idempotencyKey = First(request.IdempotencyKey, Request.Headers["Idempotency-Key"].FirstOrDefault(), string.Empty);
        return new LifecycleTransitionCommand(target, request.ExpectedVersion, request.ReasonCode, request.ReasonNotes,
            "Api", correlationId, requestReference, idempotencyKey);
    }

    private LifecycleActor Actor()
        => new(First(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, User.FindFirst("sub")?.Value,
                User.FindFirst(ClaimTypes.Email)?.Value, User.FindFirst("email")?.Value, User.Identity?.Name),
            "AuthenticatedUser");

    private async Task<bool> RequiresManagerAsync(string aggregateType, string? target)
    {
        var canonical = LifecyclePolicy.Canonicalize(aggregateType, target);
        if (!LifecyclePolicy.RequiresElevatedAuthorization(canonical)) return false;
        if (!long.TryParse(User.FindFirst("roleId")?.Value, out var roleId)) return true;
        return !await _roleGate.IsManagerOrAdminAsync(roleId, TenantId());
    }

    private long TenantId()
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0
            ? id
            : throw new LifecycleValidationException("Business Unit ID is required.");

    private static string First(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

public sealed record LifecycleTransitionRequest(
    string? TargetStatusCode,
    int ExpectedVersion,
    string? ReasonCode,
    string? ReasonNotes,
    string? CorrelationId,
    string? RequestReference,
    string? IdempotencyKey);

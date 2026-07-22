using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialRouting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-routing")]
public sealed class CommercialRoutingController : ControllerBase
{
    private readonly ICommercialRoutingApplicationService _routing;

    public CommercialRoutingController(ICommercialRoutingApplicationService routing) => _routing = routing;

    [HttpPost("leads/{leadId:long}/route")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<RoutingDecisionResponse>> Route(
        long leadId, [FromBody] RouteLeadRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.RouteLeadAsync(TenantId(), new RouteLeadCommand(
            leadId, request.IdempotencyKey, request.CorrelationId, request.ScopeKeys), ct));

    [HttpPost("leads/{leadId:long}/assign")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<RoutingDecisionResponse>> Assign(
        long leadId, [FromBody] AssignLeadRoutingRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.AssignLeadAsync(TenantId(), new ManualAssignLeadCommand(
            leadId, request.AssignedToUserId, UserId(), request.IdempotencyKey,
            request.CorrelationId, request.AssignmentScope, request.Comment,
            true, request.ExpectedAssigneeId), ct));

    [HttpGet("queue")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<QueuePageResponse>> Queue(
        [FromQuery] WorkItemStatus? status = null,
        [FromQuery] string? search = null,
        [FromQuery] bool overdueOnly = false,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default) =>
        Ok(await _routing.GetQueueAsync(TenantId(), status, search, overdueOnly, pageNumber, pageSize, ct));

    [HttpPost("queue/{id:long}/claim")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<UnassignedQueueItemResponse>> Claim(
        long id, [FromBody] QueueLeaseRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.ClaimAsync(
            TenantId(), id, new QueueLeaseCommand(request.ExpectedVersion, RequiredUserId(), request.LeaseMinutes), ct));

    [HttpPost("queue/{id:long}/release")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<UnassignedQueueItemResponse>> Release(
        long id, [FromBody] QueueVersionRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.ReleaseAsync(
            TenantId(), id, new QueueReleaseCommand(request.ExpectedVersion, RequiredUserId()), ct));

    [HttpPost("queue/{id:long}/assign")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<RoutingDecisionResponse>> AssignQueueItem(
        long id, [FromBody] AssignQueueItemRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.AssignQueueItemAsync(TenantId(), id, new AssignQueueItemCommand(
            request.ExpectedVersion, request.AssignedToUserId, UserId(), request.IdempotencyKey,
            request.CorrelationId, request.AssignmentScope, request.Comment), ct));

    [HttpPost("queue/bulk-assign")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<IReadOnlyList<BulkQueueAssignmentResult>>> BulkAssignQueue(
        [FromBody] BulkAssignQueueRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.BulkAssignQueueAsync(TenantId(), new BulkAssignQueueCommand(
            request.Items, request.AssignedToUserId, UserId(), request.IdempotencyKeyPrefix,
            request.CorrelationId, request.AssignmentScope, request.Comment), ct));

    [HttpPost("customer-identifiers")]
    [RequireManagerRole]
    [RequireModulePermission("Customers", PermissionAction.Edit)]
    public Task<ActionResult<CustomerIdentifier>> UpsertIdentifier(
        [FromBody] UpsertCustomerIdentifierCommand request, CancellationToken ct) =>
        Execute(async () => await _routing.UpsertIdentifierAsync(TenantId(), request, ct));

    [HttpPost("customer-ownerships")]
    [RequireManagerRole]
    [RequireModulePermission("Customers", PermissionAction.Edit)]
    public Task<ActionResult<CustomerOwnership>> CreateOwnership(
        [FromBody] CreateCustomerOwnershipCommand request, CancellationToken ct) =>
        Execute(async () => await _routing.CreateOwnershipAsync(TenantId(), request, ct));

    [HttpGet("customers/{customerId:long}")]
    [RequireModulePermission("Customers", PermissionAction.View)]
    public async Task<ActionResult<CustomerRoutingProfileResponse>> CustomerProfile(
        long customerId, CancellationToken ct)
    {
        var result = await _routing.GetCustomerProfileAsync(TenantId(), customerId, ct);
        return result == null ? NotFound() : Ok(result);
    }

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (RoutingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (RoutingConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0
        ? id
        : throw new RoutingConflictException("Business Unit ID is required.");

    private long? UserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    private long RequiredUserId() => UserId()
        ?? throw new RoutingConflictException("Authenticated user ID is required.");
}

public sealed record RouteLeadRequest(
    string IdempotencyKey,
    string CorrelationId,
    IReadOnlyDictionary<OwnershipScope, string?>? ScopeKeys = null);

public sealed record AssignLeadRoutingRequest(
    long AssignedToUserId,
    long? ExpectedAssigneeId,
    string IdempotencyKey,
    string CorrelationId,
    AssignmentScope AssignmentScope = AssignmentScope.LeadOnly,
    string? Comment = null);

public sealed record QueueLeaseRequest(long ExpectedVersion, int LeaseMinutes = 15);
public sealed record QueueVersionRequest(long ExpectedVersion);

public sealed record AssignQueueItemRequest(
    long ExpectedVersion,
    long AssignedToUserId,
    string IdempotencyKey,
    string CorrelationId,
    AssignmentScope AssignmentScope = AssignmentScope.LeadOnly,
    string? Comment = null);

public sealed record BulkAssignQueueRequest(
    IReadOnlyList<BulkQueueAssignmentItem> Items,
    long AssignedToUserId,
    string IdempotencyKeyPrefix,
    string CorrelationId,
    AssignmentScope AssignmentScope = AssignmentScope.LeadOnly,
    string? Comment = null);

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
    private readonly IRoleGate _roles;

    public CommercialRoutingController(ICommercialRoutingApplicationService routing, IRoleGate roles)
    {
        _routing = routing;
        _roles = roles;
    }

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

    [HttpGet("owner-options")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<RoutingOwnerOptionResponse>>> OwnerOptions(CancellationToken ct) =>
        Ok(await _routing.GetOwnerOptionsAsync(TenantId(), ct));

    /// <summary>
    /// The ownership control on the lead detail screen.
    ///
    /// <para>This action cannot carry <c>[RequireManagerRole]</c> the way its four siblings do,
    /// because the authority it needs is not a property of the caller alone: taking an unowned lead
    /// or putting down your own is ordinary rep work, and moving somebody else's is a manager's
    /// call. An authorization attribute cannot see which of those a request is. So the caller's
    /// rank is resolved here and passed into the command, and
    /// <c>CommercialRoutingApplicationService</c> decides — against the lead's CURRENT owner, in
    /// the same transaction that writes the change.</para>
    ///
    /// <para>Before this, the action carried the module permission and nothing else, so a rep the
    /// manager-only routing queue refused could perform the identical reassignment here.</para>
    /// </summary>
    [HttpPut("leads/{leadId:long}/owner")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<LeadOwnershipResponse>> ChangeOwner(
        long leadId, [FromBody] ChangeLeadOwnerRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.ChangeLeadOwnershipAsync(TenantId(), new ChangeLeadOwnershipCommand(
            leadId, request.Action, request.AssignedToUserId, UserId(), await IsManagerAsync(),
            request.ExpectedAssignmentVersion,
            request.IdempotencyKey, request.CorrelationId, request.Comment), ct));

    /// <summary>
    /// Reads the tenant's fallback lead owner — "when Nexora can't work out who owns an inquiry,
    /// give it to ___" — together with whether routing will actually use that person and why.
    /// View permission, because the answer explains where a rep's inquiries came from.
    /// </summary>
    [HttpGet("default-owner")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<ActionResult<DefaultLeadOwnerResponse>> DefaultOwner(CancellationToken ct) =>
        Execute(async () => await _routing.GetDefaultOwnerAsync(TenantId(), ct));

    /// <summary>
    /// Sets or clears the tenant's fallback lead owner. Send <c>defaultOwnerUserId: null</c> to
    /// clear it. Manager-only: it is a tenant-wide routing rule, not a per-lead decision.
    /// </summary>
    [HttpPut("default-owner")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<DefaultLeadOwnerResponse>> SetDefaultOwner(
        [FromBody] SetDefaultLeadOwnerRequest request, CancellationToken ct) =>
        Execute(async () => await _routing.SetDefaultOwnerAsync(TenantId(),
            new SetDefaultLeadOwnerCommand(request.DefaultOwnerUserId, UserId()), ct));

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
        catch (RoutingForbiddenException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }); }
        catch (RoutingConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Whether the caller holds manager or admin authority in this tenant. Resolved through the
    /// same <see cref="IRoleGate"/> that backs <c>[RequireManagerRole]</c>, so the endpoint that
    /// has to ask the question in code cannot drift from the four that state it as an attribute.
    /// A caller with no parsable role claim is not a manager.
    /// </summary>
    private async Task<bool> IsManagerAsync()
    {
        var roleClaim = User.FindFirst("roleId")?.Value;
        return long.TryParse(roleClaim, out var roleId) && roleId > 0
            && await _roles.IsManagerOrAdminAsync(roleId, TenantId());
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

public sealed record ChangeLeadOwnerRequest(
    LeadOwnershipAction Action,
    long? AssignedToUserId,
    long ExpectedAssignmentVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? Comment = null);

/// <summary>Body of PUT default-owner. A null <c>DefaultOwnerUserId</c> clears the setting.</summary>
public sealed record SetDefaultLeadOwnerRequest(long? DefaultOwnerUserId);

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

using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.InboundLogistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// FR-MAS-01..05. Inbound supplier shipments, the Saudi entry-point master and the lead-time policy
/// the Material Available Date is derived from.
///
/// <para>Shipment routes are gated on <c>Orders</c> because decision R3 keys the inbound shipment to
/// the Supplier PO, and the whole supplier-PO surface is already gated that way — a buyer who may
/// acknowledge and receive an order is the same person who records where it has got to. The
/// <c>Shipments</c> module is deliberately NOT used: it means outbound customer despatch, and
/// borrowing it would grant inbound rights to whoever was given despatch rights.</para>
///
/// <para>The entry-point master is written under <c>Products</c>, which is the module that already
/// governs the warehouse master — it is the same kind of governed logistics reference data — while
/// its READ sits on <c>Orders</c> so the buyer recording a milestone can always populate the
/// dropdown.</para>
/// </summary>
[ApiController]
[Route("api/inbound-shipments")]
[Authorize]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(
    ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Procurement)]
public sealed class InboundShipmentsController(
    IInboundShipmentApplicationService service,
    ILogger<InboundShipmentsController> logger) : ControllerBase
{
    [HttpGet("purchase-orders/{purchaseOrderId:long}")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> GetForPurchaseOrder(long purchaseOrderId)
        => ExecuteAsync(async () => Ok(
            await service.GetPurchaseOrderShipmentsAsync(TenantId(), purchaseOrderId, RequestAborted)));

    [HttpPost]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> Create([FromBody] CreateInboundShipmentRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateShipmentAsync(new CreateInboundShipmentCommand(
                TenantId(), request.PurchaseOrderId, request.Lines ?? [], IdempotencyKey(), Actor(),
                CorrelationId(), request.CarrierName, request.TrackingReference, request.EtaDate,
                request.PortOfEntryId, request.ReadyAtFactoryOn), RequestAborted);
            return Created($"/api/inbound-shipments/{result.Id}", result);
        });

    /// <summary>
    /// FR-MAS-01. Records a milestone. The server owns the ladder: a backwards or repeated
    /// transition is refused with a 409 rather than accepted and silently reordered.
    /// </summary>
    [HttpPost("{shipmentId:long}/milestones")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> RecordMilestone(long shipmentId, [FromBody] RecordInboundShipmentMilestoneRequest request)
        => ExecuteAsync(async () => Ok(await service.RecordMilestoneAsync(
            new RecordInboundShipmentMilestoneCommand(TenantId(), shipmentId, request.ExpectedVersion,
                request.Milestone, request.OccurredOn, IdempotencyKey(), Actor(), CorrelationId(),
                request.PortOfEntryId, request.EtaDate, request.Note), RequestAborted)));

    /// <summary>
    /// FR-MAS-02, manual path. Maintains carrier, tracking reference and ETA.
    ///
    /// <para>There is no carrier-API counterpart to this route. D4 — the Phase 1 carrier subset — is
    /// still open, and a fabricated integration against an unnamed provider would be a mock in a
    /// production path. <c>TrackingSource</c> exists so an API-sourced update is distinguishable
    /// from a keyed one the day one is built.</para>
    /// </summary>
    [HttpPost("{shipmentId:long}/tracking")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> UpdateTracking(long shipmentId, [FromBody] UpdateInboundShipmentTrackingRequest request)
        => ExecuteAsync(async () => Ok(await service.UpdateTrackingAsync(
            new UpdateInboundShipmentTrackingCommand(TenantId(), shipmentId, request.ExpectedVersion,
                IdempotencyKey(), Actor(), CorrelationId(), request.CarrierName, request.TrackingReference,
                request.EtaDate, request.ClearEta, request.Note), RequestAborted)));

    // ------------------------------------------------------------------ entry-point master

    [HttpGet("ports-of-entry")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> GetPorts([FromQuery] bool includeInactive = false)
        => ExecuteAsync(async () => Ok(
            await service.GetPortsOfEntryAsync(TenantId(), includeInactive, RequestAborted)));

    [HttpPost("ports-of-entry")]
    [RequireModulePermission("Products", PermissionAction.Create)]
    public Task<IActionResult> CreatePort([FromBody] CreatePortOfEntryRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreatePortOfEntryAsync(new CreatePortOfEntryCommand(
                TenantId(), request.Code, request.Name, request.Kind, IdempotencyKey(), Actor(),
                CorrelationId(), request.CountryCode ?? "SA", request.City), RequestAborted);
            return Created($"/api/inbound-shipments/ports-of-entry/{result.Id}", result);
        });

    [HttpPost("ports-of-entry/{portOfEntryId:long}/activation")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<IActionResult> SetPortActivation(long portOfEntryId, [FromBody] SetPortOfEntryActiveRequest request)
        => ExecuteAsync(async () => Ok(await service.SetPortOfEntryActiveAsync(
            new SetPortOfEntryActiveCommand(TenantId(), portOfEntryId, request.IsActive,
                request.ExpectedVersion, IdempotencyKey(), Actor(), CorrelationId()), RequestAborted)));

    /// <summary>
    /// Loads the canonical Saudi entry points on an explicit operator request. Nothing is seeded
    /// automatically; a tenant that wants a different list simply never calls this.
    /// </summary>
    [HttpPost("ports-of-entry/import-standard")]
    [RequireModulePermission("Products", PermissionAction.Create)]
    public Task<IActionResult> ImportStandardPorts()
        => ExecuteAsync(async () => Ok(await service.ImportStandardSaudiPortsAsync(
            new ImportStandardSaudiPortsCommand(TenantId(), IdempotencyKey(), Actor(), CorrelationId()),
            RequestAborted)));

    // ------------------------------------------------------------------ lead-time policy

    [HttpGet("policy")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> GetPolicy()
        => ExecuteAsync(async () => Ok(await service.GetPolicyAsync(TenantId(), RequestAborted)));

    /// <summary>
    /// FR-MAS-03. Sets the customs-clearance and putaway lead times. Manager or admin only: the two
    /// numbers re-derive every Material Available Date in the tenant, and those dates are what
    /// customer delivery promises are made against.
    /// </summary>
    [HttpPut("policy")]
    [RequireManagerRole]
    public Task<IActionResult> UpdatePolicy([FromBody] UpdateInboundLogisticsPolicyRequest request)
        => ExecuteAsync(async () => Ok(await service.UpdatePolicyAsync(
            new UpdateInboundLogisticsPolicyCommand(TenantId(), IdempotencyKey(), Actor(), CorrelationId(),
                request.CustomsClearanceLeadDays, request.PutawayLeadDays, request.ClearLeadTimes),
            RequestAborted)));

    // ------------------------------------------------------------------ plumbing

    private CancellationToken RequestAborted => HttpContext.RequestAborted;

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InboundLogisticsConflictException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Inbound shipment conflict", exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unauthorized(Problem(StatusCodes.Status401Unauthorized, "Invalid authentication context",
                exception.Message));
        }
        catch (InboundLogisticsValidationException exception) when (IsNotFound(exception.Message))
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Inbound shipment record not found",
                exception.Message));
        }
        catch (InboundLogisticsValidationException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid inbound shipment request",
                exception.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Inbound shipment conflict",
                "The shipment changed during this request. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
                                                  && IsDatabaseConflict(postgres))
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Inbound shipment conflict",
                "The request conflicts with an existing or concurrent shipment record. Reload and try again."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid inbound shipment request",
                "The request violates an inbound shipment data constraint."));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid inbound shipment request",
                exception.Message));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Inbound shipment request failed. CorrelationId={CorrelationId}",
                SafeCorrelationId());
            return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                StatusCodes.Status500InternalServerError, "Inbound shipment request failed",
                "The inbound shipment request could not be completed."));
        }
    }

    private static bool IsDatabaseConflict(PostgresException exception)
        => exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation
            or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected;

    private static bool IsNotFound(string message)
        => message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };

    private long TenantId()
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
            ? value
            : throw new UnauthorizedAccessException("A valid tenant claim is required.");

    private string Actor()
        => User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.Identity?.Name
            ?? throw new UnauthorizedAccessException("An authenticated actor claim is required.");

    private string IdempotencyKey() => RequiredHeader("Idempotency-Key");

    private string CorrelationId() => RequiredHeader("X-Correlation-ID");

    private string RequiredHeader(string name)
    {
        if (!Request.Headers.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} header is required.");
        var normalized = value.ToString().Trim();
        if (normalized.Length > 130 || normalized.Any(char.IsControl))
            throw new ArgumentException($"{name} header must contain at most 130 printable characters.");
        return normalized;
    }

    private string SafeCorrelationId()
        => Request.Headers.TryGetValue("X-Correlation-ID", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString().Trim()
            : HttpContext.TraceIdentifier;
}

public sealed record CreateInboundShipmentRequest(
    long PurchaseOrderId,
    IReadOnlyCollection<InboundShipmentLineCommand>? Lines,
    string? CarrierName = null,
    string? TrackingReference = null,
    DateOnly? EtaDate = null,
    long? PortOfEntryId = null,
    DateOnly? ReadyAtFactoryOn = null);

public sealed record RecordInboundShipmentMilestoneRequest(
    long ExpectedVersion,
    string Milestone,
    DateOnly OccurredOn,
    long? PortOfEntryId = null,
    DateOnly? EtaDate = null,
    string? Note = null);

public sealed record UpdateInboundShipmentTrackingRequest(
    long ExpectedVersion,
    string? CarrierName = null,
    string? TrackingReference = null,
    DateOnly? EtaDate = null,
    bool ClearEta = false,
    string? Note = null);

public sealed record CreatePortOfEntryRequest(
    string Code,
    string Name,
    string Kind,
    string? CountryCode = null,
    string? City = null);

public sealed record SetPortOfEntryActiveRequest(bool IsActive, long ExpectedVersion);

public sealed record UpdateInboundLogisticsPolicyRequest(
    int? CustomsClearanceLeadDays = null,
    int? PutawayLeadDays = null,
    bool ClearLeadTimes = false);

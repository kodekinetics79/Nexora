using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Traceability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Gate 5 Module 5 — FR-MTR-01..05.
///
/// <para>Gated on the <c>Products</c> module because that is the seeded permission that already
/// governs stock and availability, and a warehouse or quality user who can see inventory can see
/// the lots behind it. A dedicated <c>Material Traceability</c> module would express the quality
/// role more precisely — in particular it would let quarantine release be granted separately from
/// catalogue editing — but a new module needs a catalogue entry AND a seeded <c>Module</c> row
/// before any non-super-admin can be given it, and shipping the screen ahead of the seed would
/// produce a page that 403s for every real user with nothing on screen explaining why.</para>
/// </summary>
[ApiController]
[Route("api/material-traceability")]
[Authorize]
[RequiresEntitlement(TypedEntitlementCatalog.Inventory)]
public sealed class MaterialTraceabilityController(
    IMaterialTraceabilityService service,
    IMaterialLotCertificateService certificates,
    ILogger<MaterialTraceabilityController> logger) : ControllerBase
{
    [HttpGet("lots")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public Task<IActionResult> SearchLots(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] long? supplierId,
        [FromQuery] long? productId,
        [FromQuery] long? warehouseId,
        [FromQuery] bool expiredCertificatesOnly = false,
        [FromQuery] int limit = 50)
        => ExecuteAsync(async () => Ok(await service.SearchLotsAsync(TenantId(),
            new LotSearchQuery(search, status, supplierId, productId, warehouseId,
                expiredCertificatesOnly, limit), RequestAborted)));

    /// <summary>FR-MTR-03 where-from: this lot back to the supplier PO that bought it.</summary>
    [HttpGet("lots/{materialLotId:long}")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public Task<IActionResult> GetLot(long materialLotId)
        => ExecuteAsync(async () => Ok(await service.GetLotAsync(TenantId(), materialLotId, RequestAborted)));

    /// <summary>FR-MTR-03 where-used: this sales order forward to every fulfilment lot.</summary>
    [HttpGet("orders/{orderId:long}/trace")]
    [RequireModulePermission("Products", PermissionAction.View)]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> GetOrderTrace(long orderId)
        => ExecuteAsync(async () => Ok(await service.GetOrderTraceAsync(TenantId(), orderId, RequestAborted)));

    [HttpGet("lots/{materialLotId:long}/certificates")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public Task<IActionResult> ListCertificates(long materialLotId)
        => ExecuteAsync(async () => Ok(await certificates.ListAsync(TenantId(), materialLotId, RequestAborted)));

    /// <summary>
    /// FR-MTR-02. Files a compliance document against a lot. The bytes go through the shared
    /// inspection and content-addressed evidence pipeline; nothing about storage is re-implemented.
    /// </summary>
    [HttpPost("lots/{materialLotId:long}/certificates")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    [RequestSizeLimit(MaterialLotCertificateService.MaximumDocumentBytes)]
    public async Task<IActionResult> AttachCertificate(
        long materialLotId,
        IFormFile? file,
        [FromForm] string certificateType,
        [FromForm] string? certificateNumber,
        [FromForm] string? issuedBy,
        [FromForm] DateOnly? issuedOn,
        [FromForm] DateOnly? expiresOn,
        [FromForm] string? notes)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "No certificate uploaded",
                "Attach the certificate document."));
        if (file.Length > MaterialLotCertificateService.MaximumDocumentBytes)
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Certificate too large",
                "The certificate exceeds the 25 MB limit."));

        byte[] bytes;
        await using (var content = new MemoryStream())
        {
            await file.CopyToAsync(content, RequestAborted);
            bytes = content.ToArray();
        }

        return await ExecuteAsync(async () =>
        {
            var view = await certificates.AttachAsync(
                new RecordLotCertificateCommand(TenantId(), materialLotId, certificateType,
                    certificateNumber, issuedBy, issuedOn, expiresOn, notes, Actor()),
                file.FileName, bytes, file.ContentType, RequestAborted);
            return Created($"/api/material-traceability/lots/{materialLotId}/certificates/{view.Id}", view);
        });
    }

    /// <summary>FR-MTR-05. Holds a lot and takes its stock out of availability.</summary>
    [HttpPost("lots/{materialLotId:long}/quarantine")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<IActionResult> Quarantine(long materialLotId, [FromBody] QuarantineLotRequest request)
        => ExecuteAsync(async () => Ok(await service.QuarantineAsync(new QuarantineLotCommand(
            TenantId(), materialLotId, request.ExpectedVersion, request.ReasonCode, request.Reason,
            Actor(), CorrelationId(), IdempotencyKey()), RequestAborted)));

    /// <summary>FR-MTR-05. The authorized release. Reason required, actor recorded, event written.</summary>
    [HttpPost("lots/{materialLotId:long}/release")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<IActionResult> Release(long materialLotId, [FromBody] ReleaseLotRequest request)
        => ExecuteAsync(async () => Ok(await service.ReleaseAsync(new ReleaseLotCommand(
            TenantId(), materialLotId, request.ExpectedVersion, request.Reason,
            Actor(), CorrelationId(), IdempotencyKey()), RequestAborted)));

    /// <summary>
    /// FR-MTR-01 forward link. States which lot fulfilled a sales-order line, and which delivery
    /// note it left on. This is the row where-used trace reads, and the second point at which a
    /// quarantined lot is refused.
    /// </summary>
    [HttpPost("lot-consumptions")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> DeclareConsumption([FromBody] DeclareLotConsumptionRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.DeclareConsumptionAsync(new DeclareLotConsumptionCommand(
                TenantId(), request.MaterialLotId, request.OrderId, request.OrderItemId,
                request.ShipmentId, request.Quantity, request.ComplianceOverrideReason,
                Actor(), CorrelationId(), IdempotencyKey()), RequestAborted);
            return Created($"/api/material-traceability/lot-consumptions/{result.Id}", result);
        });

    private CancellationToken RequestAborted => HttpContext.RequestAborted;

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MaterialTraceabilityConflictException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Material traceability conflict", exception.Message));
        }
        catch (DocumentInspectionException exception)
        {
            // The intake contract every other upload door uses: a machine code the UI branches on
            // plus the user-safe reason inspection recorded.
            var problem = Problem(StatusCodes.Status400BadRequest, "Certificate rejected",
                exception.Inspection.Reason);
            problem.Extensions["errorCode"] = exception.Inspection.ErrorCode;
            return BadRequest(problem);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unauthorized(Problem(StatusCodes.Status401Unauthorized, "Invalid authentication context", exception.Message));
        }
        catch (MaterialTraceabilityValidationException exception) when (IsNotFound(exception.Message))
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Material record not found", exception.Message));
        }
        catch (MaterialTraceabilityValidationException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid traceability request", exception.Message));
        }
        catch (ERP_RFQ_Automation.Inventory.StockLedgerException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Material traceability conflict", exception.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Material traceability conflict",
                "The lot changed during this request. Reload and try again."));
        }
        catch (PostgresException exception) when (IsDatabaseConflict(exception))
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Material traceability conflict",
                "The request conflicts with an existing or concurrent lot record. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && IsDatabaseConflict(postgres))
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Material traceability conflict",
                "The request conflicts with an existing or concurrent lot record. Reload and try again."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid traceability request",
                "The request violates a material traceability data constraint."));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid traceability request", exception.Message));
        }
        catch (ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageUnavailableException exception)
        {
            // Lot certificates are written to the same immutable evidence store as every other
            // upload, so a dead store must answer the same way here. It has to be caught HERE and
            // not left to EvidenceStorageProblemFilter: the catch-all below handles the exception
            // first, and an MVC exception filter only ever sees UNHANDLED ones. Without this the
            // QA operator filing a mill certificate gets "the material traceability request could
            // not be completed" — the 2026-08-12 shrug, one screen over.
            logger.LogError(exception,
                "Material traceability request refused: durable evidence storage is unavailable "
                + "(configuration fault: {IsConfigurationFault}). CorrelationId={CorrelationId}",
                exception.IsConfigurationFault, SafeCorrelationId());
            return ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageProblemFilter.ToResult(exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Material traceability request failed. CorrelationId={CorrelationId}",
                SafeCorrelationId());
            return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                StatusCodes.Status500InternalServerError, "Material traceability request failed",
                "The material traceability request could not be completed."));
        }
    }

    private static bool IsDatabaseConflict(PostgresException exception)
        => exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation
            or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.RaiseException;

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

public sealed record QuarantineLotRequest(long ExpectedVersion, string ReasonCode, string Reason);

public sealed record ReleaseLotRequest(long ExpectedVersion, string Reason);

public sealed record DeclareLotConsumptionRequest(
    long MaterialLotId,
    long OrderId,
    long OrderItemId,
    decimal Quantity,
    long? ShipmentId = null,
    string? ComplianceOverrideReason = null);

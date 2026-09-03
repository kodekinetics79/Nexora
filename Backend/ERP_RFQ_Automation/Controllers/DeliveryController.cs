using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Gate 7 Module 7 — outbound delivery, proof of delivery and delivery exceptions
/// (FR-DLM-01, 02, 03, 05, 07).
///
/// <para>Gated on the seeded <c>Shipments</c> module, which already governs the despatch screens.
/// Confirming a delivery is an <c>Edit</c>, because it changes what the customer is billable for;
/// reading a note or a POD is a <c>View</c>.</para>
///
/// <para><b>What is deliberately absent.</b> There is no vehicle, no driver, no route, no
/// scheduling and no tracking feed, per decision R22. Tech Connect uses third-party carriers and
/// owns no fleet; the GPS on a POD is a coordinate stamped at the moment of signature and nothing
/// polls it. There is also no SMS or WhatsApp notification — email only, and email is the existing
/// channel.</para>
/// </summary>
[ApiController]
[Route("api/delivery")]
[Authorize]
public sealed class DeliveryController(
    IDeliveryConfirmationService confirmations,
    IDeliveryProofEvidenceService evidence,
    IDeliveryNoteReadService notes,
    IDeliveredQuantityLedger ledger,
    ILogger<DeliveryController> logger) : ControllerBase
{
    /// <summary>FR-DLM-01. The delivery note, assembled server-side from the record.</summary>
    [HttpGet("shipments/{shipmentId:long}/note")]
    [RequireModulePermission("Shipments", PermissionAction.View)]
    public Task<IActionResult> GetNote(long shipmentId)
        => ExecuteAsync(async () =>
        {
            var note = await notes.GetAsync(TenantId(), shipmentId, RequestAborted);
            return note is null ? NotFound(Problem(StatusCodes.Status404NotFound,
                "Shipment not found", "That shipment was not found in this business unit."))
                : Ok(note);
        });

    /// <summary>
    /// FR-DLM-02. Awarded, despatched, awaiting confirmation, accepted and refused, per order line.
    /// </summary>
    [HttpGet("orders/{orderId:long}/delivered-quantities")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> GetDeliveredQuantities(long orderId)
        => ExecuteAsync(async () => Ok(await ledger.ForOrderAsync(TenantId(), orderId, RequestAborted)));

    /// <summary>
    /// FR-DLM-05. Moves a shipment along the ladder. DELIVERED and DELIVERY_EXCEPTION are refused
    /// here by design — they are outcomes of what the customer accepted, not selections.
    /// </summary>
    [HttpPost("shipments/{shipmentId:long}/status")]
    [RequireModulePermission("Shipments", PermissionAction.Edit)]
    public Task<IActionResult> Transition(long shipmentId, [FromBody] TransitionDeliveryRequest request)
        => ExecuteAsync(async () => Ok(new
        {
            shipmentId,
            deliveryStatus = await confirmations.TransitionAsync(
                TenantId(), shipmentId, request.Status, Actor(), RequestAborted)
        }));

    /// <summary>
    /// FR-DLM-03. Files a signature, stamp or condition photograph against a shipment, through the
    /// shared inspection and content-addressed evidence pipeline. Returns the attachment id the
    /// confirmation then references.
    /// </summary>
    [HttpPost("shipments/{shipmentId:long}/proof-evidence")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
    [RequireModulePermission("Shipments", PermissionAction.Edit)]
    [RequestSizeLimit(DeliveryProofEvidenceService.MaximumEvidenceBytes)]
    public async Task<IActionResult> CaptureEvidence(
        long shipmentId, IFormFile? file, [FromForm] string kind)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "No evidence uploaded",
                "Attach the signature, stamp or photograph."));
        if (file.Length > DeliveryProofEvidenceService.MaximumEvidenceBytes)
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Evidence too large",
                "The evidence exceeds the 25 MB limit."));

        byte[] bytes;
        await using (var content = new MemoryStream())
        {
            await file.CopyToAsync(content, RequestAborted);
            bytes = content.ToArray();
        }

        return await ExecuteAsync(async () => Ok(await evidence.CaptureAsync(
            TenantId(), shipmentId, kind, file.FileName, bytes, file.ContentType, Actor(),
            RequestAborted)));
    }

    /// <summary>
    /// FR-DLM-03 and FR-DLM-02. Records the POD and what the customer accepted, in one write. The
    /// resulting status is derived from the quantities and is not a field on this request.
    /// </summary>
    [HttpPost("shipments/{shipmentId:long}/confirmation")]
    [RequireModulePermission("Shipments", PermissionAction.Edit)]
    public Task<IActionResult> Confirm(long shipmentId, [FromBody] ConfirmDeliveryCommand command)
        => ExecuteAsync(async () =>
        {
            var view = await confirmations.ConfirmAsync(
                TenantId(), shipmentId, IdempotencyKey(), command, Actor(), RequestAborted);
            return Created($"/api/delivery/shipments/{shipmentId}/confirmation", view);
        });

    [HttpGet("shipments/{shipmentId:long}/confirmation")]
    [RequireModulePermission("Shipments", PermissionAction.View)]
    public Task<IActionResult> GetConfirmation(long shipmentId)
        => ExecuteAsync(async () =>
        {
            var view = await confirmations.GetAsync(TenantId(), shipmentId, RequestAborted);
            return view is null ? NotFound(Problem(StatusCodes.Status404NotFound,
                "No proof of delivery", "This shipment has not been confirmed received."))
                : Ok(view);
        });

    /// <summary>
    /// FR-DLM-07, commercial half. Re-supply or credit. Append-only: a shortfall is decided once.
    /// No liability, no claim, no investigation — those belong to the carrier's process, not here.
    /// </summary>
    [HttpPost("shortfalls/{proofLineId:long}/decision")]
    [RequireModulePermission("Shipments", PermissionAction.Edit)]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> RecordDecision(
        long proofLineId, [FromBody] RecordShortfallDecisionCommand command)
        => ExecuteAsync(async () => Ok(await confirmations.RecordShortfallDecisionAsync(
            TenantId(), proofLineId, command, Actor(), RequestAborted)));

    private CancellationToken RequestAborted => HttpContext.RequestAborted;

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DeliveryConflictException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Delivery conflict", exception.Message));
        }
        catch (DocumentInspectionException exception)
        {
            var problem = Problem(StatusCodes.Status400BadRequest, "Evidence rejected",
                exception.Inspection.Reason);
            problem.Extensions["errorCode"] = exception.Inspection.ErrorCode;
            return BadRequest(problem);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unauthorized(Problem(StatusCodes.Status401Unauthorized,
                "Invalid authentication context", exception.Message));
        }
        catch (DeliveryValidationException exception) when (
            exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Delivery record not found",
                exception.Message));
        }
        catch (DeliveryValidationException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid delivery request",
                exception.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Delivery conflict",
                "The shipment changed during this request. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && IsDatabaseConflict(postgres))
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Delivery conflict",
                "The request conflicts with an existing or concurrent delivery record."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid delivery request",
                "The request violates a delivery data constraint."));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid delivery request",
                exception.Message));
        }
        catch (ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageUnavailableException exception)
        {
            // Proof-of-delivery evidence lands in the same immutable store as every other upload.
            // Caught HERE rather than left to EvidenceStorageProblemFilter, because the catch-all
            // below would handle it first and an MVC exception filter only sees UNHANDLED ones —
            // a warehouse operator capturing a signed POD would get "the delivery request could
            // not be completed", retry, and file a ticket for a cause the product already knows.
            logger.LogError(exception,
                "Delivery request refused: durable evidence storage is unavailable "
                + "(configuration fault: {IsConfigurationFault}). CorrelationId={CorrelationId}",
                exception.IsConfigurationFault, HttpContext.TraceIdentifier);
            return ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageProblemFilter.ToResult(exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Delivery request failed. CorrelationId={CorrelationId}",
                HttpContext.TraceIdentifier);
            return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                StatusCodes.Status500InternalServerError, "Delivery request failed",
                "The delivery request could not be completed."));
        }
    }

    private static bool IsDatabaseConflict(PostgresException exception)
        => exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation
            or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected;

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

    /// <summary>
    /// Required, not optional. A driver confirming a round on a flaky connection retries, and a
    /// second POD against one consignment would double every accepted unit on it.
    /// </summary>
    private string IdempotencyKey()
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var value)
            || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Idempotency-Key header is required.");
        var normalized = value.ToString().Trim();
        if (normalized.Length > 130 || normalized.Any(char.IsControl))
            throw new ArgumentException(
                "Idempotency-Key header must contain at most 130 printable characters.");
        return normalized;
    }
}

public sealed record TransitionDeliveryRequest(string Status);

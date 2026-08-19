using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Procurement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/procurement")]
[Authorize]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Procurement)]
public sealed class ProcurementController(
    IProcurementApplicationService service,
    ILogger<ProcurementController> logger) : ControllerBase
{
    [HttpGet("rfqs/{rfqId:long}/workbench")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public Task<IActionResult> GetWorkbench(long rfqId)
        => ExecuteAsync(async () => Ok(await service.GetWorkbenchAsync(TenantId(), rfqId, RequestAborted)));

    [HttpPost("sourcing-cases")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> CreateOrOpenSourcingCase([FromBody] CreateSourcingCaseRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateOrOpenSourcingCaseAsync(new CreateSourcingCaseCommand(
                TenantId(), request.RfqId, request.RfqItemId, request.SearchLimit,
                request.SourceEntireQuantity, IdempotencyKey(), Actor(), CorrelationId()), RequestAborted);
            return Created($"/api/procurement/sourcing-cases/{result.Id}", result);
        });

    [HttpGet("sourcing-cases/{sourcingCaseId:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> GetSourcingCase(long sourcingCaseId)
        => ExecuteAsync(async () => Ok(await service.GetSourcingCaseAsync(
            TenantId(), sourcingCaseId, RequestAborted)));

    [HttpPost("sourcing-cases/{sourcingCaseId:long}/supplier-candidates/search")]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.SupplierSearch)]
    public Task<IActionResult> SearchSourcingCandidates(long sourcingCaseId,
        [FromBody] SearchSourcingCandidatesRequest request)
        => ExecuteAsync(async () => Ok(await service.SearchSourcingCandidatesAsync(
            new SearchSourcingCandidatesCommand(TenantId(), sourcingCaseId, request.Limit,
                request.ExpectedVersion, IdempotencyKey(), Actor(), CorrelationId()), RequestAborted)));

    [HttpPost("sourcing-cases/{sourcingCaseId:long}/supplier-rfqs")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> PrepareSupplierRfq(long sourcingCaseId, [FromBody] PrepareSupplierRfqRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                TenantId(), sourcingCaseId, request.SupplierId, request.DueOn, request.ExpectedVersion,
                IdempotencyKey(), Actor(), CorrelationId()), RequestAborted);
            return Created($"/api/procurement/solicitations/{result.SupplierSolicitationId}", result);
        });

    [HttpPost("sourcing-cases/{sourcingCaseId:long}/supplier-rfqs/{supplierSolicitationId:long}/queue")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> QueuePreparedSupplierRfq(
        long sourcingCaseId,
        long supplierSolicitationId,
        [FromBody] QueuePreparedSupplierRfqRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.QueuePreparedSupplierRfqAsync(new QueuePreparedSupplierRfqCommand(
                TenantId(), sourcingCaseId, supplierSolicitationId, request.ExpectedSourcingCaseVersion,
                request.ExpectedSolicitationVersion, IdempotencyKey(), Actor(), CorrelationId()), RequestAborted);
            return Ok(result);
        });

    [HttpGet("purchase-orders")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public Task<IActionResult> SearchPurchaseOrders([FromQuery] string? search = null, [FromQuery] int limit = 50)
        => ExecuteAsync(async () => Ok(await service.SearchPurchaseOrdersAsync(TenantId(), search, limit, RequestAborted)));

    [HttpPost("solicitations")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public IActionResult CreateSolicitation([FromBody] CreateSolicitationRequest request)
    {
        _ = request;
        return Problem(
            statusCode: StatusCodes.Status410Gone,
            title: "Direct Supplier RFQ creation has been retired.",
            detail: "Open a persisted Sourcing Case, select a governed candidate, then approve the numbered Supplier RFQ for delivery.");
    }

    [HttpPost("solicitations/{solicitationId:long}/retry")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> RetrySolicitation(long solicitationId)
        => ExecuteAsync(async () => Ok(await service.RetrySolicitationAsync(new RetrySolicitationCommand(
            TenantId(), solicitationId, IdempotencyKey(), Actor(), CorrelationId()), RequestAborted)));

    /// <summary>
    /// Records that a Supplier RFQ reached the supplier by a route Nexora did not drive — a phone
    /// call, a hand-over, the supplier's own portal, or the buyer's own mailbox.
    ///
    /// <para>The recording user comes from the authenticated principal, never from the body: this
    /// record is the sole evidence that the supplier was contacted, and a caller that could name
    /// its own recorder could attribute a delivery that never happened to somebody else.</para>
    /// </summary>
    [HttpPost("solicitations/{solicitationId:long}/delivery-record")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> RecordSolicitationDelivery(long solicitationId,
        [FromBody] RecordSolicitationDeliveryRequest request)
        => ExecuteAsync(async () => Ok(await service.RecordSolicitationDeliveryAsync(
            new RecordSolicitationDeliveryCommand(TenantId(), solicitationId, request.DeliveryChannel,
                request.Note, request.ExpectedVersion, IdempotencyKey(), Actor(), CorrelationId()),
            RequestAborted)));

    [HttpPost("supplier-quotes")]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> CaptureSupplierQuote([FromBody] CaptureSupplierQuoteRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CaptureSupplierQuoteAsync(new CaptureSupplierQuoteCommand(
                TenantId(), request.SolicitationId, request.SupplierQuoteReference, request.Revision,
                request.ValidUntil, IdempotencyKey(), Actor(), CorrelationId(), request.Lines,
                request.Incoterms), RequestAborted);
            return Created("/api/procurement/supplier-quotes", result);
        });

    [HttpGet("rfq-items/{rfqItemId:long}/quote-comparison")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> CompareQuotes(long rfqItemId)
        => ExecuteAsync(async () => Ok(await service.CompareQuotesAsync(TenantId(), rfqItemId, RequestAborted)));

    [HttpPost("awards")]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public Task<IActionResult> ApproveAward([FromBody] ApproveAwardRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.ApproveAwardAsync(new ApproveAwardCommand(
                TenantId(), request.SupplierQuotedItemId, request.Quantity, request.ExpectedQuoteVersion,
                IdempotencyKey(), Actor(), CorrelationId(), UserId(), request.Rationale), RequestAborted);
            return Created($"/api/procurement/awards/{result.Id}", result);
        });

    [HttpPost("purchase-orders")]
    [RequireModulePermission("Orders", PermissionAction.Create)]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public Task<IActionResult> CreatePurchaseOrder([FromBody] CreateSupplierPurchaseOrderRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreatePurchaseOrderAsync(new CreatePurchaseOrderCommand(
                TenantId(), request.RfqId, request.SupplierId, request.CurrencyId, request.WarehouseId,
                request.ExpectedOn, request.AwardIds, IdempotencyKey(), Actor(),
                CorrelationId(), request.Incoterm, request.PortOfLoading,
                request.PortOfDischarge), RequestAborted);
            return Created($"/api/procurement/purchase-orders/{result.Id}", result);
        });

    /// <summary>
    /// FR-SPO-01. Approves a draft supplier purchase order for release.
    ///
    /// <para>The approver identity comes from the authenticated principal, never from the request
    /// body — a client that could name its own approver could name the award approver and defeat
    /// the segregation-of-duties check by assertion.</para>
    /// </summary>
    [HttpPost("purchase-orders/{purchaseOrderId:long}/approve")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> ApprovePurchaseOrder(long purchaseOrderId, [FromBody] ApproveSupplierPurchaseOrderRequest request)
        => ExecuteAsync(async () => Ok(await service.ApprovePurchaseOrderAsync(new ApprovePurchaseOrderCommand(
            TenantId(), purchaseOrderId, request.ExpectedVersion, UserId(),
            IdempotencyKey(), Actor(), CorrelationId()), RequestAborted)));

    [HttpPost("purchase-orders/{purchaseOrderId:long}/issue")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> IssuePurchaseOrder(long purchaseOrderId, [FromBody] IssueSupplierPurchaseOrderRequest request)
        => ExecuteAsync(async () => Ok(await service.IssuePurchaseOrderAsync(new IssuePurchaseOrderCommand(
            TenantId(), purchaseOrderId, request.ExpectedVersion, request.DeliveryEvidenceReference,
            IdempotencyKey(), Actor(), CorrelationId(), request.DeliveryEvidenceSha256,
            request.DeliveredOn), RequestAborted)));

    /// <summary>
    /// FR-SPO-06. Sets or corrects Incoterm, ports and per-line customs data before dispatch.
    /// Omitted fields are left unchanged rather than cleared, so a one-line correction cannot wipe
    /// the terms the rest of the order depends on.
    /// </summary>
    [HttpPost("purchase-orders/{purchaseOrderId:long}/trade-terms")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> AmendPurchaseOrderTradeTerms(long purchaseOrderId,
        [FromBody] AmendPurchaseOrderTradeTermsRequest request)
        => ExecuteAsync(async () => Ok(await service.AmendPurchaseOrderTradeTermsAsync(
            new AmendPurchaseOrderTradeTermsCommand(
                TenantId(), purchaseOrderId, request.ExpectedVersion, IdempotencyKey(), Actor(),
                CorrelationId(), request.Incoterm, request.PortOfLoading, request.PortOfDischarge,
                request.Lines), RequestAborted)));

    /// <summary>
    /// FR-SPO-03. Records the supplier's answer — accepted, rejected, or countered with a revised
    /// lead time or ship date.
    ///
    /// <para>Nexora has no supplier portal, so this is a buyer keying in what the supplier said.
    /// The supplier contact is therefore taken from the body while the recording user is taken
    /// from the authenticated principal; they are different people and the audit trail keeps them
    /// apart.</para>
    /// </summary>
    [HttpPost("purchase-orders/{purchaseOrderId:long}/acknowledge")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> AcknowledgePurchaseOrder(long purchaseOrderId,
        [FromBody] AcknowledgeSupplierPurchaseOrderRequest request)
        => ExecuteAsync(async () => Ok(await service.AcknowledgePurchaseOrderAsync(
            new AcknowledgeSupplierPurchaseOrderCommand(
                TenantId(), purchaseOrderId, request.ExpectedVersion, request.AcknowledgementStatus,
                request.AcknowledgedBy, IdempotencyKey(), Actor(), CorrelationId(),
                request.RevisedLeadTimeDays, request.CommittedShipDate, request.Note), RequestAborted)));

    /// <summary>
    /// Cancels a purchase order that will never be received. Until this route existed a stranded
    /// DRAFT purchase order permanently suppressed its RFQ line's net sourcing requirement, so the
    /// line could never be re-sourced and the customer demand was unfulfillable with no operator
    /// recourse.
    /// </summary>
    [HttpPost("purchase-orders/{purchaseOrderId:long}/cancel")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public Task<IActionResult> CancelPurchaseOrder(long purchaseOrderId, [FromBody] CancelSupplierPurchaseOrderRequest request)
        => ExecuteAsync(async () => Ok(await service.CancelPurchaseOrderAsync(new CancelPurchaseOrderCommand(
            TenantId(), purchaseOrderId, request.ExpectedVersion, request.Reason,
            IdempotencyKey(), Actor(), CorrelationId()), RequestAborted)));

    [HttpPost("goods-receipts")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<IActionResult> PostGoodsReceipt([FromBody] PostGoodsReceiptRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.PostGoodsReceiptAsync(new PostGoodsReceiptCommand(
                TenantId(), request.PurchaseOrderId, request.WarehouseId, request.ReceiptNumber,
                request.ReceivedOn, request.ExpectedPurchaseOrderVersion, request.Lines, IdempotencyKey(),
                Actor(), CorrelationId()), RequestAborted);
            return Created($"/api/procurement/goods-receipts/{result.Id}", result);
        });

    private CancellationToken RequestAborted => HttpContext.RequestAborted;

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ProcurementConflictException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Procurement conflict", exception.Message));
        }
        // The lot recorder's refusals are the receipt screen's validation messages — "this line is
        // serial-tracked; one serial number per received unit is required" is the operator's
        // instruction, not an internal fault. They used to fall through to the catch-all below and
        // surface as a 500 with generic copy, which is how a correct guard reads as a broken screen.
        catch (Traceability.MaterialTraceabilityConflictException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Procurement conflict", exception.Message));
        }
        catch (Traceability.MaterialTraceabilityValidationException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid procurement request", exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unauthorized(Problem(StatusCodes.Status401Unauthorized, "Invalid authentication context", exception.Message));
        }
        catch (ProcurementValidationException exception) when (IsNotFound(exception.Message))
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Procurement record not found", exception.Message));
        }
        catch (ProcurementValidationException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid procurement request", exception.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Procurement conflict",
                "The procurement record changed during this request. Reload and try again."));
        }
        catch (PostgresException exception) when (IsDatabaseConflict(exception))
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Procurement conflict",
                "The request conflicts with an existing or concurrent procurement record. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && IsDatabaseConflict(postgres))
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Procurement conflict",
                "The request conflicts with an existing or concurrent procurement record. Reload and try again."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid procurement request",
                "The request violates a procurement data constraint."));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid procurement request", exception.Message));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Procurement request failed. CorrelationId={CorrelationId}", SafeCorrelationId());
            return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                StatusCodes.Status500InternalServerError, "Procurement request failed",
                "The procurement request could not be completed."));
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

    private long? UserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value,
            out var value) && value > 0 ? value : null;

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

public sealed record CreateSolicitationRequest(
    long RfqId,
    long SupplierId,
    IReadOnlyCollection<long> RfqItemIds,
    DateTime? DueOn);

public sealed record CreateSourcingCaseRequest(
    long RfqId,
    long RfqItemId,
    int SearchLimit = 10,
    bool SourceEntireQuantity = false);

public sealed record SearchSourcingCandidatesRequest(int Limit, long ExpectedVersion);

public sealed record PrepareSupplierRfqRequest(long SupplierId, DateTime? DueOn, long ExpectedVersion);
public sealed record QueuePreparedSupplierRfqRequest(long ExpectedSourcingCaseVersion, long ExpectedSolicitationVersion);

/// <summary>
/// Carries no actor and no timestamp. Both are server-authoritative: the recorder is the
/// authenticated principal and the moment is the server's clock, so neither can be asserted by a
/// caller.
/// </summary>
public sealed record RecordSolicitationDeliveryRequest(
    string DeliveryChannel,
    string Note,
    long ExpectedVersion);

public sealed record CaptureSupplierQuoteRequest(
    long SolicitationId,
    string SupplierQuoteReference,
    int Revision,
    DateTime ValidUntil,
    IReadOnlyCollection<CaptureSupplierQuoteLine> Lines,
    string? Incoterms = null);

public sealed record ApproveAwardRequest(
    long SupplierQuotedItemId,
    decimal Quantity,
    long ExpectedQuoteVersion,
    string? Rationale);

/// <summary>
/// FR-SPO-06. The shipping and customs terms are OPTIONAL at creation and correctable until
/// dispatch, but they must be carried when they are sent.
///
/// <para>They were missing from this record while <see cref="CreatePurchaseOrderCommand"/>
/// declared them, the service normalised and persisted them
/// (<c>ProcurementApplicationService.CreatePurchaseOrderAsync</c>), and the workbench dialog
/// collected and posted all three
/// (<c>Frontend/src/pages/Procurement/Sourcing/SourcingWorkbenchPage.tsx</c>). Model binding
/// discarded them silently: the buyer keyed an Incoterm, saw the order created, and the order
/// carried none — which then suppressed the non-delivered-term duty warning that reads it, and
/// forced the terms to be re-keyed through the separate amendment endpoint.</para>
/// </summary>
public sealed record CreateSupplierPurchaseOrderRequest(
    long RfqId,
    long SupplierId,
    long CurrencyId,
    long WarehouseId,
    DateOnly ExpectedOn,
    IReadOnlyCollection<long> AwardIds,
    string? Incoterm = null,
    string? PortOfLoading = null,
    string? PortOfDischarge = null);

public sealed record ApproveSupplierPurchaseOrderRequest(long ExpectedVersion);

public sealed class IssueSupplierPurchaseOrderRequest
{
    public long ExpectedVersion { get; init; }
    public string DeliveryEvidenceReference { get; init; } = string.Empty;
    public string DeliveryEvidenceSha256 { get; init; } = string.Empty;
    public DateTime DeliveredOn { get; init; }
}

public sealed record CancelSupplierPurchaseOrderRequest(
    long ExpectedVersion,
    string Reason);

public sealed record AmendPurchaseOrderTradeTermsRequest(
    long ExpectedVersion,
    string? Incoterm = null,
    string? PortOfLoading = null,
    string? PortOfDischarge = null,
    IReadOnlyCollection<PurchaseOrderLineTradeTerms>? Lines = null);

public sealed record AcknowledgeSupplierPurchaseOrderRequest(
    long ExpectedVersion,
    string AcknowledgementStatus,
    string AcknowledgedBy,
    int? RevisedLeadTimeDays = null,
    DateOnly? CommittedShipDate = null,
    string? Note = null);

public sealed record PostGoodsReceiptRequest(
    long PurchaseOrderId,
    long WarehouseId,
    string ReceiptNumber,
    DateTime ReceivedOn,
    long ExpectedPurchaseOrderVersion,
    IReadOnlyCollection<PostGoodsReceiptLine> Lines);

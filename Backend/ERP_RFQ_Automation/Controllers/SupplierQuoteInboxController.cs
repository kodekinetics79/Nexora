using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/supplier-quote-inbox")]
public sealed class SupplierQuoteInboxController(
    SupplierQuoteInboxService service,
    SupplierNegotiationService negotiation,
    SupplierQuoteCommercialService commercial,
    SupplierQuoteDocumentIntakeService documentIntake,
    ILogger<SupplierQuoteInboxController> logger) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> Search([FromQuery] string? status = null, [FromQuery] int limit = 50) =>
        ExecuteAsync(async () => Ok(await service.SearchAsync(TenantId(), status, limit, RequestAborted)));

    [HttpGet("{supplierQuoteId:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> Get(long supplierQuoteId) => ExecuteAsync(async () =>
        Ok(await service.GetAsync(TenantId(), supplierQuoteId, RequestAborted)));

    [HttpGet("{supplierQuoteId:long}/negotiation")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> GetNegotiation(long supplierQuoteId) => ExecuteAsync(async () =>
        Ok(await negotiation.GetAsync(TenantId(), supplierQuoteId, RequestAborted)));

    [HttpPost("{supplierQuoteId:long}/negotiation-decisions")]
    [RequireModulePermission("Supplier Negotiation", PermissionAction.Edit)]
    public Task<IActionResult> DecideNegotiation(long supplierQuoteId,
        [FromBody] SupplierNegotiationDecisionRequest request) => ExecuteAsync(async () =>
        Ok(await negotiation.DecideAsync(new SupplierNegotiationCommand(TenantId(), supplierQuoteId,
            request.ExpectedQuoteVersion, request.RecommendationCode, request.Disposition, request.Reason,
            RequiredHeader("Idempotency-Key"), Actor(), RequiredHeader("X-Correlation-ID")),
            RequestAborted)));

    [HttpPost]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> Capture([FromBody] CaptureSupplierQuoteInboxRequest request) =>
        ExecuteAsync(async () =>
        {
            var result = await service.CaptureAsync(new CaptureSupplierQuoteCommand(
                TenantId(), request.SupplierId, request.SupplierSolicitationId, request.SourcingCaseId,
                request.NexoraSerial, request.SupplierQuoteReference, request.RevisionNumber,
                request.CaptureChannel, request.SourceDocumentId, request.SourceIdentity, request.SourceSha256,
                request.CurrencyId, request.ValidUntil, request.Incoterms, request.FreightAmount,
                request.TaxAmount, request.DutyAmount, request.OtherAmount, request.DiscountAmount,
                request.PaymentTerms, request.Notes, request.Lines, request.Evidence,
                RequiredHeader("Idempotency-Key"), Actor(), RequiredHeader("X-Correlation-ID")),
                RequestAborted);
            return Created($"/api/supplier-quote-inbox/{result.SupplierQuoteId}", result);
        });

    [HttpPost("documents")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
    [RequestSizeLimit(25 * 1024 * 1024)]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public Task<IActionResult> Upload([FromForm] SupplierQuoteDocumentUploadRequest request) =>
        ExecuteAsync(async () =>
        {
            if (request.File is null)
                throw new SupplierQuoteValidationException("A Supplier Quote file is required.");
            await using var content = request.File.OpenReadStream();
            var result = await documentIntake.IngestAsync(new SupplierQuoteDocumentIntakeCommand(
                    TenantId(), request.SupplierId, request.SupplierSolicitationId, request.SourcingCaseId,
                    request.NexoraSerial, request.SupplierQuoteReference, request.RevisionNumber,
                    request.CurrencyId, request.ValidUntil, request.Incoterms, request.PaymentTerms,
                    request.Notes, RequiredHeader("Idempotency-Key"), Actor(),
                    RequiredHeader("X-Correlation-ID"), request.FreightAmount, request.TaxAmount,
                    request.DutyAmount, request.OtherAmount, request.DiscountAmount),
                content, request.File.FileName, request.File.Length, RequestAborted);
            return Accepted(result);
        });

    [HttpPost("{supplierQuoteId:long}/revisions/{revisionId:long}/evidence/{evidenceId:long}/reviews")]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public Task<IActionResult> Review(long supplierQuoteId, long revisionId, long evidenceId,
        [FromBody] ReviewSupplierQuoteFieldRequest request) => ExecuteAsync(async () =>
    {
        await service.ReviewAsync(new ReviewSupplierQuoteFieldCommand(TenantId(), supplierQuoteId,
            revisionId, evidenceId, request.Status, request.CorrectedValue, request.Reason, Actor(),
            RequiredHeader("X-Correlation-ID")), RequestAborted);
        return NoContent();
    });

    [HttpPost("{supplierQuoteId:long}/comparison-projections")]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public Task<IActionResult> Project(long supplierQuoteId, [FromBody] ProjectSupplierQuoteRequest request) =>
        ExecuteAsync(async () => Ok(await commercial.ProjectAsync(new ProjectSupplierQuoteCommand(
            TenantId(), supplierQuoteId, request.ExpectedVersion, RequiredHeader("Idempotency-Key"),
            Actor(), RequiredHeader("X-Correlation-ID")), RequestAborted)));

    [HttpPost("customer-quote-pricing")]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    [RequireModulePermission("Quotations", PermissionAction.Edit)]
    public Task<IActionResult> ApplyCustomerPricing([FromBody] ApplyCustomerQuotePricingRequest request) =>
        ExecuteAsync(async () => Ok(await commercial.ApplyPricingAsync(new ApplyCustomerQuotePricingCommand(
            TenantId(), request.QuoteItemId, request.SourcingAwardId, request.TargetMarginPercent,
            request.Rationale, RequiredHeader("Idempotency-Key"), Actor(),
            RequiredHeader("X-Correlation-ID")), RequestAborted)));

    private CancellationToken RequestAborted => HttpContext.RequestAborted;

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(Problem(StatusCodes.Status401Unauthorized, "Invalid authentication context",
                "A valid authenticated tenant and actor are required."));
        }
        catch (SupplierQuoteNotFoundException exception)
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Supplier Quote not found", exception.Message));
        }
        catch (SupplierQuoteConflictException exception)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Supplier Quote conflict", exception.Message));
        }
        catch (SupplierQuoteValidationException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid Supplier Quote", exception.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem(StatusCodes.Status409Conflict, "Supplier Quote conflict",
                "The Supplier Quote changed during this request. Reload and try again."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid Supplier Quote",
                "The request violates a Supplier Quote data constraint."));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid Supplier Quote", exception.Message));
        }
        catch (ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageUnavailableException exception)
        {
            // Must precede the catch-all: a document store that cannot be written is not a
            // "Supplier Quote request failed", and answering 500 sends the supplier back to
            // re-attach a quotation that will be refused identically every time.
            logger.LogError(exception,
                "Supplier Quote document intake refused: durable evidence storage is unavailable "
                + "(configuration fault: {IsConfigurationFault}). CorrelationId={CorrelationId}",
                exception.IsConfigurationFault, SafeCorrelationId());
            return ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageProblemFilter.ToResult(exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Supplier Quote Inbox request failed. CorrelationId={CorrelationId}",
                SafeCorrelationId());
            return StatusCode(StatusCodes.Status500InternalServerError,
                Problem(StatusCodes.Status500InternalServerError, "Supplier Quote Inbox request failed",
                    "The Supplier Quote request could not be completed."));
        }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
        ? value : throw new UnauthorizedAccessException("A valid tenant claim is required.");
    private string Actor() => User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("An authenticated actor claim is required.");
    private string RequiredHeader(string name)
    {
        var value = Request.Headers[name].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))
            throw new ArgumentException($"{name} is required and must not exceed 160 printable characters.");
        return value;
    }
    private string SafeCorrelationId() => Request.Headers["X-Correlation-ID"].FirstOrDefault()?.Trim()
        ?? HttpContext.TraceIdentifier;
    private static ProblemDetails Problem(int status, string title, string detail) =>
        new() { Status = status, Title = title, Detail = detail };
}

public sealed record CaptureSupplierQuoteInboxRequest(
    long SupplierId,
    long SupplierSolicitationId,
    long SourcingCaseId,
    string NexoraSerial,
    string SupplierQuoteReference,
    int RevisionNumber,
    string CaptureChannel,
    long? SourceDocumentId,
    string SourceIdentity,
    string SourceSha256,
    long CurrencyId,
    DateTime? ValidUntil,
    string? Incoterms,
    decimal FreightAmount,
    decimal TaxAmount,
    string? PaymentTerms,
    string? Notes,
    IReadOnlyCollection<CaptureSupplierQuoteLine> Lines,
    IReadOnlyCollection<CaptureSupplierQuoteEvidence> Evidence,
    // Trailing and defaulted so an existing client that posts no duty keeps working and records a
    // truthful zero, rather than being rejected or — worse — having a duty invented for it.
    decimal DutyAmount = 0m,
    decimal OtherAmount = 0m,
    decimal DiscountAmount = 0m);

public sealed record ProjectSupplierQuoteRequest(long ExpectedVersion);
public sealed record SupplierNegotiationDecisionRequest(long ExpectedQuoteVersion,
    string RecommendationCode, string Disposition, string Reason);
public sealed record ApplyCustomerQuotePricingRequest(long QuoteItemId, long SourcingAwardId,
    decimal TargetMarginPercent, string Rationale);

public sealed record ReviewSupplierQuoteFieldRequest(string Status, string? CorrectedValue, string Reason);

public sealed class SupplierQuoteDocumentUploadRequest
{
    public IFormFile? File { get; init; }
    public long SupplierId { get; init; }
    public long SupplierSolicitationId { get; init; }
    public long SourcingCaseId { get; init; }
    public string NexoraSerial { get; init; } = string.Empty;
    public string SupplierQuoteReference { get; init; } = string.Empty;
    public int RevisionNumber { get; init; } = 1;
    public long CurrencyId { get; init; }
    public DateTime? ValidUntil { get; init; }
    public string? Incoterms { get; init; }
    public string? PaymentTerms { get; init; }
    public string? Notes { get; init; }

    /// <summary>
    /// The round charges, entered alongside the file. A spreadsheet of part numbers and unit prices
    /// does not state the freight, and the intake path used to hardcode zero for it — so the
    /// headline ingestion path produced quotes whose landed cost was the bare unit price.
    /// </summary>
    public decimal FreightAmount { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal DutyAmount { get; init; }
    public decimal OtherAmount { get; init; }
    public decimal DiscountAmount { get; init; }
}

using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake;
using ERP_RFQ_Automation.Platform.Entitlements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// FR-COM-01. Upload a customer purchase order and read it, instead of re-keying it.
///
/// <para>Deliberately a separate controller from <see cref="CustomerAwardsController"/>: the award
/// controller owns the matching aggregate's command surface, and this is an intake concern that
/// happens to produce one of its commands. The routes sit under the same path so the two read as
/// one API.</para>
/// </summary>
[ApiController]
[Route("api/customer-awards/purchase-orders")]
[Authorize]
public sealed class CustomerPurchaseOrderDocumentsController(
    ICustomerPurchaseOrderDocumentService service,
    ILogger<CustomerPurchaseOrderDocumentsController> logger) : ControllerBase
{
    /// <summary>
    /// Reads an uploaded PO (native or scanned PDF, Word, Excel or CSV) and returns what the
    /// document says, for a human to confirm. Nothing is persisted as a commercial record here.
    /// </summary>
    [HttpPost("document-extractions")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
    [RequireModulePermission("Customer Awards", PermissionAction.Create)]
    [RequestSizeLimit(CustomerPurchaseOrderDocumentService.MaximumDocumentBytes)]
    public async Task<IActionResult> Extract(IFormFile? file)
    {
        if (!TryGetTenant(out var businessUnitId))
            return BadRequest(Problem(400, "Invalid tenant", "A valid businessUnitId claim is required."));
        if (file is null || file.Length == 0)
            return BadRequest(Problem(400, "No document uploaded", "Attach the customer's purchase-order file."));
        if (file.Length > CustomerPurchaseOrderDocumentService.MaximumDocumentBytes)
            return BadRequest(Problem(400, "Document too large", "The purchase-order document exceeds the 25 MB limit."));

        byte[] bytes;
        await using (var content = new MemoryStream())
        {
            await file.CopyToAsync(content, HttpContext.RequestAborted);
            bytes = content.ToArray();
        }

        try
        {
            var result = await service.ExtractAsync(businessUnitId, file.FileName, bytes, file.ContentType,
                Actor(), HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (DocumentInspectionException exception)
        {
            // The intake contract every other upload door uses: a machine code the UI branches on
            // plus the user-safe reason inspection recorded.
            return UnprocessableEntity(new
            {
                status = 422,
                title = "The purchase-order document was not accepted",
                detail = exception.Inspection.Reason,
                code = exception.Inspection.ErrorCode,
                outcome = exception.Inspection.Status.ToString(),
                retryable = exception.Inspection.IsRetryable,
            });
        }
        catch (CustomerPurchaseOrderDocumentException exception)
        {
            // An honest, terminal "we could not read this". NEVER a successful extraction with
            // zero lines: an empty purchase order looks reviewed and agrees with every quotation.
            return UnprocessableEntity(new
            {
                status = 422,
                title = "The purchase-order document could not be read",
                detail = exception.Message,
                code = exception.Code,
            });
        }
        catch (EntitlementDeniedException exception)
        {
            logger.LogWarning("Customer PO document upload denied for tenant {BusinessUnitId}: {Reason}",
                businessUnitId, exception.Message);
            return EntitlementProblemFilter.ToResult(exception);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(400, "Invalid purchase-order document", exception.Message));
        }
    }

    /// <summary>
    /// Creates the customer PO from a reviewed extraction and links it to the document it was read
    /// from. Every value in the command is the buyer's, taken from the document or corrected by the
    /// reviewer — never defaulted from our own quotation.
    /// </summary>
    [HttpPost("from-document")]
    [RequireModulePermission("Customer Awards", PermissionAction.Create)]
    public async Task<IActionResult> CreateFromDocument(
        [FromBody] CreateCustomerPurchaseOrderFromDocumentCommand command)
    {
        if (!TryGetTenant(out var businessUnitId))
            return BadRequest(Problem(400, "Invalid tenant", "A valid businessUnitId claim is required."));

        try
        {
            var result = await service.CreateFromDocumentAsync(businessUnitId, IdempotencyKey(), CorrelationId(),
                command, Actor(), HttpContext.RequestAborted);
            return Created($"/api/customer-awards/purchase-orders/{result.Id}", result);
        }
        catch (CustomerAwardConflictException exception)
        {
            return Conflict(Problem(409, "Customer award conflict", exception.Message));
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Conflict(Problem(409, "Customer award conflict", exception.Message));
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.SerializationFailure)
        {
            return Conflict(Problem(409, "Customer award conflict",
                "The request conflicts with a concurrent or existing customer award record. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation
                or PostgresErrorCodes.SerializationFailure)
        {
            return Conflict(Problem(409, "Customer award conflict",
                "The request conflicts with a concurrent or existing customer award record. Reload and try again."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(400, "Invalid customer award request",
                "The customer award request violates a data constraint."));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(404, "Customer award record not found", exception.Message));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(400, "Invalid customer award request", exception.Message));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };

    private bool TryGetTenant(out long businessUnitId)
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId) && businessUnitId > 0;

    private string Actor() => User.FindFirst("email")?.Value ?? User.Identity?.Name ?? "system";

    private string IdempotencyKey() => Header("Idempotency-Key", required: true)!;

    private string CorrelationId() => Header("X-Correlation-ID", required: false) ?? HttpContext.TraceIdentifier;

    private string? Header(string name, bool required)
    {
        if (Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.ToString().Trim();
        if (required) throw new ArgumentException($"{name} header is required.");
        return null;
    }
}

using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialDocuments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-inbox/classifications")]
public sealed class CommercialDocumentClassificationController(
    CommercialDocumentClassificationService service) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> Search([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] CommercialDocumentType? documentType = null,
        [FromQuery] CommercialDocumentReviewStatus? reviewStatus = null,
        [FromQuery] SupplierQuoteProjectionState? projectionState = null,
        CancellationToken cancellationToken = default) => ExecuteReadAsync(async () => Ok(
            await service.SearchInboxAsync(TenantId(), new CommercialDocumentInboxQuery(page, pageSize,
                documentType, reviewStatus, projectionState), cancellationToken)));

    [HttpGet("{id:guid}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> Detail(Guid id, CancellationToken cancellationToken) =>
        ExecuteReadAsync(async () => Ok(await service.GetInboxDetailAsync(TenantId(), id, cancellationToken)));

    [HttpPost]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public async Task<IActionResult> Classify(
        [FromBody] ClassifyCommercialDocumentApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.ClassifyAsync(TenantId(), new ClassifyCommercialDocumentRequest(
                request.SourceDocumentId, RequiredHeader("Idempotency-Key"), request.Signals,
                request.Matches), cancellationToken);
            return Created($"/api/commercial-inbox/classifications/{result.Id}", result);
        }
        catch (CommercialDocumentConflictException exception)
        {
            return Conflict(Problem(exception.Message, StatusCodes.Status409Conflict));
        }
        catch (CommercialDocumentNotFoundException)
        {
            return NotFound(Problem("The source document was not found.", StatusCodes.Status404NotFound));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, StatusCodes.Status400BadRequest));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(Problem("A valid authenticated tenant is required.", StatusCodes.Status401Unauthorized));
        }
    }

    [HttpPost("{id:guid}/confirm")]
    [RequireManagerRole]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public async Task<IActionResult> Confirm(Guid id,
        [FromBody] ConfirmCommercialDocumentApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await service.ConfirmAsync(TenantId(), id, request.ExpectedVersion,
                request.DocumentType, request.EvidenceJson, Actor(), request.Reason, request.Matches,
                cancellationToken));
        }
        catch (CommercialDocumentConflictException exception)
        {
            return Conflict(Problem(exception.Message, StatusCodes.Status409Conflict));
        }
        catch (CommercialDocumentNotFoundException)
        {
            return NotFound(Problem("The classification was not found.", StatusCodes.Status404NotFound));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, StatusCodes.Status400BadRequest));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(Problem("A valid authenticated tenant and actor are required.", StatusCodes.Status401Unauthorized));
        }
    }

    [HttpPost("{id:guid}/reject")]
    [RequireManagerRole]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public async Task<IActionResult> Reject(Guid id,
        [FromBody] RejectCommercialDocumentApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await service.RejectAsync(TenantId(), id, request.ExpectedVersion, Actor(),
                request.Reason, cancellationToken));
        }
        catch (CommercialDocumentConflictException exception)
        {
            return Conflict(Problem(exception.Message, StatusCodes.Status409Conflict));
        }
        catch (CommercialDocumentNotFoundException)
        {
            return NotFound(Problem("The classification was not found.", StatusCodes.Status404NotFound));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, StatusCodes.Status400BadRequest));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(Problem("A valid authenticated tenant and actor are required.", StatusCodes.Status401Unauthorized));
        }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenantId)
        && tenantId > 0
        ? tenantId
        : throw new UnauthorizedAccessException("A valid tenant claim is required.");

    private string Actor() => User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("An authenticated actor claim is required.");

    private string RequiredHeader(string name)
    {
        var value = Request.Headers[name].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException($"{name} is required and must not exceed 256 characters.");
        return value;
    }

    private async Task<IActionResult> ExecuteReadAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (CommercialDocumentNotFoundException)
        {
            return NotFound(Problem("The classification was not found.", StatusCodes.Status404NotFound));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, StatusCodes.Status400BadRequest));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(Problem("A valid authenticated tenant is required.", StatusCodes.Status401Unauthorized));
        }
    }

    private static ProblemDetails Problem(string detail, int status) => new()
        { Status = status, Title = "Commercial document classification", Detail = detail };
}

public sealed record ClassifyCommercialDocumentApiRequest(
    long SourceDocumentId,
    CommercialDocumentClassificationSignals Signals,
    CommercialDocumentMatchReferences? Matches = null);

public sealed record ConfirmCommercialDocumentApiRequest(
    int ExpectedVersion,
    CommercialDocumentType DocumentType,
    string EvidenceJson,
    string Reason,
    CommercialDocumentMatchReferences? Matches = null);

public sealed record RejectCommercialDocumentApiRequest(int ExpectedVersion, string Reason);

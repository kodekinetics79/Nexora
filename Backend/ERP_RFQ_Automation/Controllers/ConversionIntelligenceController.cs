using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Intelligence.Conversion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Intelligence.Conversion;

/// <summary>
/// HTTP surface for the Lead -&gt; RFQ conversion intelligence. The business unit
/// always comes from the caller's JWT ("businessUnitId" claim), exactly like
/// LeadController — never from the route or body.
/// </summary>
[Route("api/intelligence/leads")]
[ApiController]
[Authorize]
public class ConversionIntelligenceController : ControllerBase
{
    private readonly ILeadConversionIntelligence _intelligence;
    private readonly ILogger<ConversionIntelligenceController>? _logger;

    public ConversionIntelligenceController(
        ILeadConversionIntelligence intelligence,
        ILogger<ConversionIntelligenceController>? logger = null)
    {
        _intelligence = intelligence;
        _logger = logger;
    }

    /// <summary>Dry-run conversion: catalog matches, normalization and confidence per line.</summary>
    [HttpGet("{id}/conversion-preview")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<ConversionPreview>> GetConversionPreview(long id, CancellationToken ct)
    {
        try
        {
            if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request",
                    "Business Unit ID is required."));

            var preview = await _intelligence.PreviewAsync(id, businessUnitId, ct);
            return Ok(preview);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Lead not found", ex.Message));
        }
        catch (Exception ex)
        {
            return InternalError(ex, "Conversion preview failed.",
                "The conversion preview could not be built.");
        }
    }

    /// <summary>Convert the lead into an RFQ applying the request's per-line choices.</summary>
    [HttpPost("{id}/convert")]
    [RequireModulePermission("Leads", PermissionAction.Create)]
    [RequireModulePermission("RFQ Management", PermissionAction.Create)]
    public async Task<ActionResult> Convert(long id, [FromBody] ConvertRequest request, CancellationToken ct)
    {
        try
        {
            if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request",
                    "Business Unit ID is required."));

            request ??= new ConvertRequest();
            request.ActingUser = User.Identity?.Name ?? "System";

            var rfqId = await _intelligence.ConvertAsync(id, businessUnitId, request, ct);
            return Ok(new { rfqId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Lead not found", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Conversion gates (lifecycle, duplicate flag, unreviewed AI facts, missing
            // inquiry fields). The messages are written for the user and safe to render.
            return Conflict(Problem(StatusCodes.Status409Conflict, "Lead not converted", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Lead not converted", ex.Message));
        }
        catch (Exception ex)
        {
            return InternalError(ex, "Lead conversion failed.",
                "The lead could not be converted to an RFQ.");
        }
    }

    private bool TryGetAuthenticatedBusinessUnitId(out long businessUnitId) =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId)
        && businessUnitId > 0;

    /// <summary>
    /// The exception detail goes to the log with the trace id; the caller gets a
    /// renderable, non-leaking RFC 7807 body carrying the same trace id. The previous
    /// bodies interpolated ex.Message into a 500 string, which both leaked internals
    /// and could not be rendered by the frontend's error surface.
    /// </summary>
    private ObjectResult InternalError(Exception exception, string operation, string detail)
    {
        _logger?.LogError(exception, "{Operation} Trace {TraceId}.", operation, HttpContext.TraceIdentifier);
        return StatusCode(StatusCodes.Status500InternalServerError,
            Problem(StatusCodes.Status500InternalServerError, "The request could not be completed.", detail));
    }

    /// <summary>
    /// RFC 7807 body carrying the request's trace identifier, so a caller reporting a
    /// failure gives support an id that ties straight back to the server log entry.
    /// Same pattern as SupplierController and RfqController.
    /// </summary>
    private ProblemDetails Problem(int status, string title, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return problem;
    }
}

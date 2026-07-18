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

    public ConversionIntelligenceController(ILeadConversionIntelligence intelligence)
    {
        _intelligence = intelligence;
    }

    /// <summary>Dry-run conversion: catalog matches, normalization and confidence per line.</summary>
    [HttpGet("{id}/conversion-preview")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<ConversionPreview>> GetConversionPreview(long id, CancellationToken ct)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");

            var preview = await _intelligence.PreviewAsync(id, businessUnitId, ct);
            return Ok(preview);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error building conversion preview: {ex.Message}");
        }
    }

    /// <summary>Convert the lead into an RFQ applying the request's per-line choices.</summary>
    [HttpPost("{id}/convert")]
    [RequireModulePermission("Leads", PermissionAction.Create)]
    public async Task<ActionResult> Convert(long id, [FromBody] ConvertRequest request, CancellationToken ct)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");

            request ??= new ConvertRequest();
            request.ActingUser = User.Identity?.Name ?? "System";

            var rfqId = await _intelligence.ConvertAsync(id, businessUnitId, request, ct);
            return Ok(new { rfqId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error converting lead: {ex.Message}");
        }
    }
}

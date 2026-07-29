using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/LeadIngestion")]
[Authorize]
public sealed class LeadIngestionController : ControllerBase
{
    private readonly ILeadIdentityApplicationService _service;
    private readonly ISecurityScanRecoveryService _securityScanRecovery;
    private readonly ILogger<LeadIngestionController> _log;
    public LeadIngestionController(
        ILeadIdentityApplicationService service,
        ISecurityScanRecoveryService securityScanRecovery,
        ILogger<LeadIngestionController> log)
    { _service = service; _securityScanRecovery = securityScanRecovery; _log = log; }

    [HttpGet("batches/{batchId:guid}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> Batch(Guid batchId, CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        var result = await _service.GetBatchAsync(bu, batchId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("batches/{batchId:guid}/retry-blocked-files")]
    [RequireModulePermission("Leads", PermissionAction.Create)]
    public async Task<IActionResult> RetryBlockedFiles(Guid batchId, CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        if (await _service.GetBatchAsync(bu, batchId, ct) is null)
            return NotFound();
        return Ok(await _securityScanRecovery.RetryBatchAsync(bu, batchId, ct));
    }

    [HttpGet("match-reviews")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> PossibleMatches(CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        return Ok(await _service.GetPossibleMatchesAsync(bu, ct));
    }

    [HttpGet("leads/{leadId:long}/revisions")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> Revisions(long leadId, CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        return Ok(await _service.GetRevisionsAsync(bu, leadId, ct));
    }

    [HttpPost("match-reviews/{occurrenceId:long}/decision")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<IActionResult> Decide(long occurrenceId, [FromBody] MatchDecisionRequest request, CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return BadRequest(new { message = "Reason and idempotency key are required." });
        try
        {
            var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "authenticated-user";
            return Ok(await _service.DecideMatchAsync(bu, occurrenceId, request, actor, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "This review changed. Refresh and retry." }); }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }
        catch (Exception ex)
        {
            var correlationId = HttpContext.TraceIdentifier;
            _log.LogError(ex, "Lead match review failed. CorrelationId={CorrelationId}", correlationId);
            return StatusCode(500, new { message = "The match decision could not be completed.", correlationId });
        }
    }

    [HttpGet("analytics")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public async Task<IActionResult> Analytics([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        if (to <= from) return BadRequest(new { message = "The analytics window must have a positive duration." });
        return Ok(await _service.GetAnalyticsAsync(bu, from, to, ct));
    }

    private bool TryTenant(out long bu) => long.TryParse(User.FindFirstValue("businessUnitId"), out bu) && bu > 0;
}

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
    private readonly ICommercialAccessContext _commercialAccess;
    private readonly ILogger<LeadIngestionController> _log;
    public LeadIngestionController(
        ILeadIdentityApplicationService service,
        ISecurityScanRecoveryService securityScanRecovery,
        ICommercialAccessContext commercialAccess,
        ILogger<LeadIngestionController> log)
    { _service = service; _securityScanRecovery = securityScanRecovery; _commercialAccess = commercialAccess; _log = log; }

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

    /// <summary>
    /// Operator discovery: which batches still hold files that a malware-scanner outage blocked.
    /// Deliberately independent of the batch page, whose retry control disappears once a hold has
    /// been recorded as Rejected — an infrastructure outage must never become a dead end.
    /// </summary>
    [HttpGet("blocked-files")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> BlockedFiles(CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        var batches = await _securityScanRecovery.ListBlockedBatchesAsync(bu, ct);
        return Ok(new
        {
            blockedFiles = batches.Sum(x => x.BlockedFiles),
            batches
        });
    }

    /// <summary>
    /// Tenant-wide replay of every scanner-blocked file from its immutable source object — no
    /// batch id and no re-upload required. Capped per call; re-invoke while <c>moreRemaining</c> is true.
    /// </summary>
    [HttpPost("retry-blocked-files")]
    [RequireModulePermission("Leads", PermissionAction.Create)]
    public async Task<IActionResult> RetryAllBlockedFiles(CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        var result = await _securityScanRecovery.RetryTenantAsync(bu, ct);
        _log.LogInformation(
            "Tenant-wide security-scan recovery requested for business unit {BusinessUnitId}: Eligible={Eligible} Queued={Queued} StillAwaiting={StillAwaiting}.",
            bu, result.Eligible, result.Queued, result.StillAwaiting);
        return Ok(result);
    }

    [HttpGet("match-reviews")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> PossibleMatches(CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        return Ok(await _service.GetPossibleMatchesAsync(bu, ct));
    }

    [HttpGet("duplicates")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> DuplicateUploads(CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });
        return Ok(await _service.GetDuplicateUploadsAsync(bu, ct));
    }

    [HttpGet("leads/{leadId:long}/revisions")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> Revisions(long leadId, CancellationToken ct)
    {
        if (!TryTenant(out var bu)) return BadRequest(new { message = "A valid businessUnitId claim is required." });

        // Every revision row carries the before and after JSON of a changed field, so this is the
        // lead's commercial detail in another shape. It answers the same way GET api/leads/{id}
        // does about the same id, rather than on the tenant predicate alone.
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();

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

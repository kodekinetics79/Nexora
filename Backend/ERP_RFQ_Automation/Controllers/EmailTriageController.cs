using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Ingestion.Triage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// The inbound-mail audit surface. Its whole reason to exist: a message the gate stopped must
/// remain VISIBLE and REVERSIBLE. A misclassified enquiry is a lost deal, so every fetched
/// message — including every rejection — is listed here with the machine-readable reasons
/// behind the decision and a one-click replay.
/// </summary>
[ApiController]
[Route("api/email-triage")]
[Authorize]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.EmailIntake)]
public sealed class EmailTriageController : ControllerBase
{
    private readonly IEmailTriageService _service;
    private readonly ILogger<EmailTriageController> _log;

    public EmailTriageController(IEmailTriageService service, ILogger<EmailTriageController> log)
    {
        _service = service;
        _log = log;
    }

    /// <param name="outcome">
    /// Inquiry | CommercialNonInquiry | Noise | Uncertain | Legacy (pre-gate mail). Omit for all.
    /// </param>
    [HttpGet]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] string? outcome = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        return Ok(await _service.ListAsync(businessUnitId, outcome, page, pageSize, ct));
    }

    /// <summary>
    /// One message with the state of every part it was made of.
    ///
    /// <para>Same tenant claim, same module permission and the same class-level entitlement as
    /// the list: this is the same audit surface at a different zoom level, and an endpoint that
    /// shows a message the list would not have shown is a hole with a different name.</para>
    /// </summary>
    [HttpGet("{id:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<IActionResult> Get(long id, CancellationToken ct = default)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        try
        {
            return Ok(await _service.GetAsync(businessUnitId, id, ct));
        }
        catch (KeyNotFoundException)
        {
            // 404 for another tenant's message as well as for one that does not exist: the two
            // must be indistinguishable, or the endpoint answers "does this id exist?" for
            // every tenant in the database.
            return NotFound();
        }
    }

    [HttpPost("{id:long}/reprocess")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<IActionResult> Reprocess(
        long id, [FromBody] ReprocessEmailRequest request, CancellationToken ct)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "A reason is required." });

        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Request.Headers["Idempotency-Key"].ToString()
            : request.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest(new { message = "An idempotency key is required." });

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "authenticated-user";
        try
        {
            return Ok(await _service.ReprocessAsync(
                businessUnitId, id, actor, request.Reason.Trim(), idempotencyKey.Trim(), ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            var correlationId = HttpContext.TraceIdentifier;
            _log.LogError(ex, "Email reprocess failed for ingest {IngestId}. CorrelationId={CorrelationId}",
                id, correlationId);
            return StatusCode(500, new { message = "The message could not be reprocessed.", correlationId });
        }
    }

    private bool TryTenant(out long businessUnitId)
        => long.TryParse(User.FindFirstValue("businessUnitId"), out businessUnitId) && businessUnitId > 0;
}

public sealed record ReprocessEmailRequest(string Reason, string? IdempotencyKey = null);

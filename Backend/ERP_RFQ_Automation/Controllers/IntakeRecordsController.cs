using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Ingestion.CanonicalRecord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Specification §1: ONE queryable record per processed email. Everything the pipeline
/// persisted about a message — source occurrence, classification, the unified attachment
/// inventory with each file's fate, the extracted RFQ, per-field evidence where it exists,
/// validation findings, the duplicate/revision verdict, the audit trail, and one derived
/// final status — behind a single GET, instead of six screens and a database console.
/// </summary>
[ApiController]
[Route("api/intake-records")]
[Authorize]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.EmailIntake)]
public sealed class IntakeRecordsController : ControllerBase
{
    private readonly ICanonicalIntakeRecordService _service;
    private readonly ICommercialAccessContext _commercialAccess;

    public IntakeRecordsController(
        ICanonicalIntakeRecordService service, ICommercialAccessContext commercialAccess)
    {
        _service = service;
        _commercialAccess = commercialAccess;
    }

    [HttpGet("{emailIngestId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<CanonicalIntakeRecord>> ByEmailIngest(
        long emailIngestId, CancellationToken ct)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        var record = await _service.GetByEmailIngestIdAsync(businessUnitId, emailIngestId, ct);
        if (record is null) return NotFound();

        // An email-ingest id is a small integer a caller can simply count through, and once the
        // message has produced a lead this record is that lead's whole extraction — header, lines,
        // evidence spans, audit trail. Reaching it by the message id rather than the lead id must
        // not answer what GET api/leads/{id} refuses; a message that produced no lead is mailbox
        // triage rather than somebody's opportunity, and stays readable by anyone who may see the
        // mailbox at all.
        if (record.Header is { LeadId: var leadId }
            && !await _commercialAccess.CanAccessLeadAsync(leadId, ct))
            return NotFound();

        return Ok(record);
    }

    [HttpGet("by-lead/{leadId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<CanonicalIntakeRecord>> ByLead(long leadId, CancellationToken ct)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        // The commercial access plane, applied exactly as LeadController.GetLeadById and
        // ProcessingEvidenceController apply it to this same lead id: the tenant predicate alone
        // would let one rep read another rep's extracted RFQ, and an out-of-scope lead is
        // deliberately indistinguishable from one that does not exist.
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();

        var record = await _service.GetByLeadIdAsync(businessUnitId, leadId, ct);
        return record is null ? NotFound() : Ok(record);
    }

    private bool TryTenant(out long businessUnitId)
        => long.TryParse(User.FindFirstValue("businessUnitId"), out businessUnitId)
           && businessUnitId > 0;
}

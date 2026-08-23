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

    public IntakeRecordsController(ICanonicalIntakeRecordService service) => _service = service;

    [HttpGet("{emailIngestId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<CanonicalIntakeRecord>> ByEmailIngest(
        long emailIngestId, CancellationToken ct)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        var record = await _service.GetByEmailIngestIdAsync(businessUnitId, emailIngestId, ct);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpGet("by-lead/{leadId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<CanonicalIntakeRecord>> ByLead(long leadId, CancellationToken ct)
    {
        if (!TryTenant(out var businessUnitId))
            return BadRequest(new { message = "A valid businessUnitId claim is required." });
        var record = await _service.GetByLeadIdAsync(businessUnitId, leadId, ct);
        return record is null ? NotFound() : Ok(record);
    }

    private bool TryTenant(out long businessUnitId)
        => long.TryParse(User.FindFirstValue("businessUnitId"), out businessUnitId)
           && businessUnitId > 0;
}

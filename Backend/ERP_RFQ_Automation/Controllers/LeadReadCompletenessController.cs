using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction.Anchoring;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Whether Nexora read the WHOLE document behind a request, for the one line on Decide that says
/// "Read 3 of about 42 lines — check the document".
///
/// <para>A separate read on purpose. The fact is written once, at extraction, as a PARTIAL_READ
/// finding on the extraction run (<see cref="PartialReadFact"/>); it is tiny, it never changes,
/// and keeping it off the decision workbench keeps that heavy read and its screen untouched.</para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/leads/{leadId:long}/read-completeness")]
public sealed class LeadReadCompletenessController : ControllerBase
{
    private readonly ErpRfqAutomationContext _db;
    private readonly ICommercialAccessContext _commercialAccess;

    public LeadReadCompletenessController(ErpRfqAutomationContext db, ICommercialAccessContext commercialAccess)
    {
        _db = db;
        _commercialAccess = commercialAccess;
    }

    [HttpGet]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<LeadReadCompletenessDto>> Get(long leadId, CancellationToken ct)
    {
        if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var businessUnitId) || businessUnitId <= 0)
            return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        return Ok(await ReadAsync(_db, businessUnitId, leadId, ct));
    }

    /// <summary>
    /// The fact recorded for the lead's LATEST read: a later, complete read of the same request (a
    /// re-upload, an amendment) clears the warning; an older partial read never speaks for it.
    /// </summary>
    internal static async Task<LeadReadCompletenessDto> ReadAsync(
        ErpRfqAutomationContext db, long businessUnitId, long leadId, CancellationToken ct)
    {
        var latestInquiryId = await db.Set<CanonicalInquiry>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId)
            .OrderByDescending(x => x.Id)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (latestInquiryId is null) return LeadReadCompletenessDto.Complete;

        var message = await db.Set<ValidationFinding>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.InquiryId == latestInquiryId
                        && x.Code == PartialReadFact.FindingCode)
            .OrderByDescending(x => x.Id)
            .Select(x => x.Message)
            .FirstOrDefaultAsync(ct);
        return PartialReadFact.TryParse(message, out var fact)
            ? new LeadReadCompletenessDto(true, fact.LinesRead, fact.LinesExpected, fact.PartsUnread)
            : LeadReadCompletenessDto.Complete;
    }
}

/// <param name="Partial">True when part of the document was not read.</param>
/// <param name="LinesRead">Lines Nexora read.</param>
/// <param name="LinesExpected">About how many lines the document prints, when the pages show it; otherwise null.</param>
/// <param name="PartsUnread">Parts of the document the reading could not get through.</param>
public sealed record LeadReadCompletenessDto(bool Partial, int? LinesRead, int? LinesExpected, int PartsUnread)
{
    public static LeadReadCompletenessDto Complete { get; } = new(false, null, null, 0);
}

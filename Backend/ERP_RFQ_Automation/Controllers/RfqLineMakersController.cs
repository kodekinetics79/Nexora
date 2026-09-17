using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// The makers a customer accepts for one RFQ line ("ABB, GE or Eaton"), kept where the document
/// reader already writes them: the line's "Approved manufacturers" field. Saving re-checks the
/// line's own suppliers so anyone carrying a newly accepted maker shows up in Find supplier.
/// </summary>
[ApiController]
[Authorize]
[Route("api/rfq")]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Rfq)]
public sealed class RfqLineMakersController(
    ErpRfqAutomationContext db,
    IProcurementApplicationService procurement,
    ICommercialAccessContext? access = null) : ControllerBase
{
    public const string ApprovedMakersKey = "Approved manufacturers";
    public const int MaxMakers = 12;
    public const int MaxMakerLength = 120;

    public sealed record SaveMakersCommand(string? AcceptedMakers);

    [HttpPut("{rfqId:long}/items/{itemId:long}/makers")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    public async Task<IActionResult> Save(long rfqId, long itemId, [FromBody] SaveMakersCommand command, CancellationToken ct)
    {
        if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenant) || tenant <= 0) return Unauthorized();
        if (access is null || !await access.CanAccessRfqAsync(rfqId, ct)) return NotFound();

        var makers = Normalise(command.AcceptedMakers);
        if (makers.Count > MaxMakers)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Makers not saved", Detail = $"List at most {MaxMakers} makers." });
        if (makers.Any(x => x.Length > MaxMakerLength))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Makers not saved", Detail = "One of the makers is too long." });

        var line = await db.Rfqitems.Include(x => x.Rfq)
            .SingleOrDefaultAsync(x => x.Id == itemId && x.Rfqid == rfqId && x.Rfq.BusinessUnitId == tenant, ct);
        if (line is null) return NotFound();

        var extra = ExtraFieldsJson.Deserialize(line.ExtraFields) ?? new Dictionary<string, string>();
        foreach (var key in extra.Keys.Where(k => string.Equals(k.Trim(), ApprovedMakersKey, StringComparison.OrdinalIgnoreCase)).ToList())
            extra.Remove(key);
        if (makers.Count > 0) extra[ApprovedMakersKey] = string.Join("; ", makers);
        line.ExtraFields = ExtraFieldsJson.Serialize(extra);
        line.ModifiedBy = Actor();
        line.ModifiedDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var caseId = await db.SourcingCases.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.RfqItemId == itemId)
            .OrderByDescending(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(ct);
        if (caseId is not null)
        {
            try
            {
                await procurement.RefreshCandidatesAfterSupplierChangeAsync(
                    new RefreshSourcingCandidatesCommand(tenant, caseId.Value, Actor(), $"makers-{itemId}-{Guid.NewGuid():N}"), ct);
            }
            catch (ProcurementConflictException) { /* a closed case keeps its suppliers */ }
        }
        return Ok(new { acceptedMakers = makers });
    }

    /// <summary>"ABB 1SDA; GE THQL32010 ;; eaton\nABB 1SDA" → ["ABB 1SDA", "GE THQL32010", "eaton"].</summary>
    public static IReadOnlyList<string> Normalise(string? text) =>
        (text ?? string.Empty).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => string.Join(' ', x.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string Actor() =>
        User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "authenticated-user";
}

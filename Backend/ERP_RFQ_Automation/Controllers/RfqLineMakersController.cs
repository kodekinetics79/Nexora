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

    public sealed record OfferedPartCommand(string? PartNumber, string? MakerName, string? Kind, string? Note, string? Specs, long? ProductId);

    /// <summary>
    /// The part actually offered for this line when the one asked for is obsolete (REPLACEMENT) or
    /// discontinued with no successor (EQUIVALENT). Sending an empty part number clears it.
    /// </summary>
    [HttpPut("{rfqId:long}/items/{itemId:long}/offered-part")]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    public async Task<IActionResult> SaveOfferedPart(long rfqId, long itemId, [FromBody] OfferedPartCommand command, CancellationToken ct)
    {
        if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenant) || tenant <= 0) return Unauthorized();
        if (access is null || !await access.CanAccessRfqAsync(rfqId, ct)) return NotFound();

        var line = await db.Rfqitems.Include(x => x.Rfq)
            .SingleOrDefaultAsync(x => x.Id == itemId && x.Rfqid == rfqId && x.Rfq.BusinessUnitId == tenant, ct);
        if (line is null) return NotFound();

        var part = Clean(command.PartNumber, 100);
        var maker = Clean(command.MakerName, 150);
        if (part is null && maker is null)
        {
            line.OfferedPartNumber = line.OfferedMakerName = line.OfferedKind = line.OfferedNote = line.OfferedSpecs = null;
        }
        else
        {
            var kind = string.IsNullOrWhiteSpace(command.Kind) ? OfferedPartKinds.Replacement : command.Kind.Trim().ToUpperInvariant();
            if (!OfferedPartKinds.IsKnown(kind))
                return BadRequest(new ProblemDetails { Status = 400, Title = "Not saved", Detail = "Say whether this is the maker's replacement or an equivalent." });
            var specs = Clean(command.Specs, OfferedPartKinds.MaxSpecs);
            if (kind == OfferedPartKinds.Equivalent && specs is null)
                return BadRequest(new ProblemDetails { Status = 400, Title = "Not saved", Detail = "Give the specs the customer needs to judge the equivalent." });
            line.OfferedPartNumber = part;
            line.OfferedMakerName = maker;
            line.OfferedKind = kind;
            line.OfferedNote = Clean(command.Note, OfferedPartKinds.MaxNote);
            line.OfferedSpecs = specs;

            // The catalogue product for the offered part takes over the line, so stock, suppliers
            // and pricing all follow what is really being sold. The swap is remembered.
            if (command.ProductId is long productId && productId != line.ProductId)
            {
                var exists = await db.Products.AnyAsync(x => x.Id == productId && x.Buid == tenant, ct);
                if (!exists) return BadRequest(new ProblemDetails { Status = 400, Title = "Not saved", Detail = "That catalogue part was not found." });
                var replaced = line.ProductId;
                line.ProductId = productId;
                if (replaced is long from && from != productId
                    && !await db.ProductSupersessions.AnyAsync(x => x.BusinessUnitId == tenant && x.SupersededProductId == from && x.ReplacementProductId == productId, ct))
                {
                    db.ProductSupersessions.Add(new Inventory.Commercial.ProductSupersession
                    {
                        BusinessUnitId = tenant, SupersededProductId = from, ReplacementProductId = productId,
                        Kind = kind == OfferedPartKinds.Equivalent
                            ? Inventory.Commercial.ProductSupersessionKind.FormFitFunctionReplacement
                            : Inventory.Commercial.ProductSupersessionKind.DirectReplacement,
                        EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
                        EvidenceReference = $"rfq-line:{itemId}", CreatedBy = Actor(), CreatedOn = DateTime.UtcNow
                    });
                }
            }
        }
        line.ModifiedBy = Actor();
        line.ModifiedDate = DateTime.UtcNow;

        // A draft quote already holding this line shows what is offered straight away; quotes the
        // customer already has are never touched (a revision re-reads this line).
        var sentence = OfferedPartKinds.Sentence(line.OfferedKind, line.OfferedMakerName, line.OfferedPartNumber,
            line.ManufacturerPartNumber, line.OfferedNote);
        var draftLines = await db.QuoteItems
            .Where(x => x.RfqitemId == itemId && x.Quote.BusinessUnitId == tenant && x.Quote.SentOn == null)
            .ToListAsync(ct);
        foreach (var draftLine in draftLines)
        {
            draftLine.OfferedNote = sentence;
            draftLine.OfferedSpecs = line.OfferedSpecs;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            offeredPartNumber = line.OfferedPartNumber, offeredMakerName = line.OfferedMakerName,
            offeredKind = line.OfferedKind, offeredNote = line.OfferedNote, offeredSpecs = line.OfferedSpecs,
            productId = line.ProductId,
        });
    }

    private static string? Clean(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

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

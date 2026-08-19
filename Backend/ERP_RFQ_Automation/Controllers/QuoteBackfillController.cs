using System.Security.Claims;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Carrying already-issued quotes into Nexora.
///
/// A tenant arrives holding open quotes it sent before this system existed. Until they are in
/// here, the pipeline shows only the work that started after go-live, which is not the tenant's
/// actual position. These endpoints close that gap; everything they create is an ordinary quote
/// afterwards, distinguished only by <c>Origin = BACKFILL</c> and the customer's own reference.
/// </summary>
[Authorize]
[Route("api/quotes/backfill")]
[ApiController]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(
    ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Quotes)]
public sealed class QuoteBackfillController : ControllerBase
{
    private readonly QuoteBackfillService _backfill;
    private readonly ILogger<QuoteBackfillController> _log;

    public QuoteBackfillController(QuoteBackfillService backfill, ILogger<QuoteBackfillController> log)
    {
        _backfill = backfill; _log = log;
    }

    /// <summary>Carries in ONE quote entered by a person.</summary>
    [HttpPost]
    public async Task<IActionResult> Backfill([FromBody] QuoteBackfillRequest request, CancellationToken ct)
    {
        var businessUnitId = TenantId();
        if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

        try
        {
            var result = await _backfill.BackfillAsync(request, businessUnitId, ActorEmail(), ct);
            // A repeat import is not an error; it is the expected outcome of fixing a file and
            // uploading it again. It answers 200 with what is already there, not 201.
            return result.AlreadyPresent ? Ok(result) : CreatedAtAction(nameof(Backfill), new { id = result.QuoteId }, result);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    private long TenantId() =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;

    private string ActorEmail() =>
        User.FindFirst(ClaimTypes.Email)?.Value
        ?? User.FindFirst("email")?.Value
        ?? User.Identity?.Name
        ?? "unknown";
}

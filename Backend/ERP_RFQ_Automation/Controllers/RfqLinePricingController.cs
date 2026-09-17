using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;

using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Pricing an RFQ line that stock covers, from the RFQ screen: one read with shelf, suggested
/// price and price history; one write that puts the chosen price on the quote draft.
/// </summary>
[ApiController]
[Authorize]
[Route("api/rfq")]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Rfq)]
public sealed class RfqLinePricingController(
    IStockLinePricingService pricing,
    IQuoteService quotes,
    ICommercialAccessContext? access = null) : ControllerBase
{
    [HttpGet("{rfqId:long}/items/{itemId:long}/stock-price")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public async Task<IActionResult> Get(long rfqId, long itemId, [FromQuery] long? productId, CancellationToken ct)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        if (access is null || !await access.CanAccessRfqAsync(rfqId, ct)) return NotFound();
        var view = await pricing.GetAsync(tenant, rfqId, itemId, ct, productId);
        return view is null ? NotFound() : Ok(view);
    }

    /// <summary>Stock of the other makers the customer accepts for this line.</summary>
    [HttpGet("{rfqId:long}/items/{itemId:long}/other-makers-in-stock")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public async Task<IActionResult> OtherMakers(long rfqId, long itemId, CancellationToken ct)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        if (access is null || !await access.CanAccessRfqAsync(rfqId, ct)) return NotFound();
        return Ok(await pricing.OtherMakersInStockAsync(tenant, rfqId, itemId, ct));
    }

    public sealed record UseStockPriceCommand(decimal UnitPrice, bool ExStock = true, long? CurrencyId = null, bool ReviseIfSent = false, long? ProductId = null, int? LeadTimeDays = null);

    [HttpPost("{rfqId:long}/items/{itemId:long}/stock-price")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.Create)]
    public async Task<IActionResult> Use(long rfqId, long itemId, [FromBody] UseStockPriceCommand command, CancellationToken ct)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        if (access is null || !await access.CanAccessRfqAsync(rfqId, ct)) return NotFound();
        AcceptedMakerStock? otherMaker = null;
        if (command.ProductId is not null)
        {
            otherMaker = (await pricing.OtherMakersInStockAsync(tenant, rfqId, itemId, ct, inStockOnly: false))
                .FirstOrDefault(x => x.ProductId == command.ProductId);
            if (otherMaker is null)
                return Conflict(new ProblemDetails { Status = 409, Title = "Price not added", Detail = "That product is not one of the makers the customer accepts for this line." });
        }
        try
        {
            var quote = await quotes.PriceRfqLineAsync(rfqId, itemId, tenant, Actor(), command.UnitPrice, command.ExStock, command.CurrencyId, ct,
                command.ReviseIfSent, otherMaker?.ProductId, otherMaker?.Label, command.LeadTimeDays);
            return Ok(new { quoteId = quote.Id, quoteNo = quote.QuoteNo });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Price not added", Detail = ex.Message });
        }
    }

    public sealed record SaveMarginCommand(decimal? MarginPercent);

    [HttpGet("stock-margin")]
    [RequireModulePermission("Quote Configuration", PermissionAction.View)]
    public async Task<IActionResult> GetMargin(CancellationToken ct)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        return Ok(new { marginPercent = await pricing.GetStandardMarginAsync(tenant, ct) });
    }

    [HttpPut("stock-margin")]
    [RequireModulePermission("Quote Configuration", PermissionAction.Edit)]
    public async Task<IActionResult> SaveMargin([FromBody] SaveMarginCommand command, CancellationToken ct)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        try
        {
            await pricing.SaveStandardMarginAsync(tenant, command.MarginPercent, Actor(), ct);
            return Ok(new { marginPercent = await pricing.GetStandardMarginAsync(tenant, ct) });
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Margin not saved", Detail = "Margin must be between 0% and 1000%." });
        }
    }

    private bool TryTenant(out long tenant) =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out tenant) && tenant > 0;

    private string Actor() =>
        User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "authenticated-user";
}

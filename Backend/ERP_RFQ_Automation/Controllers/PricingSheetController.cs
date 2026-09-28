using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// The Pricing sheet: every part the company sells, with its landed cost and sale price in the
/// currency its keeper chose. Anyone who can see products can read it; only someone who can edit
/// products (a manager or other authorised person) can change it. A sales rep still sets the margin
/// on each quote, in the RFQ pricing window, from these figures.
///
/// Landed cost is <see cref="Product.UnitCost"/> and sale price <see cref="Product.SellingPrice"/>:
/// the same two fields the product form and the import sheet already write, so there is one price
/// per part, not two that drift. Who changed a price and when is read from the master-data audit,
/// which records every one of these fields with its before and after value.
/// </summary>
[ApiController]
[Authorize]
[Route("api/pricing-sheet")]
public sealed class PricingSheetController(ErpRfqAutomationContext db) : ControllerBase
{
    private const int MaxPageSize = 200;
    private const int MaxRowsPerSave = 500;
    private const decimal MaxPrice = 1_000_000_000m;
    private static readonly string[] PriceFields = ["UnitCost", "SellingPrice", "PriceCurrencyId"];

    public sealed record PricingCurrency(long Id, string Code, bool IsBase);

    public sealed record PricingRow(
        long ProductId, string PartNo, string? Name, string? Unit,
        long? CurrencyId, string? CurrencyCode, decimal? LandedCost, decimal? SalePrice,
        decimal? LastPurchasePrice, decimal OnHand, DateTime? ChangedOn, string? ChangedBy);

    public sealed record PricingPage(
        IReadOnlyList<PricingRow> Rows, int Total, int Page, int PageSize, int MissingCount,
        IReadOnlyList<PricingCurrency> Currencies);

    [HttpGet]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult<PricingPage>> Get(
        [FromQuery] string? search, [FromQuery] bool missingOnly = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // The tenant's own parts only. A shared catalogue row (no tenant) is visible to everyone, so
        // a price written onto it would reach every other company's quotes.
        var parts = db.Products.AsNoTracking().Where(p => p.Buid == tenant && p.IsActive != false);
        var missing = (IQueryable<Product> q) => q.Where(p => p.UnitCost == null || p.UnitCost <= 0m
            || p.SellingPrice == null || p.SellingPrice <= 0m || p.PriceCurrencyId == null);
        var missingCount = await missing(parts).CountAsync(ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            parts = parts.Where(p => p.PartNo.ToLower().Contains(term)
                || (p.ProductName != null && p.ProductName.ToLower().Contains(term))
                || (p.Description != null && p.Description.ToLower().Contains(term)));
        }
        if (missingOnly) parts = missing(parts);

        var total = await parts.CountAsync(ct);
        var rows = await parts
            .OrderBy(p => p.PartNo).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new
            {
                p.Id, p.PartNo, Name = p.ProductName ?? p.Description, Unit = p.Uom != null ? p.Uom.UomCode : null,
                p.PriceCurrencyId, CurrencyCode = p.PriceCurrency != null ? p.PriceCurrency.Code : null,
                p.UnitCost, p.SellingPrice, p.FinalLandedCost
            })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var onHand = (await db.Set<Models.Inventory>().AsNoTracking()
                .Where(i => i.Buid == tenant && i.ProductId != null && ids.Contains(i.ProductId.Value))
                .Select(i => new { ProductId = i.ProductId!.Value, i.QtyOnHand })
                .ToListAsync(ct))
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.QtyOnHand));
        var changes = await LastPriceChangesAsync(tenant, ids, ct);

        var currencies = await db.Currencies.AsNoTracking()
            .Where(c => c.BusinessUnitId == tenant && c.IsActive == true)
            .OrderByDescending(c => c.IsBaseCurrency == true).ThenBy(c => c.Code)
            .Select(c => new PricingCurrency(c.Id, c.Code, c.IsBaseCurrency == true))
            .ToListAsync(ct);

        return Ok(new PricingPage(
            rows.Select(r => new PricingRow(
                r.Id, r.PartNo, r.Name, r.Unit, r.PriceCurrencyId, r.CurrencyCode,
                Positive(r.UnitCost), Positive(r.SellingPrice), Positive(r.FinalLandedCost),
                onHand.GetValueOrDefault(r.Id),
                changes.TryGetValue(r.Id, out var c) ? c.On : null,
                changes.TryGetValue(r.Id, out var c2) ? c2.By : null)).ToList(),
            total, page, pageSize, missingCount, currencies));
    }

    public sealed record PriceChange(long ProductId, decimal? LandedCost, decimal? SalePrice, long? CurrencyId);
    public sealed record SaveCommand(IReadOnlyList<PriceChange> Rows);

    [HttpPut]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public async Task<IActionResult> Save([FromBody] SaveCommand command, CancellationToken ct)
    {
        if (!TryTenant(out var tenant)) return Unauthorized();
        var changes = command?.Rows ?? [];
        if (changes.Count == 0) return BadRequest(Problem("Nothing to save.", "There are no changed prices."));
        if (changes.Count > MaxRowsPerSave)
            return BadRequest(Problem("Too many rows.", $"Save at most {MaxRowsPerSave} parts at a time."));
        if (changes.Select(c => c.ProductId).Distinct().Count() != changes.Count)
            return BadRequest(Problem("A part appears twice.", "Each part can be saved once per save."));

        var ids = changes.Select(c => c.ProductId).ToList();
        var products = await db.Products
            .Where(p => p.Buid == tenant && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var currencies = await db.Currencies.AsNoTracking()
            .Where(c => c.BusinessUnitId == tenant && c.IsActive == true)
            .Select(c => c.Id).ToListAsync(ct);

        var problems = new List<string>();
        foreach (var change in changes)
        {
            if (!products.TryGetValue(change.ProductId, out var product))
            {
                problems.Add($"Part {change.ProductId} is not one of your company's parts.");
                continue;
            }
            var label = product.PartNo;
            if (change.LandedCost is { } cost && (cost <= 0m || cost > MaxPrice))
                problems.Add($"{label}: landed cost must be above 0.");
            if (change.SalePrice is { } sale && (sale <= 0m || sale > MaxPrice))
                problems.Add($"{label}: sale price must be above 0.");
            if ((change.LandedCost is not null || change.SalePrice is not null) && change.CurrencyId is null)
                problems.Add($"{label}: choose the currency these prices are in.");
            if (change.CurrencyId is { } currencyId && !currencies.Contains(currencyId))
                problems.Add($"{label}: choose one of your company's currencies.");
        }
        if (problems.Count > 0)
            return BadRequest(Problem("Prices not saved.", string.Join(" ", problems.Take(10))
                + (problems.Count > 10 ? $" And {problems.Count - 10} more." : "")));

        var now = DateTime.UtcNow;
        var actor = Actor();
        foreach (var change in changes)
        {
            var product = products[change.ProductId];
            product.UnitCost = change.LandedCost is { } cost ? Math.Round(cost, 4) : null;
            product.SellingPrice = change.SalePrice is { } sale ? Math.Round(sale, 4) : null;
            product.PriceCurrencyId = change.CurrencyId;
            if (db.Entry(product).State == EntityState.Modified)
            {
                product.ModifiedBy = actor;
                product.ModifiedOn = now;
            }
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { saved = changes.Count });
    }

    /// <summary>The last time each part's landed cost, sale price or price currency moved, and who moved it.</summary>
    private async Task<Dictionary<long, (DateTime On, string By)>> LastPriceChangesAsync(long tenant, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var events = await db.MasterDataChangeEvents.AsNoTracking()
            .Where(e => e.BusinessUnitId == tenant && e.EntityType == MasterDataEntityTypes.Product && ids.Contains(e.EntityId)
                && e.Fields.Any(f => PriceFields.Contains(f.FieldName)))
            .Select(e => new { e.EntityId, e.OccurredOn, e.Actor })
            .ToListAsync(ct);
        return events.GroupBy(e => e.EntityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.OccurredOn).Select(e => (e.OccurredOn, e.Actor)).First());
    }

    private static decimal? Positive(decimal? value) => value is > 0m ? value : null;

    private static ProblemDetails Problem(string title, string detail) => new() { Status = 400, Title = title, Detail = detail };

    private bool TryTenant(out long tenant) =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out tenant) && tenant > 0;

    private string Actor() =>
        User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "authenticated-user";
}

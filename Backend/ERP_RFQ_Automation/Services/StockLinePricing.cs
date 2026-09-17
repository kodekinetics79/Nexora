using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// Everything a rep needs to price an RFQ line that stock covers, in one read: what is on the
/// shelf and where, the price the company would charge (selling price, else cost plus the
/// company's standard margin), and what this part last sold for and last won an order at.
///
/// <para>Nothing is held. Quoting goes to hundreds of customers, so stock is offered
/// "ex stock, subject to prior sale" and is reserved only when the customer commits with an order
/// (<c>OrderStockReservationService</c>).</para>
/// </summary>
public interface IStockLinePricingService
{
    Task<StockLinePriceView?> GetAsync(long businessUnitId, long rfqId, long rfqItemId, CancellationToken ct);
    Task<decimal?> GetStandardMarginAsync(long businessUnitId, CancellationToken ct);
    Task SaveStandardMarginAsync(long businessUnitId, decimal? marginPercent, string actor, CancellationToken ct);
}

public sealed record StockLinePriceView(
    long RfqItemId,
    long? ProductId,
    string? PartNumber,
    string? Description,
    string? Maker,
    decimal RequestedQuantity,
    string? Unit,
    StockOnShelf Stock,
    StockPriceSuggestion Price,
    PartTrackRecord TrackRecord,
    IReadOnlyList<PriceReference> History,
    QuoteLineNow? OnQuote,
    StockCurrency? Currency);

public sealed record StockOnShelf(decimal OnHand, decimal Free, decimal HeldForOrders, IReadOnlyList<StockPlace> Places);

public sealed record StockPlace(string Warehouse, decimal OnHand, decimal Free);

/// <summary>Source: SELLING_PRICE, COST_PLUS_MARGIN, COST_ONLY (cost known, no margin set) or NONE.</summary>
public sealed record StockPriceSuggestion(string Source, decimal? SellingPrice, decimal? UnitCost, decimal? MarginPercent, decimal? UnitPrice);

/// <summary>Kind: SOLD (an order line), WON (a quote the customer accepted), QUOTED (any other sent quote).</summary>
public sealed record PriceReference(string Kind, decimal UnitPrice, string? CurrencyCode, decimal Quantity, string? Customer, DateTime? On, string Reference)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long? QuoteId { get; init; }
}

/// <summary>
/// Owner, 2026-09-17: the company's own record on this part — what it last quoted (to anyone) and
/// when it last won the order, or that it never has. Won = an accepted quote, or an order that did
/// not come from a quote already counted.
/// </summary>
public sealed record PartTrackRecord(PriceReference? LastQuoted, PriceReference? LastWon, int TimesQuoted, int TimesWon);

public sealed record QuoteLineNow(long QuoteId, string QuoteNo, decimal UnitPrice, bool ExStock, string? CurrencyCode);

public sealed record StockCurrency(long Id, string Code);

public sealed class StockLinePricingService : IStockLinePricingService
{
    /// <summary>How many prices to other customers the window lists under the two headline figures.</summary>
    public const int HistoryRows = 5;

    private static readonly HashSet<string> WonCodes = new(StringComparer.OrdinalIgnoreCase) { "ACCEPTED", "ORDERED" };
    private static readonly HashSet<string> NotSentCodes = new(StringComparer.OrdinalIgnoreCase) { "DRAFT", "CANCELLED" };

    private readonly ErpRfqAutomationContext _db;

    public StockLinePricingService(ErpRfqAutomationContext db) => _db = db;

    public async Task<StockLinePriceView?> GetAsync(long businessUnitId, long rfqId, long rfqItemId, CancellationToken ct)
    {
        var line = await _db.Rfqitems.AsNoTracking()
            .Where(x => x.Id == rfqItemId && x.Rfqid == rfqId && x.Rfq.BusinessUnitId == businessUnitId)
            .Select(x => new
            {
                x.Id, x.ProductId, x.ManufacturerPartNumber, x.ItemMaterialCode, x.ProductShortDescription,
                x.ProductShortName, x.ManufacturerName, x.Quantity, x.UnitOfMeasure
            })
            .SingleOrDefaultAsync(ct);
        if (line is null) return null;

        var product = line.ProductId is null ? null : await _db.Products.AsNoTracking()
            .Where(x => x.Id == line.ProductId)
            .Select(x => new { x.SellingPrice, x.UnitCost, x.FinalSalesPrice })
            .SingleOrDefaultAsync(ct);

        var stock = await ShelfAsync(businessUnitId, line.ProductId, ct);
        var margin = await GetStandardMarginAsync(businessUnitId, ct);
        var price = Suggest(product?.SellingPrice ?? product?.FinalSalesPrice ?? stock.SellingPrice,
            stock.UnitCost ?? product?.UnitCost, margin);

        var history = line.ProductId is null
            ? new List<PriceReference>()
            : await HistoryAsync(businessUnitId, line.ProductId.Value, ct);
        var quotes = history.Where(x => x.Kind is "QUOTED" or "WON").ToList();
        var wonQuoteIds = history.Where(x => x.Kind == "WON").Select(x => x.QuoteId).ToHashSet();
        var wins = history.Where(x => x.Kind == "WON" || x.Kind == "SOLD" && (x.QuoteId is null || !wonQuoteIds.Contains(x.QuoteId))).ToList();
        var lastQuoted = quotes.FirstOrDefault();
        var lastWon = wins.FirstOrDefault();
        var track = new PartTrackRecord(lastQuoted, lastWon, quotes.Count, wins.Count);

        var onQuote = await _db.QuoteItems.AsNoTracking()
            .Where(x => x.RfqitemId == rfqItemId && x.Quote.BusinessUnitId == businessUnitId)
            .OrderByDescending(x => x.Quote.Id)
            .Select(x => new QuoteLineNow(x.QuoteId, x.Quote.QuoteNo, x.UnitPrice, x.DeliveryLeadTime == 0,
                x.Quote.Currency != null ? x.Quote.Currency.Code : null))
            .FirstOrDefaultAsync(ct);

        var currency = await _db.Quotes.AsNoTracking()
            .Where(x => x.Rfqid == rfqId && x.BusinessUnitId == businessUnitId && x.Currency != null)
            .OrderByDescending(x => x.Id)
            .Select(x => new StockCurrency(x.Currency!.Id, x.Currency.Code))
            .FirstOrDefaultAsync(ct)
            ?? await _db.Currencies.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive == true && x.IsBaseCurrency == true)
                .Select(x => new StockCurrency(x.Id, x.Code))
                .FirstOrDefaultAsync(ct);

        return new StockLinePriceView(
            line.Id, line.ProductId,
            line.ManufacturerPartNumber ?? line.ItemMaterialCode,
            line.ProductShortDescription ?? line.ProductShortName,
            line.ManufacturerName,
            line.Quantity ?? 0m,
            line.UnitOfMeasure,
            new StockOnShelf(stock.OnHand, stock.Free, stock.Held, stock.Places),
            price,
            track,
            history.Where(x => !ReferenceEquals(x, lastQuoted) && !ReferenceEquals(x, lastWon)).Take(HistoryRows).ToList(),
            onQuote,
            currency);
    }

    /// <summary>
    /// Selling price wins. Without one, cost plus the company margin. A cost with no margin set is
    /// offered as the cost itself so the rep sees a starting figure, never a silent zero.
    /// </summary>
    public static StockPriceSuggestion Suggest(decimal? sellingPrice, decimal? unitCost, decimal? marginPercent)
    {
        if (sellingPrice is > 0m)
            return new StockPriceSuggestion("SELLING_PRICE", sellingPrice, unitCost, marginPercent, Math.Round(sellingPrice.Value, 2));
        if (unitCost is > 0m && marginPercent is not null)
            return new StockPriceSuggestion("COST_PLUS_MARGIN", null, unitCost, marginPercent,
                Math.Round(unitCost.Value * (1m + marginPercent.Value / 100m), 2));
        if (unitCost is > 0m)
            return new StockPriceSuggestion("COST_ONLY", null, unitCost, null, Math.Round(unitCost.Value, 2));
        return new StockPriceSuggestion("NONE", null, null, marginPercent, null);
    }

    private async Task<(decimal OnHand, decimal Free, decimal Held, List<StockPlace> Places, decimal? UnitCost, decimal? SellingPrice)> ShelfAsync(
        long businessUnitId, long? productId, CancellationToken ct)
    {
        if (productId is null) return (0m, 0m, 0m, new List<StockPlace>(), null, null);

        var rows = await _db.Set<Models.Inventory>().AsNoTracking()
            .Where(x => x.Buid == businessUnitId && x.ProductId == productId)
            .Select(x => new
            {
                x.Id, x.QtyOnHand, x.AllocatedQuantity, x.QuarantineQuantity, x.DamagedQuantity,
                x.ExpiredQuantity, x.SafetyStockQuantity, x.UnitCost, x.SellingPrice, x.WarehouseId
            })
            .ToListAsync(ct);
        var warehouseIds = rows.Where(x => x.WarehouseId != null).Select(x => x.WarehouseId!.Value).Distinct().ToArray();
        var warehouseNames = await _db.Warehouses.AsNoTracking()
            .Where(x => warehouseIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.WarehouseName, ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var reserved = await _db.Set<StockReservation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && ids.Contains(x.InventoryId) && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId)
            .Select(x => new { InventoryId = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.InventoryId, x => x.Quantity, ct);

        var places = rows
            .GroupBy(x => x.WarehouseId is long w && warehouseNames.TryGetValue(w, out var name) ? name : "Unassigned")
            .Select(g => new StockPlace(g.Key, g.Sum(x => x.QtyOnHand), g.Sum(x => InventoryQuantityMath.AvailableToPromise(
                x.QtyOnHand, reserved.GetValueOrDefault(x.Id), x.AllocatedQuantity, x.QuarantineQuantity,
                x.DamagedQuantity, x.ExpiredQuantity, x.SafetyStockQuantity))))
            .Where(x => x.OnHand > 0m)
            .OrderByDescending(x => x.Free)
            .ToList();

        var onHand = rows.Sum(x => x.QtyOnHand);
        // Cost weighted by what is on each shelf, so one old row priced long ago does not set the figure.
        var costed = rows.Where(x => x.UnitCost is > 0m && x.QtyOnHand > 0m).ToList();
        decimal? cost = costed.Count == 0 ? rows.FirstOrDefault(x => x.UnitCost is > 0m)?.UnitCost
            : Math.Round(costed.Sum(x => x.UnitCost!.Value * x.QtyOnHand) / costed.Sum(x => x.QtyOnHand), 4);
        return (onHand, places.Sum(x => x.Free), reserved.Values.Sum(), places, cost,
            rows.FirstOrDefault(x => x.SellingPrice is > 0m)?.SellingPrice);
    }

    private async Task<List<PriceReference>> HistoryAsync(long businessUnitId, long productId, CancellationToken ct)
    {
        var sold = await _db.OrderItems.AsNoTracking()
            .Where(x => x.ProductId == productId && x.Order.BusinessUnitId == businessUnitId && x.Order.IsActive && x.UnitPrice > 0m)
            .OrderByDescending(x => x.Order.OrderDate).ThenByDescending(x => x.Id)
            .Take(50)
            .Select(x => new
            {
                x.UnitPrice, x.Quantity, x.Order.OrderDate, x.Order.OrderNo, x.Order.QuoteId,
                Customer = x.Order.Customer.Name,
                Currency = x.Order.Currency != null ? x.Order.Currency.Code : null,
                Status = x.Order.Status.SetupCode, StatusValue = x.Order.Status.SetupValue
            })
            .ToListAsync(ct);

        var quoted = await _db.QuoteItems.AsNoTracking()
            .Where(x => x.ProductId == productId && x.Quote.BusinessUnitId == businessUnitId && x.UnitPrice > 0m)
            .OrderByDescending(x => x.Quote.QuoteDate).ThenByDescending(x => x.Id)
            .Take(100)
            .Select(x => new
            {
                x.UnitPrice, x.Quantity, x.Quote.QuoteDate, x.Quote.QuoteNo, x.QuoteId,
                Customer = x.Quote.Customer != null ? x.Quote.Customer.Name : null,
                Currency = x.Quote.Currency != null ? x.Quote.Currency.Code : null,
                Status = x.Quote.Status != null ? x.Quote.Status.SetupCode : null,
                StatusValue = x.Quote.Status != null ? x.Quote.Status.SetupValue : null
            })
            .ToListAsync(ct);

        var all = new List<PriceReference>();
        all.AddRange(sold
            .Where(x => !string.Equals(LifecyclePolicy.Canonicalize("Order", x.Status, x.StatusValue), "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .Select(x => new PriceReference("SOLD", x.UnitPrice, x.Currency, x.Quantity, x.Customer, x.OrderDate, x.OrderNo) { QuoteId = x.QuoteId }));
        foreach (var q in quoted)
        {
            var code = LifecyclePolicy.Canonicalize("Quote", q.Status, q.StatusValue) ?? string.Empty;
            if (NotSentCodes.Contains(code)) continue;
            all.Add(new PriceReference(WonCodes.Contains(code) ? "WON" : "QUOTED", q.UnitPrice, q.Currency, q.Quantity, q.Customer, q.QuoteDate, q.QuoteNo) { QuoteId = q.QuoteId });
        }
        return all.OrderByDescending(x => x.On ?? DateTime.MinValue).ToList();
    }

    public async Task<decimal?> GetStandardMarginAsync(long businessUnitId, CancellationToken ct) =>
        await _db.QuoteConfigurations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .Select(x => x.StockMarginPercent)
            .FirstOrDefaultAsync(ct);

    public async Task SaveStandardMarginAsync(long businessUnitId, decimal? marginPercent, string actor, CancellationToken ct)
    {
        if (marginPercent is < 0m or > 1000m)
            throw new ArgumentOutOfRangeException(nameof(marginPercent), "Margin must be between 0% and 1000%.");
        var config = await _db.QuoteConfigurations.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct);
        if (config is null)
        {
            config = new QuoteConfiguration { BusinessUnitId = businessUnitId };
            _db.QuoteConfigurations.Add(config);
        }
        config.StockMarginPercent = marginPercent is null ? null : Math.Round(marginPercent.Value, 2);
        config.ModifiedBy = actor;
        config.ModifiedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

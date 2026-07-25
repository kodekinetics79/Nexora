using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.ProductIntelligence;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Inventory.Commercial;

public interface ICommercialLineResolutionApplicationService
{
    Task<IReadOnlyList<LeadLineCommercialResolution>> ResolveLeadAsync(
        long businessUnitId, long leadId, int resourceLimit, CancellationToken ct = default);
    Task LinkRfqAsync(long businessUnitId, long leadId, long rfqId, CancellationToken ct = default);
}

public sealed class CommercialLineResolutionApplicationService(
    ErpRfqAutomationContext db,
    IProductItemResolver productResolver,
    ILeadLineCommercialResolutionService commercialResolver)
    : ICommercialLineResolutionApplicationService
{
    private static readonly HashSet<int> SupportedLimits = [10, 20, 50];
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<LeadLineCommercialResolution>> ResolveLeadAsync(
        long businessUnitId, long leadId, int resourceLimit, CancellationToken ct = default)
    {
        if (businessUnitId <= 0 || leadId <= 0) throw new ArgumentOutOfRangeException(nameof(leadId));
        if (!SupportedLimits.Contains(resourceLimit))
            throw new ArgumentOutOfRangeException(nameof(resourceLimit), "Resource limit must be 10, 20, or 50.");

        var lead = await db.Leads.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
            ?? throw new KeyNotFoundException("Lead was not found in this tenant.");
        if (!lead.CurrentRevisionId.HasValue)
            throw new InvalidOperationException("The lead has no immutable current revision.");

        var revision = await db.Set<LeadRevision>().AsNoTracking().Include(x => x.Items)
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == lead.CurrentRevisionId.Value, ct);
        var existing = await db.Set<LeadLineCommercialResolution>()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == revision.Id)
            .OrderBy(x => x.LeadLineId).ToListAsync(ct);
        if (existing.Count == revision.Items.Count)
            return existing.Select(Hydrate).ToArray();

        foreach (var line in revision.Items.OrderBy(x => x.LineNumber))
        {
            if (existing.Any(x => x.LeadLineId == line.Id)) continue;
            var snapshot = ParseSnapshot(line.SnapshotJson);
            var requestedPart = First(snapshot.Part, snapshot.MaterialCode, snapshot.Description, $"LINE-{line.LineNumber}");
            var quantity = snapshot.Quantity > 0m ? snapshot.Quantity : 1m;
            var product = await productResolver.ResolveAsync(new ProductResolutionRequest(
                businessUnitId, revision.Id, line.Id, requestedPart, snapshot.Manufacturer,
                snapshot.Description, [new("lead_revision_line", $"lead-revision:{revision.Id}:line:{line.Id}", requestedPart)]), ct);
            var productId = product.DecisionState == ProductResolutionDecisionState.AutoLinked
                ? product.ResolvedProductId : null;
            var inventory = productId.HasValue
                ? await InventorySnapshotsAsync(businessUnitId, productId.Value, ct) : [];
            var incoming = productId.HasValue
                ? await db.IncomingInventory.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                    && x.ProductId == productId.Value).ToListAsync(ct) : [];
            var resolved = await commercialResolver.ResolveAsync(new CommercialResolutionRequest(
                businessUnitId, leadId, revision.Id, line.Id, requestedPart, quantity, productId,
                product.DecisionState == ProductResolutionDecisionState.ReviewRequired,
                inventory, incoming, resourceLimit), ct);
            resolved.ProductResolutionJson = JsonSerializer.Serialize(product, EvidenceJson);
            resolved.FulfilmentJson = JsonSerializer.Serialize(resolved.Fulfilment, EvidenceJson);
            resolved.RelatedResourcesJson = JsonSerializer.Serialize(resolved.RelatedResources, EvidenceJson);
            resolved.EvidenceReference = $"lead-revision:{revision.Id}:line:{line.Id}";
            resolved.InventoryAsOfUtc = inventory.Count == 0 ? resolved.ResolvedOn : inventory.Max(x => x.AsOf);
            db.Add(resolved); existing.Add(resolved);
        }
        await db.SaveChangesAsync(ct);
        return existing.OrderBy(x => x.LeadLineId).Select(Hydrate).ToArray();
    }

    public async Task LinkRfqAsync(long businessUnitId, long leadId, long rfqId, CancellationToken ct = default)
    {
        var rows = await db.Set<LeadLineCommercialResolution>().Where(x => x.BusinessUnitId == businessUnitId
            && x.LeadId == leadId && x.RfqId == null).ToListAsync(ct);
        rows.ForEach(x => x.RfqId = rfqId);
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyCollection<InventorySnapshot>> InventorySnapshotsAsync(
        long businessUnitId, long productId, CancellationToken ct)
    {
        var stocks = await (from stock in db.Set<Models.Inventory>().AsNoTracking()
            join warehouse in db.Set<Warehouse>().AsNoTracking() on stock.WarehouseId equals warehouse.Id
            where stock.Buid == businessUnitId && stock.ProductId == productId
                && warehouse.BusinessUnitId == businessUnitId
            select new { stock, warehouse }).ToListAsync(ct);
        var ids = stocks.Select(x => x.stock.Id).ToArray();
        var reserved = await db.Set<StockReservation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && ids.Contains(x.InventoryId)
                && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId).Select(x => new { Id = x.Key, Qty = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Qty, ct);
        return stocks.Select(x => new InventorySnapshot
        {
            BusinessUnitId = businessUnitId, ProductId = productId, InventoryId = x.stock.Id,
            WarehouseId = x.warehouse.Id, WarehouseCode = x.warehouse.WarehouseCode,
            OnHand = x.stock.QtyOnHand, Reserved = reserved.GetValueOrDefault(x.stock.Id),
            Allocated = x.stock.AllocatedQuantity, Quarantine = x.stock.QuarantineQuantity,
            Damaged = x.stock.DamagedQuantity, Expired = x.stock.ExpiredQuantity,
            SafetyStock = x.stock.SafetyStockQuantity,
            AsOf = x.stock.ModifiedOn ?? x.stock.CreatedOn
        }).ToArray();
    }

    private static LeadLineCommercialResolution Hydrate(LeadLineCommercialResolution row)
    {
        row.Fulfilment = JsonSerializer.Deserialize<FulfilmentRoute>(row.FulfilmentJson, EvidenceJson) ?? new();
        row.RelatedResources = JsonSerializer.Deserialize<RelatedResource[]>(row.RelatedResourcesJson, EvidenceJson) ?? [];
        return row;
    }

    private static LineSnapshot ParseSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Text(params string[] names)
        {
            foreach (var name in names)
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            return null;
        }
        decimal Number(params string[] names)
        {
            foreach (var name in names)
                if (root.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)) return number;
            return 0m;
        }
        return new(Text("part", "ManufacturerPartNumber", "manufacturerPartNumber"),
            Text("material", "ItemMaterialCode", "itemMaterialCode"),
            Text("manufacturer", "ManufacturerName", "manufacturerName"),
            Text("description", "ProductShortDescription", "productShortDescription", "ItemText", "itemText"),
            Number("quantity", "Quantity"));
    }

    private static string First(params string?[] values) =>
        values.First(x => !string.IsNullOrWhiteSpace(x))!.Trim();
    private sealed record LineSnapshot(string? Part, string? MaterialCode, string? Manufacturer,
        string? Description, decimal Quantity);
}

public sealed class EfLocalRelatedResourceRepository(ErpRfqAutomationContext db)
    : ILocalRelatedResourceRepository
{
    public async Task<IReadOnlyList<RelatedResource>> SearchAsync(long businessUnitId,
        string normalizedPartNumber, long? productId, CancellationToken ct = default)
    {
        var aliases = await db.ProductAliases.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive
                && (x.ProductId == productId || x.NormalizedValue == normalizedPartNumber))
            .Select(x => new RelatedResource { ResourceId = $"alias:{x.Id}", BusinessUnitId = businessUnitId,
                Kind = RelatedResourceKind.ProductAlias, ProductId = x.ProductId, DisplayName = x.Value,
                MatchReason = "Approved tenant product alias", Score = x.NormalizedValue == normalizedPartNumber ? 1m : .8m,
                EvidenceReference = $"product-alias:{x.Id}" }).ToListAsync(ct);
        var history = await db.SupplierQuotedItems.AsNoTracking().Include(x => x.Supplier)
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive && x.ItemName != null
                && (EF.Functions.ILike(x.ItemName, normalizedPartNumber) || EF.Functions.ILike(x.ItemName, $"%{normalizedPartNumber}%")))
            .OrderByDescending(x => x.QuoteDate).Take(100)
            .Select(x => new RelatedResource { ResourceId = $"supplier-quote:{x.Id}", BusinessUnitId = businessUnitId,
                Kind = RelatedResourceKind.SupplierQuoteHistory, SupplierId = x.SupplierId,
                DisplayName = x.Supplier.Name, MatchReason = "Tenant-local supplier quote history",
                Score = .7m, EvidenceReference = $"supplier-quoted-item:{x.Id}" }).ToListAsync(ct);
        return aliases.Concat(history).ToArray();
    }
}

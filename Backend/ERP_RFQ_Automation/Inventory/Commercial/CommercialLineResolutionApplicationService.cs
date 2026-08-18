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
        long businessUnitId, long leadId, int resourceLimit, CancellationToken ct = default,
        bool forceRefresh = true);
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
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<LeadLineCommercialResolution>> ResolveLeadAsync(
        long businessUnitId, long leadId, int resourceLimit, CancellationToken ct = default,
        bool forceRefresh = true)
    {
        if (businessUnitId <= 0 || leadId <= 0) throw new ArgumentOutOfRangeException(nameof(leadId));
        if (!SupportedLimits.Contains(resourceLimit))
            throw new ArgumentOutOfRangeException(nameof(resourceLimit), "Resource limit must be 10, 20, or 50.");

        if (db.Database.CurrentTransaction is not null)
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({$"commercial-resolution:{businessUnitId}:{leadId}"}, 0))", ct);
            return await ResolveLeadCoreAsync(businessUnitId, leadId, resourceLimit, forceRefresh, ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({$"commercial-resolution:{businessUnitId}:{leadId}"}, 0))", ct);
            var result = await ResolveLeadCoreAsync(businessUnitId, leadId, resourceLimit, forceRefresh, ct);
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task<IReadOnlyList<LeadLineCommercialResolution>> ResolveLeadCoreAsync(
        long businessUnitId, long leadId, int resourceLimit, bool forceRefresh, CancellationToken ct)
    {

        var lead = await db.Leads.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
            ?? throw new KeyNotFoundException("Lead was not found in this tenant.");
        if (!lead.CurrentRevisionId.HasValue)
            throw new InvalidOperationException("The lead has no immutable current revision.");

        var revision = await db.Set<LeadRevision>().AsNoTracking().Include(x => x.Items)
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == lead.CurrentRevisionId.Value, ct);
        var existing = await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == revision.Id)
            .OrderBy(x => x.LeadLineId).ToListAsync(ct);
        var latest = Latest(existing);
        if (!forceRefresh && latest.Count == revision.Items.Count && latest.All(x => x.ResourceLimit >= resourceLimit))
            return latest.Select(Hydrate).ToArray();

        var batchId = Guid.NewGuid();

        foreach (var line in revision.Items.OrderBy(x => x.LineNumber))
        {
            var prior = latest.FirstOrDefault(x => x.LeadLineId == line.Id);
            if (!forceRefresh && prior is not null && prior.ResourceLimit >= resourceLimit) continue;
            var snapshot = ParseSnapshot(line.SnapshotJson);
            var requestedPart = First(snapshot.Part, snapshot.MaterialCode, snapshot.Description, $"LINE-{line.LineNumber}");
            var quantity = snapshot.Quantity > 0m ? snapshot.Quantity : 1m;
            if (IsServiceLine(snapshot))
            {
                var serviceResolution = new LeadLineCommercialResolution
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = leadId,
                    LeadRevisionId = revision.Id,
                    LeadLineId = line.Id,
                    ResolutionBatchId = batchId,
                    ResourceLimit = resourceLimit,
                    RequestedPartNumber = requestedPart,
                    RequestedQuantity = quantity,
                    Classification = CommercialResolutionClassification.NonInventoryService,
                    AvailableToPromise = 0m,
                    IncomingAvailable = 0m,
                    Fulfilment = new FulfilmentRoute
                    {
                        Classification = FulfilmentRouteClassification.NoStock,
                        RequestedQuantity = quantity,
                        ShortageQuantity = 0m,
                    },
                    ProductResolutionJson = JsonSerializer.Serialize(new
                    {
                        decisionState = "NonInventoryService",
                        method = "LocalDeterministicServiceClassification",
                    }),
                    RelatedResourcesJson = "[]",
                    ResolutionMethod = "LocalDeterministicServiceClassification",
                    EvidenceReference = $"lead-revision:{revision.Id}:line:{line.Id}",
                    InventoryAsOfUtc = DateTime.UtcNow,
                    ResolvedOn = DateTime.UtcNow,
                };
                serviceResolution.FulfilmentJson = JsonSerializer.Serialize(serviceResolution.Fulfilment, EvidenceJson);
                db.Add(serviceResolution);
                existing.Add(serviceResolution);
                continue;
            }
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
            var productEvidence = productId.HasValue
                ? await db.Products.AsNoTracking()
                    .Where(x => x.Buid == businessUnitId && x.Id == productId.Value)
                    .Select(x => new { x.LeadTime, x.UnitCost }).SingleAsync(ct)
                : null;
            var baseCurrencies = productEvidence?.UnitCost is not null
                ? await db.Currencies.AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive == true && x.IsBaseCurrency == true)
                    .OrderBy(x => x.Id).Take(2).Select(x => x.Code).ToArrayAsync(ct)
                : [];
            var evidencedCurrency = baseCurrencies.Length == 1 ? baseCurrencies[0] : null;
            var resolved = await commercialResolver.ResolveAsync(new CommercialResolutionRequest(
                businessUnitId, leadId, revision.Id, line.Id, requestedPart, quantity, productId,
                product.DecisionState == ProductResolutionDecisionState.ReviewRequired,
                inventory, incoming, resourceLimit, productEvidence?.LeadTime,
                evidencedCurrency is null ? null : productEvidence?.UnitCost, evidencedCurrency), ct);
            resolved.ProductResolutionJson = JsonSerializer.Serialize(product, EvidenceJson);
            resolved.ResolutionBatchId = batchId;
            resolved.ResourceLimit = resourceLimit;
            resolved.FulfilmentJson = JsonSerializer.Serialize(resolved.Fulfilment, EvidenceJson);
            resolved.RelatedResourcesJson = JsonSerializer.Serialize(resolved.RelatedResources, EvidenceJson);
            resolved.EvidenceReference = $"lead-revision:{revision.Id}:line:{line.Id}";
            resolved.InventoryAsOfUtc = inventory.Count == 0 ? resolved.ResolvedOn : inventory.Max(x => x.AsOf);
            db.Add(resolved); existing.Add(resolved);
        }
        await db.SaveChangesAsync(ct);
        return Latest(existing).Select(Hydrate).ToArray();
    }

    public async Task LinkRfqAsync(long businessUnitId, long leadId, long rfqId, CancellationToken ct = default)
    {
        var validRfq = await db.Rfqs.AsNoTracking().AnyAsync(x => x.BusinessUnitId == businessUnitId
            && x.Id == rfqId && x.LeadId == leadId, ct);
        if (!validRfq) throw new KeyNotFoundException("The RFQ does not belong to this tenant and lead.");
        var rows = await db.Set<LeadLineCommercialResolution>().Where(x => x.BusinessUnitId == businessUnitId
            && x.LeadId == leadId && x.RfqId == null).ToListAsync(ct);
        var revisionLineNumbers = await db.Set<LeadItemRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rows.Select(r => r.LeadLineId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.LineNumber, ct);
        var rfqItems = await db.Rfqitems.AsNoTracking().Where(x => x.Rfqid == rfqId)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var unassigned = new HashSet<long>(rfqItems.Select(x => x.Id));
        foreach (var row in rows.OrderBy(x => revisionLineNumbers.GetValueOrDefault(x.LeadLineId)).ThenBy(x => x.LeadLineId))
        {
            var lineNumber = revisionLineNumbers.GetValueOrDefault(row.LeadLineId);
            var exact = rfqItems.FirstOrDefault(x => unassigned.Contains(x.Id)
                && int.TryParse(x.LineItemNo, out var rfqLineNumber) && rfqLineNumber == lineNumber);
            exact ??= rfqItems.FirstOrDefault(x => unassigned.Contains(x.Id)
                && x.ProductId.HasValue && x.ProductId == row.ProductId);
            exact ??= rfqItems.FirstOrDefault(x => unassigned.Contains(x.Id)
                && PartIdentity(x) == CommercialInventoryNormalization.PartNumber(row.RequestedPartNumber));
            if (exact is null && unassigned.Count == rows.Count(r => r.RfqItemId is null))
                exact = rfqItems.FirstOrDefault(x => unassigned.Contains(x.Id));
            // RfqId and RfqItemId must be written TOGETHER or not at all.
            //
            // nexora_guard_commercial_line_resolution_update permits exactly one mutation of an
            // otherwise-immutable row: both columns moving NULL -> NOT NULL in the same update.
            // This used to set RfqId before searching for a matching RFQ line, so a resolution
            // with no match — the normal outcome for a line the operator EXCLUDED from the RFQ —
            // was left with RfqId set and RfqItemId null. The guard then raised
            // "commercial line resolutions are immutable" (P0001) and failed the entire
            // conversion with a 500. Every partial conversion hit this.
            if (exact is null) continue;
            row.RfqId = rfqId;
            row.RfqItemId = exact.Id;
            unassigned.Remove(exact.Id);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string PartIdentity(Rfqitem item)
    {
        var value = First(item.ManufacturerPartNumber, item.ItemMaterialCode, item.ProductShortName,
            item.ProductShortDescription, $"LINE-{item.Id}");
        return CommercialInventoryNormalization.PartNumber(value);
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

    private static List<LeadLineCommercialResolution> Latest(IEnumerable<LeadLineCommercialResolution> rows) => rows
        .GroupBy(x => x.LeadLineId)
        .Select(group => group.OrderByDescending(x => x.ResolvedOn).ThenByDescending(x => x.Id).First())
        .OrderBy(x => x.LeadLineId)
        .ToList();

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
                // ValueKind IS THE GUARD. JsonElement.TryGetDecimal does not return false for an
                // element that is not a number — it THROWS InvalidOperationException. The Try
                // prefix covers a value that will not fit a decimal, nothing more. Text() above
                // gets this right; this did not, and the asymmetry is the whole defect.
                //
                // A line whose quantity the document did not state is serialised as
                // "quantity": null — deliberately, because the extractor is instructed to
                // return null rather than invent a number, and a line a reviewer can correct is
                // better than one that never existed. Reading it threw, ResolveLead mapped
                // InvalidOperationException to 409, and the lead screen showed the raw serializer
                // sentence "The requested operation requires an element of type 'Number', but the
                // target element has type 'Null'." ONE unquantified line took out supplier
                // resolution for the ENTIRE lead — observed on lead 470 in production.
                if (root.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDecimal(out var number)) return number;
            // Zero is the honest answer for "not stated", and the caller already treats it as
            // such: `snapshot.Quantity > 0m ? snapshot.Quantity : 1m`.
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
    private static bool IsServiceLine(LineSnapshot snapshot)
    {
        var identity = string.Join(' ', new[] { snapshot.Part, snapshot.MaterialCode, snapshot.Description }
            .Where(value => !string.IsNullOrWhiteSpace(value))).ToUpperInvariant();
        return identity.Contains("SERVICE", StringComparison.Ordinal)
            || identity.Contains("LABOR", StringComparison.Ordinal)
            || identity.Contains("LABOUR", StringComparison.Ordinal)
            || identity.Contains("INSTALLATION", StringComparison.Ordinal)
            || identity.Contains("NON-INVENTORY", StringComparison.Ordinal);
    }
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

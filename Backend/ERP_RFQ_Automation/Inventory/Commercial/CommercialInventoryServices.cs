using System.Collections.Concurrent;

namespace ERP_RFQ_Automation.Inventory.Commercial;

public static class CommercialInventoryNormalization
{
    public static string PartNumber(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }
}

public sealed class ProductIdentityResolver : IProductIdentityResolver
{
    public ProductIdentityResolution Resolve(
        long businessUnitId,
        string identifier,
        IReadOnlyCollection<ProductAlias> aliases,
        IReadOnlyCollection<ProductSupersession> supersessions,
        DateOnly asOf)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        var normalized = CommercialInventoryNormalization.PartNumber(identifier);
        var matches = aliases
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive && x.NormalizedValue == normalized)
            .Select(x => x.ProductId)
            .Distinct()
            .ToArray();

        if (matches.Length == 0)
            return new(businessUnitId, identifier, null, false, [], "NoLocalIdentityMatch");
        if (matches.Length > 1)
            return new(businessUnitId, identifier, null, false, [], "PossibleMatchReview");

        var path = new List<long> { matches[0] };
        var visited = new HashSet<long>(path);
        var current = matches[0];
        while (true)
        {
            var replacements = supersessions
                .Where(x => x.BusinessUnitId == businessUnitId
                            && x.SupersededProductId == current
                            && x.EffectiveOn <= asOf
                            && (x.EndsOn == null || x.EndsOn >= asOf))
                .Select(x => x.ReplacementProductId)
                .Distinct()
                .ToArray();

            if (replacements.Length == 0) break;
            if (replacements.Length > 1)
                return new(businessUnitId, identifier, null, true, path, "PossibleMatchReview");
            if (!visited.Add(replacements[0]))
                throw new InvalidOperationException("Product supersession cycle detected.");

            current = replacements[0];
            path.Add(current);
        }

        return new(businessUnitId, identifier, current, path.Count > 1, path,
            path.Count > 1 ? "AliasAndSupersession" : "ProductAlias");
    }
}

/// <summary>A process-local ledger useful for portable workflows and tests; durable adapters implement the same append-only contract.</summary>
public sealed class InMemoryInventoryMovementLedger : IInventoryMovementLedger
{
    private readonly ConcurrentDictionary<(long Tenant, string Key), InventoryMovement> _byKey = new();

    public Task<InventoryMovement> AppendAsync(InventoryMovement movement, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate(movement);
        var stored = _byKey.GetOrAdd((movement.BusinessUnitId, movement.IdempotencyKey), movement);
        if (stored != movement)
            throw new InvalidOperationException("The idempotency key already identifies a different inventory movement.");
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<InventoryMovement>> ReadAsync(
        long businessUnitId, long inventoryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<InventoryMovement> result = _byKey.Values
            .Where(x => x.BusinessUnitId == businessUnitId && x.InventoryId == inventoryId)
            .OrderBy(x => x.OccurredOn)
            .ThenBy(x => x.Id)
            .ToArray();
        return Task.FromResult(result);
    }

    private static void Validate(InventoryMovement movement)
    {
        if (movement.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(movement.BusinessUnitId));
        if (movement.ProductId <= 0 || movement.InventoryId <= 0 || movement.WarehouseId <= 0)
            throw new ArgumentException("Product, inventory, and warehouse identities are required.");
        if (movement.Quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(movement.Quantity));
        ArgumentException.ThrowIfNullOrWhiteSpace(movement.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(movement.SourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(movement.SourceId);
    }
}

public sealed class FulfilmentRouteService : IFulfilmentRouteService
{
    public FulfilmentRoute Classify(decimal requestedQuantity, IReadOnlyCollection<InventorySnapshot> snapshots)
    {
        if (requestedQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(requestedQuantity));
        ArgumentNullException.ThrowIfNull(snapshots);

        var tenantCount = snapshots.Select(x => x.BusinessUnitId).Distinct().Take(2).Count();
        var productCount = snapshots.Select(x => x.ProductId).Distinct().Take(2).Count();
        if (tenantCount > 1 || productCount > 1)
            throw new ArgumentException("All inventory snapshots must belong to one tenant and one product.");

        var remaining = requestedQuantity;
        var allocations = new List<WarehouseFulfilmentAllocation>();
        foreach (var snapshot in snapshots
                     .Where(x => x.AvailableToPromise > 0m)
                     .OrderByDescending(x => x.AvailableToPromise)
                     .ThenBy(x => x.WarehouseId)
                     .ThenBy(x => x.InventoryId))
        {
            var quantity = Math.Min(remaining, snapshot.AvailableToPromise);
            allocations.Add(new(snapshot.WarehouseId, snapshot.InventoryId, snapshot.WarehouseCode,
                quantity, snapshot.AvailableToPromise));
            remaining -= quantity;
            if (remaining == 0m) break;
        }

        var allocated = requestedQuantity - remaining;
        var classification = allocated == 0m
            ? FulfilmentRouteClassification.NoStock
            : remaining > 0m
                ? FulfilmentRouteClassification.PartialStock
                : allocations.Count == 1
                    ? FulfilmentRouteClassification.SingleWarehouse
                    : FulfilmentRouteClassification.MultipleWarehouses;

        return new FulfilmentRoute
        {
            Classification = classification,
            RequestedQuantity = requestedQuantity,
            AllocatedQuantity = allocated,
            ShortageQuantity = remaining,
            Allocations = allocations,
        };
    }
}

public sealed class LocalRelatedResourceSearch(ILocalRelatedResourceRepository repository)
    : ILocalRelatedResourceSearch
{
    private static readonly HashSet<int> SupportedLimits = [10, 20, 50];

    public async Task<IReadOnlyList<RelatedResource>> SearchAsync(
        RelatedResourceQuery query, CancellationToken ct = default)
    {
        if (query.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(query.BusinessUnitId));
        if (!SupportedLimits.Contains(query.Limit))
            throw new ArgumentOutOfRangeException(nameof(query.Limit), "Related-resource limit must be 10, 20, or 50.");

        var normalized = CommercialInventoryNormalization.PartNumber(query.PartNumber);
        var local = await repository.SearchAsync(query.BusinessUnitId, normalized, query.ProductId, ct);
        return local
            .Where(x => x.BusinessUnitId == query.BusinessUnitId)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.ResourceId, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToArray();
    }
}

public sealed class LeadLineCommercialResolutionService(
    IFulfilmentRouteService fulfilment,
    ILocalRelatedResourceSearch resources) : ILeadLineCommercialResolutionService
{
    public async Task<LeadLineCommercialResolution> ResolveAsync(
        CommercialResolutionRequest request, CancellationToken ct = default)
    {
        if (request.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(request.BusinessUnitId));
        if (request.RequestedQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(request.RequestedQuantity));
        if (request.Inventory.Any(x => x.BusinessUnitId != request.BusinessUnitId)
            || request.Incoming.Any(x => x.BusinessUnitId != request.BusinessUnitId))
            throw new ArgumentException("Commercial evidence must be tenant-qualified.");
        if (request.ProductId is not null
            && (request.Inventory.Any(x => x.ProductId != request.ProductId)
                || request.Incoming.Any(x => x.ProductId != request.ProductId)))
            throw new ArgumentException("Commercial evidence must belong to the resolved product.");

        var route = fulfilment.Classify(request.RequestedQuantity, request.Inventory);
        var incoming = request.Incoming.Sum(x => x.OpenQuantity);
        var related = Array.Empty<RelatedResource>();
        CommercialResolutionClassification classification;

        if (request.RequiresPossibleMatchReview)
        {
            classification = CommercialResolutionClassification.PossibleMatchReview;
        }
        else if (request.ProductId is null)
        {
            classification = CommercialResolutionClassification.UnknownProduct;
            related = (await resources.SearchAsync(new RelatedResourceQuery(
                request.BusinessUnitId, request.RequestedPartNumber, null, request.RelatedResourceLimit), ct)).ToArray();
        }
        else if (route.ShortageQuantity == 0m)
        {
            classification = CommercialResolutionClassification.KnownInStock;
        }
        else if (incoming > 0m)
        {
            classification = CommercialResolutionClassification.KnownIncoming;
        }
        else
        {
            classification = CommercialResolutionClassification.KnownShortage;
            related = (await resources.SearchAsync(new RelatedResourceQuery(
                request.BusinessUnitId, request.RequestedPartNumber, request.ProductId, request.RelatedResourceLimit), ct)).ToArray();
        }

        return new LeadLineCommercialResolution
        {
            BusinessUnitId = request.BusinessUnitId,
            LeadId = request.LeadId,
            LeadRevisionId = request.LeadRevisionId,
            LeadLineId = request.LeadLineId,
            ProductId = request.ProductId,
            RequestedPartNumber = request.RequestedPartNumber,
            RequestedQuantity = request.RequestedQuantity,
            Classification = classification,
            AvailableToPromise = request.Inventory.Sum(x => x.AvailableToPromise),
            IncomingAvailable = incoming,
            Fulfilment = route,
            RelatedResources = related,
            ResolutionMethod = "LocalDeterministicCommercialInventory",
            ResolvedOn = DateTime.UtcNow,
        };
    }
}

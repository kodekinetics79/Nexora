namespace ERP_RFQ_Automation.Inventory.Commercial;

public sealed record ProductIdentityResolution(
    long BusinessUnitId,
    string RequestedIdentifier,
    long? ProductId,
    bool IsSuperseded,
    IReadOnlyList<long> SupersessionPath,
    string Method);

public interface IProductIdentityResolver
{
    ProductIdentityResolution Resolve(
        long businessUnitId,
        string identifier,
        IReadOnlyCollection<ProductAlias> aliases,
        IReadOnlyCollection<ProductSupersession> supersessions,
        DateOnly asOf);
}

public interface IInventoryMovementLedger
{
    Task<InventoryMovement> AppendAsync(InventoryMovement movement, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryMovement>> ReadAsync(
        long businessUnitId, long inventoryId, CancellationToken ct = default);
}

public interface IFulfilmentRouteService
{
    FulfilmentRoute Classify(decimal requestedQuantity, IReadOnlyCollection<InventorySnapshot> snapshots);
}

public sealed record RelatedResourceQuery(
    long BusinessUnitId,
    string PartNumber,
    long? ProductId,
    int Limit);

public interface ILocalRelatedResourceRepository
{
    Task<IReadOnlyList<RelatedResource>> SearchAsync(
        long businessUnitId,
        string normalizedPartNumber,
        long? productId,
        CancellationToken ct = default);
}

public interface ILocalRelatedResourceSearch
{
    Task<IReadOnlyList<RelatedResource>> SearchAsync(RelatedResourceQuery query, CancellationToken ct = default);
}

public sealed record CommercialResolutionRequest(
    long BusinessUnitId,
    long LeadId,
    long LeadRevisionId,
    long LeadLineId,
    string RequestedPartNumber,
    decimal RequestedQuantity,
    long? ProductId,
    bool RequiresPossibleMatchReview,
    IReadOnlyCollection<InventorySnapshot> Inventory,
    IReadOnlyCollection<IncomingInventory> Incoming,
    int RelatedResourceLimit = 10);

public interface ILeadLineCommercialResolutionService
{
    Task<LeadLineCommercialResolution> ResolveAsync(
        CommercialResolutionRequest request, CancellationToken ct = default);
}

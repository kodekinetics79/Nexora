namespace ERP_RFQ_Automation.Inventory.Commercial;

public enum ProductAliasKind
{
    ManufacturerPartNumber,
    CustomerPartNumber,
    SupplierPartNumber,
    LegacyPartNumber,
    Barcode,
}

public sealed record ProductAlias
{
    public long Id { get; init; }
    public long BusinessUnitId { get; init; }
    public long ProductId { get; init; }
    public ProductAliasKind Kind { get; init; }
    public required string Value { get; init; }
    public required string NormalizedValue { get; init; }
    public long? AccountId { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime CreatedOn { get; init; }
    public required string CreatedBy { get; init; }
}

public enum ProductSupersessionKind
{
    DirectReplacement,
    FormFitFunctionReplacement,
    ManufacturerRecommended,
}

public sealed record ProductSupersession
{
    public long Id { get; init; }
    public long BusinessUnitId { get; init; }
    public long SupersededProductId { get; init; }
    public long ReplacementProductId { get; init; }
    public ProductSupersessionKind Kind { get; init; }
    public DateOnly EffectiveOn { get; init; }
    public DateOnly? EndsOn { get; init; }
    public string? EvidenceReference { get; init; }
    public DateTime CreatedOn { get; init; }
    public required string CreatedBy { get; init; }
}

public enum InventoryMovementType
{
    Receipt,
    Issue,
    TransferIn,
    TransferOut,
    AdjustmentIncrease,
    AdjustmentDecrease,
    Quarantine,
    QuarantineRelease,
    Damage,
    Expiration,
    ReturnReceipt,
}

/// <summary>An immutable fact. Corrections are represented by a new compensating movement.</summary>
public sealed record InventoryMovement
{
    public long Id { get; init; }
    public long BusinessUnitId { get; init; }
    public long ProductId { get; init; }
    public long InventoryId { get; init; }
    public long WarehouseId { get; init; }
    public InventoryMovementType Type { get; init; }
    public decimal Quantity { get; init; }
    public DateTime OccurredOn { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string SourceType { get; init; }
    public required string SourceId { get; init; }
    public string? Reason { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedOn { get; init; }
}

public enum IncomingInventoryStatus
{
    Planned,
    Ordered,
    Confirmed,
    InTransit,
    PartiallyReceived,
    Received,
    Cancelled,
}

public sealed record IncomingInventory
{
    public long Id { get; init; }
    public long BusinessUnitId { get; init; }
    public long ProductId { get; init; }
    public long? InventoryId { get; init; }
    public long WarehouseId { get; init; }
    public decimal OrderedQuantity { get; init; }
    public decimal ReceivedQuantity { get; init; }
    public decimal AllocatedQuantity { get; init; }
    public DateOnly ExpectedOn { get; init; }
    public IncomingInventoryStatus Status { get; init; }
    public required string SourceType { get; init; }
    public required string SourceId { get; init; }

    public decimal OpenQuantity
    {
        get
        {
            if (OrderedQuantity < 0m || ReceivedQuantity < 0m || AllocatedQuantity < 0m)
                throw new InvalidOperationException("Incoming inventory quantities cannot be negative.");
            return Status is IncomingInventoryStatus.Received or IncomingInventoryStatus.Cancelled
                ? 0m
                : Math.Max(0m, OrderedQuantity - ReceivedQuantity - AllocatedQuantity);
        }
    }
}

public sealed record InventorySnapshot
{
    public long BusinessUnitId { get; init; }
    public long ProductId { get; init; }
    public long InventoryId { get; init; }
    public long WarehouseId { get; init; }
    public required string WarehouseCode { get; init; }
    public decimal OnHand { get; init; }
    public decimal Reserved { get; init; }
    public decimal Allocated { get; init; }
    public decimal Quarantine { get; init; }
    public decimal Damaged { get; init; }
    public decimal Expired { get; init; }
    public decimal SafetyStock { get; init; }
    public DateTime AsOf { get; init; }

    /// <summary>
    /// Available-to-promise. The negative-quantity guard stays here — a snapshot built from
    /// corrupt inputs must fail loudly rather than be clamped into a plausible-looking zero — but
    /// the arithmetic itself delegates to <see cref="InventoryQuantityMath.AvailableToPromise"/>.
    /// It used to be spelled out again in this getter, which is how the two copies would have
    /// diverged the first time a bucket was added on one side only.
    /// </summary>
    public decimal AvailableToPromise
    {
        get
        {
            if (OnHand < 0m || Reserved < 0m || Allocated < 0m || Quarantine < 0m
                || Damaged < 0m || Expired < 0m || SafetyStock < 0m)
                throw new InvalidOperationException("Inventory snapshot quantities cannot be negative.");
            return InventoryQuantityMath.AvailableToPromise(
                OnHand, Reserved, Allocated, Quarantine, Damaged, Expired, SafetyStock);
        }
    }
}

public enum CommercialResolutionClassification
{
    KnownInStock,
    KnownIncoming,
    KnownShortage,
    UnknownProduct,
    PossibleMatchReview,
    NonInventoryService,
}

public sealed class LeadLineCommercialResolution
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long LeadRevisionId { get; set; }
    public long LeadLineId { get; set; }
    public long? RfqId { get; set; }
    public long? RfqItemId { get; set; }
    public long? ProductId { get; set; }
    public Guid ResolutionBatchId { get; set; }
    public int ResourceLimit { get; set; }
    public string RequestedPartNumber { get; set; } = string.Empty;
    public decimal RequestedQuantity { get; set; }
    public CommercialResolutionClassification Classification { get; set; }
    public decimal AvailableToPromise { get; set; }
    public decimal IncomingAvailable { get; set; }
    public decimal ProjectedShortage { get; set; }
    public int? LeadTimeDays { get; set; }
    public DateOnly? ExpectedAvailableOn { get; set; }
    public decimal? UnitCost { get; set; }
    public string? CostCurrencyCode { get; set; }
    public string FulfilmentJson { get; set; } = "{}";
    public string RelatedResourcesJson { get; set; } = "[]";
    public string ProductResolutionJson { get; set; } = "{}";
    public string ResolutionMethod { get; set; } = string.Empty;
    public string? EvidenceReference { get; set; }
    public DateTime InventoryAsOfUtc { get; set; }
    public DateTime ResolvedOn { get; set; }
    public FulfilmentRoute Fulfilment { get; set; } = new();
    public IReadOnlyList<RelatedResource> RelatedResources { get; set; } = [];
}

public enum RelatedResourceKind
{
    ApprovedSupplier,
    SupplierQuoteHistory,
    PurchaseHistory,
    CatalogProduct,
    ProductAlias,
    ProductSupersession,
}

public sealed record RelatedResource
{
    public required string ResourceId { get; init; }
    public long BusinessUnitId { get; init; }
    public RelatedResourceKind Kind { get; init; }
    public long? ProductId { get; init; }
    public long? SupplierId { get; init; }
    public required string DisplayName { get; init; }
    public required string MatchReason { get; init; }
    public decimal Score { get; init; }
    public required string EvidenceReference { get; init; }
}

public enum FulfilmentRouteClassification
{
    NoStock,
    SingleWarehouse,
    MultipleWarehouses,
    PartialStock,
}

public sealed record WarehouseFulfilmentAllocation(
    long WarehouseId,
    long InventoryId,
    string WarehouseCode,
    decimal Quantity,
    decimal AvailableBeforeAllocation);

public sealed record FulfilmentRoute
{
    public FulfilmentRouteClassification Classification { get; init; }
    public decimal RequestedQuantity { get; init; }
    public decimal AllocatedQuantity { get; init; }
    public decimal ShortageQuantity { get; init; }
    public IReadOnlyList<WarehouseFulfilmentAllocation> Allocations { get; init; } = [];
}

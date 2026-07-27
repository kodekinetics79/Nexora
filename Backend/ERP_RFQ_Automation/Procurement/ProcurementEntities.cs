namespace ERP_RFQ_Automation.Procurement;

public static class SourcingCaseStatuses
{
    public const string Draft = "DRAFT";
    public const string InternalSearch = "INTERNAL_SEARCH";
    public const string DiscoveryRequired = "DISCOVERY_REQUIRED";
    public const string SuppliersSelected = "SUPPLIERS_SELECTED";
    public const string OutreachReady = "OUTREACH_READY";
    public const string OutreachSent = "OUTREACH_SENT";
    public const string ResponsesPartial = "RESPONSES_PARTIAL";
    public const string ResponsesComplete = "RESPONSES_COMPLETE";
    public const string ComparisonReady = "COMPARISON_READY";
    public const string Negotiation = "NEGOTIATION";
    public const string AwardReview = "AWARD_REVIEW";
    public const string SupplierSelected = "SUPPLIER_SELECTED";
    public const string CustomerQuoteReady = "CUSTOMER_QUOTE_READY";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";
}

public static class SourcingCandidateEvidenceTypes
{
    public const string PreferredSupplier = "PREFERRED_SUPPLIER";
    public const string PriorSupplierQuote = "PRIOR_SUPPLIER_QUOTE";
    public const string PurchaseHistory = "PURCHASE_HISTORY";
    public const string PurchaseOrderHistory = "PURCHASE_ORDER_HISTORY";
    public const string SupplierMetadata = "SUPPLIER_METADATA";
}

/// <summary>
/// Immutable commercial line anchor. RFQ item rows remain authoritative source rows;
/// this identity gives downstream sourcing and quote revisions a stable tenant key.
/// </summary>
public sealed class CommercialDemandLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public string IdentityKey { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
}

public sealed class SourcingCase
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialDemandLineId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public long? LeadId { get; set; }
    public long? CustomerId { get; set; }
    public long? ProductId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public string? RequestedPartNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string Description { get; set; } = null!;
    public string? UnitOfMeasure { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal StockQuantity { get; set; }
    public decimal UnfulfilledQuantity { get; set; }
    public DateTime? RequiredOn { get; set; }
    public string? DeliveryLocation { get; set; }
    public int SearchLimit { get; set; } = 10;
    public string Priority { get; set; } = "NORMAL";
    public string Status { get; set; } = SourcingCaseStatuses.Draft;
    public string NextAction { get; set; } = "Review known suppliers";
    public string ShortageDecisionKey { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;
    public ICollection<SourcingCaseCandidate> Candidates { get; set; } = new List<SourcingCaseCandidate>();
}

/// <summary>Persisted evidence snapshot. Scores describe evidence coverage, never invented supplier performance.</summary>
public sealed class SourcingCaseCandidate
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SourcingCaseId { get; set; }
    public long SupplierId { get; set; }
    public int Rank { get; set; }
    public string EvidenceType { get; set; } = null!;
    public string RecommendationReason { get; set; } = null!;
    public string EvidenceJson { get; set; } = "{}";
    public decimal EvidenceScore { get; set; }
    public DateTime? EvidenceFreshOn { get; set; }
    public bool Selected { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}

public static class SupplierPurchaseOrderStatuses
{
    public const string Draft = "DRAFT";
    public const string Issued = "ISSUED";
    public const string PartiallyReceived = "PARTIALLY_RECEIVED";
    public const string Received = "RECEIVED";
    public const string Cancelled = "CANCELLED";
}

public static class GoodsReceiptStatuses
{
    public const string Posted = "POSTED";
}

public static class ProcurementOutboxStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Sent = "SENT";
    public const string Failed = "FAILED";
    public const string DeadLettered = "DEAD_LETTERED";
}

public sealed class SupplierPurchaseOrder
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long RfqId { get; set; }
    public long SupplierId { get; set; }
    public long CurrencyId { get; set; }
    public string PurchaseOrderNumber { get; set; } = null!;
    public string Status { get; set; } = SupplierPurchaseOrderStatuses.Draft;
    public decimal TotalValue { get; set; }
    public DateOnly? ExpectedOn { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
    public ICollection<SupplierPurchaseOrderLine> Lines { get; set; } = new List<SupplierPurchaseOrderLine>();
}

public sealed class SupplierPurchaseOrderLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierPurchaseOrderId { get; set; }
    public long SourcingAwardId { get; set; }
    public long SupplierQuotedItemId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public long ProductId { get; set; }
    public long WarehouseId { get; set; }
    public long? InventoryId { get; set; }
    public long? IncomingInventoryId { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LandedUnitCost { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class GoodsReceipt
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierPurchaseOrderId { get; set; }
    public long WarehouseId { get; set; }
    public string ReceiptNumber { get; set; } = null!;
    public string Status { get; set; } = GoodsReceiptStatuses.Posted;
    public DateTime ReceivedOn { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public ICollection<GoodsReceiptLine> Lines { get; set; } = new List<GoodsReceiptLine>();
}

public sealed class GoodsReceiptLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long GoodsReceiptId { get; set; }
    public long SupplierPurchaseOrderLineId { get; set; }
    public long ProductId { get; set; }
    public long InventoryId { get; set; }
    public long WarehouseId { get; set; }
    public long InventoryMovementId { get; set; }
    public decimal ReceivedQuantity { get; set; }
}

public sealed class ProcurementEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public long AggregateVersion { get; set; }
    public string EventType { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredOn { get; set; }
}

public sealed class ProcurementOutboxMessage
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierSolicitationId { get; set; }
    public string MessageType { get; set; } = "SUPPLIER_RFQ";
    public string Status { get; set; } = ProcurementOutboxStatuses.Pending;
    public string PayloadJson { get; set; } = "{}";
    public int AttemptCount { get; set; }
    public DateTime NextAttemptOn { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? DeadLetteredOn { get; set; }
    public DateTime? SentOn { get; set; }
    public string? ProviderReference { get; set; }
    public string? ProviderName { get; set; }
    public string? OriginCorrelationId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}

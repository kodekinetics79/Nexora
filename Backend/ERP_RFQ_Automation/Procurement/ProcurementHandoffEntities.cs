namespace ERP_RFQ_Automation.Procurement;

public static class ProcurementHandoffStatuses
{
    public const string Created = "CREATED";
    public const string ExternalPoCreated = "EXTERNAL_PO_CREATED";
    public const string SupplierConfirmed = "SUPPLIER_CONFIRMED";
    public const string PartiallyReceived = "PARTIALLY_RECEIVED";
    public const string Received = "RECEIVED";
    public const string Cancelled = "CANCELLED";
}

public sealed class ProcurementHandoff
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerOrderId { get; set; }
    public long CustomerOrderLineId { get; set; }
    public long CommercialDemandLineId { get; set; }
    public long SourcingAwardId { get; set; }
    public long SupplierQuotedItemId { get; set; }
    public long SupplierId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public long CurrencyId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public decimal RequiredQuantity { get; set; }
    public decimal SelectedUnitCost { get; set; }
    public DateOnly? RequiredOn { get; set; }
    public string DestinationType { get; set; } = "DROP_SHIP";
    public long? WarehouseId { get; set; }
    public string? DeliveryLocation { get; set; }
    public string ExternalSystemTarget { get; set; } = "MANUAL";
    public string Status { get; set; } = ProcurementHandoffStatuses.Created;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string? ExternalSupplierPoNumber { get; set; }
    public string? ExternalSupplierPoLineNumber { get; set; }
    public decimal? ExternalOrderedQuantity { get; set; }
    public decimal? ExternalApprovedUnitCost { get; set; }
    public DateOnly? ExternalExpectedOn { get; set; }
    public string? ExternalStatus { get; set; }
    public DateTime? LastSynchronizedOn { get; set; }
    public string? SourceOfTruth { get; set; }
    public bool IsAuthoritative { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}

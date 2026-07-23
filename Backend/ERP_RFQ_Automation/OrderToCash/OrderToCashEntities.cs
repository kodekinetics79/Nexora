using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.OrderToCash;

public sealed class CustomerPurchaseOrder
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialCaseId { get; set; }
    public long CustomerId { get; set; }
    public string InternalNumber { get; set; } = null!;
    public string ExternalPoNumber { get; set; } = null!;
    public string NormalizedExternalPoNumber { get; set; } = null!;
    public DateTime PoDate { get; set; }
    public DateTime ReceivedOn { get; set; }
    public long CurrencyId { get; set; }
    public string Status { get; set; } = CustomerPurchaseOrderStatuses.Draft;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
    public string? CancellationReason { get; set; }

    public BusinessUnit BusinessUnit { get; set; } = null!;
    public CommercialCase CommercialCase { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public Currency Currency { get; set; } = null!;
    public ICollection<CustomerPurchaseOrderLine> Lines { get; set; } = new List<CustomerPurchaseOrderLine>();
    public ICollection<CustomerAward> Awards { get; set; } = new List<CustomerAward>();
}

public sealed class CustomerPurchaseOrderLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerPurchaseOrderId { get; set; }
    public string ExternalLineReference { get; set; } = null!;
    public long? ProductId { get; set; }
    public string Description { get; set; } = null!;
    public decimal OrderedQuantity { get; set; }
    public int? UomId { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineAmount { get; set; }
    public long Version { get; set; } = 1;

    public CustomerPurchaseOrder PurchaseOrder { get; set; } = null!;
    public Product? Product { get; set; }
    public SetUom? Uom { get; set; }
    public ICollection<CustomerAwardLineAllocation> AwardAllocations { get; set; } = new List<CustomerAwardLineAllocation>();
}

public sealed class CustomerAward
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string AwardNumber { get; set; } = null!;
    public long CustomerPurchaseOrderId { get; set; }
    public long QuoteId { get; set; }
    public long CommercialCaseId { get; set; }
    public long CustomerId { get; set; }
    public long CurrencyId { get; set; }
    public string Status { get; set; } = CustomerAwardStatuses.Draft;
    public long Version { get; set; } = 1;
    public DateTime? ConfirmedOn { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }

    public BusinessUnit BusinessUnit { get; set; } = null!;
    public CustomerPurchaseOrder PurchaseOrder { get; set; } = null!;
    public Quote Quote { get; set; } = null!;
    public CommercialCase CommercialCase { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public Currency Currency { get; set; } = null!;
    public ICollection<CustomerAwardLineAllocation> LineAllocations { get; set; } = new List<CustomerAwardLineAllocation>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

public sealed class CustomerAwardLineAllocation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerAwardId { get; set; }
    public long CustomerPurchaseOrderLineId { get; set; }
    public long QuoteItemId { get; set; }
    public decimal AwardedQuantity { get; set; }
    public decimal UnitPriceSnapshot { get; set; }
    public decimal DiscountSnapshot { get; set; }
    public decimal TaxSnapshot { get; set; }
    public decimal TotalSnapshot { get; set; }
    public long Version { get; set; } = 1;

    public CustomerAward Award { get; set; } = null!;
    public CustomerPurchaseOrderLine PurchaseOrderLine { get; set; } = null!;
    public QuoteItem QuoteItem { get; set; } = null!;
}

public sealed class OrderToCashAuditEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public long AggregateVersion { get; set; }
    public string CommandType { get; set; } = null!;
    public string? PreviousState { get; set; }
    public string NewState { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public string? Reason { get; set; }
    public string RequestHash { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string ResultJson { get; set; } = "{}";
    public string CorrelationId { get; set; } = null!;
    public DateTime OccurredOn { get; set; }

    public BusinessUnit BusinessUnit { get; set; } = null!;
}

public sealed class OrderToCashDocumentCounter
{
    public long BusinessUnitId { get; set; }
    public string DocumentType { get; set; } = null!;
    public int CalendarYear { get; set; }
    public long NextNumber { get; set; } = 1;

    public BusinessUnit BusinessUnit { get; set; } = null!;
}

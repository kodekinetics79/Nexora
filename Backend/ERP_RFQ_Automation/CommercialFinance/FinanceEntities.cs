namespace ERP_RFQ_Automation.CommercialFinance;

public static class ReceivableDocumentTypes
{
    public const string Invoice = "Invoice";
    public const string CreditNote = "CreditNote";
    public const string DebitNote = "DebitNote";
}

public static class ReceivableDocumentStatuses
{
    public const string Draft = "Draft";
    public const string Cancelled = "Cancelled";
    public const string Issued = "Issued";
    public const string Void = "Void";
}

public static class CustomerPaymentStatuses
{
    public const string Posted = "Posted";
    public const string Reversed = "Reversed";
}

public sealed class ReceivableDocument
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long? CommercialCaseId { get; set; }
    public long CustomerId { get; set; }
    public long? OrderId { get; set; }
    public long? ParentDocumentId { get; set; }
    public long? CurrencyId { get; set; }
    public string DocumentType { get; set; } = ReceivableDocumentTypes.Invoice;
    public string Status { get; set; } = ReceivableDocumentStatuses.Draft;
    public string? DocumentNumber { get; set; }
    public DateTime DocumentDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? IssuedOn { get; set; }
    public DateTime? VoidedOn { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedBy { get; set; }
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? IssuedBy { get; set; }

    public ICollection<ReceivableDocumentLine> Lines { get; set; } = new List<ReceivableDocumentLine>();
    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

public sealed class ReceivableDocumentLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ReceivableDocumentId { get; set; }
    public long? OrderItemId { get; set; }
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }

    public ReceivableDocument Document { get; set; } = null!;
}

public sealed class CustomerPayment
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CommercialCaseId { get; set; }
    public long? CurrencyId { get; set; }
    public string ReceiptNumber { get; set; } = null!;
    public string Status { get; set; } = CustomerPaymentStatuses.Posted;
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string? Method { get; set; }
    public string? BankReference { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime? ReversedOn { get; set; }
    public string? ReversalReason { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }

    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

public sealed class PaymentAllocation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerPaymentId { get; set; }
    public long ReceivableDocumentId { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedOn { get; set; }

    public CustomerPayment Payment { get; set; } = null!;
    public ReceivableDocument Document { get; set; } = null!;
}

public sealed class LegalDocumentCounter
{
    public long BusinessUnitId { get; set; }
    public string DocumentType { get; set; } = null!;
    public int FiscalYear { get; set; }
    public long NextNumber { get; set; } = 1;
}

public sealed class CommercialFinanceAudit
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public string Action { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public DateTime OccurredOn { get; set; }
    public string DetailJson { get; set; } = "{}";
}

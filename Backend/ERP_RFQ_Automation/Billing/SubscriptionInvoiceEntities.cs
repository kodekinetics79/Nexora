namespace ERP_RFQ_Automation.Billing;

public enum SubscriptionInvoiceStatus
{
    Draft,
    Finalized,
    PartiallyPaid,
    Paid,
    Void,
    Corrected
}

/// <summary>
/// Nexora's invoice to a platform tenant. This is operator-owned subscription revenue and is
/// deliberately separate from CommercialFinance invoices a tenant sends to its own customers.
/// </summary>
public sealed class SubscriptionInvoice
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long BillingStatementId { get; set; }
    public string InvoiceNumber { get; set; } = null!;
    public SubscriptionInvoiceStatus Status { get; set; } = SubscriptionInvoiceStatus.Draft;
    public string Currency { get; set; } = "USD";
    public decimal Subtotal { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal CreditedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public string SellerSnapshotJson { get; set; } = null!;
    public string BuyerSnapshotJson { get; set; } = null!;
    public string TaxTreatment { get; set; } = null!;
    public string SourceEvidenceJson { get; set; } = null!;
    public string SourceEvidenceSha256 { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public long Version { get; set; } = 1;

    public ICollection<SubscriptionCreditNote> Credits { get; set; } = [];
    public ICollection<SubscriptionPayment> Payments { get; set; } = [];
}

public sealed class SubscriptionCreditNote
{
    public long Id { get; set; }
    public long SubscriptionInvoiceId { get; set; }
    public string CreditNumber { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public SubscriptionInvoice Invoice { get; set; } = null!;
}

public sealed class SubscriptionPayment
{
    public long Id { get; set; }
    public long SubscriptionInvoiceId { get; set; }
    public string ExternalReference { get; set; } = null!;
    public decimal Amount { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public string RecordedBy { get; set; } = null!;
    public DateTime RecordedAtUtc { get; set; }
    public SubscriptionInvoice Invoice { get; set; } = null!;
}

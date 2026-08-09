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
    public decimal RefundedAmount { get; set; }
    public decimal ReversedPaymentAmount { get; set; }
    public decimal WrittenOffAmount { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public string SellerSnapshotJson { get; set; } = null!;
    public string BuyerSnapshotJson { get; set; } = null!;
    public string TaxTreatment { get; set; } = null!;
    public string? TaxJurisdictionCode { get; set; }
    public long? TaxRuleId { get; set; }
    public long? TaxRuleVersion { get; set; }
    public string? TaxEvidenceJson { get; set; }
    public string? TaxEvidenceSha256 { get; set; }
    public DateTime? TaxDeterminedAtUtc { get; set; }
    public string SourceEvidenceJson { get; set; } = null!;
    public string SourceEvidenceSha256 { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public long Version { get; set; } = 1;

    public ICollection<SubscriptionCreditNote> Credits { get; set; } = [];
    public ICollection<SubscriptionPayment> Payments { get; set; } = [];
    public ICollection<SubscriptionRevenueAction> RevenueActions { get; set; } = [];
}

public enum SubscriptionTaxRuleStatus { Draft, Approved, Retired }

/// <summary>Versioned, maker/checker-approved legal tax determination input.</summary>
public sealed class SubscriptionTaxRule
{
    public long Id { get; set; }
    public string JurisdictionCode { get; set; } = null!;
    public string BuyerCountryCode { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public string Treatment { get; set; } = null!;
    public decimal RatePercent { get; set; }
    public string LegalAuthorityReference { get; set; } = null!;
    public string EvidenceSha256 { get; set; } = null!;
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public SubscriptionTaxRuleStatus Status { get; set; }
    public long Version { get; set; } = 1;
    public long ProposedByPlatformUserId { get; set; }
    public DateTime ProposedAtUtc { get; set; }
    public long? ApprovedByPlatformUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}

public enum SubscriptionRevenueActionKind { Void, Refund, PaymentReversal, WriteOff, Dunning }
public enum SubscriptionRevenueActionStatus { Proposed, Approved, Completed, Failed }

/// <summary>Append-only operational AR action, distinct from the legal invoice document.</summary>
public sealed class SubscriptionRevenueAction
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long SubscriptionInvoiceId { get; set; }
    public SubscriptionRevenueActionKind Kind { get; set; }
    public SubscriptionRevenueActionStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string EvidenceSha256 { get; set; } = null!;
    public string? ExternalReference { get; set; }
    /// <summary>Null only for an automated system dunning occurrence.</summary>
    public long? ProposedByPlatformUserId { get; set; }
    public DateTime ProposedAtUtc { get; set; }
    public long? ApprovedByPlatformUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public SubscriptionInvoice Invoice { get; set; } = null!;
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

namespace ERP_RFQ_Automation.Billing.Accounting;

public enum AccountingOutboxStatus
{
    Pending,
    InFlight,
    RetryScheduled,
    Acknowledged,
    Poison
}

public enum AccountingReconciliationStatus
{
    NotSent,
    AwaitingAcknowledgement,
    Reconciled,
    Exception
}

/// <summary>Durable integration boundary; payload is a frozen legal-invoice projection.</summary>
public sealed class AccountingOutboxMessage
{
    public Guid Id { get; set; }
    public long TenantId { get; set; }
    public long SubscriptionInvoiceId { get; set; }
    public string MessageType { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public string PayloadSha256 { get; set; } = null!;
    public AccountingOutboxStatus Status { get; set; }
    public AccountingReconciliationStatus ReconciliationStatus { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? WorkerId { get; set; }
    public string? LastFailureCode { get; set; }
    public string? ExternalReference { get; set; }
    public string? ExternalReceiptSha256 { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? RedrivenAtUtc { get; set; }
    public string? RedrivenBy { get; set; }
    public string? RedriveReason { get; set; }
}

public sealed record AccountingOutboxHealth(
    int Pending,
    int InFlight,
    int RetryScheduled,
    int Poison,
    DateTime? OldestUnacknowledgedAtUtc);

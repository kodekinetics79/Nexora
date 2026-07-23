namespace ERP_RFQ_Automation.CommercialFinance;

public sealed class FinanceOutboxMessage
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public long AggregateVersion { get; set; }
    public string EventType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public int SchemaVersion { get; set; } = 1;
    public DateTime OccurredOn { get; set; }
    public DateTime AvailableOn { get; set; }
    public DateTime? ProcessedOn { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeadLetteredOn { get; set; }
}

public sealed record FinanceOutboxEnvelope(
    long Id,
    long BusinessUnitId,
    Guid EventId,
    string AggregateType,
    long AggregateId,
    long AggregateVersion,
    string EventType,
    string Payload,
    int SchemaVersion,
    DateTime OccurredOn,
    int AttemptCount,
    Guid LeaseToken,
    DateTime LeaseUntil);

public sealed class FinanceOutboxLeaseConflictException : InvalidOperationException
{
    public FinanceOutboxLeaseConflictException(string message) : base(message)
    {
    }
}

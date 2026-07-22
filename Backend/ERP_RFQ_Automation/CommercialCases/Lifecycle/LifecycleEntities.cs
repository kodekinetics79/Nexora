namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public sealed class CommercialLifecycleEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialCaseId { get; set; }
    public string CommercialCaseReference { get; set; } = null!;
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public string EventType { get; set; } = null!;
    public long? PreviousStatusId { get; set; }
    public string? PreviousStatusCode { get; set; }
    public long NewStatusId { get; set; }
    public string NewStatusCode { get; set; } = null!;
    public int AggregateVersion { get; set; }
    public string ActorId { get; set; } = null!;
    public string ActorSource { get; set; } = null!;
    public DateTime OccurredOn { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonNotes { get; set; }
    public string PolicyVersion { get; set; } = null!;
    public string Source { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public string RequestReference { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;

    public ERP_RFQ_Automation.Models.BusinessUnit BusinessUnit { get; set; } = null!;
    public ERP_RFQ_Automation.Models.CommercialCase CommercialCase { get; set; } = null!;
    public LifecycleOutboxMessage OutboxMessage { get; set; } = null!;
}

public sealed class LifecycleOutboxMessage
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LifecycleEventId { get; set; }
    public string EventType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public int SchemaVersion { get; set; }
    public DateTime OccurredOn { get; set; }
    public DateTime AvailableOn { get; set; }
    public DateTime? ProcessedOn { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeadLetteredOn { get; set; }

    public ERP_RFQ_Automation.Models.BusinessUnit BusinessUnit { get; set; } = null!;
    public CommercialLifecycleEvent LifecycleEvent { get; set; } = null!;
}

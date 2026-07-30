namespace ERP_RFQ_Automation.Extraction;

public enum ExtractionDeadLetterAction
{
    RetryQueued,
    SourceObjectUnavailable,
    EvidenceIntegrityFailure,
    MalwareDetected
}

/// <summary>Append-only operator evidence for one dead-letter recovery attempt.</summary>
public sealed class ExtractionDeadLetterEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ExtractionJobId { get; set; }
    public int AttemptNumber { get; set; }
    public ExtractionDeadLetterAction Action { get; set; }
    public string Reason { get; set; } = null!;
    public string ActorId { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public DateTimeOffset CreatedOn { get; set; }
    public ExtractionJob ExtractionJob { get; set; } = null!;
}

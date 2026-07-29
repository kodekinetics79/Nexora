namespace ERP_RFQ_Automation.CommercialIntelligence.Growth;

/// <summary>
/// Append-only manager decision over a recomputed coaching finding. The evidence
/// snapshot makes the decision independently auditable after source data changes.
/// </summary>
public sealed class SalesCoachingAcknowledgement
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string FindingKey { get; set; } = null!;
    public string FindingCode { get; set; } = null!;
    public long SalesRepUserId { get; set; }
    public long ManagerUserId { get; set; }
    public string DecisionCode { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string SourceAggregateType { get; set; } = null!;
    public long SourceAggregateId { get; set; }
    public string SourceAggregateVersion { get; set; } = null!;
    public string EvidenceSnapshotJson { get; set; } = null!;
    public string PolicyVersion { get; set; } = null!;
    public DateTime FindingGeneratedAtUtc { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
}

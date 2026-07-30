using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.PlatformGovernance;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GovernedArtifactType
{
    CommercialTaxonomy,
    DocumentSkill,
    Model,
    Rule,
    Dataset,
    Connector,
    TestSuite,
    ReleaseCandidate
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GovernedLifecycleStatus
{
    Draft,
    Test,
    Production,
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HumanActionStatus
{
    Open,
    InReview,
    Escalated,
    Completed,
    Rejected
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HumanActionPriority
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class GovernedArtifact
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public GovernedArtifactType ArtifactType { get; set; }
    public string ArtifactKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public GovernedLifecycleStatus Status { get; set; } = GovernedLifecycleStatus.Draft;
    public int CurrentVersionNumber { get; set; } = 1;
    public int? ProductionVersionNumber { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public long CreatedByUserId { get; set; }
    public DateTime UpdatedOn { get; set; }
    public long UpdatedByUserId { get; set; }
    public ICollection<GovernedArtifactVersion> Versions { get; set; } = new List<GovernedArtifactVersion>();
    public ICollection<GovernedArtifactEvent> Events { get; set; } = new List<GovernedArtifactEvent>();
}

public sealed class GovernedArtifactVersion
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long GovernedArtifactId { get; set; }
    public int VersionNumber { get; set; }
    public GovernedLifecycleStatus Status { get; set; } = GovernedLifecycleStatus.Draft;
    public string DefinitionJson { get; set; } = "{}";
    public string ChangeSummary { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; }
    public long CreatedByUserId { get; set; }
    public DateTime? TestedOn { get; set; }
    public DateTime? PublishedOn { get; set; }
    public GovernedArtifact Artifact { get; set; } = null!;
}

public sealed class GovernedArtifactEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long GovernedArtifactId { get; set; }
    public int ArtifactVersionNumber { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = "{}";
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime OccurredOn { get; set; }
    public long ActorUserId { get; set; }
    public GovernedArtifact Artifact { get; set; } = null!;
}

public sealed class HumanActionItem
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public decimal Confidence { get; set; }
    public string CommercialImpact { get; set; } = string.Empty;
    public string ResumeActionCode { get; set; } = string.Empty;
    public HumanActionPriority Priority { get; set; }
    public HumanActionStatus Status { get; set; } = HumanActionStatus.Open;
    public long? AssignedToUserId { get; set; }
    public DateTime DueOn { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public long CreatedByUserId { get; set; }
    public DateTime UpdatedOn { get; set; }
    public ICollection<HumanActionEvent> Events { get; set; } = new List<HumanActionEvent>();
}

public sealed class HumanActionEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long HumanActionItemId { get; set; }
    public HumanActionStatus? FromStatus { get; set; }
    public HumanActionStatus ToStatus { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public long ActorUserId { get; set; }
    public DateTime OccurredOn { get; set; }
    public HumanActionItem Item { get; set; } = null!;
}

public sealed class TenantGovernanceAuditEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string Area { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateReference { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public string IdempotencyKey { get; set; } = string.Empty;
    public long ActorUserId { get; set; }
    public DateTime OccurredOn { get; set; }
}

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed record CreateGovernedArtifactCommand(
    GovernedArtifactType ArtifactType,
    string ArtifactKey,
    string Name,
    string Description,
    string DefinitionJson,
    string ChangeSummary);

public sealed record CreateGovernedArtifactVersionCommand(
    long ExpectedVersion,
    string DefinitionJson,
    string ChangeSummary);

public sealed record TransitionGovernedArtifactCommand(
    long ExpectedVersion,
    string Action,
    string Reason,
    int? TargetVersionNumber = null);

public sealed record GovernedArtifactSummary(
    long Id,
    GovernedArtifactType ArtifactType,
    string ArtifactKey,
    string Name,
    string Description,
    GovernedLifecycleStatus Status,
    int CurrentVersionNumber,
    int? ProductionVersionNumber,
    long Version,
    DateTime UpdatedOn,
    long UpdatedByUserId);

public sealed record GovernedArtifactVersionItem(
    long Id,
    int VersionNumber,
    GovernedLifecycleStatus Status,
    string DefinitionJson,
    string ChangeSummary,
    DateTime CreatedOn,
    long CreatedByUserId,
    DateTime? TestedOn,
    DateTime? PublishedOn);

public sealed record GovernedArtifactEventItem(
    long Id,
    int ArtifactVersionNumber,
    string Action,
    string Reason,
    DateTime OccurredOn,
    long ActorUserId);

public sealed record GovernedArtifactDetail(
    GovernedArtifactSummary Artifact,
    IReadOnlyList<GovernedArtifactVersionItem> Versions,
    IReadOnlyList<GovernedArtifactEventItem> Events);

public sealed record ArtifactTransitionResult(
    GovernedArtifactSummary Artifact,
    bool IdempotentReplay);

public sealed record CreateHumanActionCommand(
    string ActionType,
    string SourceType,
    string SourceReference,
    string Title,
    string Summary,
    string Recommendation,
    string EvidenceJson,
    decimal Confidence,
    string CommercialImpact,
    string ResumeActionCode,
    HumanActionPriority Priority,
    long? AssignedToUserId,
    DateTime DueOn);

public sealed record TransitionHumanActionCommand(
    long ExpectedVersion,
    HumanActionStatus TargetStatus,
    string Action,
    string Comment,
    long? AssignedToUserId = null);

public sealed record HumanActionItemDto(
    long Id,
    string ActionType,
    string SourceType,
    string SourceReference,
    string Title,
    string Summary,
    string Recommendation,
    string EvidenceJson,
    decimal Confidence,
    string CommercialImpact,
    string ResumeActionCode,
    HumanActionPriority Priority,
    HumanActionStatus Status,
    long? AssignedToUserId,
    DateTime DueOn,
    bool IsOverdue,
    long Version,
    DateTime UpdatedOn);

public sealed record HumanActionTransitionResult(HumanActionItemDto Item, bool IdempotentReplay);

public sealed class PlatformGovernanceValidationException(string message) : ArgumentException(message);
public sealed class PlatformGovernanceConflictException(string message) : InvalidOperationException(message);
public sealed class PlatformGovernanceNotFoundException(string message) : KeyNotFoundException(message);

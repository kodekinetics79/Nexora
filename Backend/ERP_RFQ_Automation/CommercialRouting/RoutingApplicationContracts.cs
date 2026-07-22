namespace ERP_RFQ_Automation.CommercialRouting;

public sealed record RouteLeadCommand(
    long LeadId,
    string IdempotencyKey,
    string CorrelationId,
    IReadOnlyDictionary<OwnershipScope, string?>? ScopeKeys = null);

public sealed record ManualAssignLeadCommand(
    long LeadId,
    long AssignedToUserId,
    long? AssignedByUserId,
    string IdempotencyKey,
    string CorrelationId,
    AssignmentScope AssignmentScope,
    string? Comment,
    bool EnforceExpectedAssignee,
    long? ExpectedAssigneeId);

public sealed record RoutingDecisionResponse(
    long DecisionId,
    long LeadId,
    long? CustomerId,
    long? SelectedUserId,
    CustomerMatchStatus MatchStatus,
    RoutingOutcome Outcome,
    decimal MatchConfidence,
    string DecisionCode,
    string Explanation,
    string PolicyVersion,
    string CorrelationId,
    DateTime CreatedOn,
    long? AssignmentId,
    long? WorkItemId);

public sealed record UnassignedQueueItemResponse(
    long Id,
    long LeadId,
    string CommercialCaseReference,
    string? CustomerRfqNumber,
    string? BuyerName,
    string ReasonCode,
    WorkItemStatus Status,
    int Priority,
    DateTime EnteredOn,
    DateTime SlaDueOn,
    bool IsOverdue,
    long? SuggestedCustomerId,
    long? SuggestedUserId,
    decimal MatchConfidence,
    string RequiredAction,
    long? ClaimedByUserId,
    DateTime? ClaimedUntil,
    long Version);

public sealed record QueuePageResponse(
    IReadOnlyList<UnassignedQueueItemResponse> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record QueueLeaseCommand(long ExpectedVersion, long UserId, int LeaseMinutes = 15);

public sealed record QueueReleaseCommand(long ExpectedVersion, long UserId);

public sealed record AssignQueueItemCommand(
    long ExpectedVersion,
    long AssignedToUserId,
    long? AssignedByUserId,
    string IdempotencyKey,
    string CorrelationId,
    AssignmentScope AssignmentScope = AssignmentScope.LeadOnly,
    string? Comment = null);

public sealed record BulkQueueAssignmentItem(long WorkItemId, long ExpectedVersion);

public sealed record BulkAssignQueueCommand(
    IReadOnlyList<BulkQueueAssignmentItem> Items,
    long AssignedToUserId,
    long? AssignedByUserId,
    string IdempotencyKeyPrefix,
    string CorrelationId,
    AssignmentScope AssignmentScope = AssignmentScope.LeadOnly,
    string? Comment = null);

public sealed record BulkQueueAssignmentResult(
    long WorkItemId,
    bool Succeeded,
    long? DecisionId,
    string? Error);

public sealed record UpsertCustomerIdentifierCommand(
    long CustomerId,
    CustomerIdentifierType IdentifierType,
    string Value,
    bool IsVerified,
    decimal Confidence,
    string Source);

public sealed record CreateCustomerOwnershipCommand(
    long CustomerId,
    long PrimaryUserId,
    long? BackupUserId,
    OwnershipScope Scope,
    string? ScopeKey,
    int Priority,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string Source,
    string? Reason);

public sealed record CustomerRoutingProfileResponse(
    long CustomerId,
    IReadOnlyList<CustomerIdentifier> Identifiers,
    IReadOnlyList<CustomerOwnership> Ownerships);

public sealed class RoutingNotFoundException(string message) : Exception(message);
public sealed class RoutingConflictException(string message) : Exception(message);

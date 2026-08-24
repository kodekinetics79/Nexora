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
    long? ExpectedAssigneeId,
    long? ExpectedAssignmentVersion = null);

public sealed record ChangeLeadOwnershipCommand(
    long LeadId,
    LeadOwnershipAction Action,
    long? AssignedToUserId,
    long? AssignedByUserId,
    // Whether the caller holds manager/admin authority in this tenant, resolved from the caller's
    // role by the controller. Required rather than optional, and placed next to the actor it
    // describes, so that a new caller cannot reach this command without deciding what authority it
    // is exercising — the whole defect this closes was one surface that never asked.
    bool ActorIsManager,
    long ExpectedAssignmentVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? Comment = null);

public sealed record LeadOwnershipResponse(
    long LeadId,
    long? AssignedToUserId,
    string AssignmentMethod,
    bool ManualOverride,
    long AssignmentVersion,
    DateTime? AssignedAt,
    RoutingDecisionResponse Decision);

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

/// <summary>
/// The exact wording the routing availability projection uses to explain why a user may or may
/// not be handed governed work.
///
/// <para>These are constants rather than inline literals because a second surface has to quote
/// them without being able to read them: <c>GetOwnerOptionsAsync</c> only considers users who
/// already appear in a profile, a team, an ownership or an assignment, so a user with no
/// governed profile at all is absent from its result entirely. The lead-assignment dropdown
/// still has to tell a manager WHY that person cannot be picked, and it must say the same
/// sentence the engine would have said. One copy of the sentence, two readers.</para>
/// </summary>
public static class RoutingEligibilityReasons
{
    public const string UserInactive = "User is inactive";
    public const string ProfileRequired = "Governed Sales Rep profile is required";
    public const string ProfileNotEligible = "Governed Sales Rep profile is not routing eligible";
    public const string CapacityExhausted = "Configured or measured capacity is exhausted";
    public const string Eligible = "Governed Sales Rep profile is active and eligible";

    /// <summary>
    /// A profile row exists but its effective window has not opened or has already closed.
    /// The availability projection never says this — it filters non-effective rows out before it
    /// computes a reason, so to it the user simply has no profile. Only a surface that reads the
    /// stored row directly (the maintenance screen) can tell the two apart, and it must, because
    /// "create a profile" and "extend the one you have" are different corrections.
    /// </summary>
    public const string ProfileNotEffective = "Governed Sales Rep profile is outside its effective dates";
}

public sealed record RoutingOwnerOptionResponse(
    long UserId,
    string Name,
    string Email,
    string? RoleName,
    bool IsAvailable,
    int CapacityPercent,
    RoutingWorkloadSnapshot Workload,
    bool HasGovernedProfile,
    string EligibilityReason,
    DateTime MeasuredAtUtc,
    string PolicyVersion);

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

/// <summary>Sets or clears the tenant's fallback lead owner. Null clears it.</summary>
public sealed record SetDefaultLeadOwnerCommand(long? DefaultOwnerUserId, long? SetByUserId);

/// <summary>
/// The tenant's answer to "when Nexora can't work out who owns an inquiry, give it to ___".
///
/// <para><see cref="IsEligible"/> and <see cref="EligibilityReason"/> answer the question the
/// setup screen must not have to guess at: the setting accepts any active user in the tenant, but
/// routing only USES the named person while they pass the ordinary availability test. A tenant
/// that picks someone with no governed Sales Rep profile has configured a fallback that silently
/// does nothing, and has to be told so in the same words the routing engine would use.</para>
/// </summary>
public sealed record DefaultLeadOwnerResponse(
    long? DefaultOwnerUserId,
    string? Name,
    string? Email,
    bool IsEligible,
    string EligibilityReason,
    long? SetByUserId,
    DateTime? SetOn);

public sealed class RoutingNotFoundException(string message) : Exception(message);
public sealed class RoutingConflictException(string message) : Exception(message);

/// <summary>
/// The caller is authenticated and holds the module permission, but not the AUTHORITY for this
/// particular lead — moving work that belongs to someone else. Distinct from
/// <see cref="RoutingConflictException"/> on purpose: a conflict means "try again with fresh
/// state", and retrying this would never help.
/// </summary>
public sealed class RoutingForbiddenException(string message) : Exception(message);

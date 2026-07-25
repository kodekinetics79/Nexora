using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.CommercialIntelligence.Sales;

public sealed record UpsertSalesRepProfileCommand(
    long UserId, bool IsRoutingEligible, int CapacityPercent, decimal DistributionWeight,
    IReadOnlyCollection<string> TerritoryKeys, IReadOnlyCollection<string> ProductCategoryKeys,
    DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, long ExpectedVersion,
    string ActorId, string IdempotencyKey);

public sealed record AppendCommercialActivityCommand(
    long SalesRepUserId, CommercialActivityType ActivityType, string AggregateType, long AggregateId,
    long? CustomerId, long? LeadAssignmentId, DateTime OccurredAtUtc, string OutcomeCode,
    string EvidenceReference, string ActorId, string CorrelationId, string IdempotencyKey);

public sealed record CreateFollowUpTaskCommand(
    long AssignedToUserId, string AggregateType, long AggregateId, long? CustomerId,
    DateTime DueAtUtc, int Priority, string PurposeCode, string ActorId,
    string CorrelationId, string IdempotencyKey);

public sealed record TransitionFollowUpTaskCommand(
    FollowUpStatus ToStatus, long ExpectedVersion, string ActorId, string Reason,
    string CorrelationId, string IdempotencyKey);

public sealed record RecordSalesContributionCommand(
    long SalesRepUserId, SalesContributionType ContributionType, string AggregateType,
    long AggregateId, long? CustomerId, decimal ContributionPercent, decimal? RevenueAmount,
    string? CurrencyCode, DateTime RecognizedAtUtc, string EvidenceReference,
    string ActorId, string CorrelationId, string IdempotencyKey);

public sealed record WeightedRepCandidate(
    User User,
    SalesRepProfile Profile,
    IReadOnlyCollection<SalesTeamMembership> TeamMemberships,
    IReadOnlyCollection<LeadAssignment> ActiveAssignments,
    int WorkloadPoints,
    DateTime? LastAssignedAtUtc);

public sealed record NewCustomerRoutingRequest(
    long BusinessUnitId,
    long? CustomerId,
    string? TerritoryKey,
    string? ProductCategoryKey,
    long? PreferredTeamId,
    DateTime OccurredAtUtc,
    IReadOnlyCollection<CustomerOwnership> ExistingOwnerships,
    IReadOnlyCollection<WeightedRepCandidate> Candidates);

public sealed record RepScoreBreakdown(
    long UserId, bool Eligible, decimal TotalScore, decimal TerritoryScore,
    decimal ProductScore, decimal TeamScore, decimal CapacityScore,
    decimal WorkloadScore, decimal DistributionScore, int ActiveAssignmentCount,
    string Explanation);

public sealed record WeightedRoutingResult(
    long SelectedUserId, IReadOnlyList<RepScoreBreakdown> RankedCandidates,
    string PolicyVersion);

public sealed record SalesPerformanceQuery(
    long? SalesRepUserId, DateTime FromUtc, DateTime ToUtc, DateTime AsOfUtc);

public sealed record CurrencyContributionPerformance(
    string CurrencyCode, decimal RevenueAmount, decimal WeightedRevenueAmount);

public sealed record SalesRepPerformance(
    long SalesRepUserId, int ActivityCount, int OpportunityCount, int QuoteSentCount,
    int CustomerResponseCount, int WonCount, int LostCount, decimal? WinRatePercent,
    double? AverageResponseHours, int FollowUpsCreated, int FollowUpsCompleted,
    int OpenFollowUps, int OverdueFollowUps,
    IReadOnlyList<CurrencyContributionPerformance> RevenueByCurrency);

public interface ISalesPersistence
{
    Task<bool> UserExistsAsync(long businessUnitId, long userId, CancellationToken ct);
    Task<SalesRepProfile?> GetProfileAsync(long businessUnitId, long userId, CancellationToken ct);
    Task<SalesRepProfile?> FindProfileMutationAsync(long businessUnitId, string idempotencyKey, CancellationToken ct);
    Task<SalesRepProfile> SaveProfileAsync(SalesRepProfile profile, long expectedVersion,
        string idempotencyKey, CancellationToken ct);
    Task<CommercialActivity?> FindActivityAsync(long businessUnitId, string idempotencyKey, CancellationToken ct);
    Task<CommercialActivity> AppendActivityAsync(CommercialActivity activity, CancellationToken ct);
    Task<FollowUpTask?> FindFollowUpByCreationKeyAsync(long businessUnitId, string idempotencyKey, CancellationToken ct);
    Task<FollowUpTask> CreateFollowUpAsync(FollowUpTask task, CancellationToken ct);
    Task<(FollowUpTask Task, FollowUpTransitionEvent? Replay)> GetFollowUpForTransitionAsync(
        long businessUnitId, long taskId, string idempotencyKey, CancellationToken ct);
    Task<FollowUpTransitionEvent> TransitionFollowUpAsync(FollowUpTask task,
        FollowUpTransitionEvent transition, long expectedVersion, CancellationToken ct);
    Task<SalesContribution?> FindContributionAsync(long businessUnitId, string idempotencyKey, CancellationToken ct);
    Task<SalesContribution> AppendContributionAsync(SalesContribution contribution, CancellationToken ct);
    Task<IReadOnlyList<CommercialActivity>> QueryActivitiesAsync(long businessUnitId,
        DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct);
    Task<IReadOnlyList<FollowUpTask>> QueryFollowUpsAsync(long businessUnitId,
        DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct);
    Task<IReadOnlyList<FollowUpTransitionEvent>> QueryFollowUpTransitionsAsync(long businessUnitId,
        DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct);
    Task<IReadOnlyList<SalesContribution>> QueryContributionsAsync(long businessUnitId,
        DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct);
}

public sealed class SalesValidationException(string message) : Exception(message);
public sealed class SalesConflictException(string message) : Exception(message);
public sealed class SalesNotFoundException(string message) : Exception(message);

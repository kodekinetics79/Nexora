namespace ERP_RFQ_Automation.CommercialIntelligence.Sales;

public interface ISalesApplicationService
{
    WeightedRoutingResult ScoreNewCustomer(NewCustomerRoutingRequest request);
    Task<SalesRepProfile> UpsertProfileAsync(long businessUnitId, UpsertSalesRepProfileCommand command, CancellationToken ct);
    Task<CommercialActivity> AppendActivityAsync(long businessUnitId, AppendCommercialActivityCommand command, CancellationToken ct);
    Task<FollowUpTask> CreateFollowUpAsync(long businessUnitId, CreateFollowUpTaskCommand command, CancellationToken ct);
    Task<FollowUpTransitionEvent> TransitionFollowUpAsync(long businessUnitId, long taskId,
        TransitionFollowUpTaskCommand command, CancellationToken ct);
    Task<SalesContribution> RecordContributionAsync(long businessUnitId, RecordSalesContributionCommand command, CancellationToken ct);
    Task<IReadOnlyList<SalesRepPerformance>> GetPerformanceAsync(long businessUnitId, SalesPerformanceQuery query, CancellationToken ct);
}

public sealed class SalesApplicationService(
    ISalesPersistence persistence,
    WeightedEligibleRepScoringEngine? scoringEngine = null) : ISalesApplicationService
{
    private readonly WeightedEligibleRepScoringEngine _scoringEngine = scoringEngine ?? new();

    public WeightedRoutingResult ScoreNewCustomer(NewCustomerRoutingRequest request) =>
        _scoringEngine.Score(request);

    public async Task<SalesRepProfile> UpsertProfileAsync(
        long businessUnitId, UpsertSalesRepProfileCommand command, CancellationToken ct)
    {
        ValidateTenantAndKey(businessUnitId, command.IdempotencyKey);
        Required(command.ActorId, nameof(command.ActorId));
        WeightedEligibleRepScoringEngine.RequireUtc(command.EffectiveFromUtc, nameof(command.EffectiveFromUtc));
        if (command.EffectiveToUtc.HasValue)
            WeightedEligibleRepScoringEngine.RequireUtc(command.EffectiveToUtc.Value, nameof(command.EffectiveToUtc));
        if (command.EffectiveToUtc <= command.EffectiveFromUtc)
            throw new SalesValidationException("Effective end must be after effective start.");
        if (command.CapacityPercent is < 0 or > 100 || command.DistributionWeight is <= 0 or > 1000)
            throw new SalesValidationException("Capacity or distribution weight is outside the accepted range.");
        var replay = await persistence.FindProfileMutationAsync(
            businessUnitId, command.IdempotencyKey.Trim(), ct);
        if (replay != null) return SameProfile(replay, command) ? replay
            : throw new SalesConflictException("Idempotency key was used for different profile content.");
        if (!await persistence.UserExistsAsync(businessUnitId, command.UserId, ct))
            throw new SalesNotFoundException("Sales representative was not found in this tenant.");

        var current = await persistence.GetProfileAsync(businessUnitId, command.UserId, ct);
        if (current == null && command.ExpectedVersion != 0)
            throw new SalesConflictException("Sales representative profile version changed.");
        if (current != null && current.Version != command.ExpectedVersion)
            throw new SalesConflictException("Sales representative profile version changed.");
        var now = DateTime.UtcNow;
        var profile = current ?? new SalesRepProfile
        {
            BusinessUnitId = businessUnitId,
            UserId = command.UserId,
            Version = 1
        };
        profile.IsRoutingEligible = command.IsRoutingEligible;
        profile.CapacityPercent = command.CapacityPercent;
        profile.DistributionWeight = command.DistributionWeight;
        profile.TerritoryKeys = NormalizeKeys(command.TerritoryKeys);
        profile.ProductCategoryKeys = NormalizeKeys(command.ProductCategoryKeys);
        profile.EffectiveFromUtc = command.EffectiveFromUtc;
        profile.EffectiveToUtc = command.EffectiveToUtc;
        profile.UpdatedAtUtc = now;
        profile.UpdatedBy = command.ActorId.Trim();
        profile.Version = command.ExpectedVersion + 1;
        return await persistence.SaveProfileAsync(profile, command.ExpectedVersion, command.IdempotencyKey.Trim(), ct);
    }

    public async Task<CommercialActivity> AppendActivityAsync(
        long businessUnitId, AppendCommercialActivityCommand command, CancellationToken ct)
    {
        ValidateTenantAndKey(businessUnitId, command.IdempotencyKey);
        ValidateAggregate(command.AggregateType, command.AggregateId);
        WeightedEligibleRepScoringEngine.RequireUtc(command.OccurredAtUtc, nameof(command.OccurredAtUtc));
        Required(command.ActorId, nameof(command.ActorId));
        Required(command.CorrelationId, nameof(command.CorrelationId));
        var replay = await persistence.FindActivityAsync(businessUnitId, command.IdempotencyKey.Trim(), ct);
        if (replay != null) return SameActivity(replay, command) ? replay
            : throw new SalesConflictException("Idempotency key was used for different activity content.");
        if (!await persistence.UserExistsAsync(businessUnitId, command.SalesRepUserId, ct))
            throw new SalesNotFoundException("Sales representative was not found in this tenant.");
        await ValidateReferencesAsync(businessUnitId, command.AggregateType, command.AggregateId,
            command.CustomerId, command.LeadAssignmentId, ct);
        return await persistence.AppendActivityAsync(new CommercialActivity
        {
            BusinessUnitId = businessUnitId, SalesRepUserId = command.SalesRepUserId,
            ActivityType = command.ActivityType, AggregateType = command.AggregateType.Trim(),
            AggregateId = command.AggregateId, CustomerId = command.CustomerId,
            LeadAssignmentId = command.LeadAssignmentId, OccurredAtUtc = command.OccurredAtUtc,
            OutcomeCode = command.OutcomeCode.Trim(), EvidenceReference = command.EvidenceReference.Trim(),
            ActorId = command.ActorId.Trim(), CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim()
        }, ct);
    }

    public async Task<FollowUpTask> CreateFollowUpAsync(
        long businessUnitId, CreateFollowUpTaskCommand command, CancellationToken ct)
    {
        ValidateTenantAndKey(businessUnitId, command.IdempotencyKey);
        ValidateAggregate(command.AggregateType, command.AggregateId);
        WeightedEligibleRepScoringEngine.RequireUtc(command.DueAtUtc, nameof(command.DueAtUtc));
        Required(command.PurposeCode, nameof(command.PurposeCode));
        Required(command.ActorId, nameof(command.ActorId));
        Required(command.CorrelationId, nameof(command.CorrelationId));
        var replay = await persistence.FindFollowUpByCreationKeyAsync(businessUnitId, command.IdempotencyKey.Trim(), ct);
        if (replay != null) return SameFollowUp(replay, command) ? replay
            : throw new SalesConflictException("Idempotency key was used for different follow-up content.");
        if (!await persistence.UserExistsAsync(businessUnitId, command.AssignedToUserId, ct))
            throw new SalesNotFoundException("Follow-up assignee was not found in this tenant.");
        await ValidateReferencesAsync(businessUnitId, command.AggregateType, command.AggregateId,
            command.CustomerId, null, ct);
        var now = DateTime.UtcNow;
        return await persistence.CreateFollowUpAsync(new FollowUpTask
        {
            BusinessUnitId = businessUnitId, AssignedToUserId = command.AssignedToUserId,
            AggregateType = command.AggregateType.Trim(), AggregateId = command.AggregateId,
            CustomerId = command.CustomerId, DueAtUtc = command.DueAtUtc, Priority = command.Priority,
            PurposeCode = command.PurposeCode.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now,
            Version = 1, CreatedBy = command.ActorId.Trim(), CorrelationId = command.CorrelationId.Trim(),
            CreationIdempotencyKey = command.IdempotencyKey.Trim()
        }, ct);
    }

    public async Task<FollowUpTransitionEvent> TransitionFollowUpAsync(
        long businessUnitId, long taskId, TransitionFollowUpTaskCommand command, CancellationToken ct)
    {
        ValidateTenantAndKey(businessUnitId, command.IdempotencyKey);
        Required(command.ActorId, nameof(command.ActorId));
        Required(command.Reason, nameof(command.Reason));
        Required(command.CorrelationId, nameof(command.CorrelationId));
        var loaded = await persistence.GetFollowUpForTransitionAsync(
            businessUnitId, taskId, command.IdempotencyKey.Trim(), ct);
        if (loaded.Replay != null) return SameTransition(loaded.Replay, command)
            ? loaded.Replay
            : throw new SalesConflictException("Idempotency key was used for a different transition.");
        if (loaded.Task.Version != command.ExpectedVersion)
            throw new SalesConflictException("Follow-up task version changed.");
        if (!Allowed(loaded.Task.Status, command.ToStatus))
            throw new SalesConflictException($"Follow-up cannot transition from {loaded.Task.Status} to {command.ToStatus}.");
        var now = DateTime.UtcNow;
        var transition = new FollowUpTransitionEvent
        {
            BusinessUnitId = businessUnitId, FollowUpTaskId = taskId,
            FromStatus = loaded.Task.Status, ToStatus = command.ToStatus,
            FromVersion = loaded.Task.Version, ToVersion = loaded.Task.Version + 1,
            OccurredAtUtc = now, ActorId = command.ActorId.Trim(), Reason = command.Reason.Trim(),
            CorrelationId = command.CorrelationId.Trim(), IdempotencyKey = command.IdempotencyKey.Trim()
        };
        loaded.Task.Status = command.ToStatus;
        loaded.Task.Version++;
        loaded.Task.UpdatedAtUtc = now;
        return await persistence.TransitionFollowUpAsync(loaded.Task, transition, command.ExpectedVersion, ct);
    }

    public async Task<SalesContribution> RecordContributionAsync(
        long businessUnitId, RecordSalesContributionCommand command, CancellationToken ct)
    {
        ValidateTenantAndKey(businessUnitId, command.IdempotencyKey);
        ValidateAggregate(command.AggregateType, command.AggregateId);
        WeightedEligibleRepScoringEngine.RequireUtc(command.RecognizedAtUtc, nameof(command.RecognizedAtUtc));
        if (command.ContributionPercent is <= 0 or > 100)
            throw new SalesValidationException("Contribution percent must be greater than zero and at most 100.");
        if (command.RevenueAmount.HasValue && (command.RevenueAmount < 0 || string.IsNullOrWhiteSpace(command.CurrencyCode)))
            throw new SalesValidationException("Non-negative revenue requires a currency code.");
        var replay = await persistence.FindContributionAsync(businessUnitId, command.IdempotencyKey.Trim(), ct);
        if (replay != null) return SameContribution(replay, command) ? replay
            : throw new SalesConflictException("Idempotency key was used for different contribution content.");
        if (!await persistence.UserExistsAsync(businessUnitId, command.SalesRepUserId, ct))
            throw new SalesNotFoundException("Sales representative was not found in this tenant.");
        await ValidateReferencesAsync(businessUnitId, command.AggregateType, command.AggregateId,
            command.CustomerId, null, ct);
        return await persistence.AppendContributionAsync(new SalesContribution
        {
            BusinessUnitId = businessUnitId, SalesRepUserId = command.SalesRepUserId,
            ContributionType = command.ContributionType, AggregateType = command.AggregateType.Trim(),
            AggregateId = command.AggregateId, CustomerId = command.CustomerId,
            ContributionPercent = command.ContributionPercent, RevenueAmount = command.RevenueAmount,
            CurrencyCode = command.CurrencyCode?.Trim().ToUpperInvariant(), RecognizedAtUtc = command.RecognizedAtUtc,
            EvidenceReference = command.EvidenceReference.Trim(), ActorId = command.ActorId.Trim(),
            CorrelationId = command.CorrelationId.Trim(), IdempotencyKey = command.IdempotencyKey.Trim()
        }, ct);
    }

    public async Task<IReadOnlyList<SalesRepPerformance>> GetPerformanceAsync(
        long businessUnitId, SalesPerformanceQuery query, CancellationToken ct)
    {
        WeightedEligibleRepScoringEngine.RequireTenant(businessUnitId);
        WeightedEligibleRepScoringEngine.RequireUtc(query.FromUtc, nameof(query.FromUtc));
        WeightedEligibleRepScoringEngine.RequireUtc(query.ToUtc, nameof(query.ToUtc));
        WeightedEligibleRepScoringEngine.RequireUtc(query.AsOfUtc, nameof(query.AsOfUtc));
        if (query.ToUtc <= query.FromUtc) throw new SalesValidationException("Performance period is invalid.");
        if (query.AsOfUtc < query.FromUtc || query.AsOfUtc > query.ToUtc)
            throw new SalesValidationException("Performance as-of time must fall within the requested period.");
        var activities = await persistence.QueryActivitiesAsync(businessUnitId, query.FromUtc, query.ToUtc, query.SalesRepUserId, ct);
        var followUps = await persistence.QueryFollowUpsAsync(businessUnitId, query.FromUtc, query.ToUtc, query.SalesRepUserId, ct);
        var transitions = await persistence.QueryFollowUpTransitionsAsync(businessUnitId, query.FromUtc, query.ToUtc, query.SalesRepUserId, ct);
        var contributions = await persistence.QueryContributionsAsync(businessUnitId, query.FromUtc, query.ToUtc, query.SalesRepUserId, ct);
        EnsureTenant(businessUnitId, activities.Select(x => x.BusinessUnitId)
            .Concat(followUps.Select(x => x.BusinessUnitId)).Concat(transitions.Select(x => x.BusinessUnitId))
            .Concat(contributions.Select(x => x.BusinessUnitId)));
        var users = activities.Select(x => x.SalesRepUserId).Concat(followUps.Select(x => x.AssignedToUserId))
            .Concat(contributions.Select(x => x.SalesRepUserId))
            .Concat(query.SalesRepUserId.HasValue ? [query.SalesRepUserId.Value] : [])
            .Distinct().Order().ToArray();
        return users.Select(userId => Calculate(userId, activities, followUps, transitions, contributions,
            query.FromUtc, query.ToUtc, query.AsOfUtc)).ToArray();
    }

    private static SalesRepPerformance Calculate(long userId, IReadOnlyList<CommercialActivity> allActivities,
        IReadOnlyList<FollowUpTask> allFollowUps, IReadOnlyList<FollowUpTransitionEvent> transitions,
        IReadOnlyList<SalesContribution> allContributions, DateTime fromUtc, DateTime toUtc, DateTime asOfUtc)
    {
        var activities = allActivities.Where(x => x.SalesRepUserId == userId).ToArray();
        var followUps = allFollowUps.Where(x => x.AssignedToUserId == userId).ToArray();
        var taskIds = followUps.Select(x => x.Id).ToHashSet();
        var completedTransitions = transitions
            .Where(x => taskIds.Contains(x.FollowUpTaskId) && x.ToStatus == FollowUpStatus.Completed)
            .GroupBy(x => x.FollowUpTaskId)
            .Select(group => group.OrderBy(x => x.OccurredAtUtc).First())
            .ToArray();
        FollowUpStatus StatusAt(FollowUpTask task) => transitions
            .Where(x => x.FollowUpTaskId == task.Id && x.OccurredAtUtc <= asOfUtc)
            .OrderByDescending(x => x.ToVersion).ThenByDescending(x => x.Id)
            .Select(x => (FollowUpStatus?)x.ToStatus).FirstOrDefault() ?? FollowUpStatus.Open;
        var latestOutcomes = activities
            .Where(x => x.ActivityType is CommercialActivityType.Won or CommercialActivityType.Lost)
            .GroupBy(x => (x.AggregateType, x.AggregateId))
            .Select(group => group.OrderByDescending(x => x.OccurredAtUtc)
                .ThenByDescending(x => x.Id).First().ActivityType)
            .ToArray();
        var won = latestOutcomes.Count(x => x == CommercialActivityType.Won);
        var lost = latestOutcomes.Count(x => x == CommercialActivityType.Lost);
        var decisions = won + lost;
        var responseHours = activities.Where(x => x.ActivityType == CommercialActivityType.QuoteSent)
            .Select(sent => allActivities.Where(response => response.SalesRepUserId == userId &&
                    response.AggregateType == sent.AggregateType && response.AggregateId == sent.AggregateId &&
                    response.ActivityType == CommercialActivityType.CustomerResponded && response.OccurredAtUtc >= sent.OccurredAtUtc)
                .OrderBy(response => response.OccurredAtUtc).Select(response =>
                    (double?)(response.OccurredAtUtc - sent.OccurredAtUtc).TotalHours).FirstOrDefault())
            .Where(hours => hours.HasValue).Select(hours => hours!.Value).ToArray();
        // FX fix: `GroupBy(x => x.CurrencyCode ?? string.Empty)` funnelled every revenue-bearing
        // contribution whose currency code was absent into ONE bucket keyed "", and reported that
        // bucket's sum as if it were a currency total. RecordContributionAsync does require a
        // currency alongside revenue, so this cannot be produced through this service — but
        // GetPerformanceAsync reads whatever ISalesPersistence returns, including rows written
        // before that rule and by any other writer, so the read path has to stand on its own.
        //
        // Codes are normalised on read (so "usd" and " USD " are one bucket rather than three,
        // which was the mirror-image defect), and rows with no usable code land in an explicitly
        // named UNSPECIFIED bucket that is never merged with, or presented as, a real currency.
        var revenue = allContributions.Where(x => x.SalesRepUserId == userId && x.RevenueAmount.HasValue)
            .GroupBy(x => NormalizeCurrencyCode(x.CurrencyCode), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CurrencyContributionPerformance(group.Key,
                group.Sum(x => x.RevenueAmount!.Value),
                group.Sum(x => x.RevenueAmount!.Value * x.ContributionPercent / 100m))).ToArray();
        return new SalesRepPerformance(userId, activities.Length,
            activities.Where(x => x.ActivityType == CommercialActivityType.OpportunityCreated)
                .Select(x => (x.AggregateType, x.AggregateId)).Distinct().Count(),
            activities.Count(x => x.ActivityType == CommercialActivityType.QuoteSent),
            activities.Count(x => x.ActivityType == CommercialActivityType.CustomerResponded), won, lost,
            decisions == 0 ? null : decimal.Round(won * 100m / decisions, 2),
            responseHours.Length == 0 ? null : responseHours.Average(),
            followUps.Count(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc),
            completedTransitions.Length,
            completedTransitions.Count(completed => followUps.Any(task => task.Id == completed.FollowUpTaskId
                && completed.OccurredAtUtc <= task.DueAtUtc)),
            followUps.Count(x => StatusAt(x) is FollowUpStatus.Open or FollowUpStatus.InProgress),
            followUps.Count(x => (StatusAt(x) is FollowUpStatus.Open or FollowUpStatus.InProgress) &&
                x.DueAtUtc < asOfUtc), revenue);
    }

    private static bool Allowed(FollowUpStatus from, FollowUpStatus to) => (from, to) switch
    {
        (FollowUpStatus.Open, FollowUpStatus.InProgress or FollowUpStatus.Completed or FollowUpStatus.Cancelled) => true,
        (FollowUpStatus.InProgress, FollowUpStatus.Completed or FollowUpStatus.Cancelled) => true,
        _ => false
    };

    private async Task ValidateReferencesAsync(long tenant, string aggregateType, long aggregateId,
        long? customerId, long? leadAssignmentId, CancellationToken ct)
    {
        if (!await persistence.AggregateExistsAsync(tenant, aggregateType, aggregateId, ct))
            throw new SalesNotFoundException("The referenced commercial aggregate was not found in this tenant.");
        if (customerId.HasValue &&
            !await persistence.CustomerExistsAsync(tenant, customerId.Value, ct))
            throw new SalesNotFoundException("The referenced customer was not found in this tenant.");
        if (leadAssignmentId.HasValue &&
            !await persistence.LeadAssignmentExistsAsync(tenant, leadAssignmentId.Value, ct))
            throw new SalesNotFoundException("The referenced lead assignment was not found in this tenant.");
    }

    /// <summary>
    /// The bucket revenue with no identifiable currency is reported under. It is deliberately not
    /// an ISO code: a consumer must be able to tell at a glance that this figure is NOT a
    /// currency total and must not be added to one.
    /// </summary>
    public const string UnspecifiedCurrencyCode = "UNSPECIFIED";

    private static string NormalizeCurrencyCode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(normalized) ? UnspecifiedCurrencyCode : normalized;
    }

    private static string[] NormalizeKeys(IEnumerable<string> values) => values
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static void ValidateTenantAndKey(long businessUnitId, string key)
    { WeightedEligibleRepScoringEngine.RequireTenant(businessUnitId); Required(key, nameof(key)); }
    private static void ValidateAggregate(string type, long id)
    { Required(type, nameof(type)); if (id <= 0) throw new SalesValidationException("Aggregate ID must be positive."); }
    private static void Required(string value, string name)
    { if (string.IsNullOrWhiteSpace(value)) throw new SalesValidationException($"{name} is required."); }
    private static void EnsureTenant(long tenant, IEnumerable<long> values)
    { if (values.Any(value => value != tenant)) throw new SalesConflictException("Persistence returned cross-tenant sales data."); }
    private static bool SameActivity(CommercialActivity x, AppendCommercialActivityCommand c) =>
        x.SalesRepUserId == c.SalesRepUserId && x.ActivityType == c.ActivityType &&
        x.AggregateType == c.AggregateType.Trim() && x.AggregateId == c.AggregateId &&
        x.CustomerId == c.CustomerId && x.LeadAssignmentId == c.LeadAssignmentId &&
        x.OccurredAtUtc == c.OccurredAtUtc && x.OutcomeCode == c.OutcomeCode.Trim() &&
        x.EvidenceReference == c.EvidenceReference.Trim() && x.ActorId == c.ActorId.Trim() &&
        x.CorrelationId == c.CorrelationId.Trim();
    private static bool SameProfile(SalesRepProfile x, UpsertSalesRepProfileCommand c) =>
        x.UserId == c.UserId && x.IsRoutingEligible == c.IsRoutingEligible &&
        x.CapacityPercent == c.CapacityPercent && x.DistributionWeight == c.DistributionWeight &&
        x.TerritoryKeys.SequenceEqual(NormalizeKeys(c.TerritoryKeys)) &&
        x.ProductCategoryKeys.SequenceEqual(NormalizeKeys(c.ProductCategoryKeys)) &&
        x.EffectiveFromUtc == c.EffectiveFromUtc && x.EffectiveToUtc == c.EffectiveToUtc &&
        x.UpdatedBy == c.ActorId.Trim();
    private static bool SameFollowUp(FollowUpTask x, CreateFollowUpTaskCommand c) =>
        x.AssignedToUserId == c.AssignedToUserId && x.AggregateType == c.AggregateType.Trim() &&
        x.AggregateId == c.AggregateId && x.CustomerId == c.CustomerId && x.DueAtUtc == c.DueAtUtc &&
        x.Priority == c.Priority && x.PurposeCode == c.PurposeCode.Trim() &&
        x.CreatedBy == c.ActorId.Trim() && x.CorrelationId == c.CorrelationId.Trim();
    private static bool SameTransition(FollowUpTransitionEvent x, TransitionFollowUpTaskCommand c) =>
        x.ToStatus == c.ToStatus && x.FromVersion == c.ExpectedVersion && x.ActorId == c.ActorId.Trim() &&
        x.Reason == c.Reason.Trim() && x.CorrelationId == c.CorrelationId.Trim();
    private static bool SameContribution(SalesContribution x, RecordSalesContributionCommand c) =>
        x.SalesRepUserId == c.SalesRepUserId && x.ContributionType == c.ContributionType &&
        x.AggregateType == c.AggregateType.Trim() && x.AggregateId == c.AggregateId &&
        x.CustomerId == c.CustomerId && x.ContributionPercent == c.ContributionPercent &&
        x.RevenueAmount == c.RevenueAmount && x.CurrencyCode == c.CurrencyCode?.Trim().ToUpperInvariant() &&
        x.RecognizedAtUtc == c.RecognizedAtUtc && x.EvidenceReference == c.EvidenceReference.Trim() &&
        x.ActorId == c.ActorId.Trim() && x.CorrelationId == c.CorrelationId.Trim();
}

using ERP_RFQ_Automation.CommercialIntelligence.Sales;

namespace ERP_RFQ_Automation.Tests;

public sealed class CoreSalesApplicationServiceTests
{
    private static readonly DateTime From = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Profile_update_requires_matching_version_and_normalizes_scope_keys()
    {
        var store = new MemorySalesPersistence(71, 7101);
        var service = new SalesApplicationService(store);
        var profile = await service.UpsertProfileAsync(71, new UpsertSalesRepProfileCommand(
            7101, true, 80, 1.5m, [" us-east ", "US-EAST"], [" valves "],
            From, null, 0, "manager", "profile-create"), default);

        Assert.Equal(["US-EAST"], profile.TerritoryKeys);
        Assert.Equal(["VALVES"], profile.ProductCategoryKeys);
        Assert.Same(profile, await service.UpsertProfileAsync(71, new UpsertSalesRepProfileCommand(
            7101, true, 80, 1.5m, ["US-EAST"], ["VALVES"], From, null, 0,
            "manager", "profile-create"), default));
        await Assert.ThrowsAsync<SalesConflictException>(() => service.UpsertProfileAsync(71,
            new UpsertSalesRepProfileCommand(7101, true, 100, 1, [], [], From, null, 9,
                "manager", "profile-stale"), default));
    }

    [Fact]
    public async Task Activity_append_is_idempotent_and_rejects_changed_content()
    {
        var store = new MemorySalesPersistence(71, 7101);
        var service = new SalesApplicationService(store);
        var command = Activity("activity-1", CommercialActivityType.Call);

        var first = await service.AppendActivityAsync(71, command, default);
        var replay = await service.AppendActivityAsync(71, command, default);

        Assert.Same(first, replay);
        Assert.Single(store.Activities);
        await Assert.ThrowsAsync<SalesConflictException>(() => service.AppendActivityAsync(
            71, Activity("activity-1", CommercialActivityType.Meeting), default));
    }

    [Fact]
    public async Task Follow_up_transition_is_versioned_append_only_and_terminal()
    {
        var store = new MemorySalesPersistence(71, 7101);
        var service = new SalesApplicationService(store);
        var task = await service.CreateFollowUpAsync(71, new CreateFollowUpTaskCommand(
            7101, "Quote", 501, 201, From.AddDays(5), 50, "QUOTE_RESPONSE", "manager",
            "corr-create", "follow-create"), default);

        var completed = await service.TransitionFollowUpAsync(71, task.Id,
            new TransitionFollowUpTaskCommand(FollowUpStatus.Completed, 1, "rep", "Customer replied",
                "corr-complete", "follow-complete"), default);
        var replay = await service.TransitionFollowUpAsync(71, task.Id,
            new TransitionFollowUpTaskCommand(FollowUpStatus.Completed, 1, "rep", "Customer replied",
                "corr-complete", "follow-complete"), default);

        Assert.Same(completed, replay);
        Assert.Equal(2, task.Version);
        Assert.Single(store.Transitions);
        await Assert.ThrowsAsync<SalesConflictException>(() => service.TransitionFollowUpAsync(71, task.Id,
            new TransitionFollowUpTaskCommand(FollowUpStatus.InProgress, 2, "rep", "Reopen",
                "corr-reopen", "follow-reopen"), default));
    }

    [Fact]
    public async Task Performance_is_calculated_from_events_and_keeps_currencies_separate()
    {
        var store = new MemorySalesPersistence(71, 7101);
        store.Activities.AddRange(
        [
            PersistedActivity(1, CommercialActivityType.OpportunityCreated, From.AddDays(1)),
            PersistedActivity(1, CommercialActivityType.QuoteSent, From.AddDays(2)),
            PersistedActivity(1, CommercialActivityType.CustomerResponded, From.AddDays(2).AddHours(6)),
            PersistedActivity(1, CommercialActivityType.Won, From.AddDays(3)),
            PersistedActivity(2, CommercialActivityType.Lost, From.AddDays(4))
        ]);
        store.FollowUps.AddRange(
        [
            new FollowUpTask { Id = 10, BusinessUnitId = 71, AssignedToUserId = 7101, CreatedAtUtc = From.AddDays(1), DueAtUtc = From.AddDays(2), Status = FollowUpStatus.Completed },
            new FollowUpTask { Id = 11, BusinessUnitId = 71, AssignedToUserId = 7101, CreatedAtUtc = From.AddDays(1), DueAtUtc = From.AddDays(2), Status = FollowUpStatus.Open }
        ]);
        store.Transitions.Add(new FollowUpTransitionEvent { Id = 20, BusinessUnitId = 71, FollowUpTaskId = 10, ToStatus = FollowUpStatus.Completed, OccurredAtUtc = From.AddDays(2) });
        store.Contributions.AddRange(
        [
            Contribution("USD", 1000, 50), Contribution("USD", 500, 100), Contribution("EUR", 200, 25)
        ]);
        var service = new SalesApplicationService(store);

        var result = Assert.Single(await service.GetPerformanceAsync(71,
            new SalesPerformanceQuery(7101, From, From.AddMonths(1), From.AddDays(10)), default));

        Assert.Equal(50m, result.WinRatePercent);
        Assert.Equal(6d, result.AverageResponseHours);
        Assert.Equal(1, result.FollowUpsCompleted);
        Assert.Equal(1, result.OverdueFollowUps);
        Assert.Collection(result.RevenueByCurrency,
            eur => { Assert.Equal("EUR", eur.CurrencyCode); Assert.Equal(50m, eur.WeightedRevenueAmount); },
            usd => { Assert.Equal("USD", usd.CurrencyCode); Assert.Equal(1500m, usd.RevenueAmount); Assert.Equal(1000m, usd.WeightedRevenueAmount); });
    }

    [Fact]
    public async Task Performance_fails_closed_on_cross_tenant_persistence_data()
    {
        var store = new MemorySalesPersistence(71, 7101);
        store.Activities.Add(WithTenant(PersistedActivity(1, CommercialActivityType.Call, From.AddDays(1)), 72));
        var service = new SalesApplicationService(store);

        await Assert.ThrowsAsync<SalesConflictException>(() => service.GetPerformanceAsync(71,
            new SalesPerformanceQuery(null, From, From.AddMonths(1), From.AddDays(10)), default));

        static CommercialActivity WithTenant(CommercialActivity value, long tenant)
        { value.BusinessUnitId = tenant; return value; }
    }

    private static AppendCommercialActivityCommand Activity(string key, CommercialActivityType type) =>
        new(7101, type, "Lead", 501, 201, null, From.AddDays(1), "", "evidence:1",
            "rep", "corr-1", key);

    private static CommercialActivity PersistedActivity(long aggregateId, CommercialActivityType type, DateTime occurred) =>
        new() { BusinessUnitId = 71, SalesRepUserId = 7101, AggregateType = "Opportunity", AggregateId = aggregateId, ActivityType = type, OccurredAtUtc = occurred };

    private static SalesContribution Contribution(string currency, decimal revenue, decimal percent) =>
        new() { BusinessUnitId = 71, SalesRepUserId = 7101, AggregateType = "Order", AggregateId = 1,
            CurrencyCode = currency, RevenueAmount = revenue, ContributionPercent = percent, RecognizedAtUtc = From.AddDays(5) };

    private sealed class MemorySalesPersistence(long tenant, params long[] users) : ISalesPersistence
    {
        private long _id;
        private readonly HashSet<long> _users = users.ToHashSet();
        public List<CommercialActivity> Activities { get; } = [];
        public List<FollowUpTask> FollowUps { get; } = [];
        public List<FollowUpTransitionEvent> Transitions { get; } = [];
        public List<SalesContribution> Contributions { get; } = [];
        public SalesRepProfile? Profile { get; private set; }
        private readonly Dictionary<string, SalesRepProfile> _profileMutations = [];

        public Task<bool> UserExistsAsync(long businessUnitId, long userId, CancellationToken ct) =>
            Task.FromResult(businessUnitId == tenant && _users.Contains(userId));
        public Task<SalesRepProfile?> GetProfileAsync(long businessUnitId, long userId, CancellationToken ct) => Task.FromResult(Profile);
        public Task<SalesRepProfile?> FindProfileMutationAsync(long businessUnitId, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(_profileMutations.GetValueOrDefault(idempotencyKey));
        public Task<SalesRepProfile> SaveProfileAsync(SalesRepProfile profile, long expectedVersion, string idempotencyKey, CancellationToken ct)
        { Profile = profile; _profileMutations[idempotencyKey] = profile; return Task.FromResult(profile); }
        public Task<CommercialActivity?> FindActivityAsync(long businessUnitId, string key, CancellationToken ct) =>
            Task.FromResult(Activities.SingleOrDefault(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key));
        public Task<CommercialActivity> AppendActivityAsync(CommercialActivity activity, CancellationToken ct)
        { activity.Id = ++_id; Activities.Add(activity); return Task.FromResult(activity); }
        public Task<FollowUpTask?> FindFollowUpByCreationKeyAsync(long businessUnitId, string key, CancellationToken ct) =>
            Task.FromResult(FollowUps.SingleOrDefault(x => x.BusinessUnitId == businessUnitId && x.CreationIdempotencyKey == key));
        public Task<FollowUpTask> CreateFollowUpAsync(FollowUpTask task, CancellationToken ct)
        { task.Id = ++_id; FollowUps.Add(task); return Task.FromResult(task); }
        public Task<(FollowUpTask Task, FollowUpTransitionEvent? Replay)> GetFollowUpForTransitionAsync(long businessUnitId, long taskId, string key, CancellationToken ct)
        {
            var task = FollowUps.SingleOrDefault(x => x.BusinessUnitId == businessUnitId && x.Id == taskId)
                ?? throw new SalesNotFoundException("Follow-up was not found.");
            return Task.FromResult((task, Transitions.SingleOrDefault(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key)));
        }
        public Task<FollowUpTransitionEvent> TransitionFollowUpAsync(FollowUpTask task, FollowUpTransitionEvent transition, long expectedVersion, CancellationToken ct)
        { transition.Id = ++_id; Transitions.Add(transition); return Task.FromResult(transition); }
        public Task<SalesContribution?> FindContributionAsync(long businessUnitId, string key, CancellationToken ct) =>
            Task.FromResult(Contributions.SingleOrDefault(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key));
        public Task<SalesContribution> AppendContributionAsync(SalesContribution contribution, CancellationToken ct)
        { contribution.Id = ++_id; Contributions.Add(contribution); return Task.FromResult(contribution); }
        public Task<IReadOnlyList<CommercialActivity>> QueryActivitiesAsync(long businessUnitId, DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommercialActivity>>(Activities.Where(x => x.OccurredAtUtc >= from && x.OccurredAtUtc < to && (!user.HasValue || x.SalesRepUserId == user)).ToArray());
        public Task<IReadOnlyList<FollowUpTask>> QueryFollowUpsAsync(long businessUnitId, DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FollowUpTask>>(FollowUps.Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc < to && (!user.HasValue || x.AssignedToUserId == user)).ToArray());
        public Task<IReadOnlyList<FollowUpTransitionEvent>> QueryFollowUpTransitionsAsync(long businessUnitId, DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FollowUpTransitionEvent>>(Transitions.Where(x => x.OccurredAtUtc >= from && x.OccurredAtUtc < to).ToArray());
        public Task<IReadOnlyList<SalesContribution>> QueryContributionsAsync(long businessUnitId, DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SalesContribution>>(Contributions.Where(x => x.RecognizedAtUtc >= from && x.RecognizedAtUtc < to && (!user.HasValue || x.SalesRepUserId == user)).ToArray());
    }
}

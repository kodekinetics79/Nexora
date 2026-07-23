using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

public sealed class AiGovernanceServiceTests
{
    [Fact]
    public async Task MissingPolicy_DeniesBeforeReservationAndRecordsNoBudget()
    {
        using var fixture = new Fixture(withPolicy: false);

        var error = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("missing-policy"), "Ollama", "test", "private RFQ text", 16, 100, 1, default));

        Assert.Equal("policy_missing", error.Code);
        await using var db = fixture.Database.ContextFor(null);
        var request = Assert.Single(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(AiCallStatuses.Denied, request.Status);
        Assert.Empty(await db.AiBudgetPeriods.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task HardBudget_DeniesAtomicallyWithoutCallingProvider()
    {
        using var fixture = new Fixture(hardLimit: 10);

        var error = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("over-budget"), "Ollama", "test", new string('x', 40), 40, 10, 1, default));

        Assert.Equal("hard_budget_exceeded", error.Code);
        await using var db = fixture.Database.ContextFor(null);
        Assert.Equal(0, (await db.AiBudgetPeriods.IgnoreQueryFilters().SingleAsync()).ReservedTokens);
    }

    [Fact]
    public async Task SuccessfulCall_SettlesUsageAndStoresOnlyHashesAndCounts()
    {
        using var fixture = new Fixture(hardLimit: 10_000);
        const string input = "confidential customer RFQ";
        const string output = "confidential model result";
        var reservation = await fixture.Service.ReserveAsync(
            fixture.Context("success"), "Ollama", "test", input, input.Length, 100, 2, default);
        await fixture.Service.RecordAttemptAsync(reservation, new AiAttemptCompletion(
            1, AiCallStatuses.Succeeded, 200, "provider-1", 7, 5, AiTokenSources.ProviderExact,
            15, 1000, AiGovernanceService.Hash(output), null, DateTime.UtcNow, DateTime.UtcNow), default);
        await fixture.Service.CompleteAsync(reservation, AiCallStatuses.Succeeded,
            7, 5, AiTokenSources.ProviderExact, output, null, default);
        await fixture.Service.CompleteAsync(reservation, AiCallStatuses.Succeeded,
            7, 5, AiTokenSources.ProviderExact, output, null, default);

        await using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters().SingleAsync();
        var budget = await db.AiBudgetPeriods.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AiCallStatuses.Succeeded, request.Status);
        Assert.Equal(input.Length, request.InputCharacters);
        Assert.Equal(output.Length, request.OutputCharacters);
        Assert.DoesNotContain(input, request.InputHash ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(output, request.OutputHash ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, budget.ReservedTokens);
        Assert.Equal(12, budget.SettledTokens);
        Assert.Single(await db.AiCallAttempts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task StaleReservation_IsConservativelySettledAsUnknown()
    {
        using var fixture = new Fixture(hardLimit: 10_000);
        var reservation = await fixture.Service.ReserveAsync(
            fixture.Context("stale"), "Ollama", "test", "input", 100, 10, 2, default);
        var reconciler = new AiReservationReconciler(fixture.ScopeFactory, fixture.TenantScope);

        Assert.Equal(1, await reconciler.ReconcileAsync(DateTime.UtcNow.AddMinutes(1), default));

        await using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters().SingleAsync();
        var budget = await db.AiBudgetPeriods.IgnoreQueryFilters().SingleAsync();
        var attempt = await db.AiCallAttempts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AiCallStatuses.Unknown, request.Status);
        Assert.Equal(reservation.ReservedTokens, request.InputTokens);
        Assert.Equal(AiCallStatuses.Unknown, attempt.Status);
        Assert.Equal("stale_reservation_reconciled", attempt.ErrorCode);
        Assert.Equal(reservation.ReservedTokens, attempt.InputTokens);
        Assert.Equal(0, budget.ReservedTokens);
        Assert.Equal(reservation.ReservedTokens, budget.SettledTokens);
    }

    [Fact]
    public async Task IdempotencyCollision_IsRejectedWithoutASecondReservation()
    {
        using var fixture = new Fixture(hardLimit: 10_000);
        var context = fixture.Context("same-key");
        await fixture.Service.ReserveAsync(context, "Ollama", "test", "first", 5, 10, 1, default);

        var error = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            context, "Ollama", "test", "different", 9, 10, 1, default));

        Assert.Equal("idempotency_collision", error.Code);
        await using var db = fixture.Database.ContextFor(null);
        Assert.Single(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class Fixture : IDisposable
    {
        private const long BusinessUnitId = 44_001;
        private readonly ServiceProvider _provider;
        public TestDb Database { get; } = new();
        public IAiGovernanceService Service { get; }
        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();
        public ITenantScopeAccessor TenantScope => _provider.GetRequiredService<ITenantScopeAccessor>();

        public Fixture(bool withPolicy = true, long? hardLimit = null)
        {
            using (var db = Database.ContextFor(null))
            {
                Seed.EnsureBusinessUnit(db, BusinessUnitId);
                if (withPolicy)
                    db.AiProcessingPolicies.Add(new AiProcessingPolicy
                    {
                        BusinessUnitId = BusinessUnitId,
                        IsEnabled = true,
                        ExternalProcessingAllowed = true,
                        AllowedPurposes = AiPurposes.RfqExtraction,
                        MonthlyHardTokenLimit = hardLimit,
                        UpdatedOn = DateTime.UtcNow,
                        UpdatedBy = "test"
                    });
                db.SaveChanges();
            }

            var tenantScope = new TenantScopeAccessor();
            _provider = new ServiceCollection()
                .AddSingleton<ITenantScopeAccessor>(tenantScope)
                .AddScoped(_ => Database.ContextFor(tenantScope.BusinessUnitId))
                .BuildServiceProvider();
            Service = new AiGovernanceService(ScopeFactory, tenantScope);
        }

        public AiCallContext Context(string key) =>
            new(BusinessUnitId, AiPurposes.RfqExtraction, key, "test-v1");

        public void Dispose()
        {
            _provider.Dispose();
            Database.Dispose();
        }
    }
}

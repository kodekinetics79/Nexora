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
    public async Task InputLargerThanDeclaredMaximum_IsDeniedBeforeLedgerMutation()
    {
        using var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("oversized-input"), "Ollama", "test", "six bytes", 5, 10, 1, default));

        Assert.Equal("input_too_large", error.Code);
        await using var db = fixture.Database.ContextFor(null);
        Assert.Empty(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiBudgetPeriods.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task LocalProvider_DoesNotRequireExternalProcessingConsent()
    {
        using var fixture = new Fixture(externalProcessingAllowed: false);

        var reservation = await fixture.Service.ReserveAsync(
            fixture.Context("local-provider", AiProviderClass.Local),
            "OllamaLocal", "test", "local input", 32, 10, 1, default);

        Assert.NotEqual(Guid.Empty, reservation.RequestId);
    }

    [Fact]
    public async Task ExternalProvider_RequiresExternalProcessingConsent()
    {
        using var fixture = new Fixture(externalProcessingAllowed: false);

        var error = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("external-provider", AiProviderClass.External), "Ollama", "test", "external input", 32, 10, 1, default));

        Assert.Equal("external_processing_denied", error.Code);
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
        Assert.Equal(AiProviderClass.Local, request.ProviderClass);
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

    [Fact]
    public async Task External_dependency_is_reserved_only_at_or_below_ten_percent()
    {
        using var fixture = new Fixture(hardLimit: 100_000);
        for (var i = 0; i < 9; i++)
        {
            var local = await fixture.Service.ReserveAsync(fixture.Context($"local-{i}"), "Ollama", "test", "local", 10, 10, 1, default);
            await fixture.Service.CompleteAsync(local, AiCallStatuses.Succeeded, 1, 1, AiTokenSources.Estimated, null, null, default);
        }
        var external = await fixture.Service.ReserveAsync(fixture.Context("external-one", AiProviderClass.External), "Ollama", "test", "external", 10, 10, 1, default);
        await fixture.Service.CompleteAsync(external, AiCallStatuses.Succeeded, 1, 1, AiTokenSources.Estimated, null, null, default);
        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("external-two", AiProviderClass.External), "Ollama", "test", "external", 10, 10, 1, default));
        Assert.Equal("external_dependency_cap", denied.Code);
    }

    [Fact]
    public async Task Pending_external_request_counts_toward_dependency_cap()
    {
        using var fixture = new Fixture(hardLimit: 100_000);
        for (var i = 0; i < 9; i++)
        {
            var local = await fixture.Service.ReserveAsync(fixture.Context($"pending-local-{i}"),
                "Ollama", "test", "local", 10, 10, 1, default);
            await fixture.Service.CompleteAsync(local, AiCallStatuses.Succeeded,
                1, 1, AiTokenSources.Estimated, null, null, default);
        }

        await fixture.Service.ReserveAsync(fixture.Context("pending-external-one", AiProviderClass.External),
            "External", "test", "external", 10, 10, 1, default);
        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("pending-external-two", AiProviderClass.External),
            "External", "test", "external", 10, 10, 1, default));

        Assert.Equal("external_dependency_cap", denied.Code);
    }

    [Fact]
    public async Task Per_document_budget_denies_additional_reservation_atomically()
    {
        using var fixture = new Fixture(hardLimit: 100_000, maxTokensPerDocument: 25);
        await fixture.Service.ReserveAsync(fixture.Context("document-first", extractionJobId: 551),
            "Ollama", "test", "local", 10, 10, 1, default);

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("document-second", extractionJobId: 551),
            "Ollama", "test", "local", 10, 10, 1, default));

        Assert.Equal("document_budget_exceeded", denied.Code);
    }

    [Fact]
    public async Task External_cost_uses_only_versioned_tenant_rate_policy()
    {
        using var fixture = new Fixture(hardLimit: 100_000,
            externalInputRate: 2m, externalOutputRate: 4m);
        for (var i = 0; i < 9; i++)
        {
            var local = await fixture.Service.ReserveAsync(fixture.Context($"priced-local-{i}"),
                "Ollama", "test", "local", 10, 10, 1, default);
            await fixture.Service.CompleteAsync(local, AiCallStatuses.Succeeded,
                1, 1, AiTokenSources.Estimated, null, null, default);
        }
        var external = await fixture.Service.ReserveAsync(
            fixture.Context("priced-external", AiProviderClass.External),
            "External", "test", "external", 10, 10, 1, default);
        await fixture.Service.CompleteAsync(external, AiCallStatuses.Succeeded,
            1_000, 500, AiTokenSources.ProviderExact, "result", null, default);

        await using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == external.RequestId);
        Assert.Equal(.004m, request.EstimatedCost);
        Assert.Equal("USD", request.CostCurrency);
        Assert.Equal("EstimatedConfiguredRate", request.CostStatus);
        Assert.Equal("test-rate-v1", request.CostPricingVersion);
    }

    [Fact]
    public async Task Caller_cannot_replace_the_authenticated_tenant_context()
    {
        using var fixture = new Fixture();
        var forged = fixture.Context("forged-tenant") with { BusinessUnitId = 99_999 };

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            forged, "Ollama", "test", "private input", 32, 10, 1, default));

        Assert.Equal("tenant_context_mismatch", denied.Code);
    }

    private sealed class Fixture : IDisposable
    {
        private const long BusinessUnitId = 44_001;
        private readonly ServiceProvider _provider;
        public TestDb Database { get; } = new();
        public IAiGovernanceService Service { get; }
        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();
        public ITenantScopeAccessor TenantScope => _provider.GetRequiredService<ITenantScopeAccessor>();

        public Fixture(
            bool withPolicy = true,
            long? hardLimit = null,
            bool externalProcessingAllowed = true,
            long? maxTokensPerDocument = null,
            decimal? externalInputRate = null,
            decimal? externalOutputRate = null)
        {
            using (var db = Database.ContextFor(null))
            {
                Seed.EnsureBusinessUnit(db, BusinessUnitId);
                if (withPolicy)
                    db.AiProcessingPolicies.Add(new AiProcessingPolicy
                    {
                        BusinessUnitId = BusinessUnitId,
                        IsEnabled = true,
                        ExternalProcessingAllowed = externalProcessingAllowed,
                        AllowedPurposes = AiPurposes.RfqExtraction,
                        MonthlyHardTokenLimit = hardLimit,
                        MaxTokensPerDocument = maxTokensPerDocument,
                        ExternalInputCostPerMillionTokens = externalInputRate,
                        ExternalOutputCostPerMillionTokens = externalOutputRate,
                        ExternalCostCurrency = externalInputRate.HasValue ? "USD" : null,
                        ExternalPricingVersion = externalInputRate.HasValue ? "test-rate-v1" : null,
                        UpdatedOn = DateTime.UtcNow,
                        UpdatedBy = "test"
                    });
                db.SaveChanges();
            }

            var tenantScope = new TenantScopeAccessor();
            _provider = new ServiceCollection()
                .AddSingleton<ITenantScopeAccessor>(tenantScope)
                .AddSingleton<ITenantContext>(new StubTenant(BusinessUnitId))
                .AddScoped(_ => Database.ContextFor(tenantScope.BusinessUnitId))
                .BuildServiceProvider();
            Service = new AiGovernanceService(
                ScopeFactory,
                tenantScope,
                _provider.GetRequiredService<ITenantContext>());
        }

        public AiCallContext Context(
            string key,
            AiProviderClass providerClass = AiProviderClass.Local,
            long? extractionJobId = null) =>
            new(BusinessUnitId, AiPurposes.RfqExtraction, key, "test-v1",
                ProviderClass: providerClass, ExtractionJobId: extractionJobId);

        public void Dispose()
        {
            _provider.Dispose();
            Database.Dispose();
        }
    }
}

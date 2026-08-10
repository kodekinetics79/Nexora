using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    public async Task Retry_attempt_key_is_a_new_governed_request_and_a_same_attempt_replay_still_dedups()
    {
        // THE dead-letter root cause: extraction keys used to omit the lease attempt, so
        // every retry (lease reclaim, worker restart, manual re-drive) replayed attempt
        // one's keys and was refused as a duplicate before a single model call. The key
        // must distinguish ATTEMPTS while an identical (job, chunk, attempt) replay is
        // still deduplicated.
        using var fixture = new Fixture(hardLimit: 100_000);
        var attemptOne = fixture.Context("extraction:job:33:a1:chunk:1:12", extractionJobId: 33);
        await fixture.Service.ReserveAsync(attemptOne, "Ollama", "test", "chunk text", 16, 10, 1, default);

        var replay = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            attemptOne, "Ollama", "test", "chunk text", 16, 10, 1, default));
        Assert.Equal("duplicate_request", replay.Code);
        Assert.Contains("duplicate_request", replay.Message); // legible on catch-all surfaces

        var attemptTwo = fixture.Context("extraction:job:33:a2:chunk:1:12", extractionJobId: 33);
        var reservation = await fixture.Service.ReserveAsync(
            attemptTwo, "Ollama", "test", "chunk text", 16, 10, 1, default);

        Assert.NotEqual(Guid.Empty, reservation.RequestId);
        await using var db = fixture.Database.ContextFor(null);
        Assert.Equal(2, await db.AiRequests.IgnoreQueryFilters()
            .CountAsync(x => x.Status == AiCallStatuses.Reserved));
    }

    [Fact]
    public async Task External_reservations_require_an_authorization_whatever_the_ratio_says()
    {
        // This test used to assert that nine local calls bought one external call of
        // headroom. That is what the defect looked like from the inside: absence of an
        // authorization was not a refusal, it merely left the call subject to a ratio — and
        // the ratio was drainable, because every local call bought more external headroom.
        //
        // Nine settled local calls now buy nothing. The FIRST unauthorized external
        // reservation is refused by the allow-list, and the ledger says which control did it.
        using var fixture = new Fixture(hardLimit: 100_000);
        for (var i = 0; i < 9; i++)
        {
            var local = await fixture.Service.ReserveAsync(fixture.Context($"local-{i}"), "Ollama", "test", "local", 10, 10, 1, default);
            await fixture.Service.CompleteAsync(local, AiCallStatuses.Succeeded, 1, 1, AiTokenSources.Estimated, null, null, default);
        }

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("external-one", AiProviderClass.External), "Ollama", "test", "external", 10, 10, 1, default));
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, denied.Code);

        // …and the same reservation succeeds the moment the tenant authorizes the endpoint,
        // so this is a gate and not a switch-off.
        fixture.AuthorizeResolvedEndpoint();
        var reservation = await fixture.Service.ReserveAsync(
            fixture.Context("external-two", AiProviderClass.External), "Ollama", "test", "external", 10, 10, 1, default);
        Assert.NotEqual(Guid.Empty, reservation.RequestId);
    }

    [Fact]
    public async Task An_endpoint_this_process_cannot_even_name_is_refused()
    {
        // A provider/model pair matching no descriptor is a destination the governance model
        // cannot identify — so it cannot be authorized, and it fails closed rather than
        // falling back to a ratio. This is what kept the agent's Anthropic origin ungoverned:
        // it had no descriptor at all.
        using var fixture = new Fixture(hardLimit: 100_000);
        fixture.AuthorizeResolvedEndpoint();

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("unnameable-endpoint", AiProviderClass.External),
            "SomeOtherProvider", "test", "external", 10, 10, 1, default));

        Assert.Equal(AiExternalProviderTrustReasons.EndpointUnresolved, denied.Code);
    }

    [Fact]
    public async Task Authorized_endpoint_is_exempt_from_the_ceiling_even_at_full_external_ratio()
    {
        // The headline rescope: with no local model every governed call is external, the
        // ratio is 100%, and the OLD ceiling denied ~9 in 10 extractions even for a tenant
        // holding a valid authorization. An allow-list-authorized endpoint is now exempt
        // from the ratio; the ledger records which authorization exempted it, and under
        // which declared posture.
        using var fixture = new Fixture(hardLimit: 100_000);
        var authorizationId = fixture.AuthorizeResolvedEndpoint();

        var reservation = await fixture.Service.ReserveAsync(
            fixture.Context("authorized-full-ratio", AiProviderClass.External),
            "Ollama", "test", "external", 10, 10, 1, default);

        Assert.NotEqual(Guid.Empty, reservation.RequestId);
        await using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == reservation.RequestId);
        Assert.Equal(AiCallStatuses.Reserved, request.Status);
        Assert.Equal(authorizationId, request.ExternalAuthorizationId);
        Assert.Equal(nameof(InferencePosture.ExternalAuthorized), request.InferencePosture);
    }

    [Fact]
    public async Task Unauthorized_external_is_denied_and_the_ledger_records_no_authorization()
    {
        // No authorization row: the reservation is refused outright, and the denied ledger
        // row carries no authorization linkage — so "which calls went external under whose
        // authorization" stays answerable from the ledger alone.
        using var fixture = new Fixture(hardLimit: 100_000);

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("unauthorized-full-ratio", AiProviderClass.External),
            "Ollama", "test", "external", 10, 10, 1, default));

        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, denied.Code);
        await using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Null(request.ExternalAuthorizationId);
        Assert.Null(request.InferencePosture);
    }

    [Fact]
    public async Task Authorization_for_a_different_model_does_not_authorize_this_call()
    {
        // A grant is for one destination, not "external in general". A grant for another
        // model at the same endpoint is not this destination — mismatch fails closed.
        using var fixture = new Fixture(hardLimit: 100_000);
        fixture.AuthorizeResolvedEndpoint(model: "some-other-model");

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("wrong-model-authorized", AiProviderClass.External),
            "Ollama", "test", "external", 10, 10, 1, default));

        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, denied.Code);
    }

    [Fact]
    public async Task Another_tenants_authorization_does_not_authorize_this_tenant()
    {
        using var fixture = new Fixture(hardLimit: 100_000);
        fixture.AuthorizeResolvedEndpoint(businessUnitId: Fixture.OtherBusinessUnitId);

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("other-tenants-grant", AiProviderClass.External),
            "Ollama", "test", "external", 10, 10, 1, default));

        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, denied.Code);
    }

    [Fact]
    public async Task The_agents_anthropic_destination_is_governed_like_every_other_endpoint()
    {
        // Before the Anthropic origin had a descriptor it could never appear in an allow-list
        // row, so it could never be authorized, expired or revoked — the only surviving
        // control was a free-text provider-name match. Now it is refused without a grant and
        // permitted with one, exactly like the extraction endpoint.
        using var fixture = new Fixture(hardLimit: 100_000);
        var anthropic = fixture.AnthropicDestination;

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("agent-unauthorized", AiProviderClass.External),
            anthropic.Provider, anthropic.Model, "conversation", 32, 10, 1, default));
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, denied.Code);

        fixture.AuthorizeDestination(anthropic);
        var reservation = await fixture.Service.ReserveAsync(
            fixture.Context("agent-authorized", AiProviderClass.External),
            anthropic.Provider, anthropic.Model, "conversation", 32, 10, 1, default);

        await using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == reservation.RequestId);
        Assert.Equal(anthropic.Provider, request.Provider);
        Assert.NotNull(request.ExternalAuthorizationId);
    }

    [Fact]
    public async Task Authorized_endpoint_is_still_denied_when_the_hard_budget_is_exhausted()
    {
        // The exemption is CEILING-ONLY. The same authorized destination that passes the
        // ratio is still stopped by the monthly hard token budget.
        using var fixture = new Fixture(hardLimit: 10);
        fixture.AuthorizeResolvedEndpoint();

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("authorized-over-hard-budget", AiProviderClass.External),
            "Ollama", "test", new string('x', 40), 40, 10, 1, default));

        Assert.Equal("hard_budget_exceeded", denied.Code);
    }

    [Fact]
    public async Task Authorized_endpoint_is_still_denied_when_the_document_budget_is_exhausted()
    {
        // 5 per pass × AiGovernanceService.DocumentBudgetRetryCycles (5) = 25 lifetime —
        // the identical enforcement threshold this test asserted before the ceiling
        // gained retry headroom.
        using var fixture = new Fixture(hardLimit: 100_000, maxTokensPerDocument: 5);
        fixture.AuthorizeResolvedEndpoint();
        await fixture.Service.ReserveAsync(
            fixture.Context("authorized-document-first", AiProviderClass.External, extractionJobId: 771),
            "Ollama", "test", "external", 10, 10, 1, default);

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Service.ReserveAsync(
            fixture.Context("authorized-document-second", AiProviderClass.External, extractionJobId: 771),
            "Ollama", "test", "external", 10, 10, 1, default));

        Assert.Equal("document_budget_exceeded", denied.Code);
    }

    [Fact]
    public async Task Per_document_budget_denies_additional_reservation_atomically()
    {
        // 5 per pass × AiGovernanceService.DocumentBudgetRetryCycles (5) = 25 lifetime —
        // same threshold as before the retry headroom (see that constant's sizing note).
        using var fixture = new Fixture(hardLimit: 100_000, maxTokensPerDocument: 5);
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
        // The allow-list is a GATE on every external reservation, not merely a ceiling exemption,
        // so an external call must name a destination the tenant has authorized. This test is
        // about COST attribution and never intended to assert anything about egress permission;
        // it predates the gate and was still reserving against the unresolvable provider name
        // "External", which no configured endpoint matches. Authorizing the resolved destination
        // and naming it is the fixture catching up with the control, not a relaxation of it —
        // UnauthorizedExternal_IsDeniedAtReservation_NotMerelyRatioLimited is where the refusal
        // is pinned.
        fixture.AuthorizeResolvedEndpoint();
        for (var i = 0; i < 9; i++)
        {
            var local = await fixture.Service.ReserveAsync(fixture.Context($"priced-local-{i}"),
                "Ollama", "test", "local", 10, 10, 1, default);
            await fixture.Service.CompleteAsync(local, AiCallStatuses.Succeeded,
                1, 1, AiTokenSources.Estimated, null, null, default);
        }
        var external = await fixture.Service.ReserveAsync(
            fixture.Context("priced-external", AiProviderClass.External),
            "Ollama", "test", "external", 10, 10, 1, default);
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
        public const long BusinessUnitId = 44_001;
        public const long OtherBusinessUnitId = 44_002;

        /// <summary>The deployment's configured external endpoint, as the resolver sees it.</summary>
        private const string ResolvedBaseUrl = "https://ollama.example/";
        private const string ResolvedEndpoint = "https://ollama.example";
        private const string ResolvedModel = "test";

        private readonly ServiceProvider _provider;
        private readonly ErpRfqAutomationContext _trustDb;
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
            var tenantContext = new StubTenant(BusinessUnitId);
            _provider = new ServiceCollection()
                .AddSingleton<ITenantScopeAccessor>(tenantScope)
                .AddSingleton<ITenantContext>(tenantContext)
                .AddScoped(_ => Database.ContextFor(tenantScope.BusinessUnitId))
                .BuildServiceProvider();

            // The REAL allow-list gate, wired exactly as production wires it: the ledger's
            // ceiling exemption is only ever granted by a live authorization row, so these
            // tests exercise the true fail-closed matching rather than a stub's opinion.
            var resolver = new AiProviderEndpointResolver(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ollama:BaseUrl"] = ResolvedBaseUrl,
                    ["Ollama:Model"] = ResolvedModel
                }).Build(),
                new NoopLogger<AiProviderEndpointResolver>());
            AnthropicDestination = resolver.Anthropic;
            _trustDb = Database.ContextFor(BusinessUnitId);
            var trust = new AiExternalProviderTrustService(
                _trustDb, tenantContext, resolver, new NoopLogger<AiExternalProviderTrustService>());

            Service = new AiGovernanceService(
                ScopeFactory,
                tenantScope,
                _provider.GetRequiredService<ITenantContext>(),
                trust);
        }

        /// <summary>The agent's Claude destination, as the resolver names it.</summary>
        public AiProviderDescriptor AnthropicDestination { get; } = null!;

        public AiCallContext Context(
            string key,
            AiProviderClass providerClass = AiProviderClass.Local,
            long? extractionJobId = null) =>
            new(BusinessUnitId, AiPurposes.RfqExtraction, key, "test-v1",
                ProviderClass: providerClass, ExtractionJobId: extractionJobId);

        /// <summary>Authorizes an arbitrary resolved destination for this tenant.</summary>
        public void AuthorizeDestination(AiProviderDescriptor destination)
        {
            using var db = Database.ContextFor(null);
            db.AiExternalProviderAuthorizations.Add(new AiExternalProviderAuthorization
            {
                BusinessUnitId = BusinessUnitId,
                Provider = destination.Provider,
                Endpoint = destination.Endpoint,
                Model = destination.Model,
                AllowedPurposes = AiPurposes.RfqExtraction,
                UnstructuredDocumentsAllowed = false,
                Justification = "DPA-2026-14 signed.",
                AuthorizedByUserId = 4242,
                AuthorizedBy = "user:4242",
                AuthorizedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        /// <summary>
        /// Inserts a live allow-list authorization row directly (the attributed admin
        /// surface is covered by AiExternalProviderAllowListTests; here only the row's
        /// effect on the ledger is under test).
        /// </summary>
        public long AuthorizeResolvedEndpoint(
            long businessUnitId = BusinessUnitId, string model = ResolvedModel)
        {
            using var db = Database.ContextFor(null);
            Seed.EnsureBusinessUnit(db, businessUnitId);
            var row = new AiExternalProviderAuthorization
            {
                BusinessUnitId = businessUnitId,
                Provider = "Ollama",
                Endpoint = ResolvedEndpoint,
                Model = model,
                AllowedPurposes = AiPurposes.RfqExtraction,
                UnstructuredDocumentsAllowed = true,
                Justification = "DPA-2026-14 signed.",
                AuthorizedByUserId = 4242,
                AuthorizedBy = "user:4242",
                AuthorizedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            };
            db.AiExternalProviderAuthorizations.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        public void Dispose()
        {
            _trustDb.Dispose();
            _provider.Dispose();
            Database.Dispose();
        }
    }
}

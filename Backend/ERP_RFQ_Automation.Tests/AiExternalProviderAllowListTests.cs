using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The external-provider allow-list replaced a binary "External => always refuse" rule
/// that had silently disabled every AI extraction in production. These tests pin the four
/// properties that make the replacement STRONGER than the rule it replaces:
///
/// <list type="number">
/// <item>an endpoint a tenant deliberately authorized may process unstructured documents;</item>
/// <item>any other external endpoint is still refused, with the original message and zero egress;</item>
/// <item>one tenant's authorization is invisible and unusable to another tenant;</item>
/// <item>the allow-list is a gate, not a bypass — the reserve/settle token ledger still records
///       every governed call on the authorized path.</item>
/// </list>
/// </summary>
public sealed class AiExternalProviderAllowListTests
{
    private const string AuthorizedEndpoint = "https://ollama.com";
    private const string AuthorizedModel = "deepseek-v4-pro";

    // ---- 1. an authorized endpoint permits unstructured extraction -------

    [Fact]
    public async Task AuthorizedEndpoint_PermitsUnstructuredExtraction()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);
        fixture.SeedLocalCallHistory(9); // stay inside the external-dependency ceiling

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Equal(1, llm.CallCount);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("External provider authorized"));
    }

    [Fact]
    public async Task Authorization_IsScopedToTheAuthorizedEndpointAndModel()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true, model: AuthorizedModel);

        // Same tenant, same authorized origin, but the deployment now points at a
        // different model. A grant is for one destination, not "external in general".
        var otherModel = fixture.Descriptor with { Model = "some-other-model" };
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, otherModel, AiPurposes.RfqExtraction, true, default);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, decision.Reason);
    }

    [Fact]
    public async Task Authorization_ForStructuredWorkOnly_StillRefusesUnstructuredDocuments()
    {
        using var fixture = new Fixture();
        // The tenant authorized the endpoint, but NOT whole-document egress. The
        // high-risk switch is deliberately separate and defaults to off.
        await fixture.AuthorizeAsync(unstructuredAllowed: false);

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains(AiExternalProviderTrustReasons.UnstructuredNotAuthorized, outcome.ReviewReason);
    }

    [Fact]
    public async Task RevokedAuthorization_StopsBeingUsableImmediately()
    {
        using var fixture = new Fixture();
        var granted = await fixture.AuthorizeAsync(unstructuredAllowed: true);
        await fixture.Trust.RevokeAsync(fixture.TenantId, 4242, Guid.NewGuid().ToString("N"),
            new RevokeAiExternalProviderCommand(granted.Authorization.Id, "Contract ended."), default);

        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.Revoked, decision.Reason);
    }

    [Fact]
    public async Task Platform_audit_failure_rolls_back_the_effective_provider_grant()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Trust.AuthorizeAsync(fixture.TenantId, 4242, Guid.NewGuid().ToString("N"),
                new AuthorizeAiExternalProviderCommand(
                    "Ollama", AuthorizedEndpoint, AuthorizedModel, AiPurposes.RfqExtraction,
                    true, "DPA-2026-14 signed; approved by the data protection officer.", null),
                default,
                (_, _) => throw new InvalidOperationException("platform audit unavailable")));

        var view = await fixture.Trust.GetAsync(fixture.TenantId, default);
        Assert.Empty(view.Authorizations);
    }

    // ---- 2. an unauthorized external endpoint is still refused -----------

    [Fact]
    public async Task UnauthorizedExternalEndpoint_IsStillRefusedWithTheOriginalMessage()
    {
        using var fixture = new Fixture();

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(2, outcome.ExpectedItemCount);
        Assert.Equal(0, outcome.ExtractedItemCount);
        Assert.Equal(0, llm.CallCount); // not one byte of the document left the process
        Assert.Contains("locally reduced, redacted field/row payload", outcome.ReviewReason);
        Assert.Contains("human review", outcome.ReviewReason);
        Assert.Contains(AiExternalProviderTrustReasons.NotAuthorized, outcome.ReviewReason);
    }

    [Fact]
    public async Task AuthorizedEndpoint_StillRefusedWhilePolicyForbidsExternalProcessing()
    {
        // The allow-list narrows the policy; it can never widen it. A tenant sitting on
        // AiProcessingPolicy.CreateSecureDefault (ExternalProcessingAllowed = false) is
        // refused even with an authorization row present.
        using var fixture = new Fixture(externalProcessingAllowed: false);
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.PolicyExternalProcessingDenied, decision.Reason);
    }

    [Fact]
    public async Task MissingGate_FailsClosed_SoNoUnregisteredDependencyCanTurnEgressOn()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        // Same tenant, same authorization — but the gate itself was not injected.
        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var extractor = new ChunkedExtractionService(
            llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

        var outcome = await extractor.ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains(AiExternalProviderTrustReasons.GateUnavailable, outcome.ReviewReason);
    }

    [Fact]
    public async Task LoopbackEndpoint_CannotBeAuthorized_BecauseItIsNotExternal()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
            fixture.Trust.AuthorizeAsync(fixture.TenantId, 4242, Guid.NewGuid().ToString("N"),
                new AuthorizeAiExternalProviderCommand("Ollama", "http://127.0.0.1:11434/",
                    null, AiPurposes.RfqExtraction, true, "Local box.", null), default));
    }

    // ---- 3. cross-tenant isolation ---------------------------------------

    [Fact]
    public async Task TenantCannotSeeOrUseAnotherTenantsAuthorization()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        // Tenant B has its own policy allowing external processing, but no authorization.
        var other = fixture.ForOtherTenant();

        var view = await other.Trust.GetAsync(other.TenantId, default);
        Assert.Empty(view.Authorizations);
        Assert.False(view.ResolvedProviderIsAuthorizedForUnstructured);

        var decision = await other.Trust.EvaluateAsync(
            other.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, decision.Reason);

        var llm = new GovernedStubLlm(fixture.Descriptor, other.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await other.Extractor(llm).ExtractUnstructuredAsync(Doc(other.TenantId, 2));
        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task EvaluationRefusesWhenTheCallerAsksAboutADifferentTenant()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        // A service scoped to tenant A asking about tenant B is a forgery attempt, not a
        // lookup — refuse without touching the table at all.
        var decision = await fixture.Trust.EvaluateAsync(
            Fixture.OtherTenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.TenantMismatch, decision.Reason);
    }

    [Fact]
    public async Task AuthorizationRowsAreTenantFilteredAtTheDatabase()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        using var otherScoped = fixture.Database.ContextFor(Fixture.OtherTenantId);
        Assert.Empty(await otherScoped.AiExternalProviderAuthorizations.ToListAsync());

        using var unfiltered = fixture.Database.ContextFor(null);
        Assert.Single(await unfiltered.AiExternalProviderAuthorizations.ToListAsync());
    }

    // ---- 4. the governance ledger is untouched on the allowed path -------

    [Fact]
    public async Task AllowedPath_StillReservesAndSettlesTheGovernanceLedger()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);
        fixture.SeedLocalCallHistory(9);

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);

        using var db = fixture.Database.ContextFor(null);
        var request = Assert.Single(await db.AiRequests.IgnoreQueryFilters()
            .Where(x => x.BusinessUnitId == fixture.TenantId
                        && x.ProviderClass == AiProviderClass.External).ToListAsync());
        Assert.Equal(AiCallStatuses.Succeeded, request.Status);
        Assert.Equal(AiProviderClass.External, request.ProviderClass);
        Assert.Equal(AiPurposes.RfqExtraction, request.Operation);
        Assert.True(request.ReservedTokens > 0);
        Assert.NotNull(request.CompletedOn);

        var budget = Assert.Single(await db.AiBudgetPeriods.IgnoreQueryFilters()
            .Where(x => x.BusinessUnitId == fixture.TenantId).ToListAsync());
        // Reservation released on settle, actual usage banked.
        Assert.Equal(0, budget.ReservedTokens);
        Assert.Equal(GovernedStubLlm.InputTokens + GovernedStubLlm.OutputTokens, budget.SettledTokens);
    }

    [Fact]
    public async Task AuthorizedEndpoint_IsExemptFromTheExternalDependencyCeiling()
    {
        // The rescoped ceiling: it governs UNAUTHORIZED external usage only. On a
        // deployment with no local model every governed call is external (the ratio is
        // 100%), and the old ratio check denied ~9 in 10 extractions even for a tenant
        // holding a valid authorization. An allow-list-authorized destination is exempt
        // from the ratio; the ledger records the exempting authorization id and the
        // deployment's declared posture, and every other control still applies.
        using var fixture = new Fixture();
        var granted = await fixture.AuthorizeAsync(unstructuredAllowed: true);
        // Deliberately NO local call history: the external ratio is 100%.

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Equal(1, llm.CallCount);

        using var db = fixture.Database.ContextFor(null);
        var request = Assert.Single(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(AiCallStatuses.Succeeded, request.Status);
        Assert.Equal(granted.Authorization.Id, request.ExternalAuthorizationId);
        Assert.Equal(nameof(InferencePosture.ExternalAuthorized), request.InferencePosture);
    }

    [Fact]
    public async Task UnauthorizedExternal_IsDeniedAtReservation_NotMerelyRatioLimited()
    {
        // Nine local calls + one external is 10%, which the default ceiling permits — and
        // under the old code that was enough for structured line-item data (part numbers,
        // quantities, unit prices, customer names) to leave for a third party with no
        // attributed authorization, no justification, no expiry and no revocation path. A
        // ratio is a cost control; it was never an authorization.
        //
        // The reservation is now refused by the allow-list itself, whatever the ratio says,
        // and the denied ledger row records WHY. Reverting the gate re-opens the nine-in-ten
        // window and fails this test.
        using var fixture = new Fixture();
        fixture.SetDependencyCeilingPercent(5m);
        fixture.SeedLocalCallHistory(9);

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Governance.ReserveAsync(
            new AiCallContext(fixture.TenantId, AiPurposes.RfqExtraction, "unauthorized-structured", "test-v1",
                ProviderClass: AiProviderClass.External),
            fixture.Descriptor.Provider, fixture.Descriptor.Model, "external", 32, 10, 1, default));

        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, denied.Code);
        using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters()
            .SingleAsync(x => x.ProviderClass == AiProviderClass.External);
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, request.ErrorCode);
        Assert.Equal(AiCallStatuses.Denied, request.Status);
        Assert.Null(request.ExternalAuthorizationId);
    }

    [Fact]
    public async Task StructuredExtractionToAnAuthorizedEndpoint_IsStillPermitted()
    {
        // The gate is a narrower door, not a wall: the same reservation succeeds once the
        // tenant has authorized the destination. Without this the fix above would be
        // indistinguishable from switching external processing off.
        using var fixture = new Fixture();
        var granted = await fixture.AuthorizeAsync(unstructuredAllowed: false);
        fixture.SeedLocalCallHistory(9);

        var reservation = await fixture.Governance.ReserveAsync(
            new AiCallContext(fixture.TenantId, AiPurposes.RfqExtraction, "authorized-structured", "test-v1",
                ProviderClass: AiProviderClass.External),
            fixture.Descriptor.Provider, fixture.Descriptor.Model, "external", 32, 10, 1, default);

        Assert.NotEqual(Guid.Empty, reservation.RequestId);
        using var db = fixture.Database.ContextFor(null);
        var request = await db.AiRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == reservation.RequestId);
        Assert.Equal(AiCallStatuses.Reserved, request.Status);
        Assert.Equal(granted.Authorization.Id, request.ExternalAuthorizationId);
    }

    [Fact]
    public async Task AnExpiredAuthorization_DeniesTheReservationRatherThanLosingAnExemption()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);
        fixture.SeedLocalCallHistory(9);
        fixture.ExpireAuthorizations();

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Governance.ReserveAsync(
            new AiCallContext(fixture.TenantId, AiPurposes.RfqExtraction, "expired-grant", "test-v1",
                ProviderClass: AiProviderClass.External),
            fixture.Descriptor.Provider, fixture.Descriptor.Model, "external", 32, 10, 1, default));

        Assert.Equal(AiExternalProviderTrustReasons.Expired, denied.Code);
    }

    [Fact]
    public void Resolver_DeclaresAndLogsTheInferencePostureAtStartup()
    {
        // The posture is informational telemetry resolved once at startup: it must be
        // visible on the resolver AND in the very startup line that already announces the
        // endpoint resolution.
        var externalLog = new CapturingLogger<AiProviderEndpointResolver>();
        var external = new AiProviderEndpointResolver(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "https://ollama.com/",
                ["Ollama:Model"] = AuthorizedModel
            }).Build(), externalLog);
        Assert.Equal(InferencePosture.ExternalAuthorized, external.Posture);
        Assert.Contains(externalLog.Messages, m => m.Contains("Posture=ExternalAuthorized"));

        var localLog = new CapturingLogger<AiProviderEndpointResolver>();
        var local = new AiProviderEndpointResolver(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            localLog);
        Assert.Equal(InferencePosture.LocalFirst, local.Posture);
        Assert.Contains(localLog.Messages, m => m.Contains("Posture=LocalFirst"));
    }

    [Fact]
    public async Task RefusedPath_NeverOpensAGovernanceReservation()
    {
        using var fixture = new Fixture();

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        using var db = fixture.Database.ContextFor(null);
        Assert.Empty(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiBudgetPeriods.IgnoreQueryFilters().ToListAsync());
    }

    // ---- endpoint normalisation ------------------------------------------

    [Theory]
    [InlineData("https://ollama.com/", "https://ollama.com")]
    [InlineData("https://OLLAMA.com/api/chat", "https://ollama.com")]
    [InlineData("https://ollama.com:443", "https://ollama.com")]
    [InlineData("http://provider.internal:8080/v1/", "http://provider.internal:8080")]
    public void EndpointNormalization_ReducesConfiguredUrlsToOneCanonicalOrigin(string configured, string expected)
    {
        Assert.True(AiProviderEndpoint.TryNormalize(configured, out var endpoint, out var reason));
        Assert.Equal(expected, endpoint);
        Assert.Equal(AiProviderEndpointReasons.NonLoopbackEndpoint, reason);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/", AiProviderEndpointReasons.LoopbackEndpoint)]
    [InlineData("http://localhost:11434/", AiProviderEndpointReasons.LoopbackEndpoint)]
    public void LoopbackEndpoints_AreStillClassifiedLocal(string configured, string expectedReason)
    {
        var descriptor = AiProviderEndpoint.Describe("Ollama", configured, "m");
        Assert.Equal(AiProviderClass.Local, descriptor.ProviderClass);
        Assert.Equal(expectedReason, descriptor.ClassificationReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://provider.example/")]
    [InlineData("https://user:secret@provider.example/")]
    public void UnusableEndpoints_FailClosedAsExternalAndUnresolved(string? configured)
    {
        var descriptor = AiProviderEndpoint.Describe("Ollama", configured, "m");
        Assert.Equal(AiProviderClass.External, descriptor.ProviderClass);
        Assert.False(descriptor.IsResolved);
    }

    // ---- fixture ----------------------------------------------------------

    private static DocumentExtractionInput Doc(long tenantId, int rows) => new()
    {
        BusinessUnitId = tenantId,
        LineItemRegions = Enumerable.Range(0, rows).Select(i => $"row {i}").ToList(),
        HeaderText = "buyer: Acme"
    };

    private sealed class Fixture : IDisposable
    {
        public const long DefaultTenantId = 91_001;
        public const long OtherTenantId = 91_002;

        private readonly ServiceProvider _provider;
        private readonly ErpRfqAutomationContext _trustDb;
        private readonly bool _ownsDatabase;

        public TestDb Database { get; }
        public long TenantId { get; }
        public AiExternalProviderTrustService Trust { get; }
        public IAiGovernanceService Governance { get; }
        public AiProviderDescriptor Descriptor { get; }

        public Fixture(bool externalProcessingAllowed = true)
            : this(new TestDb(), DefaultTenantId, externalProcessingAllowed, ownsDatabase: true)
        {
        }

        private Fixture(TestDb database, long tenantId, bool externalProcessingAllowed, bool ownsDatabase)
        {
            Database = database;
            TenantId = tenantId;
            _ownsDatabase = ownsDatabase;

            using (var seed = Database.ContextFor(null))
            {
                Seed.EnsureBusinessUnit(seed, tenantId);
                if (!seed.AiProcessingPolicies.IgnoreQueryFilters().Any(x => x.BusinessUnitId == tenantId))
                {
                    var policy = AiProcessingPolicy.CreateSecureDefault(tenantId, "test", DateTime.UtcNow);
                    policy.ExternalProcessingAllowed = externalProcessingAllowed;
                    policy.AllowedProvider = "Ollama";
                    policy.AllowedModel = AuthorizedModel;
                    // EgressPolicy is now enforced (it used to be persisted and read by
                    // nothing): whole unstructured documents may only egress when the tenant's
                    // own policy says so, INDEPENDENTLY of what any one destination grant
                    // allows. These tests are about the destination grant, so the tenant-level
                    // switch is opened here; AiAndMailEgressGuardTests covers the switch itself.
                    policy.EgressPolicy = AiEgressPolicies.FullDocument;
                    seed.AiProcessingPolicies.Add(policy);
                }
                seed.SaveChanges();
            }

            var tenantScope = new TenantScopeAccessor();
            var tenantContext = new StubTenant(tenantId);
            _provider = new ServiceCollection()
                .AddSingleton<ITenantScopeAccessor>(tenantScope)
                .AddSingleton<ITenantContext>(tenantContext)
                .AddScoped(_ => Database.ContextFor(tenantScope.BusinessUnitId ?? tenantId))
                .BuildServiceProvider();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "https://ollama.com/",
                ["Ollama:Model"] = AuthorizedModel
            }).Build();
            var resolver = new AiProviderEndpointResolver(
                configuration, new NoopLogger<AiProviderEndpointResolver>());
            Descriptor = resolver.Current;

            _trustDb = Database.ContextFor(tenantId);
            Trust = new AiExternalProviderTrustService(
                _trustDb, tenantContext, resolver, new NoopLogger<AiExternalProviderTrustService>());

            // The gate is a REQUIRED dependency of the ledger: the external-dependency
            // ceiling exempts allow-list-authorized destinations, and only the gate can
            // say which those are.
            Governance = new AiGovernanceService(
                _provider.GetRequiredService<IServiceScopeFactory>(), tenantScope, tenantContext, Trust);
        }

        /// <summary>A second tenant sharing the same physical database.</summary>
        public Fixture ForOtherTenant() => new(Database, OtherTenantId, true, ownsDatabase: false);

        /// <summary>
        /// Completed LOCAL governed calls, so an external call sits inside the ledger's
        /// local-first external-dependency ceiling. Mirrors the ratio the existing
        /// AiGovernanceServiceTests use.
        /// </summary>
        public void SeedLocalCallHistory(int count)
        {
            using var db = Database.ContextFor(null);
            var now = DateTime.UtcNow.AddMinutes(-10);
            for (var i = 0; i < count; i++)
                db.AiRequests.Add(new AiRequest
                {
                    Id = Guid.NewGuid(),
                    BusinessUnitId = TenantId,
                    Operation = AiPurposes.RfqExtraction,
                    IdempotencyKey = $"seed-local-{TenantId}-{i}-{Guid.NewGuid():N}",
                    PromptHash = new string('A', 64),
                    PromptVersion = "seed-v1",
                    Provider = "Ollama",
                    ProviderClass = AiProviderClass.Local,
                    Model = "local-model",
                    Status = AiCallStatuses.Succeeded,
                    InputHash = new string('A', 64),
                    TokenSource = AiTokenSources.Estimated,
                    CostStatus = AiCostStatuses.LocalUnpriced,
                    InputTokens = 1,
                    OutputTokens = 1,
                    CreatedOn = now.AddSeconds(i),
                    CompletedOn = now.AddSeconds(i)
                });
            db.SaveChanges();
        }

        /// <summary>Ages every authorization out, so "live" is genuinely time-bounded.</summary>
        public void ExpireAuthorizations()
        {
            using var db = Database.ContextFor(null);
            foreach (var row in db.AiExternalProviderAuthorizations.IgnoreQueryFilters()
                         .Where(x => x.BusinessUnitId == TenantId).ToList())
                row.ExpiresOn = DateTime.UtcNow.AddMinutes(-1);
            db.SaveChanges();
        }

        public void SetDependencyCeilingPercent(decimal percent)
        {
            using var db = Database.ContextFor(null);
            var policy = db.AiProcessingPolicies.IgnoreQueryFilters().Single(x => x.BusinessUnitId == TenantId);
            policy.ExternalDependencyCeilingPercent = percent;
            db.SaveChanges();
        }

        public ChunkedExtractionService Extractor(ILLMService llm) => new(
            llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>(), Trust);

        public Task<AiExternalProviderMutationResult> AuthorizeAsync(
            bool unstructuredAllowed, string? model = AuthorizedModel) =>
            Trust.AuthorizeAsync(TenantId, 4242, Guid.NewGuid().ToString("N"),
                new AuthorizeAiExternalProviderCommand(
                    "Ollama", "https://ollama.com/", model, AiPurposes.RfqExtraction,
                    unstructuredAllowed, "DPA-2026-14 signed; approved by the data protection officer.",
                    null),
                default);

        public void Dispose()
        {
            _trustDb.Dispose();
            _provider.Dispose();
            if (_ownsDatabase) Database.Dispose();
        }
    }

    /// <summary>
    /// An LLM stub that behaves like the real client where it matters for this suite: it
    /// reports the resolved provider descriptor, and it drives the REAL governance ledger
    /// (reserve -> attempt -> settle). That is what lets these tests prove the allow-list
    /// is an extra gate rather than a bypass.
    /// </summary>
    private sealed class GovernedStubLlm : ILLMService
    {
        public const long InputTokens = 1_200;
        public const long OutputTokens = 340;

        private readonly IAiGovernanceService _governance;
        private readonly Queue<LeadExtractionResult?> _responses;

        public GovernedStubLlm(AiProviderDescriptor descriptor, IAiGovernanceService governance,
            params LeadExtractionResult?[] responses)
        {
            ProviderDescriptor = descriptor;
            _governance = governance;
            _responses = new Queue<LeadExtractionResult?>(responses);
        }

        public AiProviderDescriptor ProviderDescriptor { get; }
        public AiProviderClass ProviderClass => ProviderDescriptor.ProviderClass;
        public int CallCount { get; private set; }

        public async Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var reservation = await _governance.ReserveAsync(
                context with { ProviderClass = ProviderClass }, ProviderDescriptor.Provider,
                ProviderDescriptor.Model, fullText,
                Math.Max(1, System.Text.Encoding.UTF8.GetByteCount(fullText)), 4096, 3, cancellationToken);

            var started = DateTime.UtcNow;
            var result = _responses.Count > 0 ? _responses.Dequeue() : null;
            await _governance.RecordAttemptAsync(reservation, new AiAttemptCompletion(
                1, result is null ? AiCallStatuses.Failed : AiCallStatuses.Succeeded, 200, "req-1",
                InputTokens, OutputTokens, AiTokenSources.ProviderExact, 12, null, null,
                result is null ? "invalid_output" : null, started, DateTime.UtcNow), CancellationToken.None);
            await _governance.CompleteAsync(reservation,
                result is null ? AiCallStatuses.Failed : AiCallStatuses.Succeeded,
                InputTokens, OutputTokens, AiTokenSources.ProviderExact,
                result is null ? null : "{}", result is null ? "invalid_output" : null,
                CancellationToken.None);
            return result;
        }

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

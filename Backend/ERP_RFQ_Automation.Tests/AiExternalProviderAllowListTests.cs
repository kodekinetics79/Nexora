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

    // ---- the read-only readiness pre-flight -------------------------------
    //
    // Five controls in three layers, each discoverable only by fixing the previous one and
    // resubmitting, is what dead-lettered every document the 2026-08 pilot sent. These pin
    // that the pre-flight reports ALL of them at once and never disagrees with the gate.

    [Fact]
    public async Task Readiness_ReportsEveryClosedControlAtOnce_NotJustTheFirst()
    {
        using var fixture = new Fixture(externalProcessingAllowed: false);
        fixture.MutatePolicy(policy =>
        {
            policy.EgressPolicy = AiEgressPolicies.RedactedFieldsOnly;
            policy.AllowedModel = "Deepseek-V4-Pro"; // one capital letter, in a different layer
        });

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);

        Assert.False(report.Ready);
        var closed = report.Checks
            .Where(check => check.Status == AiReadinessStatus.Fail)
            .Select(check => check.Code).ToArray();
        Assert.Contains(AiReadinessCodes.ExternalProcessingAllowed, closed);
        Assert.Contains(AiReadinessCodes.AuthorizationPresent, closed);
        Assert.Contains(AiReadinessCodes.EgressPolicyWholeDocument, closed);
        Assert.Contains(AiReadinessCodes.PolicyModelAllowed, closed);
        Assert.Equal(closed.Length, report.BlockingCount);

        // ...and the dependency ceiling is NOT one of them. It reads shut only because control
        // 4 is shut, its instruction is "authorize this destination (controls 5-7)", and
        // counting it turned two closed settings into three — sending an operator to author a
        // grant that either already exists or is not what is stopping the document.
        var ceiling = report.Checks.Single(check => check.Code == AiReadinessCodes.DependencyCeiling);
        Assert.Equal(AiReadinessStatus.Blocked, ceiling.Status);
        Assert.Null(ceiling.DenialReason);
        Assert.DoesNotContain(AiReadinessCodes.DependencyCeiling, closed);
        Assert.Contains("waiting on control 4", ceiling.CurrentValue, StringComparison.Ordinal);
        // Nothing to do here, so nothing is asked of anybody.
        Assert.Equal(string.Empty, ceiling.RequiredValue);
        Assert.Equal(string.Empty, ceiling.SetItIn);

        // The enforcing gate can only ever name the first of them; that is the whole defect.
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        Assert.Equal(decision.Reason, report.FirstBlockingReason);
    }

    [Fact]
    public async Task Readiness_NeverDisagreesWithTheEnforcingGate()
    {
        using (var noGrant = new Fixture())
            await AssertNoDrift(noGrant);

        using (var structuredOnly = new Fixture())
        {
            await structuredOnly.AuthorizeAsync(unstructuredAllowed: false);
            await AssertNoDrift(structuredOnly);
        }

        using (var egressClosed = new Fixture())
        {
            await egressClosed.AuthorizeAsync(unstructuredAllowed: true);
            egressClosed.MutatePolicy(policy => policy.EgressPolicy = "whatever the operator typed");
            await AssertNoDrift(egressClosed);
        }

        using (var disabled = new Fixture())
        {
            await disabled.AuthorizeAsync(unstructuredAllowed: true);
            disabled.MutatePolicy(policy => policy.IsEnabled = false);
            await AssertNoDrift(disabled);
        }

        using (var expired = new Fixture())
        {
            await expired.AuthorizeAsync(unstructuredAllowed: true);
            expired.ExpireAuthorizations();
            await AssertNoDrift(expired);
        }

        using (var open = new Fixture())
        {
            await open.AuthorizeAsync(unstructuredAllowed: true);
            await AssertNoDrift(open);
        }
    }

    [Fact]
    public async Task Readiness_WritesNothingAtAll()
    {
        using var fixture = new Fixture();

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);
        Assert.False(report.Ready);

        using var db = fixture.Database.ContextFor(null);
        Assert.Empty(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiBudgetPeriods.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiExternalProviderAuthorizations.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.TenantGovernanceAuditEvents.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Readiness_SpellsOutTheCaseSensitiveModelComparison_WithTheStringToCopy()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);
        fixture.MutatePolicy(policy => policy.AllowedModel = "Deepseek-V4-Pro");

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);
        var model = report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyModelAllowed);

        Assert.Equal(AiReadinessStatus.Fail, model.Status);
        Assert.Equal("model_denied", model.DenialReason);
        Assert.Contains("CASE-SENSITIVE", model.Detail, StringComparison.Ordinal);
        Assert.Contains(AuthorizedModel, model.RequiredValue, StringComparison.Ordinal);
        Assert.Contains("ai-policy", model.SetItIn, StringComparison.Ordinal);

        // The allow-list gate above it says the document may go — which is exactly why the
        // pre-flight has to report this layer too.
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        Assert.True(decision.Allowed);
        Assert.False(report.Ready);
        Assert.Equal("model_denied", report.FirstBlockingReason);

        // The provider name directly above it is NOT case-sensitive.
        fixture.MutatePolicy(policy =>
        {
            policy.AllowedProvider = "OLLAMA";
            policy.AllowedModel = AuthorizedModel;
        });
        Assert.True((await fixture.Readiness().EvaluateAsync(fixture.TenantId, default)).Ready);
    }

    [Fact]
    public async Task Readiness_ClosesOnAZeroMonthlyBudget_WhichARowCanStillHold()
    {
        // The update endpoint now refuses to WRITE a zero (it is a kill switch wearing a
        // budget's clothes), but rows predating that guard, a seeder, or a hand-run statement
        // can all still hold one, and it refuses every reservation in the ledger below every
        // control the rest of this report covers. Omitting it would recreate the whole defect: an operator
        // opening every lock above, being told READY, and watching every document dead-letter
        // under a code that appeared in no row.
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);
        Assert.True((await fixture.Readiness().EvaluateAsync(fixture.TenantId, default)).Ready);

        fixture.MutatePolicy(policy => policy.MonthlyHardTokenLimit = 0);

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);
        var budget = report.Checks.Single(check => check.Code == AiReadinessCodes.MonthlyHardBudget);
        Assert.False(report.Ready);
        Assert.Equal(AiReadinessStatus.Fail, budget.Status);
        Assert.Equal("hard_budget_exceeded", budget.DenialReason);
        Assert.Equal("hard_budget_exceeded", report.FirstBlockingReason);
        Assert.Contains("ai-policy", budget.SetItIn, StringComparison.Ordinal);

        // Every lock above it still says the document may go, and the ledger still refuses it.
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        Assert.True(decision.Allowed);
        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Governance.ReserveAsync(
            new AiCallContext(fixture.TenantId, AiPurposes.RfqExtraction, "zero-budget", "test-v1",
                ProviderClass: AiProviderClass.External),
            fixture.Descriptor.Provider, fixture.Descriptor.Model, "external", 32, 10, 1, default));
        Assert.Equal("hard_budget_exceeded", denied.Code);
    }

    [Fact]
    public async Task Readiness_ClosesWhenTheMonthRunsOut_NotOnlyWhenTheLimitIsZero()
    {
        // Ordinary mid-month exhaustion reaches the identical state, so the row is read from
        // the period ledger and not merely from the policy's number.
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);
        fixture.MutatePolicy(policy => policy.MonthlyHardTokenLimit = 43);

        // 128 input bytes estimate to 32 tokens; + 10 output tokens over one attempt reserves
        // 42 of the 43. (These were 32 bytes when the reservation added a BYTE count to a
        // token count — the numbers move with the unit fix, the intent does not.)
        await fixture.Governance.ReserveAsync(
            new AiCallContext(fixture.TenantId, AiPurposes.RfqExtraction, "spends-the-month", "test-v1",
                ProviderClass: AiProviderClass.External),
            fixture.Descriptor.Provider, fixture.Descriptor.Model, "external", 128, 10, 1, default);

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);
        var budget = report.Checks.Single(check => check.Code == AiReadinessCodes.MonthlyHardBudget);
        Assert.False(report.Ready);
        Assert.Equal("hard_budget_exceeded", budget.DenialReason);
        Assert.Contains("42", budget.CurrentValue, StringComparison.Ordinal);

        var denied = await Assert.ThrowsAsync<AiPolicyDeniedException>(() => fixture.Governance.ReserveAsync(
            new AiCallContext(fixture.TenantId, AiPurposes.RfqExtraction, "one-too-many", "test-v1",
                ProviderClass: AiProviderClass.External),
            fixture.Descriptor.Provider, fixture.Descriptor.Model, "external", 32, 10, 1, default));
        Assert.Equal("hard_budget_exceeded", denied.Code);
    }

    [Fact]
    public async Task Readiness_WithNoMonthlyCeiling_WarnsWithoutInventingAConstraint()
    {
        // Two things have to be true at once. Nothing is shut — a budget row reported as
        // blocking because nobody typed a number would send operators to change a control that
        // is not closed — and yet "no monthly ceiling" is unbounded spend on a tenant that has
        // not gone live, which is not something to print a green tick next to.
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);
        var budget = report.Checks.Single(check => check.Code == AiReadinessCodes.MonthlyHardBudget);

        Assert.Equal(AiReadinessStatus.Warn, budget.Status);
        Assert.Null(budget.DenialReason);
        Assert.Contains("unbounded", budget.CurrentValue, StringComparison.Ordinal);

        // A warning is not a refusal: the documents extract.
        Assert.True(report.Ready);
        Assert.Equal(0, report.BlockingCount);
        Assert.Equal(1, report.WarningCount);
        Assert.Null(report.FirstBlockingReason);

        // And it stops warning once somebody decides a number.
        fixture.MutatePolicy(policy => policy.MonthlyHardTokenLimit = 2_000_000);
        var decided = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);
        Assert.Equal(AiReadinessStatus.Pass,
            decided.Checks.Single(check => check.Code == AiReadinessCodes.MonthlyHardBudget).Status);
        Assert.Equal(0, decided.WarningCount);
    }

    [Fact]
    public async Task Readiness_OnALocalDeployment_GreysTheEgressControls_ButStillTestsThePolicyRow()
    {
        using var fixture = new Fixture();
        var loopback = new AiProviderEndpointResolver(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://127.0.0.1:11434/",
                ["Ollama:Model"] = "qwen2.5:14b"
            }).Build(),
            new NoopLogger<AiProviderEndpointResolver>());

        var report = await fixture.Readiness(loopback).EvaluateAsync(fixture.TenantId, default);

        Assert.Equal(AiProviderClass.Local, report.ResolvedProvider.ProviderClass);
        foreach (var code in new[]
                 {
                     AiReadinessCodes.ExternalProcessingAllowed, AiReadinessCodes.AuthorizationPresent,
                     AiReadinessCodes.AuthorizationLive, AiReadinessCodes.AuthorizationPurpose,
                     AiReadinessCodes.EgressPolicyWholeDocument, AiReadinessCodes.AuthorizationUnstructured,
                     AiReadinessCodes.DependencyCeiling
                 })
        {
            var greyed = report.Checks.Single(check => check.Code == code);
            Assert.Equal(AiReadinessStatus.NotApplicable, greyed.Status);
            Assert.Null(greyed.DenialReason);
            Assert.Equal(string.Empty, greyed.RequiredValue);
            Assert.Equal(string.Empty, greyed.SetItIn);
        }

        // The policy row's own purpose/provider/model tests still run for a LOCAL reservation,
        // so they are still reported: this fixture's AllowedModel names the external model.
        Assert.Equal(AiReadinessStatus.Pass,
            report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyEnabled).Status);
        Assert.Equal(AiReadinessStatus.Pass,
            report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyProviderAllowed).Status);
        var model = report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyModelAllowed);
        Assert.Equal(AiReadinessStatus.Fail, model.Status);
        Assert.Equal("model_denied", model.DenialReason);
    }

    [Fact]
    public async Task Readiness_RefusesToReportOnAnotherTenant()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        var report = await fixture.Readiness().EvaluateAsync(Fixture.OtherTenantId, default);

        Assert.False(report.Ready);
        Assert.Equal(AiExternalProviderTrustReasons.TenantMismatch, report.FirstBlockingReason);
    }

    [Fact]
    public async Task Readiness_IsSafeToPasteIntoATicket()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        var json = System.Text.Json.JsonSerializer.Serialize(
            await fixture.Readiness().EvaluateAsync(fixture.TenantId, default));

        Assert.DoesNotContain("DPA-2026-14", json, StringComparison.Ordinal);        // justification
        Assert.DoesNotContain("data protection officer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buyer: Acme", json, StringComparison.Ordinal);        // document content
    }

    [Fact]
    public async Task Readiness_AtShippedSecureDefaults_IsNotReady_AndNamesExternalProcessing()
    {
        // A tenant on the day it is provisioned. Its first document died with
        // external_processing_denied and nothing else — the first of several closed controls,
        // with no hint that more were queued behind it. That is what cost the 2026-08 pilot
        // its days: one lock revealed per submission.
        using var fixture = new Fixture();
        fixture.ResetToShippedSecureDefaults();

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);

        Assert.False(report.Ready);
        var external = report.Checks.Single(check => check.Code == AiReadinessCodes.ExternalProcessingAllowed);
        Assert.Equal(AiReadinessStatus.Fail, external.Status);
        Assert.Equal(AiExternalProviderTrustReasons.PolicyExternalProcessingDenied, external.DenialReason);
        Assert.Equal("ExternalProcessingAllowed = false", external.CurrentValue);
        Assert.Equal("ExternalProcessingAllowed = true", external.RequiredValue);
        Assert.Contains("ai-policy", external.SetItIn, StringComparison.Ordinal);
        Assert.Equal(AiExternalProviderTrustReasons.PolicyExternalProcessingDenied, report.FirstBlockingReason);

        // The controls the shipped defaults leave OPEN must stay open. A report that reddened
        // rows nobody has to touch would send operators to change working settings, which is
        // the failure mode a diagnostic tool is least forgiven for.
        Assert.Equal(AiReadinessStatus.Pass,
            report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyPresent).Status);
        Assert.Equal(AiReadinessStatus.Pass,
            report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyEnabled).Status);
        Assert.Equal(AiReadinessStatus.Pass,
            report.Checks.Single(check => check.Code == AiReadinessCodes.PolicyPurposeAllowed).Status);
    }

    [Fact]
    public async Task Readiness_AfterOpeningOnlyExternalProcessing_StillNamesTheAllowListAndTheEgressPolicy()
    {
        // The defining symptom, reproduced: opening external processing is the obvious first
        // move, and under the old behaviour the NEXT document died on the allow-list and the
        // one after that on the egress policy. The report must name every remaining lock now,
        // not merely the next one — that property is the entire point of the pre-flight.
        using var fixture = new Fixture();
        fixture.ResetToShippedSecureDefaults();
        fixture.MutatePolicy(policy => policy.ExternalProcessingAllowed = true);

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);

        Assert.False(report.Ready);
        Assert.Equal(AiReadinessStatus.Pass,
            report.Checks.Single(check => check.Code == AiReadinessCodes.ExternalProcessingAllowed).Status);

        var grant = report.Checks.Single(check => check.Code == AiReadinessCodes.AuthorizationPresent);
        Assert.Equal(AiReadinessStatus.Fail, grant.Status);
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, grant.DenialReason);
        Assert.Contains("ai-providers", grant.SetItIn, StringComparison.Ordinal);

        var egress = report.Checks.Single(check => check.Code == AiReadinessCodes.EgressPolicyWholeDocument);
        Assert.Equal(AiReadinessStatus.Fail, egress.Status);
        Assert.Equal(AiExternalProviderTrustReasons.EgressPolicyForbidsWholeDocuments, egress.DenialReason);
        Assert.Equal($"EgressPolicy = \"{AiEgressPolicies.RedactedFieldsOnly}\"", egress.CurrentValue);
        Assert.Equal($"EgressPolicy = \"{AiEgressPolicies.FullDocument}\"", egress.RequiredValue);
        Assert.Contains("ai-policy", egress.SetItIn, StringComparison.Ordinal);

        // Two distinct locks in two different layers, reported together and reachable in one
        // submission. The enforcing gate can still only ever name the first of them.
        Assert.True(report.BlockingCount >= 2);
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, decision.Reason);
        Assert.Equal(decision.Reason, report.FirstBlockingReason);
    }

    [Fact]
    public async Task Readiness_OnAFullyConfiguredTenant_IsReady()
    {
        using var fixture = new Fixture();
        await fixture.AuthorizeAsync(unstructuredAllowed: true);

        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);

        Assert.True(report.Ready);
        Assert.Null(report.FirstBlockingReason);
        Assert.Equal(0, report.BlockingCount);
        Assert.DoesNotContain(report.Checks, check => check.Status == AiReadinessStatus.Fail);

        // Ready is a report and never an enforcement input, so the gate has to agree on its
        // own: a green chain that the gate then refuses is the defect running backwards.
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task RefactoredGate_StillRefusesEveryUnauthorizedUnstructuredCall_WithItsOriginalReasonCode()
    {
        // THE regression. Making the chain reportable meant threading a lock list through the
        // enforcing method and replacing its early returns with a derived verdict. If that
        // moved a return, softened a comparison, or let one closed lock fall through, a
        // document egresses that never could before — and no amount of diagnostics is worth
        // that. Every closed lock is driven through the REAL extraction path here, not the
        // evaluator, and must still refuse with the identical code and zero provider calls.

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.PolicyMissing,
            fixture =>
            {
                using var db = fixture.Database.ContextFor(null);
                db.AiProcessingPolicies.RemoveRange(db.AiProcessingPolicies.IgnoreQueryFilters()
                    .Where(x => x.BusinessUnitId == fixture.TenantId));
                db.SaveChanges();
                return Task.CompletedTask;
            });

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.PolicyDisabled,
            async fixture =>
            {
                await fixture.AuthorizeAsync(unstructuredAllowed: true);
                fixture.MutatePolicy(policy => policy.IsEnabled = false);
            });

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.PolicyExternalProcessingDenied,
            async fixture =>
            {
                await fixture.AuthorizeAsync(unstructuredAllowed: true);
                fixture.MutatePolicy(policy => policy.ExternalProcessingAllowed = false);
            });

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.NotAuthorized, _ => Task.CompletedTask);

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.Revoked,
            async fixture =>
            {
                var granted = await fixture.AuthorizeAsync(unstructuredAllowed: true);
                await fixture.Trust.RevokeAsync(fixture.TenantId, 4242, Guid.NewGuid().ToString("N"),
                    new RevokeAiExternalProviderCommand(granted.Authorization.Id, "Contract ended."), default);
            });

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.Expired,
            async fixture =>
            {
                await fixture.AuthorizeAsync(unstructuredAllowed: true);
                fixture.ExpireAuthorizations();
            });

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.PurposeNotAuthorized,
            fixture => fixture.Trust.AuthorizeAsync(fixture.TenantId, 4242, Guid.NewGuid().ToString("N"),
                new AuthorizeAiExternalProviderCommand(
                    "Ollama", "https://ollama.com/", AuthorizedModel, AiPurposes.BoqDraft,
                    true, "DPA-2026-14 signed; approved by the data protection officer.", null),
                default));

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.EgressPolicyForbidsWholeDocuments,
            async fixture =>
            {
                await fixture.AuthorizeAsync(unstructuredAllowed: true);
                fixture.MutatePolicy(policy => policy.EgressPolicy = AiEgressPolicies.RedactedFieldsOnly);
            });

        await AssertTheExtractionPathStillRefuses(
            AiExternalProviderTrustReasons.UnstructuredNotAuthorized,
            fixture => fixture.AuthorizeAsync(unstructuredAllowed: false));
    }

    /// <summary>
    /// Closes one lock, submits a real document down the enforcing path, and pins that it
    /// died with that lock's own code having reached no provider and opened no reservation.
    /// </summary>
    private static async Task AssertTheExtractionPathStillRefuses(
        string expectedReason, Func<Fixture, Task> closeTheLock)
    {
        using var fixture = new Fixture();
        await closeTheLock(fixture);

        var llm = new GovernedStubLlm(fixture.Descriptor, fixture.Governance,
            Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await fixture.Extractor(llm).ExtractUnstructuredAsync(Doc(fixture.TenantId, 2));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount); // not one byte of the document left the process
        Assert.Contains(expectedReason, outcome.ReviewReason);

        using var db = fixture.Database.ContextFor(null);
        Assert.Empty(await db.AiRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiBudgetPeriods.IgnoreQueryFilters().ToListAsync());
    }

    private static async Task AssertNoDrift(Fixture fixture)
    {
        var decision = await fixture.Trust.EvaluateAsync(
            fixture.TenantId, fixture.Descriptor, AiPurposes.RfqExtraction, true, default);
        var report = await fixture.Readiness().EvaluateAsync(fixture.TenantId, default);

        Assert.Equal(decision.Allowed, report.Ready);
        Assert.Equal(decision.Allowed ? null : decision.Reason, report.FirstBlockingReason);
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

    // ---- the deployment does not consent on a tenant's behalf -------------
    //
    // Both callers of this gate — the token ledger's AuthorizeExternalAsync and the
    // conversational extraction gate — reach it only when the resolved provider class is
    // External. So every grant the auto-authorization path can write is a decision to send one
    // tenant's document text to a host they do not control. It shipped defaulting to ON, wrote
    // those rows as "system:auto-internal", and the read-only pre-flight performs no writes and
    // so could not model it: the console went on reporting the very controls closed that a
    // submitted document had already opened.

    [Fact]
    public async Task DeploymentAutoAuthorization_IsOffByDefault_AndThePreFlightThenAgreesWithTheGate()
    {
        using var database = new TestDb();
        const long tenantId = 91_401;
        SeedSecureDefaultPolicy(database, tenantId);

        // Configuration is PRESENT — this is a composed application, not a bare unit test —
        // and simply does not mention the auto-authorization key.
        var configuration = OllamaConfiguration();
        var resolver = new AiProviderEndpointResolver(
            configuration, new NoopLogger<AiProviderEndpointResolver>());
        using var db = database.ContextFor(tenantId);
        var trust = new AiExternalProviderTrustService(
            db, new StubTenant(tenantId), resolver,
            new NoopLogger<AiExternalProviderTrustService>(), configuration);

        var decision = await trust.EvaluateAsync(
            tenantId, resolver.Current, AiPurposes.RfqExtraction, unstructuredPayload: true, default);

        Assert.Equal(AiProviderClass.External, resolver.Current.ProviderClass);
        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.PolicyExternalProcessingDenied, decision.Reason);

        // Nothing was decided on the tenant's behalf: no grant exists, and the policy still
        // says exactly what provisioning wrote.
        using (var check = database.ContextFor(null))
        {
            Assert.Empty(await check.AiExternalProviderAuthorizations.IgnoreQueryFilters().ToListAsync());
            var policy = await check.AiProcessingPolicies.IgnoreQueryFilters()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.False(policy.ExternalProcessingAllowed);
            Assert.Equal(AiEgressPolicies.RedactedFieldsOnly, policy.EgressPolicy);
            Assert.Equal("test", policy.UpdatedBy);
        }

        // The property the whole pre-flight exists for: what an operator is shown and what a
        // document actually meets are the same statement.
        var report = await new AiExtractionReadinessService(db, trust, resolver)
            .EvaluateAsync(tenantId, default);
        Assert.False(report.Ready);
        Assert.Equal(decision.Reason, report.FirstBlockingReason);
    }

    [Fact]
    public async Task DeploymentAutoAuthorization_WhenExplicitlyTurnedOn_WritesARealRevocableGrant()
    {
        // A single-tenant installation whose deployer IS the customer may still opt in. It gets
        // real rows rather than a short circuit, so revocation, the Trust Center and the ledger
        // all keep working on the evidence they always used — the grant simply names the
        // deployment, and its justification names the key that authorised it.
        using var database = new TestDb();
        const long tenantId = 91_402;
        SeedSecureDefaultPolicy(database, tenantId);

        var configuration = OllamaConfiguration(
            (AiExternalProviderTrustService.AutoProvisionConfigKey, "true"));
        var resolver = new AiProviderEndpointResolver(
            configuration, new NoopLogger<AiProviderEndpointResolver>());
        using var db = database.ContextFor(tenantId);
        var trust = new AiExternalProviderTrustService(
            db, new StubTenant(tenantId), resolver,
            new NoopLogger<AiExternalProviderTrustService>(), configuration);

        var decision = await trust.EvaluateAsync(
            tenantId, resolver.Current, AiPurposes.RfqExtraction, unstructuredPayload: true, default);

        Assert.True(decision.Allowed);
        using var check = database.ContextFor(null);
        var grant = Assert.Single(
            await check.AiExternalProviderAuthorizations.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(AiExternalProviderTrustService.AutoProvisionActor, grant.AuthorizedBy);
        Assert.Contains(AiExternalProviderTrustService.AutoProvisionConfigKey, grant.Justification,
            StringComparison.Ordinal);
        var policy = await check.AiProcessingPolicies.IgnoreQueryFilters()
            .SingleAsync(x => x.BusinessUnitId == tenantId);
        Assert.True(policy.ExternalProcessingAllowed);
        Assert.Equal(AiEgressPolicies.FullDocument, policy.EgressPolicy);
    }

    private static IConfiguration OllamaConfiguration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = AuthorizedEndpoint + "/",
            ["Ollama:Model"] = AuthorizedModel
        };
        foreach (var (key, value) in extra) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static void SeedSecureDefaultPolicy(TestDb database, long tenantId)
    {
        using var seed = database.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, tenantId);
        seed.AiProcessingPolicies.Add(
            AiProcessingPolicy.CreateSecureDefault(tenantId, "test", DateTime.UtcNow));
        seed.SaveChanges();
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
        public IAiProviderEndpointResolver Resolver { get; }

        /// <summary>
        /// The read-only pre-flight over the SAME database and the SAME gate the extraction
        /// path uses, so a disagreement between report and enforcement is a test failure.
        /// </summary>
        public AiExtractionReadinessService Readiness(IAiProviderEndpointResolver? resolver = null) =>
            new(_trustDb, Trust, resolver ?? Resolver);

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
            Resolver = resolver;
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

        public void SetDependencyCeilingPercent(decimal percent) =>
            MutatePolicy(policy => policy.ExternalDependencyCeilingPercent = percent);

        /// <summary>
        /// Undoes the constructor's convenience opens, leaving exactly what
        /// <see cref="AiProcessingPolicy.CreateSecureDefault"/> ships. Taken FROM that factory
        /// rather than restated here, so a future change to the shipped posture reaches these
        /// tests instead of being quietly contradicted by a hardcoded copy of the old one.
        /// </summary>
        public void ResetToShippedSecureDefaults()
        {
            var shipped = AiProcessingPolicy.CreateSecureDefault(TenantId, "test", DateTime.UtcNow);
            MutatePolicy(policy =>
            {
                policy.IsEnabled = shipped.IsEnabled;
                policy.ExternalProcessingAllowed = shipped.ExternalProcessingAllowed;
                policy.AllowedPurposes = shipped.AllowedPurposes;
                policy.AllowedProvider = shipped.AllowedProvider;
                policy.AllowedModel = shipped.AllowedModel;
                policy.EgressPolicy = shipped.EgressPolicy;
                policy.ExternalDependencyCeilingPercent = shipped.ExternalDependencyCeilingPercent;
                policy.MonthlySoftTokenLimit = shipped.MonthlySoftTokenLimit;
                policy.MonthlyHardTokenLimit = shipped.MonthlyHardTokenLimit;
            });
        }

        /// <summary>Edits the tenant's policy row out of band, as an Owner would.</summary>
        public void MutatePolicy(Action<AiProcessingPolicy> change)
        {
            using var db = Database.ContextFor(null);
            var policy = db.AiProcessingPolicies.IgnoreQueryFilters().Single(x => x.BusinessUnitId == TenantId);
            change(policy);
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

using System.Reflection;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

public sealed class AiTrustCenterTests
{
    [Fact]
    public void Tenant_trust_center_is_read_only_at_both_endpoint_and_service_boundaries()
    {
        var controllerActions = typeof(PlatformGovernanceController).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        var trustRead = Assert.Single(controllerActions, method => method.Name == nameof(
            PlatformGovernanceController.GetAiTrust));
        Assert.Equal("ai-trust", Assert.Single(trustRead.GetCustomAttributes<HttpGetAttribute>()).Template);
        var providerRead = Assert.Single(controllerActions, method => method.Name == nameof(
            PlatformGovernanceController.GetExternalProviders));
        Assert.Equal("ai-trust/external-providers",
            Assert.Single(providerRead.GetCustomAttributes<HttpGetAttribute>()).Template);

        var tenantPolicyMutationRoutes = controllerActions
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Where(route => route.HttpMethods.Any(verb => verb is "PUT" or "POST")
                && route.Template?.StartsWith("ai-trust", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Empty(tenantPolicyMutationRoutes);

        var serviceSurface = typeof(AiTrustCenterService).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Collection(serviceSurface, method => Assert.Equal(nameof(AiTrustCenterService.GetAsync), method.Name));

        var providerTrustSurface = typeof(IAiExternalProviderTrust).GetMethods();
        Assert.DoesNotContain(providerTrustSurface, method => method.Name is "AuthorizeAsync" or "RevokeAsync");
        var providerServiceSurface = typeof(AiExternalProviderTrustService).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(providerServiceSurface, method => method.Name is "AuthorizeAsync" or "RevokeAsync");
    }

    [Fact]
    public void Platform_Owner_routes_are_the_only_AI_governance_mutation_surface()
    {
        var platformMutation = typeof(TenantsController).GetMethod(nameof(TenantsController.UpdateAiPolicy))!;
        Assert.Equal("{id:long}/ai-policy",
            Assert.Single(platformMutation.GetCustomAttributes<HttpPutAttribute>()).Template);
        Assert.Equal(PlatformPolicies.Owner,
            Assert.Single(platformMutation.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        foreach (var actionName in new[]
                 {
                     nameof(TenantsController.AuthorizeAiProvider),
                     nameof(TenantsController.RevokeAiProvider)
                 })
        {
            var providerMutation = typeof(TenantsController).GetMethod(actionName)!;
            Assert.Equal(PlatformPolicies.Owner,
                Assert.Single(providerMutation.GetCustomAttributes<AuthorizeAttribute>()).Policy);
            Assert.NotEmpty(providerMutation.GetCustomAttributes<HttpPostAttribute>());
        }

        var tenantActions = typeof(PlatformGovernanceController).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(tenantActions, method => method.Name.Contains("AiTrustPolicy",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Usage_reconciles_local_external_tokens_cost_and_policy_breach()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(62_021);
        Seed.BusinessUnit(context, 62_021);
        var policy = Policy(62_021);
        policy.ExternalDependencyCeilingPercent = 10;
        context.AiProcessingPolicies.Add(policy);
        context.AiRequests.AddRange(Request(62_021, AiProviderClass.Local, null),
            Request(62_021, AiProviderClass.External, 1.25m));
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_021, default);

        Assert.Equal(2, view.Usage.Requests);
        Assert.Equal(50m, view.Usage.ExternalDependencyPercent);
        Assert.True(view.Usage.DependencyCeilingBreached);
        Assert.Equal(1.25m, view.Usage.EstimatedExternalCost["USD"]);
        Assert.Equal(240, view.Usage.InputTokens + view.Usage.OutputTokens);
        // Startup-resolved deployment stance, surfaced read-only in the payload.
        Assert.Equal(nameof(InferencePosture.LocalFirst), view.InferencePosture);
    }

    [Fact]
    public async Task Trust_center_payload_declares_an_external_deployments_posture()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(62_031);
        Seed.BusinessUnit(context, 62_031);
        context.AiProcessingPolicies.Add(Policy(62_031));
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver("https://ollama.com/"))
            .GetAsync(62_031, default);

        Assert.Equal(nameof(InferencePosture.ExternalAuthorized), view.InferencePosture);
    }

    [Fact]
    public async Task Authorized_egress_is_reported_but_does_not_breach_the_ceiling()
    {
        // The defect this closes. On any deployment whose inference endpoint is not loopback
        // every call is classified External — correctly, that is the whole point of
        // AiProviderEndpoint failing closed — so the Trust Center's own raw external/total
        // share sat at 100% forever and the screen carried a standing red ceiling breach.
        // Enforcement was denying nothing: AiGovernanceService exempts a call holding a live
        // allow-list receipt, and every one of these holds one. The screen now measures the
        // same thing the enforcer does, so the egress is REPORTED — 5 of 5 external — and the
        // ceiling is silent.
        using var database = new TestDb();
        await using var context = database.ContextFor(62_041);
        Seed.BusinessUnit(context, 62_041);
        var policy = Policy(62_041);
        policy.ExternalDependencyCeilingPercent = 10;
        context.AiProcessingPolicies.Add(policy);
        for (var i = 0; i < 5; i++)
            context.AiRequests.Add(Request(62_041, AiProviderClass.External, 0.10m, authorizationId: 77));
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_041, default);

        Assert.Equal(5, view.Usage.ExternalRequests);
        Assert.Equal(5, view.Usage.AuthorizedExternalRequests);
        Assert.Equal(0m, view.Usage.ExternalDependencyPercent);
        Assert.False(view.Usage.DependencyCeilingBreached);
        // The sample is published so the percentage can be reconciled against the ledger.
        Assert.Equal(5, view.Dependency.External);
        Assert.Equal(5, view.Dependency.AuthorizedExternal);
        Assert.False(view.Dependency.CeilingBreached);
    }

    [Fact]
    public async Task Unauthorized_egress_still_breaches_the_ceiling()
    {
        // The exemption is for authorized calls only. An external call with no receipt on the
        // row consumes the ceiling exactly as before — the fix narrows what counts, it does
        // not switch the control off.
        using var database = new TestDb();
        await using var context = database.ContextFor(62_051);
        Seed.BusinessUnit(context, 62_051);
        var policy = Policy(62_051);
        policy.ExternalDependencyCeilingPercent = 10;
        context.AiProcessingPolicies.Add(policy);
        for (var i = 0; i < 3; i++)
            context.AiRequests.Add(Request(62_051, AiProviderClass.External, 0.10m, authorizationId: 77));
        context.AiRequests.Add(Request(62_051, AiProviderClass.External, 0.10m));
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_051, default);

        Assert.Equal(4, view.Usage.ExternalRequests);
        Assert.Equal(3, view.Usage.AuthorizedExternalRequests);
        Assert.Equal(25m, view.Usage.ExternalDependencyPercent);
        Assert.True(view.Usage.DependencyCeilingBreached);
    }

    [Fact]
    public async Task A_ledger_predating_the_authorization_receipt_does_not_breach()
    {
        // ExternalAuthorizationId has only been written on every authorized external
        // reservation since 555c5b8 (2026-08-10); the column arrived on 2026-08-04 and nothing
        // backfills it. So an older authorized call is indistinguishable from an unauthorized
        // one. Measured over an unbounded window those rows vote forever: twelve of them, with
        // nothing else in the ledger, reported 100% dependency and a red ceiling banner beside
        // a card reading "Monthly requests 0". The sample is bounded to the period the screen
        // reports on, which excludes them by construction.
        using var database = new TestDb();
        await using var context = database.ContextFor(62_061);
        Seed.BusinessUnit(context, 62_061);
        var policy = Policy(62_061);
        policy.ExternalDependencyCeilingPercent = 10;
        context.AiProcessingPolicies.Add(policy);
        var lastMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0,
            DateTimeKind.Utc).AddDays(-5);
        for (var i = 0; i < 12; i++)
        {
            var legacy = Request(62_061, AiProviderClass.External, 0.10m);
            legacy.CreatedOn = lastMonth;
            context.AiRequests.Add(legacy);
        }
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_061, default);

        Assert.False(view.Usage.DependencyCeilingBreached);
        Assert.Equal(0m, view.Usage.ExternalDependencyPercent);
        // And the percentage is reconcilable with the counters beside it: both say nothing
        // happened this month, instead of "100% of zero calls".
        Assert.Equal(0, view.Usage.Requests);
        Assert.Equal(0, view.Dependency.Total);
    }

    [Fact]
    public async Task A_denied_reservation_is_not_counted_as_egress()
    {
        // A refusal never reached a provider. Counted, the tile read "0 / 20 (0 authorized)"
        // for twenty REFUSALS — the control working, rendered as twenty unapproved calls.
        using var database = new TestDb();
        await using var context = database.ContextFor(62_071);
        Seed.BusinessUnit(context, 62_071);
        context.AiProcessingPolicies.Add(Policy(62_071));
        for (var i = 0; i < 20; i++)
        {
            var denied = Request(62_071, AiProviderClass.External, null);
            denied.Status = AiCallStatuses.Denied;
            context.AiRequests.Add(denied);
        }
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_071, default);

        Assert.Equal(0, view.Usage.ExternalRequests);
        Assert.Equal(0, view.Usage.Requests);
        Assert.Equal(20, view.Usage.DeniedRequests);
        Assert.False(view.Usage.DependencyCeilingBreached);
    }

    [Fact]
    public async Task Monthly_counters_report_the_whole_month_not_the_ledger_page()
    {
        // One capped query answered two different questions. The ledger tab shows the most
        // recent calls and is paged at 100; the counters above it claim to describe the month,
        // and silently saturated at the same 100 — so every total under them was wrong by the
        // overflow, on exactly the tenants busy enough to care.
        using var database = new TestDb();
        await using var context = database.ContextFor(62_081);
        Seed.BusinessUnit(context, 62_081);
        context.AiProcessingPolicies.Add(Policy(62_081));
        for (var i = 0; i < 150; i++)
            context.AiRequests.Add(Request(62_081, AiProviderClass.Local, null));
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_081, default);

        Assert.Equal(150, view.Usage.Requests);
        Assert.Equal(150, view.Usage.LocalRequests);
        // The ledger itself stays a page.
        Assert.Equal(100, view.Requests.Count);
    }

    [Fact]
    public async Task A_share_over_a_fractional_ceiling_breaches_it()
    {
        // The breach was decided on the ROUNDED share while the ceiling is stored to two
        // decimals and validated to 0..10, so 2 unauthorized in 79 — 2.53%, genuinely over a
        // 2.5% ceiling — rounded to 2.5 and 2.5 > 2.5 is false. Judged exact, reported rounded.
        using var database = new TestDb();
        await using var context = database.ContextFor(62_091);
        Seed.BusinessUnit(context, 62_091);
        var policy = Policy(62_091);
        policy.ExternalDependencyCeilingPercent = 2.5m;
        context.AiProcessingPolicies.Add(policy);
        for (var i = 0; i < 2; i++)
            context.AiRequests.Add(Request(62_091, AiProviderClass.External, 0.10m));
        for (var i = 0; i < 77; i++)
            context.AiRequests.Add(Request(62_091, AiProviderClass.Local, null));
        await context.SaveChangesAsync();

        var view = await new AiTrustCenterService(context, Resolver()).GetAsync(62_091, default);

        Assert.Equal(2.5m, view.Usage.ExternalDependencyPercent);
        Assert.True(view.Usage.DependencyCeilingBreached);
    }

    /// <summary>No base URL configured resolves to the loopback default → LocalFirst.</summary>
    private static AiProviderEndpointResolver Resolver(string? baseUrl = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = baseUrl
        }).Build(),
        new NoopLogger<AiProviderEndpointResolver>());

    private static AiProcessingPolicy Policy(long tenantId) => new()
    {
        BusinessUnitId = tenantId, IsEnabled = true, ExternalProcessingAllowed = false,
        AllowedPurposes = "RfqExtraction,BoqDraft", ExternalDependencyCeilingPercent = 10,
        RedactionRequired = true, AllowedDataClassifications = "Public,Internal",
        EgressPolicy = "RedactedFieldsOnly", DataResidency = "TenantApprovedRegion",
        RetentionDays = 30, PrivacyReviewRequired = true, Version = 1,
        UpdatedOn = DateTime.UtcNow, UpdatedBy = "test"
    };

    private static AiRequest Request(long tenantId, AiProviderClass providerClass, decimal? cost,
        long? authorizationId = null) => new()
    {
        ExternalAuthorizationId = authorizationId,
        Id = Guid.NewGuid(), BusinessUnitId = tenantId, Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"), PromptHash = new string('A', 64),
        PromptVersion = "v1", Provider = providerClass == AiProviderClass.Local ? "local" : "external",
        ProviderClass = providerClass, Model = "specialist", Status = AiCallStatuses.Succeeded,
        InputTokens = 100, OutputTokens = 20, EstimatedCost = cost,
        CostCurrency = cost.HasValue ? "USD" : null,
        CostStatus = cost.HasValue ? AiCostStatuses.EstimatedConfiguredRate : AiCostStatuses.LocalUnpriced,
        TokenSource = AiTokenSources.ProviderExact, CreatedOn = DateTime.UtcNow,
        StartedOn = DateTime.UtcNow, CompletedOn = DateTime.UtcNow
    };
}

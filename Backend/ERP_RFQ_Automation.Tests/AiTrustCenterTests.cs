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

    private static AiRequest Request(long tenantId, AiProviderClass providerClass, decimal? cost) => new()
    {
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

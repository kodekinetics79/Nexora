using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

public sealed class AiTrustCenterTests
{
    [Fact]
    public async Task Policy_update_is_versioned_idempotent_and_rollback_restores_prior_controls()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(62_001);
        Seed.BusinessUnit(context, 62_001);
        context.AiProcessingPolicies.Add(Policy(62_001));
        await context.SaveChangesAsync();
        var service = new AiTrustCenterService(context, Resolver());
        var command = Command(1) with
        {
            ExternalProcessingAllowed = true,
            AllowedProvider = "approved-provider",
            AllowedModel = "approved-model",
            ExternalDependencyCeilingPercent = 7.5m,
            Reason = "Approve redacted fallback for classified RFQs"
        };

        var updated = await service.UpdateAsync(62_001, 51, "ai-policy-update", command, default);
        var replay = await service.UpdateAsync(62_001, 51, "ai-policy-update", command, default);
        var eventId = await context.TenantGovernanceAuditEvents
            .Where(x => x.Action == "POLICY_UPDATED").Select(x => x.Id).SingleAsync();
        var rolledBack = await service.RollbackAsync(62_001, 51, "ai-policy-rollback",
            new(updated.Policy.Version, eventId, "External fallback window closed"), default);

        Assert.True(replay.IdempotentReplay);
        Assert.True(updated.Policy.ExternalProcessingAllowed);
        Assert.Equal(7.5m, updated.Policy.ExternalDependencyCeilingPercent);
        Assert.False(rolledBack.Policy.ExternalProcessingAllowed);
        Assert.Equal(10m, rolledBack.Policy.ExternalDependencyCeilingPercent);
        Assert.Equal(2, await context.TenantGovernanceAuditEvents.CountAsync());
    }

    [Fact]
    public async Task External_policy_cannot_bypass_redaction_privacy_or_dependency_ceiling()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(62_011);
        Seed.BusinessUnit(context, 62_011);
        context.AiProcessingPolicies.Add(Policy(62_011));
        await context.SaveChangesAsync();
        var service = new AiTrustCenterService(context, Resolver());

        await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() => service.UpdateAsync(
            62_011, 52, "unsafe-egress", Command(1) with
            {
                ExternalProcessingAllowed = true,
                RedactionRequired = false,
                PrivacyReviewRequired = false,
                ExternalDependencyCeilingPercent = 11,
                AllowedProvider = "provider",
                AllowedModel = "model"
            }, default));
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

    private static UpdateAiTrustPolicyCommand Command(long version) => new(version, true, false,
        "RfqExtraction,BoqDraft", null, null, 50_000, 100_000, 10_000,
        null, null, null, null, 10, true, "Public,Internal", "RedactedFieldsOnly",
        "TenantApprovedRegion", 30, false, true, null, null, null, "Test update");

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

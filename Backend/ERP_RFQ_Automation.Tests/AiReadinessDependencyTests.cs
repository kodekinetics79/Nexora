using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The extraction pre-flight's dependency row, which quotes a forecast it labels UNAUTHORIZED.
///
/// <para>It was measured with <c>AiPolicyDenials.ExternalDependencyRatio</c>, a provider-class
/// primitive that never sees the authorization receipt and therefore counts approved calls
/// too. On a deployment whose inference endpoint is not loopback that printed "one more
/// UNAUTHORIZED external call would be 100.0%" at a tenant whose every call was approved — the
/// same sentence-versus-number mismatch the AI Trust banner carried, on a row served to the
/// same tenant by the same controller. A number and the word attached to it have to describe
/// the same population.</para>
/// </summary>
public sealed class AiReadinessDependencyTests
{
    private const long Tenant = 62_501;

    [Fact]
    public async Task The_forecast_excludes_the_authorized_calls_its_wording_excludes()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        var policy = AiProcessingPolicy.CreateSecureDefault(Tenant, "test", DateTime.UtcNow);
        // Control 4 closed: the state every tenant ships in, and the one an operator is in the
        // middle of leaving when they read this screen.
        policy.ExternalProcessingAllowed = false;
        policy.ExternalDependencyCeilingPercent = 10m;
        context.AiProcessingPolicies.Add(policy);
        for (var i = 0; i < 40; i++)
            context.AiRequests.Add(Authorized(i));
        await context.SaveChangesAsync();

        var report = await ServiceFor(context).EvaluateAsync(Tenant, default);

        var ceiling = Assert.Single(report.Checks, x => x.Code == AiReadinessCodes.DependencyCeiling);
        // One unauthorized call against forty approved ones is 1 in 41, not 41 in 41.
        Assert.Contains("UNAUTHORIZED external call would be 2.4%", ceiling.CurrentValue);
        Assert.DoesNotContain("would be 100.0%", ceiling.CurrentValue);
        // And it says how many it set aside, so the arithmetic is checkable from the sentence.
        Assert.Contains("40 authorized call(s) in that sample are exempt", ceiling.CurrentValue);
    }

    private static AiExtractionReadinessService ServiceFor(
        ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
    {
        var resolver = new AiProviderEndpointResolver(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "https://ollama.hosted.example.com"
            }).Build(),
            new NoopLogger<AiProviderEndpointResolver>());
        var trust = new AiExternalProviderTrustService(context, new StubTenant(Tenant), resolver,
            new NoopLogger<AiExternalProviderTrustService>(), new ConfigurationBuilder().Build());
        return new AiExtractionReadinessService(context, trust, resolver);
    }

    private static AiRequest Authorized(int minutesAgo) => new()
    {
        Id = Guid.NewGuid(), BusinessUnitId = Tenant, Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"), PromptHash = new string('a', 64),
        PromptVersion = "v1", Provider = "Ollama", ProviderClass = AiProviderClass.External,
        Model = "deepseek-v4-pro", Status = AiCallStatuses.Succeeded,
        InputTokens = 100, OutputTokens = 20, ExternalAuthorizationId = 77,
        CostStatus = AiCostStatuses.RateUnavailable, TokenSource = AiTokenSources.ProviderExact,
        CreatedOn = DateTime.UtcNow.AddMinutes(-minutesAgo),
        StartedOn = DateTime.UtcNow.AddMinutes(-minutesAgo),
        CompletedOn = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };
}

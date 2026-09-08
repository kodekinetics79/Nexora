using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Xunit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The external-dependency ceiling governs UNAUTHORIZED external usage. Quality Analytics
/// judged the raw external share against it, so every deployment whose inference endpoint is
/// not loopback carried a standing "Critical" recommendation while enforcement denied nothing
/// — under a sentence that already told the reader authorized calls were exempt. A Critical
/// nobody can act on is how an auditor learns to skip the list.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class QualityAnalyticsExternalDependencyTests(PostgreSqlTestDatabase database)
{
    // QualityAnalyticsService joins occurrences to documents on a composite key, which SQLite
    // cannot translate — the same reason the existing Wave-1 quality coverage is PostgreSQL-only.
    private const long Tenant = 71_101;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Authorized_egress_is_reported_in_full_but_raises_no_recommendation()
    {
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        // Comfortably past the default 30-row minimum sample, so the recommendation is
        // evaluated rather than skipped for want of evidence.
        for (var i = 0; i < 40; i++)
            context.AiRequests.Add(External(authorizationId: 501));
        await context.SaveChangesAsync();

        var view = await new QualityAnalyticsService(context).GetAsync(Tenant, 30, null, default);

        // Egress is still published whole — the auditor's question is "did anything leave the
        // box", and the answer is yes, all of it.
        var raw = Assert.Single(view.Metrics, x => x.Key == "external-dependency");
        Assert.Equal(100m, raw.Value);
        // And the governed ratio is zero, because every one of those calls was approved.
        var governed = Assert.Single(view.Metrics, x => x.Key == "unauthorized-external-dependency");
        Assert.Equal(0m, governed.Value);
        Assert.DoesNotContain(view.Recommendations, x => x.Priority == "Critical");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Unauthorized_egress_still_raises_the_recommendation()
    {
        await using var context = database.ContextFor(Tenant + 1);
        Seed.EnsureBusinessUnit(context, Tenant + 1);
        for (var i = 0; i < 30; i++)
            context.AiRequests.Add(External(authorizationId: 501, tenantId: Tenant + 1));
        for (var i = 0; i < 10; i++)
            context.AiRequests.Add(External(authorizationId: null, tenantId: Tenant + 1));
        await context.SaveChangesAsync();

        var view = await new QualityAnalyticsService(context).GetAsync(Tenant + 1, 30, null, default);

        var governed = Assert.Single(view.Metrics, x => x.Key == "unauthorized-external-dependency");
        Assert.Equal(25m, governed.Value);
        var critical = Assert.Single(view.Recommendations, x => x.Priority == "Critical");
        Assert.Contains("Unauthorized external dependency is 25", critical.Evidence);
        // The claim of exemption and the number now describe the same population.
        Assert.Contains("excluded from this figure", critical.Evidence);
    }

    private static AiRequest External(long? authorizationId, long tenantId = Tenant) => new()
    {
        Id = Guid.NewGuid(), BusinessUnitId = tenantId, Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"), PromptHash = new string('A', 64),
        PromptVersion = "v1", Provider = "ollama", ProviderClass = AiProviderClass.External,
        Model = "deepseek-v4-pro", Status = AiCallStatuses.Succeeded,
        InputTokens = 100, OutputTokens = 20, ExternalAuthorizationId = authorizationId,
        CostStatus = AiCostStatuses.RateUnavailable, TokenSource = AiTokenSources.ProviderExact,
        CreatedOn = DateTime.UtcNow, StartedOn = DateTime.UtcNow, CompletedOn = DateTime.UtcNow
    };
}

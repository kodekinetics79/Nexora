using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
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

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_denied_reservation_is_not_reported_as_unauthorized_egress()
    {
        // The inversion this closes. A denial is the control WORKING — the call never reached
        // a provider — but the row carries ProviderClass.External with a null receipt, because
        // there was no authorization to record. Counting those made the metric fire hardest on
        // the tenants whose allow-list was doing its job: the gate refuses every unauthorized
        // external reservation outright, so a SUCCEEDED external row always carries a receipt,
        // and a denial was the only thing left that could move the number.
        var tenant = Tenant + 2;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        for (var i = 0; i < 31; i++)
            context.AiRequests.Add(External(authorizationId: 501, tenantId: tenant));
        for (var i = 0; i < 5; i++)
            context.AiRequests.Add(External(authorizationId: null, tenantId: tenant,
                status: AiCallStatuses.Denied));
        await context.SaveChangesAsync();

        var view = await new QualityAnalyticsService(context).GetAsync(tenant, 30, null, default);

        var governed = Assert.Single(view.Metrics, x => x.Key == "unauthorized-external-dependency");
        Assert.Equal(0m, governed.Value);
        Assert.DoesNotContain(view.Recommendations, x => x.Priority == "Critical");
        // And the raw egress figure counts what actually left the box: 31, not 36.
        var raw = Assert.Single(view.Metrics, x => x.Key == "external-dependency");
        Assert.Equal(31, raw.Numerator);
        Assert.Equal(31, raw.Denominator);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_two_screens_compute_the_same_ratio_over_the_same_rows()
    {
        // Quality Analytics used to recompute the share itself over localAi + externalAi, which
        // silently dropped ProviderClass.Unknown from the denominator while the enforcement
        // projection kept it. Same tenant, same instant, two different percentages. It now runs
        // the enforcer's own projection over its cohort, so the screens can differ only by the
        // window an operator chose.
        var tenant = Tenant + 3;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        for (var i = 0; i < 10; i++)
            context.AiRequests.Add(External(authorizationId: null, tenantId: tenant,
                providerClass: AiProviderClass.Unknown));
        for (var i = 0; i < 30; i++)
            context.AiRequests.Add(External(authorizationId: 501, tenantId: tenant));
        for (var i = 0; i < 5; i++)
            context.AiRequests.Add(External(authorizationId: null, tenantId: tenant));
        await context.SaveChangesAsync();

        var view = await new QualityAnalyticsService(context).GetAsync(tenant, 365, null, default);
        var enforcementProjection = await AiExternalDependencyEvaluator.EvaluateAsync(
            context.AiRequests, tenant, 10m, default);

        var governed = Assert.Single(view.Metrics, x => x.Key == "unauthorized-external-dependency");
        // Identical rows on both sides: Unknown-class calls stay in the denominator here the
        // way they always did for the enforcer. The two screens render at different precision
        // (2dp here, 1dp there) and that is allowed; the counts and the verdict are not.
        Assert.Equal(enforcementProjection.Total, governed.Denominator);
        Assert.Equal(enforcementProjection.External - enforcementProjection.AuthorizedExternal,
            governed.Numerator);
        Assert.Equal(45, governed.Denominator);
        Assert.Equal(enforcementProjection.ExternalSharePercent, Math.Round(governed.Value!.Value, 1));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_unauthorized_cohort_lists_only_the_documents_it_is_about()
    {
        // The Critical links to evidence, so the evidence must be the documents it accuses.
        // Both metrics shared one cohort, so "Unauthorized external AI dependency" opened a
        // list containing approved egress — the same mislabelling the recommendation itself
        // was fixed for, one click deeper.
        var tenant = Tenant + 4;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        var corpusId = await CorpusAsync(context, tenant);
        var authorized = await OccurrenceAsync(context, tenant, corpusId, 'a', "authorized-only.pdf");
        var unreceipted = await OccurrenceAsync(context, tenant, corpusId, 'b', "no-receipt.pdf");
        context.AiRequests.Add(External(authorizationId: 501, tenantId: tenant, occurrenceId: authorized));
        context.AiRequests.Add(External(authorizationId: null, tenantId: tenant, occurrenceId: unreceipted));
        await context.SaveChangesAsync();
        var service = new QualityAnalyticsService(context);

        var unauthorizedCohort = await service.GetAsync(tenant, 30, "unauthorized-external-ai", default);
        Assert.Contains(unauthorizedCohort.Records, x => x.FileName == "no-receipt.pdf");
        Assert.DoesNotContain(unauthorizedCohort.Records, x => x.FileName == "authorized-only.pdf");

        // The raw egress cohort still answers the auditor's question — everything that left.
        var egressCohort = await service.GetAsync(tenant, 30, "external-ai", default);
        Assert.Contains(egressCohort.Records, x => x.FileName == "no-receipt.pdf");
        Assert.Contains(egressCohort.Records, x => x.FileName == "authorized-only.pdf");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_recommendation_names_the_metric_it_is_about()
    {
        // The Monitor fallback carried no metric key, so clicking it reloaded the record table
        // while clearing the explanation and un-pressing every card: the page changed with
        // nothing on screen saying why.
        var tenant = Tenant + 5;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        await context.SaveChangesAsync();

        var view = await new QualityAnalyticsService(context).GetAsync(tenant, 30, null, default);

        Assert.NotEmpty(view.Recommendations);
        Assert.All(view.Recommendations, x => Assert.False(string.IsNullOrWhiteSpace(x.MetricKey)));
        Assert.All(view.Recommendations,
            x => Assert.Contains(view.Metrics, metric => metric.Key == x.MetricKey));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_long_cohort_does_not_convict_rows_that_predate_the_receipt()
    {
        // This screen's window is operator-chosen and reaches back a year, so unlike AI Trust's
        // month it can still see rows written before the authorization receipt was recorded on
        // every authorized call. Counting those as unauthorized gave the same tenant, at the
        // same second, two verdicts decided by a dropdown: Critical at 365 days, clean at 30.
        var tenant = Tenant + 6;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        var beforeReceipts = AiExternalDependencyEvaluator.ReceiptRecordingBegan.AddDays(-3);
        for (var i = 0; i < 8; i++)
        {
            var legacy = External(authorizationId: null, tenantId: tenant);
            legacy.CreatedOn = beforeReceipts;
            context.AiRequests.Add(legacy);
        }
        for (var i = 0; i < 32; i++)
            context.AiRequests.Add(External(authorizationId: 501, tenantId: tenant));
        await context.SaveChangesAsync();

        var service = new QualityAnalyticsService(context);
        var annual = await service.GetAsync(tenant, 365, null, default);
        var monthly = await service.GetAsync(tenant, 30, null, default);

        foreach (var view in new[] { annual, monthly })
        {
            var governed = Assert.Single(view.Metrics, x => x.Key == "unauthorized-external-dependency");
            Assert.Equal(0m, governed.Value);
            Assert.DoesNotContain(view.Recommendations, x => x.Priority == "Critical");
        }
    }

    private static async Task<long> CorpusAsync(ErpRfqAutomationContext context, long tenant)
    {
        var corpus = DocumentCorpus.Create(tenant, Guid.NewGuid(), CorpusSourceType.Api);
        context.Add(corpus);
        await context.SaveChangesAsync();
        return corpus.Id;
    }

    private static async Task<long> OccurrenceAsync(ErpRfqAutomationContext context, long tenant,
        long corpusId, char hashSeed, string fileName)
    {
        var document = SourceDocument.Create(tenant, corpusId, new string(hashSeed, 64),
            fileName, "application/pdf", "evidence", $"{tenant}/{fileName}", "v1", 512);
        context.Add(document);
        await context.SaveChangesAsync();
        var occurrence = SourceDocumentOccurrence.Create(tenant, document.Id, corpusId,
            $"{fileName}-occurrence", "{\"source\":\"test\"}");
        context.Add(occurrence);
        await context.SaveChangesAsync();
        return occurrence.Id;
    }

    private static AiRequest External(long? authorizationId, long tenantId = Tenant,
        string status = AiCallStatuses.Succeeded,
        AiProviderClass providerClass = AiProviderClass.External,
        long? occurrenceId = null) => new()
    {
        Id = Guid.NewGuid(), BusinessUnitId = tenantId, Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"), PromptHash = new string('A', 64),
        PromptVersion = "v1", Provider = "ollama", ProviderClass = providerClass,
        SourceDocumentOccurrenceId = occurrenceId,
        Model = "deepseek-v4-pro", Status = status,
        InputTokens = 100, OutputTokens = 20, ExternalAuthorizationId = authorizationId,
        CostStatus = AiCostStatuses.RateUnavailable, TokenSource = AiTokenSources.ProviderExact,
        CreatedOn = DateTime.UtcNow, StartedOn = DateTime.UtcNow, CompletedOn = DateTime.UtcNow
    };
}

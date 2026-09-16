using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Procurement.Discovery;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// "Ask suppliers" when the company knows too few: the internet is searched for the case's part
/// and maker, hits are ranked the way a buyer trusts them, the rep ticks the ones to add, and only
/// then does a supplier exist. Every gate speaks in the rep's words, and no test here touches the
/// internet — the provider is a stub that records what it was asked.
/// </summary>
public sealed class SupplierDiscoveryServiceTests
{
    private const string Part = "LV431831";
    private const string Maker = "Schneider Electric";

    // ---- gates ---------------------------------------------------------------

    [Fact]
    public async Task Without_a_search_key_the_rep_sees_a_neutral_not_available_message_and_nothing_is_searched()
    {
        using var harness = new Harness(apiKey: null);
        var caseId = await harness.CreateCaseAsync("no-key");

        var result = await harness.Discover(caseId);

        Assert.Equal(SupplierDiscoveryStatuses.NotConfigured, result.Status);
        Assert.Equal(SupplierDiscoveryService.NotConfiguredMessage, result.Message);
        Assert.Empty(result.Hits);
        Assert.Empty(harness.Provider.Queries);
        Assert.Equal(Maker, result.SearchedFor.Maker);
        Assert.Equal(Part, result.SearchedFor.PartNumber);
    }

    // The search engine behind discovery is Nexora's raw material, set once at platform level.
    // A client sees the finished result and never the ingredient: no provider name, no setting
    // key, no "ask your administrator" (the tenant admin cannot set it anyway).
    [Theory]
    [InlineData("ollama")]
    [InlineData("ApiKey")]
    [InlineData("SupplierDiscovery:")]
    [InlineData("administrator")]
    [InlineData("search key")]
    public void Tenant_facing_discovery_messages_never_name_the_provider_or_the_platform_setting(string forbidden)
    {
        var tenantFacing = new[] { SupplierDiscoveryService.NotConfiguredMessage, SupplierDiscoveryService.ErrorMessage };
        foreach (var message in tenantFacing)
            Assert.DoesNotContain(forbidden, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_search_runs_without_any_consent_step_because_buying_the_product_is_the_consent()
    {
        // Owner, 2026-09-16: a tenant that bought Nexora agreed to external providers and AI
        // features; a "switch it on" gate has no business reason. Even a trust chain that would
        // deny ollama.com must not stop the search — the only gate is the search key.
        using var harness = new Harness();
        harness.Trust.Decision = AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.NotAuthorized);
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("bought-the-product");

        var result = await harness.Discover(caseId);

        Assert.Equal(SupplierDiscoveryStatuses.Ready, result.Status);
        Assert.NotEmpty(harness.Provider.Queries);
        Assert.Null(harness.Trust.LastPurpose);
    }

    [Fact]
    public async Task Nothing_found_is_said_in_the_reps_words_and_is_not_cached()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => [new WebSearchResult("Alibaba", "https://www.alibaba.com/x", Part)];
        var caseId = await harness.CreateCaseAsync("no-results");

        var first = await harness.Discover(caseId);
        var second = await harness.Discover(caseId);

        Assert.Equal(SupplierDiscoveryStatuses.NoResults, first.Status);
        Assert.Equal($"Nothing found on the internet for {Part} ({Maker}). Try adding a supplier yourself.", first.Message);
        Assert.Equal(SupplierDiscoveryStatuses.NoResults, second.Status);
        Assert.Equal(4, harness.Provider.Queries.Count); // two queries, twice — an empty answer is not remembered
        await using var verify = harness.Context();
        Assert.False(await verify.SupplierDiscoverySearches.AnyAsync());
    }

    [Fact]
    public async Task A_provider_failure_is_one_plain_sentence_to_the_rep()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => throw new SupplierWebSearchException("HTTP 502");
        var caseId = await harness.CreateCaseAsync("provider-down");

        var result = await harness.Discover(caseId);

        Assert.Equal(SupplierDiscoveryStatuses.Error, result.Status);
        Assert.Equal(SupplierDiscoveryService.ErrorMessage, result.Message);
        Assert.DoesNotContain("502", result.Message);
    }

    [Fact]
    public async Task A_case_in_another_company_is_not_found()
    {
        using var harness = new Harness();
        await harness.CreateCaseAsync("other-tenant");
        var error = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            harness.Run(s => s.DiscoverAsync(new DiscoverSuppliersCommand(harness.Scenario.OtherBusinessUnitId, harness.LastCaseId, 0, 10, "qa"))));
        Assert.Contains("not found", error.Message);
    }

    // ---- the search ------------------------------------------------------------

    [Fact]
    public async Task Searches_by_maker_and_part_ranks_maker_then_distributors_then_resellers_and_marks_known_suppliers()
    {
        using var harness = new Harness();
        await using (var setup = harness.Context())
        {
            var known = await setup.Suppliers.SingleAsync(x => x.Id == ProcurementTestData.Supplier);
            known.Website = "https://www.known-distributor.com/";
            await setup.SaveChangesAsync();
        }
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("ranked");

        var result = await harness.Discover(caseId);

        Assert.Equal(SupplierDiscoveryStatuses.Ready, result.Status);
        Assert.Contains($"{Maker} {Part} distributor Saudi Arabia", harness.Provider.Queries);
        Assert.Contains($"{Maker} {Part} supplier", harness.Provider.Queries);
        Assert.False(result.FromCache);
        Assert.NotNull(result.SearchedAtUtc);
        Assert.Equal(5, result.Total);
        Assert.Equal(
            ["se.com", "known-distributor.com", "gulfswitchgear.com", "eurobreakers.de", "cheapbreakers.example"],
            result.Hits.Select(x => x.Domain).ToArray());
        Assert.Equal(SupplierRoles.Manufacturer, result.Hits[0].Role);
        Assert.Equal(ProcurementTestData.Supplier, result.Hits[1].ExistingSupplierId);
        Assert.Null(result.Hits[2].ExistingSupplierId);
        Assert.Equal("SA", result.Hits[2].Country);
        Assert.Equal("sales@gulfswitchgear.com", result.Hits[2].ContactEmail);
        Assert.Equal("Gulf Switchgear Trading Co.", result.Hits[2].Name);
        Assert.StartsWith($"Found 5 companies on the internet for {Part} ({Maker}). 1 is already on your supplier list.", result.Message);
        Assert.Equal(Maker, result.SearchedFor.Maker);
        Assert.Equal(Part, result.SearchedFor.PartNumber);
        Assert.Equal("CIRCUIT BREAKER MCCB 3P 250A", result.SearchedFor.Description);
        Assert.Empty(result.SearchedFor.AcceptableMakers);
    }

    [Fact]
    public async Task The_customers_approved_maker_list_on_the_line_widens_the_search()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("approved-makers",
            approvedMakers: "ABB (S203-C16); SIEMENS 5SY6316-7");

        var result = await harness.Discover(caseId);

        Assert.Equal(["ABB (S203-C16)", "SIEMENS 5SY6316-7"], result.SearchedFor.AcceptableMakers);
        Assert.Equal(
        [
            $"{Maker} {Part} distributor Saudi Arabia", $"{Maker} {Part} supplier",
            "ABB S203-C16 distributor Saudi Arabia", "ABB S203-C16 supplier",
            "SIEMENS 5SY6316-7 distributor Saudi Arabia", "SIEMENS 5SY6316-7 supplier"
        ], harness.Provider.Queries);
    }

    [Fact]
    public async Task A_repeat_within_thirty_days_is_answered_from_the_cache_and_costs_no_provider_call()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("cached");

        var first = await harness.Discover(caseId);
        var callsAfterFirst = harness.Provider.Queries.Count;
        var second = await harness.Discover(caseId);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(callsAfterFirst, harness.Provider.Queries.Count);
        Assert.Equal(first.Total, second.Total);
        Assert.Equal(first.Hits.Select(x => x.Id), second.Hits.Select(x => x.Id));
        Assert.Contains("(searched ", second.Message);

        // Day 31: the memory has lapsed and the internet is asked again.
        await using (var age = harness.Context())
        {
            var row = await age.SupplierDiscoverySearches.SingleAsync();
            row.SearchedAtUtc = DateTime.UtcNow.AddDays(-(SupplierDiscoverySearch.CacheDays + 1));
            await age.SaveChangesAsync();
        }
        var third = await harness.Discover(caseId);
        Assert.False(third.FromCache);
        Assert.Equal(callsAfterFirst * 2, harness.Provider.Queries.Count);
        await using var verify = harness.Context();
        Assert.Equal(1, await verify.SupplierDiscoverySearches.CountAsync());
    }

    [Fact]
    public async Task Show_ten_more_is_the_same_ranked_list_from_the_next_offset()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => Enumerable.Range(0, 23)
            .Select(i => new WebSearchResult($"Reseller {i:00}", $"https://reseller{i:00}.example/p", Part)).ToArray();
        var caseId = await harness.CreateCaseAsync("paged");

        var firstPage = await harness.Discover(caseId, offset: 0, limit: 10);
        var thirdPage = await harness.Discover(caseId, offset: 20, limit: 10);

        Assert.Equal(23, firstPage.Total);
        Assert.Equal(10, firstPage.Hits.Count);
        Assert.Equal(0, firstPage.Offset);
        Assert.Equal(10, firstPage.Limit);
        Assert.Equal("reseller00.example", firstPage.Hits[0].Domain);
        Assert.Equal(23, thirdPage.Total);
        Assert.Equal(3, thirdPage.Hits.Count);
        Assert.Equal("reseller20.example", thirdPage.Hits[0].Domain);
        Assert.True(thirdPage.FromCache);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => harness.Discover(caseId, offset: 0, limit: 5));
    }

    [Fact]
    public async Task The_supplier_pages_free_text_box_runs_the_same_search()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => StandardResults();

        var result = await harness.Run(s => s.SearchAsync(harness.Scenario.BusinessUnitId, "Schneider LV431831", 0, 10));

        Assert.Equal(SupplierDiscoveryStatuses.Ready, result.Status);
        Assert.Equal(["Schneider LV431831 supplier Saudi Arabia", "Schneider LV431831 supplier"], harness.Provider.Queries);
        Assert.Equal("Schneider LV431831", result.SearchedFor.Description);
        Assert.Equal(5, result.Total);
    }

    // ---- adopting --------------------------------------------------------------

    [Fact]
    public async Task Ticked_hits_become_discovered_tier3_suppliers_tagged_for_the_candidate_rule_and_the_case_shows_them_with_blockers()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("adopt");
        var found = await harness.Discover(caseId);
        var distributor = found.Hits.Single(x => x.Domain == "gulfswitchgear.com");
        var maker = found.Hits.Single(x => x.Domain == "se.com");
        var caseBefore = await harness.Scenario.Execute(s => s.GetSourcingCaseAsync(harness.Scenario.BusinessUnitId, caseId));

        var adopted = await harness.Adopt(caseId, distributor.Id, maker.Id);

        Assert.Equal(2, adopted.Adopted.Count);
        var gulf = adopted.Adopted.Single(x => x.HitId == distributor.Id);
        Assert.False(gulf.AlreadyExisted);
        Assert.Equal("Gulf Switchgear Trading Co.", gulf.SupplierName);
        Assert.Equal("sales@gulfswitchgear.com", gulf.ContactEmail);
        Assert.False(gulf.NeedsContactEmail);
        var se = adopted.Adopted.Single(x => x.HitId == maker.Id);
        Assert.Null(se.ContactEmail);
        Assert.True(se.NeedsContactEmail);

        await using (var verify = harness.Context())
        {
            var supplier = await verify.Suppliers.SingleAsync(x => x.Id == gulf.SupplierId);
            Assert.Equal(harness.Scenario.BusinessUnitId, supplier.Buid);
            Assert.Equal(SupplierGovernanceStatuses.Discovered, supplier.GovernanceStatus);
            Assert.Equal(SupplierTiers.Tier3OutOfNetwork, supplier.Tier);
            Assert.Equal(SupplierRoles.Distributor, supplier.Role);
            Assert.Equal("https://www.gulfswitchgear.com", supplier.Website);
            Assert.Equal($"{Part}; {Maker}", supplier.Tags);
            Assert.True(supplier.IsActive);
            Assert.Equal("qa", supplier.CreatedBy);
            Assert.Contains(Part, supplier.Comments);
        }

        var caseAfter = await harness.Scenario.Execute(s => s.GetSourcingCaseAsync(harness.Scenario.BusinessUnitId, caseId));
        Assert.Equal(caseBefore.Version + 1, caseAfter.Version);
        var candidate = caseAfter.Candidates.Single(x => x.SupplierId == gulf.SupplierId);
        Assert.Equal(SourcingCandidateEvidenceTypes.SupplierMetadata, candidate.EvidenceType);
        Assert.False(candidate.EligibleForSupplierRfq);
        Assert.Contains("Supplier approval or explicit provisional approval is required", candidate.BlockingReasons);
        var makerCandidate = caseAfter.Candidates.Single(x => x.SupplierId == se.SupplierId);
        Assert.Contains("A verified dispatch contact is required", makerCandidate.BlockingReasons);
        // The supplier the company already had keeps its place.
        Assert.Contains(caseAfter.Candidates, x => x.SupplierId == ProcurementTestData.Supplier);
    }

    [Fact]
    public async Task Adopting_the_same_company_twice_yields_the_same_supplier_and_a_known_website_is_recognised()
    {
        using var harness = new Harness();
        await using (var setup = harness.Context())
        {
            var existing = await setup.Suppliers.SingleAsync(x => x.Id == ProcurementTestData.Supplier);
            existing.Website = "https://known-distributor.com";
            await setup.SaveChangesAsync();
        }
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("adopt-twice");
        var found = await harness.Discover(caseId);
        var gulfId = found.Hits.Single(x => x.Domain == "gulfswitchgear.com").Id;
        var knownId = found.Hits.Single(x => x.Domain == "known-distributor.com").Id;

        var first = await harness.Adopt(caseId, gulfId, knownId);
        var again = await harness.Adopt(caseId, gulfId);

        var known = first.Adopted.Single(x => x.HitId == knownId);
        Assert.True(known.AlreadyExisted);
        Assert.Equal(ProcurementTestData.Supplier, known.SupplierId);
        Assert.Equal("QA Supplier", known.SupplierName);

        var gulfFirst = first.Adopted.Single(x => x.HitId == gulfId);
        var gulfAgain = Assert.Single(again.Adopted);
        Assert.False(gulfFirst.AlreadyExisted);
        Assert.True(gulfAgain.AlreadyExisted);
        Assert.Equal(gulfFirst.SupplierId, gulfAgain.SupplierId);

        await using var verify = harness.Context();
        Assert.Equal(1, await verify.Suppliers.CountAsync(x => x.Website == "https://www.gulfswitchgear.com"));
        var rediscovered = await harness.Discover(caseId);
        Assert.Equal(gulfFirst.SupplierId, rediscovered.Hits.Single(x => x.Id == gulfId).ExistingSupplierId);
    }

    [Fact]
    public async Task A_contact_email_another_supplier_already_uses_is_left_for_the_rep_to_sort_out()
    {
        using var harness = new Harness();
        await using (var setup = harness.Context())
        {
            var known = await setup.Suppliers.SingleAsync(x => x.Id == ProcurementTestData.Supplier);
            known.ContactEmail = "sales@gulfswitchgear.com";
            await setup.SaveChangesAsync();
        }
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("email-clash");
        var found = await harness.Discover(caseId);

        var adopted = await harness.Adopt(caseId, found.Hits.Single(x => x.Domain == "gulfswitchgear.com").Id);

        var gulf = Assert.Single(adopted.Adopted);
        Assert.False(gulf.AlreadyExisted);
        Assert.Null(gulf.ContactEmail);
        Assert.True(gulf.NeedsContactEmail);
    }

    [Fact]
    public async Task Adopting_without_a_search_or_with_a_stale_tick_is_refused_in_plain_words()
    {
        using var harness = new Harness();
        harness.Provider.Answer = _ => StandardResults();
        var caseId = await harness.CreateCaseAsync("adopt-stale");

        var noSearch = await Assert.ThrowsAsync<ProcurementConflictException>(() => harness.Adopt(caseId, "abc"));
        Assert.Contains("Search again", noSearch.Message);

        await harness.Discover(caseId);
        var unknownTick = await Assert.ThrowsAsync<ProcurementConflictException>(() => harness.Adopt(caseId, "not-a-hit"));
        Assert.Contains("Search again", unknownTick.Message);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => harness.Adopt(caseId));
        await using var verify = harness.Context();
        Assert.Equal(1, await verify.Suppliers.CountAsync(x => x.Buid == harness.Scenario.BusinessUnitId));
    }

    // ---- fixture ---------------------------------------------------------------

    private static IReadOnlyList<WebSearchResult> StandardResults() =>
    [
        new("Cheap breakers online", "https://cheapbreakers.example/lv431831", "Buy LV431831 online, ships from Hamburg"),
        new("Schneider LV431831 | Gulf Switchgear Trading Co.", "https://www.gulfswitchgear.com/products/lv431831",
            "Authorised Schneider Electric distributor in Dammam. LV431831 in stock. sales@gulfswitchgear.com"),
        new("LV431831 - Compact NSX250 | Schneider Electric", "https://www.se.com/sa/en/product/LV431831/", "Circuit breaker Compact NSX250F"),
        new("Known Distributor", "https://known-distributor.com/lv431831", "Official distributor of Schneider Electric in Europe"),
        new("LV431831 | Alibaba", "https://www.alibaba.com/product-detail/lv431831", "wholesale"),
        new("Euro Breakers GmbH", "https://www.eurobreakers.de/shop/lv431831", "Schneider Electric Vertriebspartner, distributor since 1980"),
    ];

    private sealed class Harness : IDisposable
    {
        public ProcurementScenario Scenario { get; } = new();
        public StubProvider Provider { get; } = new();
        public StubTrust Trust { get; } = new();
        public long LastCaseId { get; private set; }
        private readonly IConfiguration _configuration;

        public Harness(string? apiKey = "search-key")
        {
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SupplierDiscoveryConfiguration.ApiKeyKey] = apiKey
            }).Build();
        }

        public ErpRfqAutomationContext Context() => Scenario.Context();

        public async Task<long> CreateCaseAsync(string key, string? approvedMakers = null)
        {
            await using (var context = Context())
            {
                var rfq = await context.Rfqs.SingleAsync(x => x.Id == Scenario.RfqId);
                context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
                var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
                product.PreferredSupplierId = ProcurementTestData.Supplier;
                var line = await context.Rfqitems.SingleAsync(x => x.Id == Scenario.RfqItemId);
                line.ManufacturerPartNumber = Part;
                line.ManufacturerName = Maker;
                line.ProductShortDescription = "CIRCUIT BREAKER MCCB 3P 250A";
                if (approvedMakers is not null)
                    line.ExtraFields = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["Approved manufacturers"] = approvedMakers
                    });
                await context.SaveChangesAsync();
            }
            var created = await Scenario.Execute(service => service.CreateOrOpenSourcingCaseAsync(new CreateSourcingCaseCommand(
                Scenario.BusinessUnitId, Scenario.RfqId, Scenario.RfqItemId, 10, false, key, "qa", $"corr-{key}")));
            LastCaseId = created.Id;
            return created.Id;
        }

        public Task<SupplierDiscoveryResult> Discover(long caseId, int offset = 0, int limit = 10)
            => Run(s => s.DiscoverAsync(new DiscoverSuppliersCommand(Scenario.BusinessUnitId, caseId, offset, limit, "qa")));

        public Task<AdoptDiscoveredSuppliersResult> Adopt(long caseId, params string[] hitIds)
            => Run(s => s.AdoptAsync(new AdoptDiscoveredSuppliersCommand(Scenario.BusinessUnitId, caseId, hitIds, "qa", "corr-adopt")));

        public async Task<T> Run<T>(Func<SupplierDiscoveryService, Task<T>> operation)
        {
            await using var context = Context();
            var service = new SupplierDiscoveryService(context, Provider, new ProcurementApplicationService(context),
                _configuration, new NoopLogger<SupplierDiscoveryService>());
            return await operation(service);
        }

        public void Dispose() => Scenario.Dispose();
    }

    private sealed class StubProvider : ISupplierWebSearchProvider
    {
        public List<string> Queries { get; } = [];
        public Func<string, IReadOnlyList<WebSearchResult>> Answer { get; set; } = _ => [];

        public AiProviderDescriptor Destination { get; } = AiProviderEndpoint.Describe(
            AiProviderEndpointResolver.OllamaProvider, SupplierDiscoveryConfiguration.Endpoint, null);

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
        {
            Queries.Add(query);
            Assert.Equal(SupplierDiscoveryConfiguration.ProviderMaxResultsPerQuery, maxResults);
            return Task.FromResult(Answer(query));
        }
    }

    private sealed class StubTrust : IAiExternalProviderTrust
    {
        public AiExternalProviderDecision Decision { get; set; } = new(true, AiExternalProviderTrustReasons.Authorized, 1);
        public string? LastPurpose { get; private set; }
        public AiProviderDescriptor? LastDestination { get; private set; }
        public bool? LastUnstructured { get; private set; }

        public AiProviderDescriptor ResolvedProvider => AiProviderDescriptor.Unresolved();
        public IReadOnlyList<AiProviderDescriptor> KnownProviders => [];

        public Task<AiExternalProviderDecision> EvaluateAsync(long businessUnitId, AiProviderDescriptor provider,
            string purpose, bool unstructuredPayload, CancellationToken ct)
        {
            LastPurpose = purpose;
            LastDestination = provider;
            LastUnstructured = unstructuredPayload;
            return Task.FromResult(Decision);
        }

        public Task<AiExternalProviderTrustView> GetAsync(long businessUnitId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}

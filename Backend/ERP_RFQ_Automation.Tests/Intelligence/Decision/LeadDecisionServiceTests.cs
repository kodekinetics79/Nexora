using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests.Intelligence.Decision;

public sealed class LeadDecisionServiceTests
{
    private const long TenantId = 11;
    private const long OtherTenantId = 12;

    [Fact]
    public async Task Canonical_customer_wins_over_duplicate_names_and_cross_tenant_candidates()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var canonical = Seed.Customer(seed, 101, TenantId, "Duplicate Buyer");
            Seed.Customer(seed, 102, TenantId, "Duplicate Buyer");
            Seed.Customer(seed, 201, OtherTenantId, "Duplicate Buyer");
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: "Duplicate Buyer");
            lead.ResolveCommercialIdentity(canonical.Id, null, "VERIFIED");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, TenantId, default);

        Assert.Equal(101, brief.Customer.CustomerId);
        Assert.Equal(CustomerIdentityEvidence.Canonical, brief.Customer.IdentityEvidence);
        Assert.True(brief.Customer.IsDecisionGradeIdentity);
        Assert.True(brief.Customer.IsExistingCustomer);
    }

    [Fact]
    public async Task Heuristic_customer_matching_is_tenant_scoped_and_explicitly_weaker()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.Customer(seed, 101, TenantId, "Known Buyer");
            Seed.Customer(seed, 201, OtherTenantId, "Known Buyer");
            Seed.Lead(seed, 1, TenantId, buyersName: "Known Buyer");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, TenantId, default);

        Assert.Equal(101, brief.Customer.CustomerId);
        Assert.Equal(CustomerIdentityEvidence.HeuristicName, brief.Customer.IdentityEvidence);
        Assert.False(brief.Customer.IsDecisionGradeIdentity);
        Assert.Contains(brief.Reasons, reason => reason.Contains("weaker name/email match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_name_customer_candidates_remain_ambiguous()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.Customer(seed, 101, TenantId, "Ambiguous Buyer");
            Seed.Customer(seed, 102, TenantId, "Ambiguous Buyer");
            Seed.Lead(seed, 1, TenantId, buyersName: "Ambiguous Buyer");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, TenantId, default);

        Assert.Null(brief.Customer.CustomerId);
        Assert.False(brief.Customer.IsExistingCustomer);
        Assert.False(brief.Customer.IsDecisionGradeIdentity);
        Assert.Equal(CustomerIdentityEvidence.HeuristicAmbiguous, brief.Customer.IdentityEvidence);
    }

    [Fact]
    public async Task Name_only_product_candidates_contribute_no_commercial_signal()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, null, null, "Precision hydraulic pump", 10, null, "USD"));
            seed.Products.AddRange(
                Product(501, "PUMP-A", "Precision hydraulic pump assembly", 20m, 25m, 50m),
                Product(502, "PUMP-B", "Precision hydraulic pump kit", 30m, 30m, 60m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, TenantId, default);

        var item = Assert.Single(brief.Coverage.Items);
        Assert.False(item.Matched);
        Assert.Null(item.ProductId);
        Assert.Null(item.UnitPrice);
        Assert.Null(item.CatalogQtyOnHand);
        Assert.Equal(0, brief.Coverage.CoveredItems);
        Assert.Equal(0, brief.Coverage.CatalogOnHandItems);
        Assert.Null(brief.EstimatedValue);
        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal(LeadDecisionRecommendations.Skip, brief.Recommendation);
    }

    [Fact]
    public async Task Mixed_currency_lines_have_no_aggregate_value_or_margin_without_fx()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "PART-USD", null, "USD line", 2, 100m, "USD"));
            lead.LeadItems.Add(Item(1002, "PART-EUR", null, "EUR line", 3, 80m, "EUR"));
            seed.Products.Add(Product(501, "PART-USD", "USD product", 5m, 50m, 100m));
            seed.Products.Add(Product(502, "PART-EUR", "EUR product", 5m, 40m, 80m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, TenantId, default);

        Assert.Equal(100m, brief.Coverage.CoveragePct);
        Assert.Null(brief.Currency);
        Assert.Null(brief.EstimatedValue);
        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal("unknown", brief.ValueConfidence);
    }

    [Fact]
    public async Task Same_currency_margin_is_quantity_and_revenue_weighted()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "HIGH", null, "High value", 1, 100m, "USD"));
            lead.LeadItems.Add(Item(1002, "VOLUME", null, "Volume", 9, 10m, "USD"));
            seed.Products.Add(Product(501, "HIGH", "High value", 1m, 50m, 100m));
            seed.Products.Add(Product(502, "VOLUME", "Volume", 1m, 9m, 10m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, TenantId, default);

        Assert.Equal("USD", brief.Currency);
        Assert.Equal(190m, brief.EstimatedValue);
        Assert.Equal(31.1m, brief.MarginPotentialPct);
        Assert.Equal(2, brief.Coverage.CatalogOnHandItems);
        Assert.Equal(2m, brief.Coverage.CatalogOnHandQuantity);
        Assert.Contains(brief.Reasons, reason => reason.Contains("not ATP", StringComparison.Ordinal));
        Assert.DoesNotContain(brief.Reasons, reason => reason.Contains("We stock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_is_versioned_partial_and_never_emits_actionable_bid()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "KNOWN", null, "Known part", 2, 25m, "USD"));
            seed.Products.Add(Product(501, "KNOWN", "Known part", 10m, 10m, 25m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var summaries = await new LeadDecisionService(context).GetSummariesAsync([1], TenantId, default);

        var summary = Assert.Single(summaries).Value;
        Assert.Equal(100m, summary.CoveragePct);
        Assert.Equal(50m, summary.EstimatedValue);
        Assert.Equal(LeadDecisionRecommendations.Review, summary.Recommendation);
        Assert.Equal(LeadDecisionPolicy.Version, summary.PolicyVersion);
        Assert.Equal(LeadDecisionCompleteness.Partial, summary.Completeness);
        Assert.False(summary.IsActionable);
    }

    private static LeadItem Item(
        long id,
        string? code,
        string? mpn,
        string name,
        int quantity,
        decimal? unitPrice,
        string? currency) => new()
    {
        Id = id,
        ItemMaterialCode = code,
        ManufacturerPartNumber = mpn,
        ProductShortName = name,
        Quantity = quantity,
        UnitPrice = unitPrice,
        Currency = currency
    };

    private static Product Product(
        long id,
        string partNo,
        string name,
        decimal qtyOnHand,
        decimal unitCost,
        decimal sellingPrice) => new()
    {
        Id = id,
        Buid = TenantId,
        PartNo = partNo,
        ProductName = name,
        QtyOnHand = qtyOnHand,
        UnitCost = unitCost,
        SellingPrice = sellingPrice,
        IsActive = true,
        CreatedBy = "decision-tests",
        CreatedOn = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc)
    };
}

using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02CommercialLearningTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(5, 1, false)]
    [InlineData(4, 4, false)]
    [InlineData(5, 2, true)]
    public void Stocking_recommendation_requires_decided_and_won_evidence(int decided, int won, bool expected) =>
        Assert.Equal(expected, CommercialLearningRules.CanRecommendStocking(decided, won));

    [Theory]
    [InlineData(5, 2, true, true, true)]
    [InlineData(5, 2, false, true, false)]
    [InlineData(5, 2, true, false, false)]
    [InlineData(4, 2, true, true, false)]
    public void Stocking_recommendation_requires_consistent_demand_and_lead_time_evidence(
        int decided, int won, bool demandConsistent, bool leadTimeEvidence, bool expected) =>
        Assert.Equal(expected, CommercialLearningRules.CanRecommendStocking(
            decided, won, demandConsistent, leadTimeEvidence));

    [Theory]
    [InlineData("PRICE", "COMMERCIAL_CONSTRAINT")]
    [InlineData("NO_STOCK", "COMMERCIAL_CONSTRAINT")]
    [InlineData("CUSTOMER_CANCELLED", "CUSTOMER_DECISION")]
    [InlineData("NO_RESPONSE", "CUSTOMER_DECISION")]
    [InlineData("INCORRECT_COMMITMENT", "EXECUTION_REVIEW")]
    public void Loss_attribution_does_not_blame_rep_for_external_constraints(string reason, string expected) =>
        Assert.Equal(expected, CommercialLearningRules.ClassifyLoss(reason));

    [Fact]
    public void Weighted_coverage_excludes_owner_double_credit_and_includes_contribution_only_quotes()
    {
        var coverage = CommercialLearningRules.CalculateWeightedCoverage([10, 20],
            [(10, 40m), (30, 25m), (30, 35m), (40, 120m)]);

        Assert.Equal(3.6m, coverage);
    }

    [Fact]
    public void Learning_endpoints_are_authenticated_and_permission_scoped()
    {
        var controller = typeof(CommercialLearningController);
        Assert.NotNull(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).SingleOrDefault());
        AssertPermissions(nameof(CommercialLearningController.Products), "Products", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Product), "Products", "Quotations");
        AssertPermission(nameof(CommercialLearningController.InventoryDemand), "Products");
        AssertPermissions(nameof(CommercialLearningController.Supplier), "Supplier History", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Suppliers), "Supplier History", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Customer), "Customers", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Customers), "Customers", "Quotations");
        AssertPermission(nameof(CommercialLearningController.SalesRep), "Dashboard");
        AssertPermission(nameof(CommercialLearningController.SalesReps), "Dashboard");
        AssertPermissions(nameof(CommercialLearningController.RfqIntelligence), "Products", "Quotations", "RFQ Management", "Supplier History");
        AssertPermissions(nameof(CommercialLearningController.MemoryCard), "Products", "Quotations", "RFQ Management", "Supplier History");
        AssertPermission(nameof(CommercialLearningController.LearningStudio), "Dashboard");
    }

    [Fact]
    public async Task Rfq_intelligence_uses_current_available_to_promise_and_is_tenant_scoped()
    {
        using var fixture = new ProcurementScenario();
        await using (var seed = fixture.Context())
        {
            var inventory = await seed.Set<ERP_RFQ_Automation.Models.Inventory>()
                .SingleAsync(x => x.Id == ProcurementTestData.Inventory);
            inventory.AllocatedQuantity = .25m;
            inventory.QuarantineQuantity = .25m;
            inventory.SafetyStockQuantity = .5m;
            seed.StockReservations.Add(new StockReservation
            {
                BusinessUnitId = fixture.BusinessUnitId,
                InventoryId = inventory.Id,
                Quantity = .5m,
                Status = StockReservationStatus.Active,
                IdempotencyKey = "rfq-intelligence-atp",
                CreatedBy = "qa",
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        var line = Assert.Single(result.Lines);
        Assert.Equal(.5m, line.StockQuantity);
        Assert.Equal(9.5m, line.UnfulfilledQuantity);
        Assert.Equal("PARTIAL_REQUIRES_SOURCE", line.FulfilmentRoute);
        Assert.Contains("No evidence-complete Supplier offer covers the remaining demand", line.Blockers);

        await using var otherTenant = fixture.Context(fixture.OtherBusinessUnitId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new CommercialLearningService(otherTenant)
            .GetRfqIntelligenceAsync(fixture.OtherBusinessUnitId, fixture.RfqId));
    }

    [Fact]
    public async Task Rfq_intelligence_keeps_partial_supplier_award_blocked()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateAwardAsync("partial-intelligence", 3m);

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        var line = Assert.Single(result.Lines);
        Assert.Equal(5m, line.UnfulfilledQuantity);
        Assert.Contains("Supplier award covers only part of the remaining demand", line.Blockers);
        Assert.NotEqual("VIABLE_READY", result.CommercialDecision);
    }

    [Fact]
    public async Task Rfq_intelligence_does_not_treat_an_unawarded_offer_as_fulfilment()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateEligibleQuoteAsync("unawarded-intelligence");

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        var line = Assert.Single(result.Lines);
        Assert.True(line.EligibleOfferCount > 0);
        Assert.True(line.UnfulfilledQuantity > 0m);
        Assert.Contains("Select and approve a Supplier offer for the remaining demand", line.Blockers);
        Assert.NotEqual("VIABLE_READY", result.CommercialDecision);
    }

    [Fact]
    public async Task Completed_review_status_makes_immutable_review_snapshot_eligible()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateEligibleQuoteAsync("reviewed-intelligence");
        await using (var setup = fixture.Context())
        {
            var revision = await setup.SupplierQuoteRevisions.SingleAsync();
            revision.RequiresReview = true;
            Assert.Equal(ERP_RFQ_Automation.SupplierQuotes.SupplierQuoteInboxStatuses.ReadyForComparison,
                (await setup.SupplierQuotes.SingleAsync()).InboxStatus);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        Assert.True(Assert.Single(result.Lines).EligibleOfferCount > 0);
    }

    [Fact]
    public async Task Rfq_intelligence_does_not_count_an_expired_award_as_fulfilment()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateAwardAsync("expired-award-intelligence", 8m);
        await using (var setup = fixture.Context())
        {
            var offer = await setup.SupplierQuotedItems.SingleAsync();
            offer.ValidUntil = DateTime.UtcNow.AddMinutes(-1);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        var line = Assert.Single(result.Lines);
        Assert.Equal(8m, line.UnfulfilledQuantity);
        Assert.NotEqual("VIABLE_READY", result.CommercialDecision);
    }

    [Fact]
    public async Task Overdue_rfq_requires_no_quote_review_even_when_stock_covers_demand()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var inventory = await setup.Set<ERP_RFQ_Automation.Models.Inventory>().SingleAsync();
            inventory.QtyOnHand = 10m;
            var rfq = await setup.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
            rfq.BidClosingDate = DateTime.UtcNow.AddMinutes(-1);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        Assert.Equal("OVERDUE", result.SlaRisk);
        Assert.Equal("NO_QUOTE_REVIEW", result.CommercialDecision);
    }

    [Fact]
    public async Task Digital_twin_rejects_offers_that_miss_required_delivery()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateEligibleQuoteAsync("delivery-date-twin");
        await using (var setup = fixture.Context())
        {
            var line = await setup.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            line.RequiredDesiredDate = DateTime.UtcNow.AddDays(1);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
        Assert.Equal(0, Assert.Single(result.Lines).EligibleOfferCount);
        Assert.All(result.DigitalTwin.Scenarios.Where(x => x.Code is "SUPPLIER_ONLY" or
            "SPLIT_STOCK_SOURCE" or "FASTEST_DELIVERY" or "LOWEST_LANDED_COST"),
            scenario => Assert.False(scenario.Eligible));
    }

    [Fact]
    public async Task Missing_optional_terms_are_flagged_but_do_not_conflict_with_offer_eligibility()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateEligibleQuoteAsync("optional-terms-quality");

        await using var context = fixture.Context();
        var supplier = await new CommercialLearningService(context)
            .GetSupplierAsync(fixture.BusinessUnitId, ProcurementTestData.Supplier);
        Assert.Equal(1, supplier.BidQuality.EligibleOfferCount);
        Assert.Contains(supplier.BidQuality.Flags, x => x.Code == "INCOMPLETE_TERMS" && x.Severity == "WARNING");
    }

    private static void AssertPermission(string methodName, string module)
    {
        var attribute = Assert.Single(typeof(CommercialLearningController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>());
        Assert.Equal(module, attribute.ModuleName);
        Assert.Equal(PermissionAction.View, attribute.Action);
    }

    private static void AssertPermissions(string methodName, params string[] modules)
    {
        var attributes = typeof(CommercialLearningController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>().ToArray();
        Assert.Equal(modules.Order(), attributes.Select(x => x.ModuleName).Order());
        Assert.All(attributes, attribute => Assert.Equal(PermissionAction.View, attribute.Action));
    }
}

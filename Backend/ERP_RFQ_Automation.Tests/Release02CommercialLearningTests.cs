using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

        var pricing = typeof(PricingIntelligenceController);
        Assert.NotNull(pricing.GetCustomAttributes(typeof(AuthorizeAttribute), true).SingleOrDefault());
        AssertPricingPermissions(nameof(PricingIntelligenceController.GetPricePreview),
            ("Quotations", PermissionAction.View), ("RFQ Management", PermissionAction.View));
        AssertPricingPermissions(nameof(PricingIntelligenceController.ApplyPricing),
            ("Quotations", PermissionAction.Edit), ("RFQ Management", PermissionAction.View));
    }

    [Fact]
    public void Direct_price_application_is_closed_even_for_an_authenticated_editor()
    {
        var controller = new PricingIntelligenceController(null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("businessUnitId", "96001")], "test"))
                }
            }
        };

        var result = controller.ApplyPricing(96060, new ApplyPricingRequest
        {
            Lines = [new ApplyPricingLine { RfqItemId = 96070, UnitPrice = 20m }]
        }, default);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("Shadow pricing cannot be applied directly", problem.Title);
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
    public async Task Digital_twin_exposes_all_v23_routes_and_partial_immediate_quantities()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateEligibleQuoteAsync("v23-complete-twin");

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);

        Assert.Equal("SHADOW", result.DigitalTwin.Mode);
        Assert.Equal("digital-twin-v2.3", result.DigitalTwin.PolicyVersion);
        Assert.Equal(9, result.DigitalTwin.Scenarios.Count);
        Assert.Contains(result.DigitalTwin.Scenarios, x => x.Code == "BEST_MARGIN");
        Assert.Contains(result.DigitalTwin.Scenarios, x => x.Code == "LOWEST_RISK");
        Assert.Contains(result.DigitalTwin.Scenarios, x => x.Code == "APPROVED_ALTERNATE");
        var partial = Assert.Single(result.DigitalTwin.Scenarios, x => x.Code == "PARTIAL_IMMEDIATE");
        Assert.True(partial.Eligible);
        Assert.Contains(partial.Quantities, x => x.ImmediateStockQuantity > 0m && x.SupplierQuantity > 0m);
        Assert.NotEmpty(partial.CostSources);
        Assert.NotEmpty(partial.ApprovalRequirements);
    }

    [Fact]
    public async Task Shadow_pricing_partitions_currency_and_never_creates_a_synthetic_floor()
    {
        using var fixture = new ProcurementScenario();
        await using (var seed = fixture.Context())
        {
            seed.Currencies.Add(new ERP_RFQ_Automation.Models.Currency
            {
                Id = ProcurementTestData.Currency + 1, BusinessUnitId = fixture.BusinessUnitId,
                Code = "Q2", CurrencyName = "Second QA Currency", ExchangeRate = 1m,
                IsBaseCurrency = false, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
            var second = AgentSeed.RfqItem(seed, ProcurementTestData.RfqItem + 1, fixture.RfqId,
                "QA Product second currency", 5);
            second.ProductId = ProcurementTestData.Product;
            second.CurrencyId = ProcurementTestData.Currency + 1;
            second.WarehouseId = ProcurementTestData.Warehouse;
            second.UnitOfMeasure = "EA";

            seed.SetupMasters.Add(new ERP_RFQ_Automation.Models.SetupMaster
            {
                SetupId = 44, BusinessUnitId = fixture.BusinessUnitId, SetupType = "QuoteStatus",
                SetupCode = "ACCEPTED", SetupValue = "Accepted", IsActive = true,
                CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
            seed.Quotes.AddRange(
                AcceptedQuote(970_010, "QT-Q0", fixture, ProcurementTestData.Currency,
                    fixture.RfqItemId, 10m, 18m, DateTime.UtcNow.AddDays(-10)),
                AcceptedQuote(970_020, "QT-Q2", fixture, ProcurementTestData.Currency + 1,
                    ProcurementTestData.RfqItem + 1, 5m, 21m, DateTime.UtcNow.AddDays(-5)));
            seed.SupplierPurchaseHistories.AddRange(
                new ERP_RFQ_Automation.Models.SupplierPurchaseHistory
                {
                    Id = 970_001, ProductId = ProcurementTestData.Product,
                    SupplierId = ProcurementTestData.Supplier, PurchaseDate = DateTime.UtcNow.AddDays(-10),
                    Quantity = 10m, UnitPrice = 8m, Currency = "Q0", CreatedBy = "qa", CreatedOn = DateTime.UtcNow
                },
                new ERP_RFQ_Automation.Models.SupplierPurchaseHistory
                {
                    Id = 970_002, ProductId = ProcurementTestData.Product,
                    SupplierId = ProcurementTestData.Supplier, PurchaseDate = DateTime.UtcNow.AddDays(-5),
                    Quantity = 5m, UnitPrice = 9m, Currency = "Q2", CreatedBy = "qa", CreatedOn = DateTime.UtcNow
                });
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var engine = new PricingEngine(context, NullLogger<PricingEngine>.Instance);
        var preview = await engine.PriceRfqAsync(fixture.RfqId, fixture.BusinessUnitId, default);

        Assert.Equal("SHADOW", preview.Mode);
        Assert.False(preview.ApplyAllowed);
        Assert.Null(preview.Currency);
        Assert.Null(preview.Totals.RecommendedTotal);
        Assert.Equal(2, preview.Totals.ByCurrency.Count);
        Assert.Equal(2, preview.Totals.PricedLineCount);
        Assert.Equal(0, preview.Totals.UnpricedLineCount);
        Assert.All(preview.Lines.SelectMany(line => line.Signals),
            signal => Assert.Equal(PriceSignalSources.RecentQuote, signal.Source));
        Assert.All(preview.Lines, line => Assert.Null(line.FloorUnitPrice));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyPricingAsync(
            fixture.RfqId, fixture.BusinessUnitId, new ApplyPricingRequest
            {
                Lines = [new ApplyPricingLine { RfqItemId = fixture.RfqItemId, UnitPrice = 20m }]
            }, default));
    }

    [Fact]
    public async Task Shadow_pricing_ignores_supplier_purchase_history_until_it_is_strictly_tenant_owned()
    {
        using var fixture = new ProcurementScenario();
        await using (var seed = fixture.Context())
        {
            seed.SupplierPurchaseHistories.Add(new ERP_RFQ_Automation.Models.SupplierPurchaseHistory
            {
                Id = 970_030, ProductId = ProcurementTestData.Product,
                SupplierId = ProcurementTestData.Supplier, PurchaseDate = DateTime.UtcNow.AddDays(-1),
                Quantity = 10m, UnitPrice = 8m, Currency = "Q0", CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var preview = await new PricingEngine(context, NullLogger<PricingEngine>.Instance)
            .PriceRfqAsync(fixture.RfqId, fixture.BusinessUnitId, default);

        var line = Assert.Single(preview.Lines);
        Assert.Empty(line.Signals);
        Assert.True(line.NeedsAttention);
        Assert.Equal(0, preview.Totals.PricedLineCount);
        Assert.Equal(1, preview.Totals.UnpricedLineCount);
        Assert.Null(preview.Totals.RecommendedTotal);
    }

    [Fact]
    public async Task Predictive_pricing_counts_distinct_customer_order_outcomes_not_quote_lines()
    {
        using var fixture = new ProcurementScenario();
        await using (var seed = fixture.Context())
        {
            SeedPredictionMasters(seed, fixture, 970_100, 970_101);
            var quote = AcceptedQuote(970_110, "QT-ONE-OUTCOME", fixture,
                ProcurementTestData.Currency, fixture.RfqItemId, 4m, 10m,
                DateTime.UtcNow.AddDays(-4), 970_100);
            quote.QuoteItems.Add(PredictionQuoteItem(970_112, fixture.RfqItemId, 3m, 11m));
            quote.QuoteItems.Add(PredictionQuoteItem(970_113, fixture.RfqItemId, 3m, 12m));
            seed.Quotes.Add(quote);
            seed.Orders.Add(CustomerOrder(970_120, "SO-ONE-OUTCOME", fixture,
                quote.Id, 970_100, 970_101, quote.QuoteDate!.Value));
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);

        var prediction = Assert.Single(result.DigitalTwin.PredictivePricing);
        Assert.Equal("INSUFFICIENT_EVIDENCE", prediction.Status);
        Assert.Equal(1, prediction.QuoteSampleSize);
        Assert.Equal(1, prediction.CustomerOrderSampleSize);
        Assert.Null(prediction.RecommendedUnitPrice);
        Assert.Equal(0, prediction.BacktestHoldoutCount);
    }

    [Fact]
    public async Task Predictive_pricing_uses_chronological_walk_forward_holdouts()
    {
        using var fixture = new ProcurementScenario();
        await using (var seed = fixture.Context())
        {
            SeedPredictionMasters(seed, fixture, 970_200, 970_201);
            var prices = new[] { 10m, 12m, 14m, 20m };
            for (var index = 0; index < prices.Length; index++)
            {
                var quoteId = 970_210 + index * 10;
                var occurredOn = DateTime.UtcNow.AddDays(-4 + index);
                var quote = AcceptedQuote(quoteId, $"QT-WALK-{index + 1}", fixture,
                    ProcurementTestData.Currency, fixture.RfqItemId, 10m, prices[index],
                    occurredOn, 970_200);
                seed.Quotes.Add(quote);
                seed.Orders.Add(CustomerOrder(970_300 + index, $"SO-WALK-{index + 1}", fixture,
                    quote.Id, 970_200, 970_201, occurredOn));
            }
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);

        var prediction = Assert.Single(result.DigitalTwin.PredictivePricing);
        Assert.Equal("READY_SHADOW", prediction.Status);
        Assert.Equal(4, prediction.QuoteSampleSize);
        Assert.Equal(4, prediction.CustomerOrderSampleSize);
        Assert.Equal(13m, prediction.RecommendedUnitPrice);
        Assert.Equal(20m, prediction.LastWonUnitPrice);
        Assert.Equal(1, prediction.BacktestHoldoutCount);
        Assert.Equal(40m, prediction.BacktestMeanAbsolutePercentError);
        Assert.Equal(1, result.DigitalTwin.Backtest.HoldoutCount);
        Assert.Equal(40m, result.DigitalTwin.Backtest.MeanAbsolutePercentError);
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

    private static void AssertPricingPermissions(string methodName,
        params (string Module, PermissionAction Action)[] expected)
    {
        var attributes = typeof(PricingIntelligenceController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>().ToArray();
        Assert.Equal(expected.OrderBy(x => x.Module),
            attributes.Select(x => (Module: x.ModuleName, x.Action)).OrderBy(x => x.Module));
    }

    private static ERP_RFQ_Automation.Models.Quote AcceptedQuote(long id, string number,
        ProcurementScenario fixture, long currencyId, long rfqItemId, decimal quantity,
        decimal unitPrice, DateTime quoteDate, long? customerId = null)
    {
        var quote = new ERP_RFQ_Automation.Models.Quote
        {
            Id = id,
            QuoteNo = number,
            Rfqid = fixture.RfqId,
            CustomerId = customerId,
            BusinessUnitId = fixture.BusinessUnitId,
            QuoteDate = quoteDate,
            ValidUntil = quoteDate.AddDays(30),
            StatusId = 44,
            CurrencyId = currencyId,
            TotalAmount = quantity * unitPrice,
            CreatedBy = "qa",
            CreatedDate = quoteDate
        };
        quote.QuoteItems.Add(new ERP_RFQ_Automation.Models.QuoteItem
        {
            Id = id + 1,
            RfqitemId = rfqItemId,
            ProductId = ProcurementTestData.Product,
            ItemDescription = "QA Product",
            Quantity = quantity,
            UnitPrice = unitPrice,
            TotalAmount = quantity * unitPrice,
            CreatedBy = "qa",
            CreatedDate = quoteDate
        });
        return quote;
    }

    private static ERP_RFQ_Automation.Models.QuoteItem PredictionQuoteItem(long id,
        long rfqItemId, decimal quantity, decimal unitPrice) => new()
    {
        Id = id,
        RfqitemId = rfqItemId,
        ProductId = ProcurementTestData.Product,
        ItemDescription = "QA Product",
        Quantity = quantity,
        UnitPrice = unitPrice,
        TotalAmount = quantity * unitPrice,
        CreatedBy = "qa",
        CreatedDate = DateTime.UtcNow
    };

    private static void SeedPredictionMasters(ERP_RFQ_Automation.Models.ErpRfqAutomationContext seed,
        ProcurementScenario fixture, long customerId, long orderStatusId)
    {
        Seed.Customer(seed, customerId, fixture.BusinessUnitId, "Prediction QA Customer");
        seed.SetupMasters.AddRange(
            new ERP_RFQ_Automation.Models.SetupMaster
            {
                SetupId = 44, BusinessUnitId = fixture.BusinessUnitId, SetupType = "QuoteStatus",
                SetupCode = "ACCEPTED", SetupValue = "Accepted", IsActive = true,
                CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            },
            new ERP_RFQ_Automation.Models.SetupMaster
            {
                SetupId = orderStatusId, BusinessUnitId = fixture.BusinessUnitId, SetupType = "OrderStatus",
                SetupCode = "CONFIRMED", SetupValue = "Confirmed", IsActive = true,
                CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
    }

    private static ERP_RFQ_Automation.Models.Order CustomerOrder(long id, string number,
        ProcurementScenario fixture, long quoteId, long customerId, long statusId, DateTime occurredOn) => new()
    {
        Id = id,
        OrderNo = number,
        QuoteId = quoteId,
        Rfqid = fixture.RfqId,
        CustomerId = customerId,
        BusinessUnitId = fixture.BusinessUnitId,
        StatusId = statusId,
        CurrencyId = ProcurementTestData.Currency,
        OrderDate = occurredOn,
        TotalAmount = 100m,
        PaidAmount = 0m,
        IsActive = true,
        SourceType = ERP_RFQ_Automation.Models.OrderSourceTypes.CustomerAward,
        CreatedBy = "qa",
        CreatedOn = occurredOn
    };
}

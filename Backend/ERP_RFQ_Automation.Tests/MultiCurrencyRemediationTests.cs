using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Agent.Sourcing;
using ERP_RFQ_Automation.Agent.Tools;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Multi-currency remediation.
///
/// The FX authority (Fx/FxConversionService.cs) can only be trusted end-to-end if the writes
/// that feed it stop manufacturing uncomparable state and the reads that consume it stop
/// blending it. These tests pin the two halves:
///
///  * WRITE — RfqRepository.ApproveAsync no longer stamps a quote's header currency from an
///    arbitrary line, and refuses to produce a single total for lines that disagree.
///  * READ  — award ranking, order revenue and sales revenue either convert with approved
///    evidence or say, in words, that they cannot.
///
/// Plus the two non-FX defects found alongside them: one landed-cost definition instead of two
/// (which was firing a blocking CRITICAL flag on arithmetic), and a currency guard that was
/// skipped exactly when it mattered.
/// </summary>
public sealed class MultiCurrencyRemediationTests
{
    private const long Bu = 9_400;
    private const long Aed = 9_410; // base
    private const long Usd = 9_411;
    private const long Eur = 9_412;

    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    // ───────────────────────────────────────────── 1. the write that manufactured the corruption

    [Fact]
    public async Task Rfq_approval_takes_the_quote_currency_from_unanimous_lines_not_from_the_first_one()
    {
        using var database = new TestDb();
        long rfqId;
        await using (var seed = database.ContextFor(null))
        {
            rfqId = SeedApprovableRfq(seed, "RFQ-UNANIMOUS",
                (Usd, 10, 5m), (Usd, 4, 25m));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var quoteId = await new RfqRepository(db).ApproveAsync(rfqId, "user@example.com", Bu);

        var quote = await db.Quotes.Include(q => q.QuoteItems).SingleAsync(q => q.Id == quoteId);
        Assert.Equal(Usd, quote.CurrencyId);
        Assert.Equal(150m, quote.TotalAmount); // 10*5 + 4*25, all USD
    }

    [Fact]
    public async Task Rfq_approval_refuses_to_stamp_a_currency_sampled_from_one_of_several()
    {
        using var database = new TestDb();
        long rfqId;
        await using (var seed = database.ContextFor(null))
        {
            // The old code took `Rfqitems.FirstOrDefault()?.CurrencyId` — USD here — and then
            // summed the EUR line straight into the same total. Both facts were false.
            rfqId = SeedApprovableRfq(seed, "RFQ-MIXED", (Usd, 10, 5m), (Eur, 4, 25m));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RfqRepository(db).ApproveAsync(rfqId, "user@example.com", Bu));

        Assert.Contains("2 different currencies", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USD", error.Message, StringComparison.Ordinal);
        Assert.Contains("EUR", error.Message, StringComparison.Ordinal);

        // Fail CLOSED: no half-built quote is left behind for a downstream reader to find.
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Quotes.ToListAsync());
    }

    [Fact]
    public async Task Rfq_approval_refuses_when_only_some_lines_declare_a_currency()
    {
        using var database = new TestDb();
        long rfqId;
        await using (var seed = database.ContextFor(null))
        {
            rfqId = SeedApprovableRfq(seed, "RFQ-PARTIAL", (Usd, 10, 5m), (null, 4, 25m));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RfqRepository(db).ApproveAsync(rfqId, "user@example.com", Bu));

        Assert.Contains("no recognised currency", error.Message, StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Quotes.ToListAsync());
    }

    [Fact]
    public async Task Rfq_approval_reads_the_free_text_currency_code_when_the_foreign_key_is_absent()
    {
        using var database = new TestDb();
        long rfqId;
        await using (var seed = database.ContextFor(null))
        {
            rfqId = SeedApprovableRfq(seed, "RFQ-FREETEXT", (null, 10, 5m), (null, 4, 25m));
            await seed.SaveChangesAsync();
            foreach (var line in seed.Rfqitems.IgnoreQueryFilters().Where(i => i.Rfqid == rfqId).ToList())
                line.Currency = " usd "; // extraction writes the ISO code, not the FK
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var quoteId = await new RfqRepository(db).ApproveAsync(rfqId, "user@example.com", Bu);

        Assert.Equal(Usd, (await db.Quotes.SingleAsync(q => q.Id == quoteId)).CurrencyId);
    }

    [Fact]
    public async Task Rfq_approval_leaves_the_currency_null_when_no_line_declares_one()
    {
        using var database = new TestDb();
        long rfqId;
        await using (var seed = database.ContextFor(null))
        {
            rfqId = SeedApprovableRfq(seed, "RFQ-SILENT", (null, 10, 5m), (null, 4, 25m));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var quoteId = await new RfqRepository(db).ApproveAsync(rfqId, "user@example.com", Bu);

        // Nothing declares a currency, so there is no currency to get wrong — but the quote must
        // not claim one either. NULL is the honest answer, and the downstream guard in
        // SupplierQuoteCommercialService is what stops it being adopted silently later.
        var quote = await db.Quotes.SingleAsync(q => q.Id == quoteId);
        Assert.Null(quote.CurrencyId);
        Assert.Equal(150m, quote.TotalAmount);
    }

    // ───────────────────────────────────────────── 3. AI award ranking

    [Fact]
    public async Task Award_ranking_converts_before_scoring_and_picks_the_genuinely_cheaper_supplier()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            // 1 EUR = 4.30 AED, 1 USD = 3.6725 AED. So a 1,000 EUR bid is 4,300 AED and a
            // 1,200 USD bid is 4,407 AED — but 1,000 < 1,200 as bare decimals, which is exactly
            // the inversion the old scorer produced.
            seed.FxRates.Add(ApprovedRate(Eur, Aed, 4.30m));
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m));
            AgentSeed.Supplier(seed, 9_501, Bu, "Euro Supplier");
            AgentSeed.Supplier(seed, 9_502, Bu, "Dollar Supplier");
            AgentSeed.Rfq(seed, 9_600, Bu);
            Bid(seed, 9_601, 9_600, 9_501, Eur, quantity: 10, unitPrice: 100m, leadTime: 5);
            Bid(seed, 9_602, 9_600, 9_502, Usd, quantity: 10, unitPrice: 120m, leadTime: 5);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var result = await new RecommendAwardTool(db).ExecuteAsync(
            AgentSeed.Json("{\"rfqId\":9600}"), AgentContext(), default);

        Assert.True(result.Success, result.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        var root = document.RootElement;
        Assert.Equal(9_501, root.GetProperty("recommendedSupplierId").GetInt64());
        Assert.Equal("AED", root.GetProperty("comparisonCurrencyCode").GetString());

        // Both bids are reported in ONE currency, and it is the converted figure that is shown.
        var ranked = root.GetProperty("comparison").EnumerateArray().ToList();
        Assert.Equal(4_300m, ranked[0].GetProperty("TotalPrice").GetDecimal());
        Assert.Equal(4_407m, ranked[1].GetProperty("TotalPrice").GetDecimal());
    }

    [Fact]
    public async Task Award_ranking_refuses_rather_than_ranking_bids_with_no_approved_rate()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m)); // nothing joins EUR to the base
            AgentSeed.Supplier(seed, 9_501, Bu, "Euro Supplier");
            AgentSeed.Supplier(seed, 9_502, Bu, "Dollar Supplier");
            AgentSeed.Rfq(seed, 9_600, Bu);
            Bid(seed, 9_601, 9_600, 9_501, Eur, quantity: 10, unitPrice: 100m, leadTime: 5);
            Bid(seed, 9_602, 9_600, 9_502, Usd, quantity: 10, unitPrice: 120m, leadTime: 5);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var result = await new RecommendAwardTool(db).ExecuteAsync(
            AgentSeed.Json("{\"rfqId\":9600}"), AgentContext(), default);

        Assert.False(result.Success);
        Assert.Contains("No approved EUR to AED exchange rate", result.Error, StringComparison.Ordinal);
        Assert.Contains("No award is recommended", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Award_ranking_refuses_a_bid_line_that_carries_no_currency()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            AgentSeed.Supplier(seed, 9_501, Bu, "Anonymous Supplier");
            AgentSeed.Supplier(seed, 9_502, Bu, "Dollar Supplier");
            AgentSeed.Rfq(seed, 9_600, Bu);
            Bid(seed, 9_601, 9_600, 9_501, null, quantity: 10, unitPrice: 100m, leadTime: 5);
            Bid(seed, 9_602, 9_600, 9_502, Usd, quantity: 10, unitPrice: 120m, leadTime: 5);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var result = await new RecommendAwardTool(db).ExecuteAsync(
            AgentSeed.Json("{\"rfqId\":9600}"), AgentContext(), default);

        Assert.False(result.Success);
        Assert.Contains("carry no currency", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Award_ranking_needs_no_rate_when_every_bid_is_already_in_one_currency()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed); // deliberately no FxRate rows at all
            AgentSeed.Supplier(seed, 9_501, Bu, "Cheap Supplier");
            AgentSeed.Supplier(seed, 9_502, Bu, "Dear Supplier");
            AgentSeed.Rfq(seed, 9_600, Bu);
            Bid(seed, 9_601, 9_600, 9_501, Usd, quantity: 10, unitPrice: 100m, leadTime: 5);
            Bid(seed, 9_602, 9_600, 9_502, Usd, quantity: 10, unitPrice: 120m, leadTime: 5);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var result = await new RecommendAwardTool(db).ExecuteAsync(
            AgentSeed.Json("{\"rfqId\":9600}"), AgentContext(), default);

        Assert.True(result.Success, result.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        Assert.Equal(9_501, document.RootElement.GetProperty("recommendedSupplierId").GetInt64());
        Assert.Contains("no conversion was applied",
            document.RootElement.GetProperty("conversionNote").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_scorer_refuses_a_candidate_set_that_still_spans_currencies()
    {
        var bids = new IScoreCandidate[]
        {
            new StubCandidate { Price = 1_000m, LeadTime = 5, SuccessRate = 90, PriceCurrencyId = Eur },
            new StubCandidate { Price = 1_200m, LeadTime = 5, SuccessRate = 90, PriceCurrencyId = Usd }
        };

        var error = Assert.Throws<InvalidOperationException>(() => SupplierScoring.ScoreInPlace(bids));
        Assert.Contains("span 2 currencies", error.Message, StringComparison.Ordinal);
        Assert.All(bids, bid => Assert.Equal(0d, bid.Score)); // nothing was scored
    }

    [Fact]
    public void Shared_scorer_refuses_a_set_where_only_some_candidates_declare_a_currency()
    {
        var bids = new IScoreCandidate[]
        {
            new StubCandidate { Price = 1_000m, LeadTime = 5, SuccessRate = 90, PriceCurrencyId = Usd },
            new StubCandidate { Price = 1_200m, LeadTime = 5, SuccessRate = 90, PriceCurrencyId = null }
        };

        var error = Assert.Throws<InvalidOperationException>(() => SupplierScoring.ScoreInPlace(bids));
        Assert.Contains("others carry none", error.Message, StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────── 5. one landed-cost definition

    [Fact]
    public void Landed_cost_allocates_shared_charges_by_value_not_by_quantity()
    {
        // 30 EA @ 100 and 1 LOT @ 1,000: quantity-weighting would push 30/31 of the freight onto
        // the EA line, which has no commercial meaning once the units differ.
        var lines = new[] { (UnitPrice: 100m, Quantity: 30m), (UnitPrice: 1_000m, Quantity: 1m) };
        var total = LandedCostFormula.TotalLineValue(lines);

        var eaFreight = LandedCostFormula.AllocateByValue(400m, LandedCostFormula.LineValue(100m, 30m), total);
        var lotFreight = LandedCostFormula.AllocateByValue(400m, LandedCostFormula.LineValue(1_000m, 1m), total);

        Assert.Equal(300m, eaFreight);  // 3,000 / 4,000 of the freight
        Assert.Equal(100m, lotFreight); // 1,000 / 4,000 of the freight
        // Numbers unchanged by the input-tax fix, and unchanged again by R18's move from a boolean
        // to a percentage: these lines carry no tax, and freight is a real non-recoverable cost
        // that stays in landed cost at any recovery ratio. 100m is what the old `true` meant.
        Assert.Equal(110m, LandedCostFormula.UnitCost(100m, 30m, eaFreight, 0m,
            supplierInputTaxRecoverablePercent: 100m));
        Assert.Equal(1_100m, LandedCostFormula.UnitCost(1_000m, 1m, lotFreight, 0m,
            supplierInputTaxRecoverablePercent: 100m));
    }

    [Fact]
    public void Landed_cost_has_no_zero_denominator_to_divide_by()
    {
        // Both of these threw DivideByZeroException in the quantity-weighted projection code.
        Assert.Equal(0m, LandedCostFormula.AllocateByValue(500m, 0m, 0m));
        Assert.Equal(42m, LandedCostFormula.UnitCost(42m, 0m, 0m, 0m, supplierInputTaxRecoverablePercent: 100m));
    }

    [Fact]
    public void Both_landed_cost_call_sites_now_agree_on_the_same_number()
    {
        // The projection (SupplierQuoteCommercialService) and the negotiation workspace
        // (SupplierNegotiationService) both route through LandedCostFormula, so the >2% gap that
        // used to raise a blocking CRITICAL POST_SELECTION_PRICE_INCREASE cannot open up between
        // them on a revision that has not changed.
        var lines = new[] { (UnitPrice: 100m, Quantity: 30m), (UnitPrice: 1_000m, Quantity: 1m) };
        var total = LandedCostFormula.TotalLineValue(lines);

        foreach (var (unitPrice, quantity) in lines)
        {
            var lineValue = LandedCostFormula.LineValue(unitPrice, quantity);
            var freight = LandedCostFormula.AllocateByValue(400m, lineValue, total);
            var tax = LandedCostFormula.AllocateByValue(80m, lineValue, total);
            var projected = LandedCostFormula.UnitCost(unitPrice, quantity, freight, tax,
                supplierInputTaxRecoverablePercent: 100m);
            var recomputed = LandedCostFormula.UnitCost(unitPrice, quantity, freight, tax,
                supplierInputTaxRecoverablePercent: 100m);
            Assert.Equal(projected, recomputed);
            Assert.True(Math.Abs(projected - recomputed) <= projected * 0.02m);
        }
    }

    // ───────────────────────────────────────────── 6. order revenue

    [Fact]
    public async Task Order_revenue_is_converted_with_approved_rates_and_reported_in_the_base_currency()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m));
            SeedOrderCustomer(seed);
            SeedOrder(seed, 9_701, 1_000m, Usd);
            SeedOrder(seed, 9_702, 500m, Aed);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var stats = await new OrderRepository(db).GetOrderStatsAsync(Bu);

        Assert.True(stats.TotalRevenueConverted);
        Assert.Equal(4_172.50m, stats.TotalRevenue); // 1,000 * 3.6725 + 500
        Assert.Equal("AED", stats.TotalRevenueCurrency);
        Assert.Null(stats.TotalRevenueUnavailableReason);
        Assert.Equal(2, stats.RevenueByCurrency.Count);
    }

    [Fact]
    public async Task Order_revenue_fails_closed_with_a_reason_and_a_breakdown_when_a_rate_is_missing()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed); // no rates recorded at all
            SeedOrderCustomer(seed);
            SeedOrder(seed, 9_701, 1_000m, Usd);
            SeedOrder(seed, 9_702, 500m, Aed);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var stats = await new OrderRepository(db).GetOrderStatsAsync(Bu);

        // NULL, never a partial or blended figure — and never 0, which would read as "no revenue".
        Assert.Null(stats.TotalRevenue);
        Assert.False(stats.TotalRevenueConverted);
        Assert.Contains("No approved USD to AED exchange rate",
            stats.TotalRevenueUnavailableReason!, StringComparison.Ordinal);

        // The honest answer is still available.
        Assert.Equal(2, stats.TotalOrders);
        var usd = Assert.Single(stats.RevenueByCurrency, c => c.CurrencyCode == "USD");
        Assert.Equal(1_000m, usd.Subtotal);
        Assert.False(usd.Converted);
        var aed = Assert.Single(stats.RevenueByCurrency, c => c.CurrencyCode == "AED");
        Assert.True(aed.Converted);
        Assert.Equal(500m, aed.ConvertedSubtotal);
    }

    // ───────────────────────────────────────────── 7. sales revenue buckets

    [Fact]
    public async Task Sales_performance_never_merges_unlike_currencies_into_a_catch_all_bucket()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new StubSalesPersistence
        {
            Contributions =
            {
                Contribution("USD", 1_000m, from),
                Contribution("usd", 500m, from),   // same currency, sloppy casing
                Contribution(null, 700m, from),    // legacy row: revenue, no currency
                Contribution("   ", 300m, from)    // legacy row: revenue, blank currency
            }
        };

        var result = Assert.Single(await new SalesApplicationService(store).GetPerformanceAsync(
            71, new SalesPerformanceQuery(7_101, from, from.AddMonths(1), from.AddDays(1)), default));

        Assert.Collection(result.RevenueByCurrency,
            unspecified =>
            {
                // Held apart under a name no consumer can mistake for a currency.
                Assert.Equal(SalesApplicationService.UnspecifiedCurrencyCode, unspecified.CurrencyCode);
                Assert.Equal(1_000m, unspecified.RevenueAmount);
            },
            usd =>
            {
                // "usd" and "USD" are one bucket, not two.
                Assert.Equal("USD", usd.CurrencyCode);
                Assert.Equal(1_500m, usd.RevenueAmount);
            });
    }

    // ───────────────────────────────────────────── helpers

    private static AgentToolContext AgentContext() =>
        new() { BusinessUnitId = Bu, UserId = 42, UserName = "tester" };

    private static void SeedCurrencies(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        db.Currencies.AddRange(
            new Currency
            {
                Id = Aed, BusinessUnitId = Bu, Code = "AED", CurrencyName = "UAE Dirham",
                IsBaseCurrency = true, IsActive = true, CreatedBy = "test", CreatedOn = Jan1
            },
            new Currency
            {
                Id = Usd, BusinessUnitId = Bu, Code = "USD", CurrencyName = "US Dollar",
                IsBaseCurrency = false, IsActive = true, CreatedBy = "test", CreatedOn = Jan1
            },
            new Currency
            {
                Id = Eur, BusinessUnitId = Bu, Code = "EUR", CurrencyName = "Euro",
                IsBaseCurrency = false, IsActive = true, CreatedBy = "test", CreatedOn = Jan1
            });
    }

    private static FxRate ApprovedRate(long from, long to, decimal rate) => new()
    {
        BusinessUnitId = Bu, FromCurrencyId = from, ToCurrencyId = to, Rate = rate,
        EffectiveFrom = Jan1, Source = "Manual", Status = FxRateStatuses.Approved,
        ApprovedBy = "treasury", ApprovedOn = Jan1, Version = 1, CreatedBy = "test", CreatedOn = Jan1
    };

    /// <summary>An RFQ in QUOTE_PREPARATION with a resolved commercial identity, ready to approve.</summary>
    private static long SeedApprovableRfq(ErpRfqAutomationContext db, string rfqNo,
        params (long? CurrencyId, int Quantity, decimal UnitPrice)[] lines)
    {
        SeedCurrencies(db);
        const long offset = 9_500; // fresh database per test, so one fixed block is enough

        db.SetupMasters.AddRange(
            LifecycleStatus(offset + 1, "RFQStatus", "QUOTE_PREPARATION"),
            LifecycleStatus(offset + 2, "QuoteStatus", "DRAFT"));
        var customer = Seed.Customer(db, offset + 3, Bu, $"Customer {rfqNo}");
        var contact = Seed.Contact(db, offset + 4, Bu, customer.Id);
        var lead = Seed.Lead(db, offset + 5, Bu);
        db.SaveChanges();
        lead.ResolveCommercialIdentity(customer.Id, contact.Id, "CONFIRMED");
        db.SaveChanges();

        var rfq = new Rfq
        {
            Id = offset + 6,
            Rfqno = rfqNo,
            RecDate = Jan1,
            LeadId = lead.Id,
            BusinessUnitId = Bu,
            RfqstatusId = offset + 1,
            CreatedBy = "seed",
            CreatedDate = Jan1
        };
        rfq.InheritCommercialIdentity(lead);
        db.Rfqs.Add(rfq);

        var lineId = offset + 10;
        foreach (var (currencyId, quantity, unitPrice) in lines)
            db.Rfqitems.Add(new Rfqitem
            {
                Id = lineId++,
                Rfqid = rfq.Id,
                ProductShortName = "Component",
                Quantity = quantity,
                UnitPrice = unitPrice,
                CurrencyId = currencyId,
                CreatedBy = "seed",
                CreatedDate = Jan1
            });

        return rfq.Id;
    }

    private static SetupMaster LifecycleStatus(long id, string type, string code) => new()
    {
        SetupId = id,
        BusinessUnitId = Bu,
        SetupType = type,
        SetupCode = code,
        SetupValue = code,
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = Jan1
    };

    private static void Bid(ErpRfqAutomationContext db, long id, long rfqId, long supplierId,
        long? currencyId, int quantity, decimal unitPrice, int leadTime) =>
        db.Rfqitems.Add(new Rfqitem
        {
            Id = id,
            Rfqid = rfqId,
            ProductShortName = "Component",
            Quantity = quantity,
            UnitPrice = unitPrice,
            CurrencyId = currencyId,
            SupplierId = supplierId,
            LeadTime = leadTime,
            CreatedBy = "seed",
            CreatedDate = Jan1
        });

    private const long OrderStatusId = 9_460;

    private static void SeedOrderCustomer(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        Seed.Customer(db, 9_450, Bu, "Order Customer");
        db.SetupMasters.Add(LifecycleStatus(OrderStatusId, "OrderStatus", "PROCESSING"));
    }

    private static void SeedOrder(ErpRfqAutomationContext db, long id, decimal totalAmount, long currencyId)
    {
        db.Orders.Add(new Order
        {
            Id = id,
            OrderNo = $"ORD-{id}",
            BusinessUnitId = Bu,
            CustomerId = 9_450,
            StatusId = OrderStatusId,
            OrderDate = Jan1,
            TotalAmount = totalAmount,
            CurrencyId = currencyId,
            CreatedBy = "seed",
            CreatedOn = Jan1
        });
    }

    private static SalesContribution Contribution(string? currencyCode, decimal revenue, DateTime at) => new()
    {
        BusinessUnitId = 71,
        SalesRepUserId = 7_101,
        AggregateType = "Order",
        AggregateId = 1,
        ContributionPercent = 100m,
        RevenueAmount = revenue,
        CurrencyCode = currencyCode,
        RecognizedAtUtc = at.AddHours(1),
        EvidenceReference = "evidence",
        ActorId = "qa",
        CorrelationId = "corr",
        IdempotencyKey = Guid.NewGuid().ToString("N")
    };

    private sealed class StubCandidate : IScoreCandidate
    {
        public decimal Price { get; init; }
        public double LeadTime { get; init; }
        public double SuccessRate { get; init; }
        public double Score { get; set; }
        public long? PriceCurrencyId { get; init; }
    }

    /// <summary>
    /// Read-only ISalesPersistence: GetPerformanceAsync is the only path under test, and the
    /// point of the test is that the READ path stands on its own regardless of what the write
    /// path validated, so the write members are deliberately unreachable.
    /// </summary>
    private sealed class StubSalesPersistence : ISalesPersistence
    {
        public List<SalesContribution> Contributions { get; } = [];

        public Task<IReadOnlyList<SalesContribution>> QueryContributionsAsync(long businessUnitId,
            DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SalesContribution>>(Contributions
                .Where(x => x.RecognizedAtUtc >= from && x.RecognizedAtUtc < to &&
                    (!user.HasValue || x.SalesRepUserId == user)).ToArray());

        public Task<IReadOnlyList<CommercialActivity>> QueryActivitiesAsync(long businessUnitId,
            DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommercialActivity>>([]);
        public Task<IReadOnlyList<FollowUpTask>> QueryFollowUpsAsync(long businessUnitId,
            DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FollowUpTask>>([]);
        public Task<IReadOnlyList<FollowUpTransitionEvent>> QueryFollowUpTransitionsAsync(long businessUnitId,
            DateTime from, DateTime to, long? user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FollowUpTransitionEvent>>([]);

        public Task<bool> UserExistsAsync(long b, long u, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CustomerExistsAsync(long b, long c, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> LeadAssignmentExistsAsync(long b, long a, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AggregateExistsAsync(long b, string t, long a, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesRepProfile?> GetProfileAsync(long b, long u, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesRepProfile?> FindProfileMutationAsync(long b, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesRepProfile> SaveProfileAsync(SalesRepProfile p, long v, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommercialActivity?> FindActivityAsync(long b, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommercialActivity> AppendActivityAsync(CommercialActivity a, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTask?> FindFollowUpByCreationKeyAsync(long b, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTask> CreateFollowUpAsync(FollowUpTask t, CancellationToken ct) => throw new NotSupportedException();
        public Task<(FollowUpTask Task, FollowUpTransitionEvent? Replay)> GetFollowUpForTransitionAsync(
            long b, long id, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTransitionEvent> TransitionFollowUpAsync(FollowUpTask t,
            FollowUpTransitionEvent e, long v, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesContribution?> FindContributionAsync(long b, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesContribution> AppendContributionAsync(SalesContribution c, CancellationToken ct) => throw new NotSupportedException();
    }
}

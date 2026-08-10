using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Recoverable supplier input tax is not a cost, and must never reach a customer price.
///
/// The defect
/// ----------
/// <c>LandedCostFormula.UnitCost</c> folded the supplier's tax into landed unit cost:
/// <c>(unitPrice * quantity + freight + tax) / quantity</c>. In Saudi Arabia a VAT-registered
/// business reclaims the input VAT a supplier charges it, so that money is a receivable from
/// ZATCA — not a cost of the goods. Three things followed, and they compound:
///
///   1. cost was overstated by the whole tax amount;
///   2. the customer price is derived as <c>landed / (1 - margin)</c>, so the phantom cost was
///      MARKED UP by the target margin before it reached the customer;
///   3. output VAT is then added again on the customer quote line, so the customer was charged
///      tax on tax, and reported gross margin — and the digital-twin bridge built on it — was wrong.
///
/// The worked example these tests pin
/// -----------------------------------
/// One supplier line: 10 units at 100, round freight 200, supplier VAT 15% of (1,000 + 200) = 180.
///
///                              before                after
///   landed unit cost           138.0000              120.0000
///   customer price @ 20%       172.5000              150.0000
///   customer gross @ 15% VAT   198.3750              172.5000
///
/// The 18.00 of VAT per unit became a 22.50 net overcharge (18 / 0.8, the margin gross-up) and
/// 25.875 once output VAT was applied to it. Note that the defective NET price, 172.50, is exactly
/// 1.15x the correct one: the customer was quoted a full extra VAT inside the price, and then
/// charged VAT on that too.
///
/// Freight, duty and other captured charges are untouched by all of this — nobody refunds them.
///
/// What the finance panel changed afterwards
/// -----------------------------------------
/// R17: the "after" column above was aspirational. Nothing derived output VAT at all, so the fix
/// above removed the input-VAT cushion that had been silently cancelling the missing output leg.
/// The 172.50 gross line is now produced by the product, not by this test's arithmetic, and
/// <see cref="OutputTaxDerivationTests"/> covers the sell-side leg on its own terms.
///
/// R18: recoverability became a PERCENTAGE. TRUE is 100, FALSE is 0, and the values in between are
/// the partly-exempt trader a boolean had no way to describe — IAS 2.11 makes the irrecoverable
/// share a cost of the asset. Every expected number below is unchanged at the two endpoints, which
/// is the point: the type changed, the arithmetic did not.
/// </summary>
public sealed class LandedCostInputTaxTests
{
    private const decimal UnitPrice = 100m;
    private const decimal Quantity = 10m;
    private const decimal Freight = 200m;
    private const decimal SupplierVat = 180m; // 15% of merchandise 1,000 + freight 200
    private const decimal TargetMarginPercent = 20m;

    // ───────────────────────────────────────────── the formula

    [Fact]
    public void Recoverable_supplier_input_tax_never_reaches_landed_cost()
    {
        var landed = LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat,
            CommercialMatchingPolicy.FullyRecoverablePercent);

        // (100 x 10 + 200) / 10. The old formula returned 138.0000 — 1,380 / 10 — because it added
        // the 180 of reclaimable VAT to the cost of the goods.
        Assert.Equal(120.0000m, landed);
        Assert.NotEqual(138.0000m, landed);

        // The tax argument is inert under the default policy: a line that carries VAT costs exactly
        // what the same line costs with no VAT on it at all.
        Assert.Equal(LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, 0m,
            CommercialMatchingPolicy.FullyRecoverablePercent), landed);
    }

    [Fact]
    public void Non_recoverable_input_tax_is_a_real_cost_and_stays_in_landed_cost()
    {
        // A business unit that cannot reclaim input tax — not VAT-registered, or trading only in
        // exempt supplies — really did pay that money to acquire the goods. Setting the recovery
        // ratio to 0 is the whole point of the field: the old arithmetic is still available, to
        // the tenants for whom it is the truth.
        var landed = LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat, 0m);

        Assert.Equal(138.0000m, landed);
    }

    [Fact]
    public void Partial_recovery_puts_only_the_irrecoverable_share_into_cost()
    {
        // R18. The case a boolean could not express: a VAT-registered trader making partly exempt
        // supplies recovers input VAT pro-rata, and IAS 2.11 makes the irrecoverable share a cost
        // of the asset. At 70% recovery, 30% of the 180 supplier VAT — 54.00 — is cost.
        //
        //   (100 x 10 + 200 freight + 54 irrecoverable VAT) / 10 = 125.4000
        //
        // Before R18 this tenant had to choose between 120.0000 (understating cost by 5.40/unit,
        // and therefore under-pricing) and 138.0000 (overstating it by 12.60/unit, and pricing
        // itself out of the tender). Neither number was true.
        Assert.Equal(54.0000m, LandedCostFormula.CostBearingTax(SupplierVat, 70m));
        Assert.Equal(125.4000m, LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat, 70m));

        // And the two endpoints still behave exactly as the boolean did, so no tenant's numbers
        // move as a side effect of the type change: TRUE became 100, FALSE became 0.
        Assert.Equal(120.0000m, LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat,
            CommercialMatchingPolicy.FullyRecoverablePercent));
        Assert.Equal(138.0000m, LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat, 0m));
    }

    [Fact]
    public void An_out_of_range_recovery_ratio_can_never_produce_a_negative_cost()
    {
        // The database check constraint forbids these values; this is the defence behind it.
        // An unclamped 140% would return -72.00 of "cost", quietly REDUCING landed cost and
        // therefore the customer price derived from it. An unclamped -40% would return 252.00,
        // more cost than the supplier ever charged in tax.
        Assert.Equal(0m, LandedCostFormula.CostBearingTax(SupplierVat, 140m));
        Assert.Equal(SupplierVat, LandedCostFormula.CostBearingTax(SupplierVat, -40m));
    }

    [Fact]
    public void Removing_the_tax_does_not_remove_freight()
    {
        // Guard against over-correcting. Freight is a non-recoverable cost of getting the goods to
        // the warehouse and belongs in landed cost at any recovery ratio.
        Assert.Equal(120.0000m, LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat,
            CommercialMatchingPolicy.FullyRecoverablePercent));
        Assert.Equal(100.0000m, LandedCostFormula.UnitCost(UnitPrice, Quantity, 0m, SupplierVat,
            CommercialMatchingPolicy.FullyRecoverablePercent));
        Assert.Equal(0m, LandedCostFormula.CostBearingTax(SupplierVat,
            CommercialMatchingPolicy.FullyRecoverablePercent));
        Assert.Equal(SupplierVat, LandedCostFormula.CostBearingTax(SupplierVat, 0m));
    }

    // ───────────────────────────────────────────── the switch

    [Fact]
    public async Task Input_tax_recoverability_is_a_per_business_unit_policy_defaulting_to_recoverable()
    {
        using var database = new TestDb();
        const long recoverableTenant = 96_501;
        const long exemptTenant = 96_502;
        const long partlyExemptTenant = 96_503;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, recoverableTenant);
            Seed.EnsureBusinessUnit(seed, exemptTenant);
            Seed.EnsureBusinessUnit(seed, partlyExemptTenant);
            // Only the two non-default tenants have a row. The other tenant has never touched its
            // policy, and must still be costed correctly rather than falling into some other default.
            seed.CommercialMatchingPolicies.Add(new CommercialMatchingPolicy
            {
                BusinessUnitId = exemptTenant,
                SupplierInputTaxRecoverablePercent = 0m,
                CreatedOn = DateTime.UtcNow
            });
            seed.CommercialMatchingPolicies.Add(new CommercialMatchingPolicy
            {
                BusinessUnitId = partlyExemptTenant,
                SupplierInputTaxRecoverablePercent = 70m,
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(null);
        Assert.Equal(100m, await context.ResolveSupplierInputTaxRecoverablePercentAsync(recoverableTenant));
        Assert.Equal(0m, await context.ResolveSupplierInputTaxRecoverablePercentAsync(exemptTenant));
        // The value a boolean had nowhere to put.
        Assert.Equal(70m, await context.ResolveSupplierInputTaxRecoverablePercentAsync(partlyExemptTenant));
        Assert.Equal(CommercialMatchingPolicy.FullyRecoverablePercent,
            CommercialMatchingPolicy.DefaultFor(recoverableTenant).SupplierInputTaxRecoverablePercent);
    }

    // ───────────────────────────────────────────── the production paths

    [Fact]
    public async Task A_captured_supplier_line_lands_at_the_tax_free_build_up()
    {
        using var fixture = new ProcurementScenario();
        var quotedItemId = await CaptureTaxBearingQuoteAsync(fixture, "input-tax-capture");

        await using var context = fixture.Context();
        var row = await context.SupplierQuotedItems.SingleAsync(x => x.Id == quotedItemId);

        // (100 x 10 + 200 freight) / 10. Before the fix this row was written as 138.0000.
        Assert.Equal(120.0000m, row.LandedUnitCost);
        // The tax is still recorded. It is what we owe the supplier and the input VAT the tenant
        // reclaims — it is simply not a cost of the goods.
        Assert.Equal(SupplierVat, row.TaxAmount);
        Assert.Equal(Freight, row.FreightCost);
    }

    [Fact]
    public async Task The_customer_price_derived_from_an_award_does_not_embed_supplier_vat()
    {
        // This is the test that would have caught the double count. It runs the real chain:
        // capture -> award -> ApplyPricingAsync, and asserts on the persisted customer price.
        using var fixture = new ProcurementScenario();
        await SetInventoryOnHandAsync(fixture, 0m);
        var quotedItemId = await CaptureTaxBearingQuoteAsync(fixture, "input-tax-pricing");
        var award = await fixture.Execute(service => service.ApproveAwardAsync(new ApproveAwardCommand(
            fixture.BusinessUnitId, quotedItemId, Quantity, 1, "input-tax-pricing-award", "qa",
            "corr-input-tax-pricing-award", 42, "Best eligible landed cost")));

        // The award carries the same tax-free landed cost; before the fix it carried 138.0000.
        Assert.Equal(120.0000m, award.LandedUnitCost);

        var quoteItemId = await AddDraftCustomerQuoteAsync(fixture, Quantity);
        await using var context = fixture.Context();
        var priced = await new SupplierQuoteCommercialService(context).ApplyPricingAsync(
            new ApplyCustomerQuotePricingCommand(fixture.BusinessUnitId, quoteItemId, award.Id,
                TargetMarginPercent, "Evidence-backed target margin", "input-tax-pricing", "qa",
                "corr-input-tax-pricing"));

        // 120 / (1 - 0.20). The defect produced 172.500000 — the supplier's 18.00/unit of VAT
        // marked up by the margin to 22.50 and handed to the customer as price.
        Assert.Equal(120.0000m, priced.SupplierLandedUnitCost);
        Assert.Equal(150.000000m, priced.CustomerUnitPrice);
        Assert.NotEqual(172.500000m, priced.CustomerUnitPrice);

        // Stated as the invariant rather than as a number: the price must be what the tax-free
        // build-up supports, and must not have moved by the VAT at all.
        var taxFreeLanded = LandedCostFormula.UnitCost(UnitPrice, Quantity, Freight, SupplierVat,
            CommercialMatchingPolicy.FullyRecoverablePercent);
        Assert.Equal(decimal.Round(taxFreeLanded / (1m - TargetMarginPercent / 100m), 6,
            MidpointRounding.AwayFromZero), priced.CustomerUnitPrice);
        Assert.Equal(SupplierVat / Quantity / (1m - TargetMarginPercent / 100m),
            172.50m - priced.CustomerUnitPrice);

        var quoteItem = await context.QuoteItems.SingleAsync(x => x.Id == quoteItemId);
        Assert.Equal(150.000000m, quoteItem.UnitPrice);

        // R17: output VAT is now DERIVED and PERSISTED by the same call that set the price, rather
        // than being computed by this test and never by the product. With the supplier's VAT out of
        // the cost base, the customer is charged 15% exactly once: 1,725.00 gross on a 1,500.00 net
        // line, against 1,983.75 before R15 — and against 1,500.00 with no VAT at all, which is
        // what shipping R15 without R17 would have sent to the customer.
        Assert.Equal(225.00m, quoteItem.TaxAmount);
        Assert.Equal(1_725.00m, quoteItem.TotalAmount);
        Assert.Equal(15m, quoteItem.TaxRatePercentApplied);
        Assert.Equal(QuoteLineTaxCategories.Standard, quoteItem.TaxCategory);
    }

    // ───────────────────────────────────────────── fixture plumbing

    /// <summary>
    /// A single supplier line carrying real VAT: 10 at 100, freight 200, tax 180, no duty, other or
    /// discount. The shared <c>ProcurementScenario.QuoteLine</c> helper quotes zero tax, which is
    /// exactly why the defect survived its test suite.
    /// </summary>
    private static async Task<long> CaptureTaxBearingQuoteAsync(ProcurementScenario fixture, string key)
    {
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation($"{key}-sol")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var captured = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            new Procurement.CaptureSupplierQuoteCommand(fixture.BusinessUnitId, solicitation.Id,
                $"SQ-{key}", 1, DateTime.UtcNow.AddDays(30), $"{key}-quote", "qa", $"corr-{key}",
                [new Procurement.CaptureSupplierQuoteLine(fixture.RfqItemId, ProcurementTestData.Product,
                    Quantity, UnitPrice, ProcurementTestData.Currency, 5, Quantity, Freight, 0m, 0m,
                    SupplierVat, 0m, 1m, 95m)])));
        return Assert.Single(captured.LineIds);
    }

    private static async Task SetInventoryOnHandAsync(ProcurementScenario fixture, decimal quantity)
    {
        await using var context = fixture.Context();
        var inventory = await context.Set<Models.Inventory>()
            .SingleAsync(x => x.Id == ProcurementTestData.Inventory);
        inventory.QtyOnHand = quantity;
        await context.SaveChangesAsync();
    }

    private static async Task<long> AddDraftCustomerQuoteAsync(ProcurementScenario fixture, decimal quantity)
    {
        await using var context = fixture.Context();
        const long statusId = 96_599;
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = statusId,
            SetupType = "QuoteStatus",
            SetupCode = "DRAFT",
            SetupValue = "Draft",
            BusinessUnitId = fixture.BusinessUnitId,
            IsActive = true,
            CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow
        });
        var quote = new Quote
        {
            QuoteNo = $"QT-INPUTTAX-{Guid.NewGuid():N}",
            Rfqid = fixture.RfqId,
            BusinessUnitId = fixture.BusinessUnitId,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(14),
            StatusId = statusId,
            CurrencyId = ProcurementTestData.Currency,
            TotalAmount = 0m,
            CreatedBy = "qa",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            RfqitemId = fixture.RfqItemId,
            ProductId = ProcurementTestData.Product,
            ItemDescription = "QA Product",
            Quantity = quantity,
            UnitPrice = 0m,
            TotalAmount = 0m,
            CreatedBy = "qa",
            CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();
        return quote.QuoteItems.Single().Id;
    }
}

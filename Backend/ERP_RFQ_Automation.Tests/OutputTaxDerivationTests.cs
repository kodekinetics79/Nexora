using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Output VAT is DERIVED on the sell side, and a quote whose tax was never derived does not leave
/// the building.
///
/// The defect
/// ----------
/// Nothing computed output tax. <c>QuoteService.PrepareDraftFromRfqAsync</c> wrote
/// <c>TaxAmount = null</c> on every line; <c>UpdateQuoteAsync</c> took the client's value verbatim;
/// <c>ValidateQuoteItemFinancials</c> rejected only NEGATIVE tax, so null and zero both passed;
/// <c>OrderService.ResolveLineTaxAmount</c> returned the submitted amount unchanged above a
/// standing TODO; and <c>SupplierQuoteCommercialService.ApplyPricingAsync</c> recomputed the line
/// total from a STALE tax figure after changing the unit price.
///
/// Why it was invisible
/// --------------------
/// It was cancelling the input-VAT defect that decision R15 removed. Recoverable supplier input VAT
/// was being carried into landed cost, and both legs are 15% on nearly the same base, so the
/// overstated cost and the missing output tax offset to within rounding. Removing one without the
/// other is worse than removing neither:
///
///   one unit, landed cost 120, target margin 20%
///
///                                    price   VAT stated   net to seller   true margin
///     before R15 (both defects)     172.50   none         150.00          20.0%
///     after R15, no output leg      150.00   none         130.43           8.0%
///     after R15 + R17 (this)        150.00    22.50       150.00          20.0%
///
/// The middle row is what the panel escalated. Under KSA law a price carrying no separately stated
/// VAT is DEEMED VAT-inclusive, so the seller owes 15/115 ≈ 13.04% out of it — 19.57 on a 150.00
/// line — and most of a 20% gross margin is gone before anyone notices.
///
/// What the user still decides
/// ---------------------------
/// The tax CATEGORY (R19). The system infers nothing from an address or a delivery term: a
/// Riyadh-registered customer can buy for export, and a correctly zero-rated export and a domestic
/// sale where the rep forgot the 15% used to be byte-identical records.
/// </summary>
public sealed class OutputTaxDerivationTests
{
    private const decimal KsaRate = 15m;

    // ───────────────────────────────────────────── the formula

    [Fact]
    public void A_domestic_standard_rated_line_derives_the_tenants_rate()
    {
        // 10 at 150.00, no discount. Base 1,500.00; VAT 225.00; gross 1,725.00.
        var baseAmount = OutputTaxFormula.TaxableBase(10m * 150m, 0m);
        Assert.Equal(1_500.00m, baseAmount);
        Assert.Equal(225.00m, OutputTaxFormula.Derive(baseAmount, KsaRate, QuoteLineTaxCategories.Standard));
        Assert.Equal(KsaRate, OutputTaxFormula.EffectiveRatePercent(KsaRate, QuoteLineTaxCategories.Standard));
    }

    [Fact]
    public void A_zero_rated_export_derives_zero_and_is_not_the_same_record_as_a_forgotten_15_percent()
    {
        var baseAmount = OutputTaxFormula.TaxableBase(10m * 150m, 0m);

        // Zero is DERIVED, not absent: the amount, the applied rate and the category all exist.
        Assert.Equal(0m, OutputTaxFormula.Derive(baseAmount, KsaRate, QuoteLineTaxCategories.ZeroRatedExport));
        Assert.Equal(0m, OutputTaxFormula.EffectiveRatePercent(KsaRate, QuoteLineTaxCategories.ZeroRatedExport));

        // And it is distinguishable from the domestic line, which is the whole point of R19.
        Assert.NotEqual(
            OutputTaxFormula.Derive(baseAmount, KsaRate, QuoteLineTaxCategories.Standard),
            OutputTaxFormula.Derive(baseAmount, KsaRate, QuoteLineTaxCategories.ZeroRatedExport));
    }

    [Theory]
    [InlineData(QuoteLineTaxCategories.ZeroRatedExport)]
    [InlineData(QuoteLineTaxCategories.Exempt)]
    [InlineData(QuoteLineTaxCategories.OutOfScopeRcm)]
    public void Every_non_standard_category_derives_zero_at_any_tenant_rate(string category)
    {
        Assert.Equal(0m, OutputTaxFormula.Derive(1_000m, KsaRate, category));
        Assert.Equal(0m, OutputTaxFormula.Derive(1_000m, 5m, category));
        // Including when the tenant has stated no rate at all, so an export-only business unit can
        // quote without ever having to invent a standard rate it does not use.
        Assert.Equal(0m, OutputTaxFormula.Derive(1_000m, null, category));
    }

    [Fact]
    public void A_standard_rated_line_with_no_configured_rate_derives_NOTHING_rather_than_zero()
    {
        // This is the distinction the whole gate stands on. Returning 0m here would reinstate the
        // defect: a quote with no VAT on it that nothing downstream can tell apart from a correctly
        // zero-rated one, and which the tax authority reads as VAT-inclusive.
        Assert.Null(OutputTaxFormula.Derive(1_000m, null, QuoteLineTaxCategories.Standard));
        Assert.Null(OutputTaxFormula.EffectiveRatePercent(null, QuoteLineTaxCategories.Standard));

        // Zero is still expressible — it is just a different, positive statement.
        Assert.Equal(0m, OutputTaxFormula.Derive(1_000m, 0m, QuoteLineTaxCategories.Standard));
    }

    [Fact]
    public void Tax_is_charged_on_the_net_consideration_after_discount()
    {
        // 10 at 150 less a 300.00 discount: base 1,200.00, VAT 180.00. Taxing the gross would
        // over-collect 45.00 the customer never agreed to pay.
        Assert.Equal(1_200.00m, OutputTaxFormula.TaxableBase(1_500m, 300m));
        Assert.Equal(180.00m, OutputTaxFormula.Derive(1_200m, KsaRate, QuoteLineTaxCategories.Standard));

        // A discount larger than the line is a data error, not a negative supply.
        Assert.Equal(0m, OutputTaxFormula.TaxableBase(1_500m, 2_000m));
    }

    [Fact]
    public void A_null_category_is_read_as_standard_rated()
    {
        // Lines written before R19 have no category. They were domestic standard-rated sales, so
        // that is what they are read as — never as "unknown, therefore untaxed".
        Assert.Equal(QuoteLineTaxCategories.Standard, QuoteLineTaxCategories.Normalize(null));
        Assert.Equal(QuoteLineTaxCategories.Standard, QuoteLineTaxCategories.Normalize("  "));
        Assert.True(QuoteLineTaxCategories.IsTaxable(null));
        Assert.Equal(225.00m, OutputTaxFormula.Derive(1_500m, KsaRate, null));
    }

    [Fact]
    public void An_unrecognised_category_is_refused_rather_than_guessed()
    {
        Assert.False(QuoteLineTaxCategories.IsKnown("ZERO_RATED"));
        Assert.False(QuoteLineTaxCategories.IsKnown("standard rated"));
        Assert.True(QuoteLineTaxCategories.IsKnown("zero_rated_export")); // case is not the user's problem
    }

    // ───────────────────────────────────────────── the policy

    [Fact]
    public async Task The_output_tax_rate_is_a_per_business_unit_policy_defaulting_to_the_KSA_standard_rate()
    {
        using var database = new TestDb();
        const long defaultTenant = 97_101;
        const long reducedRateTenant = 97_102;
        const long unstatedRateTenant = 97_103;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, defaultTenant);
            Seed.EnsureBusinessUnit(seed, reducedRateTenant);
            Seed.EnsureBusinessUnit(seed, unstatedRateTenant);
            seed.CommercialMatchingPolicies.Add(new CommercialMatchingPolicy
            {
                BusinessUnitId = reducedRateTenant,
                OutputTaxRatePercent = 5m,
                CreatedOn = DateTime.UtcNow
            });
            seed.CommercialMatchingPolicies.Add(new CommercialMatchingPolicy
            {
                BusinessUnitId = unstatedRateTenant,
                OutputTaxRatePercent = null,
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(null);
        // No row at all: the entity's default, which is the home jurisdiction's rate.
        Assert.Equal(KsaRate, await context.ResolveOutputTaxRatePercentAsync(defaultTenant));
        Assert.Equal(5m, await context.ResolveOutputTaxRatePercentAsync(reducedRateTenant));
        Assert.Null(await context.ResolveOutputTaxRatePercentAsync(unstatedRateTenant));
    }

    // ───────────────────────────────────────────── the production path

    [Fact]
    public async Task A_quote_is_created_without_the_caller_supplying_an_actor()
    {
        // POST /api/Quote answered "The CreatedBy field is required" while the very next line of
        // the controller overwrote whatever was sent with the token's actor. [ApiController]
        // validation runs BEFORE the action body, so the endpoint rejected requests it would
        // then have ignored — a caller could only get through by inventing a value that was
        // immediately discarded.
        //
        // Attribution is the server's to decide. A request that omits it must succeed, and must
        // never persist a blank actor: Quote.CreatedBy is non-nullable, and an empty audit
        // record is worse than a named default.
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        const long tenant = 97_401;
        Seed.EnsureBusinessUnit(context, tenant);
        context.SetupMasters.Add(DraftStatus(97_499, tenant));
        await context.SaveChangesAsync();

        var service = new QuoteService(context, null!, null!);
        var created = await service.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            QuoteNo = "QT-NO-ACTOR",
            BusinessUnitId = tenant,
            QuoteDate = DateTime.UtcNow,
            // CreatedBy deliberately omitted — exactly what the controller sends before it stamps.
            QuoteItems =
            [
                new QuoteItemCreateRequestDTO
                {
                    ItemDescription = "Line with no supplied actor",
                    Quantity = 1m,
                    UnitPrice = 100m,
                },
            ],
        });

        var stored = await context.Quotes.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.False(string.IsNullOrWhiteSpace(stored.CreatedBy));
    }

    [Fact]
    public async Task A_priced_quote_line_carries_a_server_derived_tax_the_client_never_supplied()
    {
        using var database = new TestDb();
        const long tenant = 97_201;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        context.SetupMasters.Add(DraftStatus(97_299, tenant));
        await context.SaveChangesAsync();

        var service = new QuoteService(context, null!, null!);
        // The client sends tax 0.00 — precisely what used to be persisted and printed.
        var created = await service.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            QuoteNo = "QT-OUTPUTTAX-DOMESTIC",
            BusinessUnitId = tenant,
            CreatedBy = "qa",
            // Quote.QuoteDate carries a now() database default the portable lane's SQLite cannot
            // evaluate, so it is stated rather than defaulted.
            QuoteDate = DateTime.UtcNow,
            QuoteItems =
            [
                new QuoteItemCreateRequestDTO
                {
                    ItemDescription = "Domestic KSA supply",
                    Quantity = 10m,
                    UnitPrice = 150m,
                    TotalAmount = 0m,
                    TaxAmount = 0m
                }
            ]
        });

        context.ChangeTracker.Clear();
        var line = await context.QuoteItems.SingleAsync(x => x.QuoteId == created.Id);
        Assert.Equal(225.00m, line.TaxAmount);        // not the 0.00 that was submitted
        Assert.Equal(KsaRate, line.TaxRatePercentApplied);
        Assert.Equal(QuoteLineTaxCategories.Standard, line.TaxCategory);
        Assert.Equal(1_725.00m, line.TotalAmount);
        Assert.Equal(1_725.00m, (await context.Quotes.SingleAsync(x => x.Id == created.Id)).TotalAmount);
    }

    [Fact]
    public async Task A_zero_rated_export_line_derives_zero_and_records_why()
    {
        using var database = new TestDb();
        const long tenant = 97_301;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        context.SetupMasters.Add(DraftStatus(97_399, tenant));
        await context.SaveChangesAsync();

        var service = new QuoteService(context, null!, null!);
        var created = await service.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            QuoteNo = "QT-OUTPUTTAX-EXPORT",
            BusinessUnitId = tenant,
            CreatedBy = "qa",
            // Quote.QuoteDate carries a now() database default the portable lane's SQLite cannot
            // evaluate, so it is stated rather than defaulted.
            QuoteDate = DateTime.UtcNow,
            QuoteItems =
            [
                new QuoteItemCreateRequestDTO
                {
                    ItemDescription = "Export to Bahrain, ex-works Dammam",
                    Quantity = 10m,
                    UnitPrice = 150m,
                    TotalAmount = 0m,
                    TaxCategory = QuoteLineTaxCategories.ZeroRatedExport,
                    TaxCategoryReason = "Goods exported outside the GCC VAT territory; bill of lading on file."
                }
            ]
        });

        context.ChangeTracker.Clear();
        var line = await context.QuoteItems.SingleAsync(x => x.QuoteId == created.Id);
        Assert.Equal(0m, line.TaxAmount);
        Assert.Equal(0m, line.TaxRatePercentApplied);   // derived at 0, not never derived
        Assert.Equal(QuoteLineTaxCategories.ZeroRatedExport, line.TaxCategory);
        Assert.Equal("Goods exported outside the GCC VAT territory; bill of lading on file.",
            line.TaxCategoryReason);
        Assert.Equal(1_500.00m, line.TotalAmount);
    }

    [Fact]
    public async Task A_non_standard_category_without_a_reason_is_refused()
    {
        using var database = new TestDb();
        const long tenant = 97_401;
        await using var context = database.ContextFor(tenant);
        Seed.EnsureBusinessUnit(context, tenant);
        context.SetupMasters.Add(DraftStatus(97_499, tenant));
        await context.SaveChangesAsync();

        var service = new QuoteService(context, null!, null!);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateQuoteAsync(
            new QuoteCreateRequestDTO
            {
                QuoteNo = "QT-OUTPUTTAX-NOREASON",
                BusinessUnitId = tenant,
                CreatedBy = "qa",
                QuoteItems =
                [
                    new QuoteItemCreateRequestDTO
                    {
                        Quantity = 1m, UnitPrice = 100m, TotalAmount = 0m,
                        TaxCategory = QuoteLineTaxCategories.Exempt
                    }
                ]
            }));
        Assert.Contains("departs", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────── the gate

    [Fact]
    public void The_send_gate_refuses_a_line_whose_tax_was_never_derived()
    {
        var underived = new QuoteItem
        {
            CustomerLineRef = "00010",
            Quantity = 1m,
            UnitPrice = 100m,
            TaxCategory = QuoteLineTaxCategories.Standard,
            TaxRatePercentApplied = null,
            CreatedBy = "qa"
        };

        // No rate configured: the message names the fix, not the symptom.
        var blocker = QuoteService.TaxDerivationBlocker([underived], null);
        Assert.NotNull(blocker);
        Assert.Contains("00010", blocker);
        Assert.Contains("output tax rate", blocker, StringComparison.OrdinalIgnoreCase);

        // Rate configured but the line has never been priced through a derivation path.
        Assert.NotNull(QuoteService.TaxDerivationBlocker([underived], KsaRate));
    }

    [Fact]
    public void The_send_gate_passes_a_derived_line_including_a_zero_rated_one()
    {
        var domestic = new QuoteItem
        {
            CustomerLineRef = "00010", Quantity = 10m, UnitPrice = 150m, TaxAmount = 225m,
            TaxCategory = QuoteLineTaxCategories.Standard, TaxRatePercentApplied = KsaRate,
            CreatedBy = "qa"
        };
        var export = new QuoteItem
        {
            CustomerLineRef = "00020", Quantity = 10m, UnitPrice = 150m, TaxAmount = 0m,
            TaxCategory = QuoteLineTaxCategories.ZeroRatedExport,
            TaxCategoryReason = "Bill of lading on file.", TaxRatePercentApplied = 0m,
            CreatedBy = "qa"
        };

        Assert.Null(QuoteService.TaxDerivationBlocker([domestic, export], KsaRate));
        // An empty quote is not this gate's problem — other gates refuse it first.
        Assert.Null(QuoteService.TaxDerivationBlocker([], KsaRate));
    }

    [Fact]
    public void The_send_gate_refuses_a_non_standard_line_whose_reason_was_stripped()
    {
        var export = new QuoteItem
        {
            CustomerLineRef = "00020", Quantity = 1m, UnitPrice = 100m, TaxAmount = 0m,
            TaxCategory = QuoteLineTaxCategories.ZeroRatedExport, TaxCategoryReason = "   ",
            TaxRatePercentApplied = 0m, CreatedBy = "qa"
        };

        var blocker = QuoteService.TaxDerivationBlocker([export], KsaRate);
        Assert.NotNull(blocker);
        Assert.Contains("reason", blocker, StringComparison.OrdinalIgnoreCase);
    }

    private static SetupMaster DraftStatus(long id, long businessUnitId) => new()
    {
        SetupId = id,
        BusinessUnitId = businessUnitId,
        SetupType = "QuoteStatus",
        SetupCode = "DRAFT",
        SetupValue = "Draft",
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };
}

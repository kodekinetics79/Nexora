using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A quote's document must state the quote's currency, or refuse to exist.
///
/// <para><b>The defect.</b> <c>QuoteService.DraftCompletenessBlocker</c> checked the currency
/// only when the quote was still DRAFT — on the assumption that anything past DRAFT had passed
/// that gate on the way out. The PDF renderer then filled a missing currency with a hardcoded
/// <c>"USD"</c>. Backfilled and legacy quotes never went through the send gate, so production
/// (2026-09-04) holds two EXPIRED quotes with <c>CurrencyID NULL</c> and totals of 740.00 and
/// 59,200,000.00; a download of either printed "USD 59,200,000.00" as the grand total on the
/// tenant's letterhead, next to the tenant's VAT number. The send-readiness endpoint, reading
/// the same draft-scoped rule, answered <c>canSend = true</c> for both.</para>
///
/// <para>Two sources of truth: a precheck that was draft-scoped and a renderer that invented
/// the unit. Both now read one rule that is status-independent, and the fallback is gone.</para>
/// </summary>
public sealed class IssuedQuoteCurrencyTests
{
    private const long Tenant = 9_821;
    private const long SentStatusId = 98_210;
    private const long SarCurrencyId = 98_211;

    [Fact]
    public async Task A_sent_quote_with_no_currency_is_refused_a_document_instead_of_printing_USD()
    {
        // PRODUCTION'S SHAPE: quote 62 — non-draft, one priced line, tax derived, attested,
        // CurrencyID NULL. Every gate except the currency passes, which is what makes the
        // draft-scoped check the only thing standing between this row and a USD grand total.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = await SeedNonDraftQuoteAsync(context, currencyId: null);

        var service = new QuoteService(context, null!, new StubConfig());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateQuotePdfAsync(quoteId, Tenant));
        Assert.Contains("no currency", refused.Message);
        Assert.Contains("new revision", refused.Message);

        // The precheck the screen asks agrees with the renderer, on the same rule.
        var readiness = await service.EvaluateSendReadinessAsync(quoteId, Tenant);
        Assert.False(readiness.CanSend);
        var blocker = Assert.Single(readiness.Blockers, x => x.Code == "QUOTE_INCOMPLETE");
        Assert.Contains("no currency", blocker.Message);
    }

    [Fact]
    public async Task A_sent_quote_with_a_currency_prints_that_currency_and_nothing_else()
    {
        // THE CONTROL. Proves the suite is not simply refusing every non-draft, and that the
        // currency printed is the quote's own.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = await SeedNonDraftQuoteAsync(context, currencyId: SarCurrencyId);

        var service = new QuoteService(context, null!, new StubConfig());
        var pdf = await service.GenerateQuotePdfAsync(quoteId, Tenant);

        var text = PdfText(pdf);
        Assert.Contains("SAR", text);
        Assert.DoesNotContain("USD", text);

        var readiness = await service.EvaluateSendReadinessAsync(quoteId, Tenant);
        Assert.DoesNotContain(readiness.Blockers, x => x.Code == "QUOTE_INCOMPLETE");
    }

    [Fact]
    public void The_currency_rule_does_not_depend_on_the_quote_being_a_draft()
    {
        // The pure rule both callers read, pinned directly: null currency blocks regardless of
        // status, and the non-draft wording tells the rep the one thing they can still do.
        var items = new List<QuoteItem> { new() { Quantity = 1m, UnitPrice = 10m } };

        var draft = QuoteService.DraftCompletenessBlocker(true, null, DateTime.UtcNow.AddDays(30), items);
        var issued = QuoteService.DraftCompletenessBlocker(false, null, DateTime.UtcNow.AddDays(30), items);
        var complete = QuoteService.DraftCompletenessBlocker(false, SarCurrencyId, DateTime.UtcNow.AddDays(30), items);

        Assert.Contains("Set the currency", draft);
        Assert.Contains("new revision", issued);
        Assert.Null(complete);
    }

    // ------------------------------------------------------------------------ test plumbing

    private static async Task<long> SeedNonDraftQuoteAsync(ErpRfqAutomationContext context, long? currencyId)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        if (currencyId.HasValue)
            context.Currencies.Add(new Currency
            {
                Id = currencyId.Value, BusinessUnitId = Tenant, Code = "SAR",
                CurrencyName = "Saudi Riyal", CreatedBy = "seed"
            });
        var quote = new Quote
        {
            Id = 98_213,
            QuoteNo = "QT-41600",
            BusinessUnitId = Tenant,
            StatusId = SentStatusId,
            CurrencyId = currencyId,
            QuoteDate = DateTime.UtcNow.AddDays(-10),
            ValidUntil = DateTime.UtcNow.AddDays(20),
            SentOn = DateTime.UtcNow.AddDays(-9),
            TotalAmount = 851.00m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow.AddDays(-10)
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = 98_214,
            ItemDescription = "Valve, gate, 6in CL150",
            Quantity = 1m,
            UnitOfMeasure = "EA",
            UnitPrice = 740m,
            TaxAmount = 111.00m,
            TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
            TaxRatePercentApplied = 15m,
            TotalAmount = 851.00m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow.AddDays(-10)
        });
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();
        await new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quote.Id, Tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-TEST", null, "tests", default);
        return quote.Id;
    }

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    private sealed class StubConfig : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId)
            => Task.FromResult<QuoteConfiguration?>(new QuoteConfiguration
            {
                BusinessUnitId = Tenant,
                CompanyAddress = "King Fahd Road, Al Khobar 34423",
                CompanyPhone = "+966 13 800 0000",
                CompanyEmail = "sales@noorandsons.example"
            });

        public Task<QuoteConfiguration> UpsertAsync(QuoteConfiguration configurationToSave)
            => Task.FromResult(configurationToSave);

        public Task AddAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;

        public Task UpdateAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;
    }
}

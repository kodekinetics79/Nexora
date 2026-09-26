using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The Send quote window on the RFQ sets a draft's currency and validity date on their own,
/// without resubmitting the lines (the full quote update wipes any field it is not sent).
/// </summary>
public sealed class QuoteDraftTermsTests
{
    private const long Tenant = 9_901;
    private const long DraftStatusId = 99_010;
    private const long SentStatusId = 99_011;
    private const long Sar = 99_012;
    private const long Usd = 99_013;

    [Fact]
    public async Task A_draft_gets_its_currency_and_validity_and_keeps_its_lines()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = await SeedAsync(context, DraftStatusId, currencyId: null);
        var service = new QuoteService(context, null!, null!);

        var validUntil = DateTime.UtcNow.Date.AddDays(30);
        var saved = await service.SetDraftTermsAsync(quoteId, Tenant, "rep@qa", Sar, validUntil);

        Assert.Equal(Sar, saved.CurrencyId);
        Assert.Equal(validUntil, saved.ValidUntil!.Value.Date);
        Assert.Equal(740m, Assert.Single(saved.QuoteItems).UnitPrice);

        // Chosen once: another currency later would restate every price.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetDraftTermsAsync(quoteId, Tenant, "rep@qa", Usd, null));
        Assert.Contains("already has a currency", refused.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetDraftTermsAsync(quoteId, Tenant, "rep@qa", null, DateTime.UtcNow.Date.AddDays(-1)));
    }

    [Fact]
    public async Task A_sent_quote_cannot_have_its_terms_changed_here()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = await SeedAsync(context, SentStatusId, currencyId: Sar);
        var service = new QuoteService(context, null!, null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetDraftTermsAsync(quoteId, Tenant, "rep@qa", null, DateTime.UtcNow.Date.AddDays(10)));
    }

    [Fact]
    public async Task A_closing_bid_goes_out_complete_with_lines_to_follow_estimated_or_not_quoted()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = await SeedAsync(context, DraftStatusId, currencyId: Sar);
        await using (var setup = db.ContextFor(Tenant))
        {
            (await setup.Quotes.SingleAsync(q => q.Id == quoteId)).ValidUntil = DateTime.UtcNow.Date.AddDays(30);
            setup.QuoteItems.Add(new QuoteItem { Id = 99_022, QuoteId = quoteId, ItemDescription = "Late supplier", Quantity = 2m, UnitPrice = 0m, CreatedBy = "seed", CreatedDate = DateTime.UtcNow });
            setup.QuoteItems.Add(new QuoteItem { Id = 99_023, QuoteId = quoteId, ItemDescription = "Discontinued", Quantity = 1m, UnitPrice = 0m, CreatedBy = "seed", CreatedDate = DateTime.UtcNow });
            await setup.SaveChangesAsync();
        }
        context.ChangeTracker.Clear();
        var quote = await context.Quotes.AsNoTracking().Include(q => q.QuoteItems).SingleAsync(q => q.Id == quoteId);
        var service = new QuoteService(context, null!, null!);

        Assert.Contains("no price", QuoteService.DraftCompletenessBlocker(true, Sar, quote.ValidUntil, quote.QuoteItems));

        await service.SetLinePricingStatusAsync(quoteId, 99_022, Tenant, "rep@qa", "TO_FOLLOW", null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLinePricingStatusAsync(quoteId, 99_023, Tenant, "rep@qa", "NOT_QUOTED", " ", null));
        await service.SetLinePricingStatusAsync(quoteId, 99_023, Tenant, "rep@qa", "NOT_QUOTED", "Discontinued by manufacturer", null);
        var saved = await service.SetLinePricingStatusAsync(quoteId, 99_021, Tenant, "rep@qa", "ESTIMATE", null, 760m);

        await using var verify = db.ContextFor(Tenant);
        var items = await verify.QuoteItems.Where(x => x.QuoteId == quoteId).ToListAsync();
        Assert.Null(QuoteService.DraftCompletenessBlocker(true, Sar, quote.ValidUntil, items));
        Assert.Equal(("ESTIMATE", 760m), (items.Single(x => x.Id == 99_021).PricingStatus, items.Single(x => x.Id == 99_021).UnitPrice));
        Assert.Equal("Discontinued by manufacturer", items.Single(x => x.Id == 99_023).PricingNote);
        // Lines sent without a price carry no tax to derive, so they never block the send.
        Assert.DoesNotContain("Late supplier", QuoteService.TaxDerivationBlocker(items.Where(x => x.Id != 99_021), 15m) ?? "");
        Assert.Null(QuoteService.TaxDerivationBlocker(items.Where(x => x.Id != 99_021), 15m));
        Assert.Equal(760m, saved.QuoteItems.Sum(x => x.UnitPrice * x.Quantity));

        // Nothing priced at all is not a quote.
        foreach (var item in items) { item.UnitPrice = 0m; item.PricingStatus = "TO_FOLLOW"; }
        Assert.Contains("no line on this quote has a price", QuoteService.DraftCompletenessBlocker(true, Sar, quote.ValidUntil, items));
    }

    private static async Task<long> SeedAsync(ErpRfqAutomationContext context, long statusId, long? currencyId)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        context.SetupMasters.Add(new SetupMaster { SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = "DRAFT", SetupValue = "Draft", CreatedBy = "seed", CreatedOn = DateTime.UtcNow });
        context.SetupMasters.Add(new SetupMaster { SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "seed", CreatedOn = DateTime.UtcNow });
        context.Currencies.Add(new Currency { Id = Sar, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal", IsActive = true, CreatedBy = "seed" });
        context.Currencies.Add(new Currency { Id = Usd, BusinessUnitId = Tenant, Code = "USD", CurrencyName = "US Dollar", IsActive = true, CreatedBy = "seed" });
        var quote = new Quote
        {
            Id = 99_020, QuoteNo = "QT-TERMS", BusinessUnitId = Tenant, StatusId = statusId, CurrencyId = currencyId,
            QuoteDate = DateTime.UtcNow, TotalAmount = 740m, CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem { Id = 99_021, ItemDescription = "Valve", Quantity = 1m, UnitOfMeasure = "EA", UnitPrice = 740m, TotalAmount = 740m, CreatedBy = "seed", CreatedDate = DateTime.UtcNow });
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();
        return quote.Id;
    }
}

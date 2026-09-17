using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;

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

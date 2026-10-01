using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner request 2026-09-28: a rep who quoted an RFQ outside Nexora (by hand, in Excel, on the
/// customer's portal) uploads that quote. It lands on the RFQ as a sent quote with the amount they
/// quoted, the number printed on their file, and the file itself kept beside it.
/// </summary>
public sealed class QuoteUploadTests : IDisposable
{
    private const long Bu = 97_301;
    private const long DraftStatusId = 97_302;
    private const long SentStatusId = 97_303;
    private const long CustomerId = 97_304;
    private const long CurrencyId = 97_305;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-quote-upload-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Pdf = "%PDF-1.4 quote QT-EXCEL-77"u8.ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task An_uploaded_quote_is_a_sent_quote_on_its_rfq_with_the_file_kept()
    {
        var (db, ctx, rfq) = await SeedAsync(97_310);
        using var _ = db;
        await using var __ = ctx;
        var storage = Storage();
        var sentDay = DateTime.UtcNow.Date.AddDays(-3);

        var result = await Service(ctx, storage).RecordUploadedQuoteAsync(Command(rfq.Id, "QT-EXCEL-77", sentDay));

        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.Include(q => q.QuoteItems).SingleAsync(q => q.Id == result.QuoteId);
        Assert.Equal(rfq.Id, quote.Rfqid);
        Assert.Equal(CustomerId, quote.CustomerId);
        Assert.Equal(SentStatusId, quote.StatusId);
        Assert.Equal("QT-EXCEL-77", quote.ExternalQuoteReference);
        Assert.Equal(rfq.NexoraSerial, quote.NexoraSerial);
        // The day the rep says it went out, so "no reply for N days" counts from then.
        Assert.Equal(sentDay, quote.SentOn!.Value.Date);
        var line = Assert.Single(quote.QuoteItems);
        Assert.Equal(1_000m, line.UnitPrice);
        Assert.Equal(150m, line.TaxAmount);
        Assert.Equal(1_150m, quote.TotalAmount);

        Assert.Equal("Quote QT-EXCEL-77.pdf", quote.UploadedFileName);
        Assert.Equal("application/pdf", quote.UploadedFileContentType);
        Assert.Equal(Pdf.LongLength, quote.UploadedFileSize);
        await using var stream = await storage.OpenVerifiedReadAsync(quote.UploadedFileStorageUri!, quote.UploadedFileSha256!);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(Pdf, copy.ToArray());
    }

    [Fact]
    public async Task The_quote_list_shows_the_file_and_finds_the_quote_by_rfq_or_by_the_number_on_the_file()
    {
        var (db, ctx, rfq) = await SeedAsync(97_320);
        using var _ = db;
        await using var __ = ctx;
        var result = await Service(ctx, Storage()).RecordUploadedQuoteAsync(Command(rfq.Id, "QT-EXCEL-88", DateTime.UtcNow.Date));

        ctx.ChangeTracker.Clear();
        var repository = new QuoteRepository(ctx);
        var (byRfq, _) = await repository.GetAllAsync(Bu, 1, 10, rfq.Rfqno);
        var (byNumber, _) = await repository.GetAllAsync(Bu, 1, 10, "excel-88");

        var row = Assert.Single(byRfq);
        Assert.Equal(result.QuoteId, row.Id);
        Assert.Equal("Quote QT-EXCEL-77.pdf", row.UploadedFileName);
        Assert.Equal("QT-EXCEL-88", row.ExternalQuoteReference);
        Assert.Equal(result.QuoteId, Assert.Single(byNumber).Id);
        Assert.Equal("Quote QT-EXCEL-77.pdf", (await repository.GetByIdAsync(result.QuoteId, Bu)).UploadedFileName);
    }

    [Fact]
    public async Task An_rfq_that_already_has_a_quote_is_refused_by_name()
    {
        var (db, ctx, rfq) = await SeedAsync(97_330);
        using var _ = db;
        await using var __ = ctx;
        var service = Service(ctx, Storage());
        var first = await service.RecordUploadedQuoteAsync(Command(rfq.Id, "QT-A", DateTime.UtcNow.Date));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordUploadedQuoteAsync(Command(rfq.Id, "QT-B", DateTime.UtcNow.Date)));

        Assert.Contains(first.QuoteNo, refusal.Message);
        ctx.ChangeTracker.Clear();
        Assert.Equal(1, await ctx.Quotes.CountAsync(q => q.Rfqid == rfq.Id));
    }

    [Fact]
    public async Task An_unsent_draft_on_the_rfq_is_replaced_and_the_replacement_is_on_record()
    {
        var (db, ctx, rfq) = await SeedAsync(97_360);
        using var _ = db;
        await using var __ = ctx;
        var draftId = await SeedDraftAsync(ctx, rfq, 97_365);

        var result = await Service(ctx, Storage()).RecordUploadedQuoteAsync(Command(rfq.Id, "QT-EXCEL-90", DateTime.UtcNow.Date));

        ctx.ChangeTracker.Clear();
        Assert.Equal("QT-DRAFT-97365", result.ReplacedDraftNo);
        Assert.False(await ctx.Quotes.AnyAsync(q => q.Id == draftId));
        var tombstone = await ctx.QuoteRemovalRecords.SingleAsync(r => r.QuoteId == draftId);
        Assert.Equal(QuoteRemovalModes.DraftDiscarded, tombstone.Mode);
        Assert.Contains("QT-EXCEL-90", tombstone.Reason);
        Assert.Equal(result.QuoteId, (await ctx.Quotes.SingleAsync(q => q.Rfqid == rfq.Id)).Id);
    }

    [Fact]
    public async Task A_draft_whose_prices_were_confirmed_is_not_replaced()
    {
        var (db, ctx, rfq) = await SeedAsync(97_370);
        using var _ = db;
        await using var __ = ctx;
        var draftId = await SeedDraftAsync(ctx, rfq, 97_375);
        await new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(ctx).AttestAsync(
            draftId, Bu, ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SalesManager,
            "Manager", null, "tests", default);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(ctx, Storage()).RecordUploadedQuoteAsync(Command(rfq.Id, "QT-EXCEL-91", DateTime.UtcNow.Date)));

        Assert.Contains("QT-DRAFT-97375", refusal.Message);
        ctx.ChangeTracker.Clear();
        Assert.True(await ctx.Quotes.AnyAsync(q => q.Id == draftId));
    }

    [Fact]
    public async Task The_same_quote_number_cannot_be_uploaded_twice()
    {
        var (db, ctx, rfq) = await SeedAsync(97_340);
        using var _ = db;
        await using var __ = ctx;
        var second = await SecondRfqAsync(ctx, rfq, 97_345);
        var service = Service(ctx, Storage());
        var first = await service.RecordUploadedQuoteAsync(Command(rfq.Id, "QT-SAME", DateTime.UtcNow.Date));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordUploadedQuoteAsync(Command(second.Id, " QT-SAME ", DateTime.UtcNow.Date)));

        Assert.Contains(first.QuoteNo, refusal.Message);
    }

    [Fact]
    public async Task A_date_sent_in_the_future_is_refused()
    {
        var (db, ctx, rfq) = await SeedAsync(97_350);
        using var _ = db;
        await using var __ = ctx;

        await Assert.ThrowsAsync<ArgumentException>(() => Service(ctx, Storage())
            .RecordUploadedQuoteAsync(Command(rfq.Id, "QT-LATER", DateTime.UtcNow.Date.AddDays(5))));
        Assert.Empty(await ctx.Quotes.Where(q => q.Rfqid == rfq.Id).ToListAsync());
    }

    [Fact]
    public void The_sent_moment_keeps_the_calendar_day_and_is_never_in_the_future()
    {
        var now = new DateTime(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc),
            QuoteService.UploadedQuoteSentOn(new DateTime(2026, 9, 25), now));
        Assert.Equal(now, QuoteService.UploadedQuoteSentOn(new DateTime(2026, 9, 28), now));
    }

    private IEvidenceObjectStorage Storage()
        => new LocalEvidenceObjectStorage(new LocalFileStorage(_root, _root));

    private static QuoteService Service(ErpRfqAutomationContext ctx, IEvidenceObjectStorage storage)
        => new(ctx, null!, null!, evidence: storage);

    private static UploadedQuoteCommand Command(long rfqId, string number, DateTime sentOn) => new(
        Bu, rfqId, number, sentOn, sentOn.AddDays(30), CurrencyId, 1_000m,
        "Quote QT-EXCEL-77.pdf", Pdf, "application/pdf", "rep@nexora.sa", null);

    private static async Task<(TestDb db, ErpRfqAutomationContext ctx, Rfq rfq)> SeedAsync(long seed)
    {
        var db = new TestDb();
        var ctx = db.ContextFor(Bu);
        var lead = Seed.Lead(ctx, seed, Bu);
        Seed.Customer(ctx, CustomerId, Bu, "Saudi Electricity Company");
        ctx.SetupMasters.AddRange(Status(DraftStatusId, "DRAFT"), Status(SentStatusId, "SENT"));
        ctx.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = Bu, Code = "SAR", CurrencyName = "Saudi Riyal", ExchangeRate = 1m,
            IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "CONFIRMED");
        var rfq = NewRfq(seed + 3, lead);
        ctx.Rfqs.Add(rfq);
        await ctx.SaveChangesAsync();
        return (db, ctx, rfq);
    }

    private static async Task<long> SeedDraftAsync(ErpRfqAutomationContext ctx, Rfq rfq, long id)
    {
        var draft = new Quote
        {
            Id = id, QuoteNo = $"QT-DRAFT-{id}", BusinessUnitId = Bu, Rfqid = rfq.Id, CustomerId = CustomerId,
            StatusId = DraftStatusId, CurrencyId = CurrencyId, QuoteDate = DateTime.UtcNow, TotalAmount = 50,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow,
            QuoteItems = [new QuoteItem { ItemDescription = "Battery", Quantity = 1, UnitPrice = 50, TotalAmount = 50, CreatedBy = "seed", CreatedDate = DateTime.UtcNow }],
        };
        draft.InheritCommercialIdentity(rfq);
        ctx.Quotes.Add(draft);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        return id;
    }

    private static async Task<Rfq> SecondRfqAsync(ErpRfqAutomationContext ctx, Rfq first, long seed)
    {
        var lead = Seed.Lead(ctx, seed, Bu);
        await ctx.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "CONFIRMED");
        var rfq = NewRfq(seed + 3, lead);
        ctx.Rfqs.Add(rfq);
        await ctx.SaveChangesAsync();
        return rfq;
    }

    private static Rfq NewRfq(long id, Lead lead)
    {
        var rfq = new Rfq
        {
            Id = id, Rfqno = $"RFQ-{id}", RecDate = lead.RecDate, LeadId = lead.Id, BusinessUnitId = Bu,
            CustomerId = lead.CustomerId, CreatedBy = "seed", CreatedDate = DateTime.UtcNow,
        };
        rfq.InheritCommercialIdentity(lead);
        return rfq;
    }

    private static SetupMaster Status(long id, string code) => new()
    {
        SetupId = id, BusinessUnitId = Bu, SetupType = "QuoteStatus", SetupCode = code,
        SetupValue = code == "DRAFT" ? "Draft" : "Sent", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };
}

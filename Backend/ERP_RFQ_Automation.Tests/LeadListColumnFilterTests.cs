using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner 2026-09-29: "give filter on all fields" — every Leads column header filters the list.
/// Text columns match "contains", dates take an inclusive day range, Items a min/max and Status a
/// status or "not opened yet". Applied on the server, so the count is the rows.
/// </summary>
public sealed class LeadListColumnFilterTests
{
    private const long Bu = 97_200;
    private static readonly DateTime Today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);

    private static async Task<LeadRepository> SeedAsync(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
    {
        Seed.LeadStatus(context, 97_281, Bu, "Qualified");
        var a = Seed.Lead(context, 97_211, Bu, buyersName: "Noorah A Alotaibi",
            items: Enumerable.Range(1, 3).Select(i => Seed.LeadItem(97_2110 + i, $"{i}", 1)));
        a.Rfqno = "6000000028";
        a.AgreementReference = "OA-4410";
        a.BidClosingDate = Today.AddDays(2).AddHours(17);
        a.RequiredDeliveryDate = Today.AddDays(30);
        a.RecDate = Today.AddDays(-1).AddHours(9);

        var b = Seed.Lead(context, 97_212, Bu, leadStatusId: 97_281, buyersName: "Saba S Alkhambashi",
            items: Enumerable.Range(1, 12).Select(i => Seed.LeadItem(97_2120 + i, $"{i}", 1)));
        b.Rfqno = "C001832162";
        b.Clientemail = "saba@se.com.sa";
        b.BidClosingDate = Today.AddDays(9);
        b.RecDate = Today.AddDays(-10);
        await context.SaveChangesAsync();
        return new LeadRepository(context);
    }

    private static async Task<long[]> Ids(LeadRepository repo, LeadListColumnFilters columns)
    {
        var (rows, total) = await repo.GetLeadListAsync(1, 50, null, null, null, null, Bu, view: "queue", columns: columns);
        var ids = rows.Select(r => r.Id).OrderBy(id => id).ToArray();
        Assert.Equal(ids.Length, total);
        return ids;
    }

    [Fact]
    public async Task Text_columns_match_contains()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { Rfq = "000028" }));
        Assert.Equal(new long[] { 97_212 }, await Ids(repo, new() { Buyer = "se.com.sa" }));   // buyer email counts
        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { Buyer = "NOORAH" }));
        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { Agreement = "4410" }));
        var serial = (await repo.GetLeadListAsync(1, 50, null, null, null, null, Bu, view: "queue")).Item1.Single(l => l.Id == 97_212).NexoraSerial!;
        Assert.Equal(new long[] { 97_212 }, await Ids(repo, new() { Serial = serial }));
    }

    [Fact]
    public async Task Date_columns_take_an_inclusive_day_range()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        // A 17:00 closing on the last day of the range is inside it.
        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { DueFrom = Today, DueTo = Today.AddDays(2) }));
        Assert.Equal(new long[] { 97_212 }, await Ids(repo, new() { DueFrom = Today.AddDays(3) }));
        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { RequiredFrom = Today.AddDays(29), RequiredTo = Today.AddDays(30) }));
        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { ReceivedFrom = Today.AddDays(-7) }));
        Assert.Equal(new long[] { 97_212 }, await Ids(repo, new() { ReceivedTo = Today.AddDays(-2) }));
    }

    [Fact]
    public async Task Items_and_status_narrow_too()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { ItemsMax = 10 }));
        Assert.Equal(new long[] { 97_212 }, await Ids(repo, new() { ItemsMin = 11 }));
        Assert.Equal(new long[] { 97_211 }, await Ids(repo, new() { Status = "none" }));
        Assert.Equal(new long[] { 97_212 }, await Ids(repo, new() { Status = "97281" }));

        var (statuses, notOpened) = await repo.GetLeadListStatusesAsync(Bu, "queue");
        var qualified = Assert.Single(statuses);
        Assert.Equal((97_281L, "QUALIFIED", "Qualified", 1), (qualified.StatusId, qualified.Code, qualified.Label, qualified.Count));
        Assert.Equal(1, notOpened);
    }
    /// <summary>
    /// Ingested is what its cell shows: the earliest arrival of the lead's source documents, or the
    /// lead's creation when it has none. A lead read today from mail that arrived five days ago was
    /// ingested five days ago.
    /// </summary>
    [Fact]
    public async Task Ingested_filters_on_the_same_time_the_cell_shows()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        const long bu = Bu + 1;
        var now = DateTimeOffset.UtcNow;
        var mailed = Seed.Lead(context, 97_231, bu);
        mailed.CreatedDate = now.UtcDateTime;            // read today...
        var typed = Seed.Lead(context, 97_232, bu);
        typed.CreatedDate = now.AddDays(-2).UtcDateTime; // no source documents: its creation counts
        await context.SaveChangesAsync();

        var batchId = Guid.NewGuid();
        var corpus = DocumentCorpus.Create(bu, batchId, CorpusSourceType.Email);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(bu, corpus.Id, new string('a', 64), "rfq.pdf", "application/pdf", "b", "k", "v1", 10);
        context.Add(source);
        await context.SaveChangesAsync();
        var arrived = SourceDocumentOccurrence.Create(bu, source.Id, corpus.Id, "ingested-filter", "{}", receivedOn: now.AddDays(-5)); // ...from mail five days old
        context.Add(arrived);
        await context.SaveChangesAsync();
        var batch = new LeadIngestionBatch
        {
            Id = batchId, BusinessUnitId = bu, SourceChannel = "Email",
            CreatedBy = "tests", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        context.Add(new LeadIngestionOccurrence
        {
            BusinessUnitId = bu, Batch = batch, LeadId = mailed.Id, SourceDocumentId = source.Id,
            SourceDocumentOccurrenceId = arrived.Id, SourceChannel = "Email", IdempotencyKey = "ingested-filter",
            LogicalInquiryFingerprint = new string('a', 64), Classification = LeadOccurrenceClassification.New,
            Confidence = 1m, ProcessingPath = LeadProcessingPath.Deterministic,
            IngestedAtUtc = now, CreatedAtUtc = now, ActorType = "TestFixture", ActorId = "tests", CorrelationId = "ingested-filter",
        });
        await context.SaveChangesAsync();
        var repo = new LeadRepository(context);

        async Task<long[]> IngestedIds(DateTimeOffset? from, DateTimeOffset? before)
        {
            var (rows, total) = await repo.GetLeadListAsync(1, 50, null, null, null, null, bu, view: "queue",
                columns: new() { IngestedFrom = from, IngestedBefore = before });
            Assert.Equal(rows.Count(), total);
            return rows.Select(r => r.Id).OrderBy(id => id).ToArray();
        }

        Assert.Equal(new long[] { 97_232 }, await IngestedIds(now.AddDays(-3), null));   // last 3 days: only the typed one
        Assert.Equal(new long[] { 97_231 }, await IngestedIds(null, now.AddDays(-4)));   // before 4 days ago: the mailed one
        Assert.Equal(new long[] { 97_231, 97_232 }, await IngestedIds(now.AddDays(-6), now.AddHours(1)));
    }
}

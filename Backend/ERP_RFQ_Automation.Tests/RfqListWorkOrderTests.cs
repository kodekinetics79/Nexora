using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner review 2026-09-27: the RFQ list is a work queue. RFQs not yet quoted to the customer come
/// first, soonest deadline first, no deadline last; each row says whether and when a quote went out.
/// The list used to page with no order at all.
/// </summary>
public sealed class RfqListWorkOrderTests
{
    private const long Tenant = 96_101;

    [Fact]
    public async Task Unquoted_rfqs_lead_by_deadline_and_a_sent_quote_carries_its_date()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var leads = Enumerable.Range(0, 4).Select(i => Seed.Lead(context, 96_111 + i, Tenant)).ToList();
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        Rfq Add(long id, DateTime? deadline)
        {
            var lead = leads[(int)id - 1];
            var rfq = new Rfq
            {
                Id = id, Rfqno = $"RFQ-{id}", RecDate = now, BidClosingDate = deadline, BusinessUnitId = Tenant,
                LeadId = lead.Id, CreatedBy = "seed", CreatedDate = now
            };
            rfq.InheritCommercialIdentity(lead);
            context.Rfqs.Add(rfq);
            return rfq;
        }
        Add(1, now.AddDays(9));
        Add(2, null);
        Add(3, now.AddDays(-3));   // late but already quoted
        Add(4, now.AddDays(2));
        await context.SaveChangesAsync();
        var sentOn = now.AddDays(-1);
        foreach (var (quoteId, no, sent) in new[] { (11L, "QT-OLD", (DateTime?)sentOn), (12L, "QT-REV", null) })
            context.Quotes.Add(new Quote
            {
                Id = quoteId, QuoteNo = no, Rfqid = 3, BusinessUnitId = Tenant, QuoteDate = now, SentOn = sent,
                CreatedBy = "seed", CreatedDate = now
            });
        await context.SaveChangesAsync();

        var (rows, total) = await new RfqRepository(context).GetAllAsync(Tenant);
        var list = rows.ToList();

        Assert.Equal(4, total);
        Assert.Equal(new long[] { 4, 1, 2, 3 }, list.Select(r => r.Id));
        var quoted = list.Single(r => r.Id == 3);
        // The newest revision is still a draft; the customer already has the one sent yesterday.
        Assert.Equal("QT-REV", quoted.LatestQuoteNo);
        Assert.NotNull(quoted.LatestQuoteSentOn);
        Assert.Null(list.Single(r => r.Id == 4).LatestQuoteNo);
    }
}

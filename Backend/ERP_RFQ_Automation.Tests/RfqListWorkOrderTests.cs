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
    /// <summary>
    /// Owner 2026-09-29: once an RFQ becomes a quote it leaves the RFQ list. "Became a quote" means
    /// SENT: pricing the first line creates the draft, and a half-priced RFQ must stay where the rep
    /// prices it. A withdrawn quote does not count; an RFQ closed without a quote is finished too.
    /// A search (no readiness) still reaches every RFQ.
    /// </summary>
    [Fact]
    public async Task Open_list_drops_rfqs_whose_quote_was_sent_or_that_were_closed()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        const long tenant = Tenant + 1;
        var leads = Enumerable.Range(0, 6).Select(i => Seed.Lead(context, 96_211 + i, tenant)).ToList();
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = 96_290, SetupType = "RFQStatus", SetupCode = "CANCELLED", SetupValue = "Cancelled",
            BusinessUnitId = tenant, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            var rfq = new Rfq
            {
                Id = 201 + i, Rfqno = $"RFQ-{201 + i}", RecDate = now, BusinessUnitId = tenant,
                LeadId = leads[i].Id, CreatedBy = "seed", CreatedDate = now,
                RfqstatusId = i == 5 ? 96_290 : null,
            };
            rfq.InheritCommercialIdentity(leads[i]);
            context.Rfqs.Add(rfq);
        }
        await context.SaveChangesAsync();
        Quote Q(long id, long rfqId, DateTime? sentOn) => new()
        {
            Id = id, QuoteNo = $"QT-{id}", Rfqid = rfqId, BusinessUnitId = tenant, QuoteDate = now, SentOn = sentOn,
            CreatedBy = "seed", CreatedDate = now
        };
        // 201: no quote. 202: sent. 203: draft only (pricing under way). 204: sent + draft revision.
        // 205: only a withdrawn quote. 206: cancelled, no quote.
        context.Quotes.Add(Q(21, 202, now.AddDays(-1)));
        context.Quotes.Add(Q(22, 203, null));
        context.Quotes.Add(Q(23, 204, now.AddDays(-2)));
        context.Quotes.Add(Q(24, 204, null));
        var withdrawn = Q(25, 205, now.AddDays(-3));
        withdrawn.RemovedOn = now; withdrawn.RemovedBy = "seed"; withdrawn.RemovalReason = "wrong customer";
        context.Quotes.Add(withdrawn);
        await context.SaveChangesAsync();

        var repo = new RfqRepository(context);
        var (open, openTotal) = await repo.GetAllAsync(tenant, readiness: "open");
        Assert.Equal(new long[] { 201, 203, 205 }, open.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(3, openTotal);

        var (all, allTotal) = await repo.GetAllAsync(tenant);
        Assert.Equal(6, allTotal);
        Assert.Equal(6, all.Count());
    }
}

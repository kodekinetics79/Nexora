using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner 2026-09-30: the RFQs list gets a filter on every column, as the Leads list did. Text
/// columns match "contains", dates take an inclusive day range, Lines a min/max, Quote the state of
/// the RFQ's quote, and the choice columns one value. Applied on the server, so the count is the rows.
/// </summary>
public sealed class RfqListColumnFilterTests
{
    private const long Tenant = 96_301;
    private static readonly DateTime Today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);

    private static async Task<RfqRepository> SeedAsync(ErpRfqAutomationContext context)
    {
        var leads = Enumerable.Range(0, 3).Select(i => Seed.Lead(context, 96_311 + i, Tenant)).ToList();
        Seed.Customer(context, 96_381, Tenant, "Saudi Aramco");
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = 96_391, SetupType = "RFQStatus", SetupCode = "IN_PROGRESS", SetupValue = "In progress",
            BusinessUnitId = Tenant, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        Rfq Add(long id, int leadIndex, Action<Rfq> shape)
        {
            var rfq = new Rfq
            {
                Id = id, Rfqno = $"RFQ-{id}", RecDate = Today.AddDays(-1), BusinessUnitId = Tenant,
                LeadId = leads[leadIndex].Id, CreatedBy = "seed", CreatedDate = Today.AddHours(8),
            };
            rfq.InheritCommercialIdentity(leads[leadIndex]);
            shape(rfq);
            context.Rfqs.Add(rfq);
            return rfq;
        }
        Add(301, 0, r =>
        {
            r.Rfqno = "6000000028"; r.CustomerRfqReference = "SEC-TENDER-77"; r.BuyersName = "Noorah A Alotaibi";
            r.DeliveryLocation = "Jubail Industrial City"; r.AgreementReference = "OA-4410"; r.OpportunityNo = "OPP-9";
            r.Rfqtype = "Agreement"; r.InquiryType = "Tender"; r.BiddingDecision = "Bid";
            r.BidClosingDate = Today.AddDays(2).AddHours(17); r.RequiredDeliveryDate = Today.AddDays(30);
            r.CustomerId = 96_381; r.RfqstatusId = 96_391;
            foreach (var n in Enumerable.Range(1, 3)) r.Rfqitems.Add(new Rfqitem { LineItemNo = $"{n}", Quantity = 1, CreatedBy = "seed", CreatedDate = Today });
        });
        Add(302, 1, r =>
        {
            r.Rfqno = "C001832162"; r.BuyersName = "Saba S Alkhambashi"; r.Rfqtype = "Spot"; r.InquiryType = "Direct";
            r.BidClosingDate = Today.AddDays(-4); r.RecDate = Today.AddDays(-10); r.SubDate = Today.AddDays(-3);
            foreach (var n in Enumerable.Range(1, 12)) r.Rfqitems.Add(new Rfqitem { LineItemNo = $"{n}", Quantity = 1, CreatedBy = "seed", CreatedDate = Today });
        });
        Add(303, 2, r => { r.Rfqno = "KPC-0107"; r.BidClosingDate = Today.AddDays(9); });
        await context.SaveChangesAsync();

        // 301: a draft being priced. 302: sent. 303: no quote yet.
        context.Quotes.Add(new Quote { Id = 31, QuoteNo = "QT-31", Rfqid = 301, BusinessUnitId = Tenant, QuoteDate = Today, CreatedBy = "seed", CreatedDate = Today });
        context.Quotes.Add(new Quote { Id = 32, QuoteNo = "QT-32", Rfqid = 302, BusinessUnitId = Tenant, QuoteDate = Today, SentOn = Today, CreatedBy = "seed", CreatedDate = Today });
        await context.SaveChangesAsync();
        return new RfqRepository(context);
    }

    private static async Task<long[]> Ids(RfqRepository repo, RfqListColumnFilters columns)
    {
        var (rows, total) = await repo.GetAllAsync(Tenant, 1, 50, columns: columns);
        var ids = rows.Select(r => r.Id).OrderBy(id => id).ToArray();
        Assert.Equal(ids.Length, total); // the header count is the rows
        return ids;
    }

    [Fact]
    public async Task Text_columns_match_contains()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Rfq = "000028" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { CustomerRef = "tender-77" }));
        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { Buyer = "SABA" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Location = "jubail" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Agreement = "4410" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Opportunity = "opp-9" }));
    }

    [Fact]
    public async Task Choice_columns_customer_and_quote_state_narrow_to_one_value()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Customer = "96381" }));
        Assert.Equal(new long[] { 302, 303 }, await Ids(repo, new() { Customer = "none" }));
        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { RfqType = "Spot" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { InquiryType = "Tender" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Bidding = "Bid" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { StatusId = 96_391 }));
        Assert.Equal(new long[] { 303 }, await Ids(repo, new() { Quote = "none" }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Quote = "draft" }));
        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { Quote = "sent" }));
    }

    [Fact]
    public async Task Dates_and_lines_take_ranges()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { Due = "overdue", Today = Today }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { Due = "7d", Today = Today }));
        // A 17:00 closing on the last day of a custom range is inside it.
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { DueFrom = Today, DueTo = Today.AddDays(2) }));
        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { ReceivedTo = Today.AddDays(-2) }));
        Assert.Equal(new long[] { 301 }, await Ids(repo, new() { RequiredFrom = Today.AddDays(29), RequiredTo = Today.AddDays(30) }));
        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { SubmittedFrom = Today.AddDays(-7) }));
        Assert.Equal(new long[] { 301, 302, 303 }, await Ids(repo, new() { CreatedFrom = Today, CreatedTo = Today }));
        Assert.Equal(new long[] { 301, 303 }, await Ids(repo, new() { LinesMax = 10 }));
        Assert.Equal(new long[] { 302 }, await Ids(repo, new() { LinesMin = 11 }));
    }

    [Fact]
    public async Task Choices_are_the_values_in_the_list_with_counts()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        var all = await repo.GetListChoicesAsync(Tenant);
        Assert.Equal(("96381", "Saudi Aramco", 1), all.Customers.Select(c => (c.Value, c.Label, c.Count)).Single());
        Assert.Equal(2, all.NoCustomer);
        Assert.Equal(new[] { ("Agreement", 1), ("Spot", 1) }, all.RfqTypes.Select(c => (c.Label, c.Count)).ToArray());
        Assert.Equal("In progress", Assert.Single(all.Statuses).Label);

        // The open queue drops the RFQ whose quote was sent, and its values with it.
        var open = await repo.GetListChoicesAsync(Tenant, "open");
        Assert.Equal(new[] { ("Agreement", 1) }, open.RfqTypes.Select(c => (c.Label, c.Count)).ToArray());
        Assert.Equal(1, open.NoCustomer);
    }
}

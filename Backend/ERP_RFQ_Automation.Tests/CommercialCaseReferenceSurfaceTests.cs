using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialCaseReferenceSurfaceTests
{
    [Fact]
    public async Task Lead_detail_exposes_the_commercial_case_reference()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var lead = Seed.Lead(context, 701, 71);
        await context.SaveChangesAsync();

        var persistedLead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        var dto = await new LeadRepository(context).GetLeadByIdAsync(lead.Id, 71);

        Assert.Equal(persistedLead.CommercialCaseId, dto.CommercialCaseId);
        Assert.Equal(persistedLead.CommercialCaseReference, dto.CommercialCaseReference);
    }

    [Fact]
    public async Task Rfq_detail_exposes_the_commercial_case_reference()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var lead = Seed.Lead(context, 702, 72);
        var rfq = new Rfq
        {
            Id = 7702,
            Rfqno = "RFQ-7702",
            BuyersName = "Workspace Buyer",
            RecDate = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc),
            BusinessUnitId = 72,
            LeadId = lead.Id,
            CreatedBy = "seed",
            CreatedDate = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc)
        };
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();

        var persistedLead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        var dto = await new RfqRepository(context).GetByIdAsync(rfq.Id, 72);

        Assert.Equal(persistedLead.CommercialCaseId, dto.CommercialCaseId);
        Assert.Equal(persistedLead.CommercialCaseReference, dto.CommercialCaseReference);
    }

    [Fact]
    public async Task Quote_detail_exposes_the_same_nexora_serial_and_customer_identity()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var lead = Seed.Lead(context, 703, 73);
        Seed.Customer(context, 973, 73, "Known Customer");
        Seed.Contact(context, 1973, 73, 973);
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(973, 1973, "CONFIRMED");
        var rfq = new Rfq
        {
            Id = 7703,
            Rfqno = "RFQ-7703",
            RecDate = DateTime.UtcNow,
            BusinessUnitId = 73,
            LeadId = lead.Id,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();
        var quote = new Quote
        {
            Id = 8703,
            QuoteNo = "QT-8703",
            Rfqid = rfq.Id,
            BusinessUnitId = 73,
            QuoteDate = DateTime.UtcNow,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        quote.InheritCommercialIdentity(rfq);
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        var dto = await new QuoteRepository(context).GetByIdAsync(quote.Id, 73);

        Assert.Equal(lead.CommercialCaseId, dto.CommercialCaseId);
        Assert.Equal(lead.CommercialCaseReference, dto.NexoraSerial);
        Assert.Equal(973, dto.CustomerId);
        Assert.Equal(1973, dto.ContactId);
        Assert.Equal(1, dto.LifecycleVersion);
    }
}

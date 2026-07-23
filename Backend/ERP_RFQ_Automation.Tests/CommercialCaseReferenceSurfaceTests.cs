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
}

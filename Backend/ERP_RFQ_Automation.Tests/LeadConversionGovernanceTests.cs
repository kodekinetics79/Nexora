using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadConversionGovernanceTests
{
    [Fact]
    public async Task IntelligenceConversion_QualifiesAndCreatesExactlyOneRfqAtomically()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(92);
        var lead = Seed.Lead(context, 9201, 92,
            items: new[] { Seed.LeadItem(9202, "10", 5, "Pump") });
        lead.LeadItems.Single().UnitOfMeasure = "EA";
        lead.LeadItems.Single().Currency = "USD";
        lead.ResolveCommercialIdentity(9210, null, LeadCustomerMatchStatuses.Confirmed);
        Seed.Customer(context, 9210, 92, "Acme Buyer");
        context.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(
            context.BusinessUnits.Local.Single(unit => unit.Id == 92), "test"));
        await context.SaveChangesAsync();
        var service = new LeadConversionIntelligence(context);
        var request = new ConvertRequest
        {
            ActingUser = "reviewer@example.com",
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Catalog choice reviewed"
        };

        var first = await service.ConvertAsync(lead.Id, 92, request, default);
        var replay = await service.ConvertAsync(lead.Id, 92, request, default);

        Assert.Equal(first, replay);
        Assert.Single(await context.Rfqs.Where(rfq => rfq.LeadId == lead.Id).ToListAsync());
        var events = await context.CommercialLifecycleEvents
            .Where(entry => entry.AggregateId == lead.Id).OrderBy(entry => entry.OccurredOn).ToListAsync();
        Assert.Collection(events,
            entry => Assert.Equal("QUALIFIED", entry.NewStatusCode),
            entry => Assert.Equal("CONVERTED_TO_RFQ", entry.NewStatusCode));
    }

    [Fact]
    public async Task IntelligenceConversion_PreservesMissingQuantityAsNeedsReview()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(93);
        var lead = Seed.Lead(context, 9301, 93,
            items: new[] { Seed.LeadItem(9302, "20", 1, "Valve") });
        lead.LeadItems.Single().Quantity = null;
        lead.LeadItems.Single().UnitOfMeasure = null;
        lead.ResolveCommercialIdentity(9310, null, LeadCustomerMatchStatuses.Confirmed);
        Seed.Customer(context, 9310, 93, "Acme Buyer");
        context.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(
            context.BusinessUnits.Local.Single(unit => unit.Id == 93), "test"));
        await context.SaveChangesAsync();

        var rfqId = await new LeadConversionIntelligence(context).ConvertAsync(lead.Id, 93,
            new ConvertRequest
            {
                ActingUser = "reviewer@example.com",
                CreateNeedsClarification = true
            }, default);

        var rfq = await context.Rfqs.Include(row => row.Rfqitems).Include(row => row.Rfqstatus)
            .SingleAsync(row => row.Id == rfqId);
        Assert.Equal("NEEDS_REVIEW", rfq.Rfqstatus?.SetupCode);
        Assert.Null(Assert.Single(rfq.Rfqitems).Quantity);
    }

    [Fact]
    public async Task IntelligenceConversion_BlocksUnverifiedAiCommercialFacts()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(91);
        var qualified = Seed.LeadStatus(context, 901, 91, "Qualified");
        qualified.SetupCode = "QUALIFIED";
        var lead = Seed.Lead(context, 9001, 91, qualified.SetupId, "NeedsReview",
            items: new[] { Seed.LeadItem(9002, "1", 4, "Pump") });
        lead.RequiresCommercialReview = true;
        lead.CommercialFactsVerified = false;
        await context.SaveChangesAsync();

        var service = new LeadConversionIntelligence(context);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertAsync(lead.Id, 91, new ConvertRequest { ActingUser = "reviewer@example.com" }, default));

        Assert.Contains("commercial facts", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Rfqs.ToListAsync());
    }
}

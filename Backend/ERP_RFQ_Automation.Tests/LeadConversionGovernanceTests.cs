using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadConversionGovernanceTests
{
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

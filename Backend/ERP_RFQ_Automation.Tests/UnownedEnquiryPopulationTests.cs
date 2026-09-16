using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The Inbox said "Unassigned leads 0 · Every enquiry has an owner" while the Leads list
/// (Owner = Unassigned) and Sales today both showed 2. Two different populations were being
/// called by one name: the Inbox reads the ROUTING queue (GET /api/UnAssignedLead — accepted
/// enquiries nobody has claimed) and the Leads list reads every open enquiry with no owner.
/// These tests pin both counts so the Inbox can state the difference honestly.
/// </summary>
public sealed class UnownedEnquiryPopulationTests
{
    private const long Bu = 7_400;

    [Fact]
    public async Task An_unowned_enquiry_nobody_has_accepted_is_on_the_leads_list_but_not_in_the_routing_queue()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        Seed.BusinessUnit(ctx, Bu);
        // Fresh from extraction: no status yet, no owner. Exactly the two the SDET saw.
        Seed.Lead(ctx, 7_401, Bu);
        Seed.Lead(ctx, 7_402, Bu);
        await ctx.SaveChangesAsync();
        var repo = new LeadRepository(ctx);

        var (_, onLeadsList) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "open,unassigned");
        var (_, inRoutingQueue) = await repo.GetAcceptedLeadsAsync(1, 10, Bu, excludeAssigned: true);

        Assert.Equal(2, onLeadsList);
        Assert.Equal(0, inRoutingQueue);
    }

    [Fact]
    public async Task Once_accepted_the_same_unowned_enquiry_is_counted_by_both()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        Seed.BusinessUnit(ctx, Bu);
        var accepted = Seed.LeadStatus(ctx, 7_410, Bu, "Accepted");
        Seed.Lead(ctx, 7_411, Bu, leadStatusId: accepted.SetupId);
        await ctx.SaveChangesAsync();
        var repo = new LeadRepository(ctx);

        var (_, onLeadsList) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "open,unassigned");
        var (_, inRoutingQueue) = await repo.GetAcceptedLeadsAsync(1, 10, Bu, excludeAssigned: true);

        Assert.Equal(1, onLeadsList);
        Assert.Equal(1, inRoutingQueue);
    }

    [Fact]
    public async Task An_owned_enquiry_is_in_neither_unowned_population()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        Seed.BusinessUnit(ctx, Bu);
        ctx.Users.Add(new User
        {
            Id = 7_499, Buid = Bu, IsActive = true, FirstName = "Sara", LastName = "Bin Ali",
            Email = "sara.7499@nexora.test", PasswordHash = "x", ImageUrl = "", CreatedBy = "test",
        });
        var lead = Seed.Lead(ctx, 7_421, Bu);
        lead.AssignTo = 7_499;
        await ctx.SaveChangesAsync();
        var repo = new LeadRepository(ctx);

        var (_, onLeadsList) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "open,unassigned");
        var (_, inRoutingQueue) = await repo.GetAcceptedLeadsAsync(1, 10, Bu, excludeAssigned: true);

        Assert.Equal(0, onLeadsList);
        Assert.Equal(0, inRoutingQueue);
    }
}

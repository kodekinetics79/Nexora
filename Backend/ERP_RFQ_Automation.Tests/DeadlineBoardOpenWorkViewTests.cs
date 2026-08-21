using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The leads list has exactly one default: the untriaged inbox, <c>LeadStatusId == null</c> —
/// what RfqRepository spells out as "new lead to review". Every lifecycle transition stamps a
/// status, so that default drops an enquiry the moment a rep starts working it.
///
/// <para>The deadline board — the post-login landing screen of a deadline-driven product, and the
/// only screen that counts down to a bid closing date — was reading that default. A tender
/// advanced on Monday was gone on Tuesday: not moved to a working column, not greyed out, gone.
/// These tests pin the <c>open</c> view that fixes it: the whole live pipeline, minus work that
/// is genuinely finished.</para>
/// </summary>
public class DeadlineBoardOpenWorkViewTests
{
    private const long Bu = 4_401;

    [Fact]
    public async Task Open_view_keeps_the_enquiries_a_rep_is_actually_working()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        // The tenant's own status rows, in the shape a real tenant carries them: SetupValue
        // labels with no SetupCode, which is how the legacy seed wrote them and why the policy
        // canonicalises the value as well as the code.
        Seed.LeadStatus(context, 3_101, Bu, "Qualified");
        Seed.LeadStatus(context, 3_102, Bu, "Converted to RFQ");
        Seed.LeadStatus(context, 3_103, Bu, "Cancelled");
        Seed.LeadStatus(context, 3_104, Bu, "Rejected"); // legacy spelling of DISQUALIFIED

        Seed.Lead(context, 4_401, Bu);                    // untriaged mail
        Seed.Lead(context, 4_402, Bu, leadStatusId: 3_101); // qualified — being worked
        Seed.Lead(context, 4_403, Bu, leadStatusId: 3_102); // converted — still live work
        Seed.Lead(context, 4_404, Bu, leadStatusId: 3_103); // cancelled — finished
        Seed.Lead(context, 4_405, Bu, leadStatusId: 3_104); // rejected — finished
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);

        // The default view is the inbox, and that is the defect the board was landing on.
        var (inbox, inboxTotal) = await repo.GetLeadListAsync(1, 50, null, null, null, null, Bu);
        Assert.Equal(new long[] { 4_401 }, inbox.Select(l => l.Id).OrderBy(id => id));
        Assert.Equal(1, inboxTotal);

        // The open view is the live pipeline: untriaged AND in progress, finished work dropped.
        var (open, openTotal) = await repo.GetLeadListAsync(
            1, 50, null, null, null, null, Bu, view: "open");
        Assert.Equal(new long[] { 4_401, 4_402, 4_403 }, open.Select(l => l.Id).OrderBy(id => id));
        Assert.Equal(3, openTotal);

        // And the state is readable, not a tenant-local integer: the governed code for logic,
        // the tenant's own label for a person. Both were absent, which is why every screen that
        // needed to know whether work had started inferred it from the id being null.
        var qualified = open.Single(l => l.Id == 4_402);
        Assert.Equal("QUALIFIED", qualified.LeadStatusCode);
        Assert.Equal("Qualified", qualified.LeadStatusLabel);
        Assert.Null(open.Single(l => l.Id == 4_401).LeadStatusCode); // never triaged is a state too
    }

    [Fact]
    public async Task A_status_the_tenant_cannot_classify_keeps_the_lead_on_the_board()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        // A status row this tenant carries but the lifecycle vocabulary has no terminal meaning
        // for. Fail OPEN: a stale row a person can dismiss costs less than a live tender nobody
        // ever sees again — which is the failure this whole view exists to end.
        Seed.LeadStatus(context, 3_201, Bu + 1, "Awaiting customer clarification");
        Seed.Lead(context, 4_501, Bu + 1, leadStatusId: 3_201);
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);
        var (open, _) = await repo.GetLeadListAsync(
            1, 50, null, null, null, null, Bu + 1, view: "open");

        Assert.Equal(4_501, Assert.Single(open).Id);
    }
}

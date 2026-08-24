using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The leads LIST — <c>GET /api/Lead</c>, the first screen a rep opens — could not say who owned
/// a row, and no screen could ask it for the unclaimed ones.
///
/// <para>Both were incompletenesses rather than faults, which is why a green suite passed over
/// them: <c>LeadResponseDTO</c> has carried <c>AssignedToId</c>, <c>AssignedToFullName</c> and
/// <c>AssignmentVersion</c> since governed assignment shipped, the DETAIL projection sets all
/// three, and the LIST projection set none of them — so every row on the leads list reported
/// itself unowned no matter whose name was on it. Without <c>AssignmentVersion</c> in particular
/// no list row can offer an assign action at all: it is the optimistic-concurrency token
/// <c>PUT /api/commercial-routing/leads/{id}/owner</c> demands.</para>
///
/// <para>Every test here asserts a DEPENDENCE. Drop the projection lines and the first test
/// fails; drop the view tokens and the rest do.</para>
/// </summary>
public sealed class LeadListOwnerSurfaceTests
{
    private const long Bu = 930;

    private static User Rep(ErpRfqAutomationContext ctx, long id, string first, string last)
    {
        var user = new User
        {
            Id = id,
            Buid = Bu,
            IsActive = true,
            FirstName = first,
            LastName = last,
            Email = $"{first.ToLowerInvariant()}@nexora.test",
            PasswordHash = "x",
            ImageUrl = "",
            CreatedBy = "test",
        };
        ctx.Users.Add(user);
        return user;
    }

    [Fact]
    public async Task Lead_list_says_who_owns_each_row()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, Bu);
        Rep(context, 9301, "Sara", "Bin Ali");
        await context.SaveChangesAsync();

        var owned = Seed.Lead(context, 9310, Bu);
        owned.AssignTo = 9301;
        owned.AssignOn = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        owned.AssignmentMethod = LeadAssignmentMethods.Manual;
        owned.AssignmentVersion = 7;
        Seed.Lead(context, 9311, Bu);
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);
        var (rows, _) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu);

        var assigned = rows.Single(r => r.Id == 9310);
        Assert.Equal(9301, assigned.AssignedToId);
        Assert.Equal("Sara Bin Ali", assigned.AssignedToFullName);
        Assert.Equal("MANUAL", assigned.AssignmentMethod);
        // The concurrency token the owner endpoint demands. Without it the list cannot assign.
        Assert.Equal(7, assigned.AssignmentVersion);

        // And an unowned row is reported as unowned, not as an empty string pretending to a name.
        var unassigned = rows.Single(r => r.Id == 9311);
        Assert.Null(unassigned.AssignedToId);
        Assert.Null(unassigned.AssignedToFullName);
    }

    [Fact]
    public async Task Unassigned_view_returns_only_the_leads_nobody_holds()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, Bu);
        Rep(context, 9302, "Tariq", "Al-Harbi");
        await context.SaveChangesAsync();

        var owned = Seed.Lead(context, 9320, Bu);
        owned.AssignTo = 9302;
        Seed.Lead(context, 9321, Bu);
        Seed.Lead(context, 9322, Bu);
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);
        var (rows, total) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "unassigned");

        Assert.Equal(2, total);
        Assert.Equal([9321L, 9322L], rows.Select(r => r.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Mine_view_returns_only_the_reader_s_own_leads()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, Bu);
        Rep(context, 9303, "Sara", "Bin Ali");
        Rep(context, 9304, "Tariq", "Al-Harbi");
        await context.SaveChangesAsync();

        var mine = Seed.Lead(context, 9330, Bu);
        mine.AssignTo = 9303;
        var theirs = Seed.Lead(context, 9331, Bu);
        theirs.AssignTo = 9304;
        Seed.Lead(context, 9332, Bu);
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);
        var (rows, total) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "mine:9303");

        Assert.Equal(1, total);
        Assert.Equal(9330, rows.Single().Id);
    }

    [Fact]
    public async Task Mine_view_without_a_readable_reader_id_matches_nothing_rather_than_everything()
    {
        // "We cannot name the reader" must never render as somebody else's leads under a
        // "Mine" label.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, Bu);
        Rep(context, 9305, "Sara", "Bin Ali");
        await context.SaveChangesAsync();

        var lead = Seed.Lead(context, 9340, Bu);
        lead.AssignTo = 9305;
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);
        var (rows, total) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "mine");

        Assert.Equal(0, total);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Owner_filter_narrows_the_queue_view_rather_than_replacing_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, Bu);
        Rep(context, 9306, "Sara", "Bin Ali");
        await context.SaveChangesAsync();

        // Two revised leads, one already owned; and an unassigned lead that is NOT a revision.
        var revisedAndOwned = Seed.Lead(context, 9350, Bu);
        revisedAndOwned.CurrentRevisionNumber = 2;
        revisedAndOwned.AssignTo = 9306;
        var revisedAndFree = Seed.Lead(context, 9351, Bu);
        revisedAndFree.CurrentRevisionNumber = 3;
        Seed.Lead(context, 9352, Bu);
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);

        // "Revisions" alone still means every revision, owned or not.
        var (revisions, revisionTotal) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "revisions");
        Assert.Equal(2, revisionTotal);

        // Composed, it means BOTH conditions — not the owner filter instead of the queue.
        var (both, bothTotal) = await repo.GetLeadListAsync(1, 10, null, null, null, null, Bu, view: "revisions,unassigned");
        Assert.Equal(1, bothTotal);
        Assert.Equal(9351, both.Single().Id);

        // And the revisions view keeps its own escape from the untriaged-inbox default.
        Assert.Contains(revisions, r => r.Id == 9350);
    }
}

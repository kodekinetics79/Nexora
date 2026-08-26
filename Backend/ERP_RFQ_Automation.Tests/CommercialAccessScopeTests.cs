using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Pins the row boundary beneath the commercial controllers. Module permissions are tested
/// elsewhere; these tests prove that list rows and counts are scoped before pagination and that
/// an unassigned record is not ordinary sales-rep data.
/// </summary>
public sealed class CommercialAccessScopeTests
{
    private const long Bu = 96_100;
    private const long RepA = 96_101;
    private const long RepB = 96_102;
    private const long Manager = 96_103;

    [Fact]
    public async Task Rep_list_contains_only_their_assigned_leads_and_scoped_count()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(Bu);
        await SeedLeadsAsync(db);

        var scope = new AccountTeamScope(
            AccountScopeTier.AssignedAccounts, RepA, [], [RepA]);
        var (rows, total) = await new LeadRepository(db).GetLeadListAsync(
            1, 50, null, null, null, null, Bu, view: "open", accessScope: scope);

        var only = Assert.Single(rows);
        Assert.Equal(96_111, only.Id);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Forged_mine_filter_cannot_widen_the_authenticated_scope()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(Bu);
        await SeedLeadsAsync(db);

        var scope = new AccountTeamScope(
            AccountScopeTier.AssignedAccounts, RepA, [], [RepA]);
        var (rows, total) = await new LeadRepository(db).GetLeadListAsync(
            1, 50, null, null, null, null, Bu,
            view: $"open,mine:{RepB}", accessScope: scope);

        Assert.Empty(rows);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Manager_sees_managed_users_but_not_unassigned_or_other_team_work()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(Bu);
        await SeedLeadsAsync(db);

        var scope = new AccountTeamScope(
            AccountScopeTier.ManagedScope, Manager, [], [Manager, RepA, RepB]);
        var (rows, total) = await new LeadRepository(db).GetLeadListAsync(
            1, 50, null, null, null, null, Bu, view: "open", accessScope: scope);

        Assert.Equal([96_111L, 96_112L], rows.Select(x => x.Id).Order().ToArray());
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Tenant_owner_scope_includes_assigned_and_unassigned_work()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(Bu);
        await SeedLeadsAsync(db);

        var (rows, total) = await new LeadRepository(db).GetLeadListAsync(
            1, 50, null, null, null, null, Bu,
            view: "open", accessScope: AccountTeamScope.TenantWide(Manager));

        Assert.Equal([96_111L, 96_112L, 96_113L, 96_114L],
            rows.Select(x => x.Id).Order().ToArray());
        Assert.Equal(4, total);
    }

    [Fact]
    public void Inherited_rfq_quote_and_order_scope_translates_to_database_queries()
    {
        using var database = new TestDb();
        using var db = database.ContextFor(Bu);
        var scope = new AccountTeamScope(
            AccountScopeTier.AssignedAccounts, RepA, [], [RepA]);
        var asOf = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

        var rfqSql = db.Rfqs
            .Where(x => x.BusinessUnitId == Bu)
            .InCommercialScope(db, Bu, scope, asOf)
            .ToQueryString();
        var quoteSql = db.Quotes
            .Where(x => x.BusinessUnitId == Bu)
            .InCommercialScope(db, Bu, scope, asOf)
            .ToQueryString();
        var orderSql = db.Orders
            .Where(x => x.BusinessUnitId == Bu)
            .InCommercialScope(db, Bu, scope, asOf)
            .ToQueryString();

        Assert.Contains("SELECT", rfqSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", quoteSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", orderSql, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SeedLeadsAsync(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        db.Users.AddRange(User(RepA), User(RepB), User(Manager), User(96_104));

        Seed.Lead(db, 96_111, Bu).AssignTo = RepA;
        Seed.Lead(db, 96_112, Bu).AssignTo = RepB;
        Seed.Lead(db, 96_113, Bu).AssignTo = 96_104; // another manager's team
        Seed.Lead(db, 96_114, Bu).AssignTo = null;   // governed routing queue only
        await db.SaveChangesAsync();
    }

    private static User User(long id) => new()
    {
        Id = id,
        FirstName = "Scope",
        LastName = $"User {id}",
        Email = $"scope-{id}@example.test",
        PasswordHash = "x",
        ImageUrl = "n/a",
        Buid = Bu,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc)
    };
}

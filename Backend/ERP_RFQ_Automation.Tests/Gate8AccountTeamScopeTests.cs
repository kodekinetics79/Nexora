using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-CST-02 — customer records restricted to the assigned account team, with supervisors reaching
/// multiple accounts inside their scope — and, because it was blocked on exactly this, the middle
/// tier of FR-DSH-05.
///
/// <para><b>What this closes.</b> No account-team predicate existed at any layer. A sales engineer
/// could read every customer in their business unit, and the dashboard's scope was a boolean:
/// tenant-wide, or "assigned to me". The middle tier was not narrow, it was ABSENT, so a supervisor
/// held the whole tenant.</para>
///
/// <para>Every test drives the real resolver against real membership rows. Delete
/// <c>Customer.AccountTeamId</c> and none of them compile; leave it in place but stop reading it in
/// <c>AccountTeamReadFilter</c> and they fail.</para>
/// </summary>
public sealed class Gate8AccountTeamScopeTests
{
    private const long Bu = 8_300;
    private const long OtherBu = 8_390;

    private const long NorthTeam = 8_310;
    private const long SouthTeam = 8_311;
    private const long NorthSubTeam = 8_312;

    private const long Rep = 8_320;          // member of North
    private const long SouthRep = 8_321;     // member of South
    private const long Supervisor = 8_322;   // manages North
    private const long SubTeamRep = 8_323;   // member of North's sub-team

    private static readonly DateTime Now = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── the tiers ────────────────────────────────────────────────────────────

    /// <summary>
    /// A member resolves to their own teams only. Note what is NOT asserted: no rank grants breadth
    /// here — the tier comes from membership rows, and a member on no team gets an EMPTY team list,
    /// which the read filter treats as "no team-owned accounts" rather than as "no filter".
    /// </summary>
    [Fact]
    public async Task A_member_is_scoped_to_the_teams_they_are_effectively_on()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);

        Assert.Equal(AccountScopeTier.AssignedAccounts, scope.Tier);
        Assert.Equal("assigned_accounts", scope.ScopeName);
        Assert.Equal([NorthTeam], scope.TeamIds);
        Assert.Equal([Rep], scope.UserIds);
    }

    /// <summary>
    /// Clearing the Team field on the Users screen revokes the scope on the next request. This is
    /// the honest replacement for an effective-dated expiry test: membership is current state in
    /// <c>Users.TeamID</c>, so "moved off the book" is a write to that column and not a window that
    /// closes on a date nobody was ever asked for.
    /// </summary>
    [Fact]
    public async Task Moving_a_rep_off_their_team_revokes_the_account_scope()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);

        var before = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        Assert.Equal([NorthTeam], before.TeamIds);

        await AssignTeamAsync(context, (Rep, null));

        var after = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        Assert.Empty(after.TeamIds);
    }

    /// <summary>
    /// P0 — THE defect this branch exists for, stated as the product owner would: a Sales Manager
    /// sees their team's work and a Sales Rep does not.
    ///
    /// <para>Before the fix both sides of this assertion were the same list. The resolver read
    /// <c>SalesTeamMembership</c>, which has no writer in the entire product and zero rows in
    /// production, while the Users screen wrote <c>Users.TeamID</c> — so a manager resolved to
    /// <c>teamIds=[], userIds=[self]</c>, byte-for-byte a rep's scope, and the middle tier granted
    /// nothing at all.</para>
    ///
    /// <para>The rep half is the control. It fails if the fix over-corrects and hands every caller
    /// their team-mates' work, which would be a worse defect than the one being repaired.</para>
    /// </summary>
    [Fact]
    public async Task A_sales_manager_sees_their_teams_work_and_a_sales_rep_does_not()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);

        // The manager runs North and is not personally on any team — the ordinary shape, and the
        // one that used to resolve to nothing.
        var manager = await Resolve(context, RoleRanks.Manager).ResolveAsync(Supervisor, 1, Bu, Now);
        var rep = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);

        // The manager reaches the people on the team they run, and down into its sub-team.
        Assert.Equal(AccountScopeTier.ManagedScope, manager.Tier);
        Assert.Contains(Rep, manager.UserIds);
        Assert.Contains(SubTeamRep, manager.UserIds);
        Assert.Equal([NorthTeam, NorthSubTeam], manager.TeamIds);

        // ...and still not the neighbouring desk, nor the tenant.
        Assert.DoesNotContain(SouthRep, manager.UserIds);
        Assert.False(manager.IsTenantWide);

        // The rep reaches nobody but themselves, including nobody on their own team.
        Assert.Equal(AccountScopeTier.AssignedAccounts, rep.Tier);
        Assert.Equal([Rep], rep.UserIds);
        Assert.DoesNotContain(SubTeamRep, rep.UserIds);

        // The two tiers are genuinely different sets. This is the single assertion that was false
        // before the fix.
        Assert.True(manager.UserIds.Count > rep.UserIds.Count);
    }

    /// <summary>
    /// The resolver reads the column the Users screen writes, and ONLY that column. A
    /// <c>SalesTeamMembership</c> row — the table the resolver used to consult, which no screen can
    /// produce — must not widen anybody's scope, or the two sources of truth are back and the
    /// quieter one wins again.
    /// </summary>
    [Fact]
    public async Task A_legacy_membership_row_grants_no_scope_on_its_own()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);

        // SouthRep is on South via Users.TeamID. Hand them a membership row for North as well —
        // the shape a hand-written INSERT or an abandoned import would leave behind.
        context.SalesTeamMemberships.Add(new SalesTeamMembership
        {
            Id = 1, BusinessUnitId = Bu, UserId = SouthRep, TeamId = NorthTeam,
            IsPrimary = true, EffectiveFromUtc = Now.AddDays(-90)
        });
        await context.SaveChangesAsync();

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(SouthRep, 1, Bu, Now);
        Assert.Equal([SouthTeam], scope.TeamIds);

        // ...and it does not smuggle them into the North manager's roll-up either.
        var manager = await Resolve(context, RoleRanks.Manager).ResolveAsync(Supervisor, 1, Bu, Now);
        Assert.DoesNotContain(SouthRep, manager.UserIds);
    }

    /// <summary>
    /// THE middle tier. A supervisor sees the teams they manage AND everything beneath them, plus
    /// the people in those teams — and is NOT resolved tenant-wide, which is the collapse this tier
    /// exists to undo.
    /// </summary>
    [Fact]
    public async Task A_supervisor_reaches_their_managed_teams_and_their_sub_teams_but_not_the_tenant()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);

        var scope = await Resolve(context, RoleRanks.Manager).ResolveAsync(Supervisor, 1, Bu, Now);

        Assert.Equal(AccountScopeTier.ManagedScope, scope.Tier);
        Assert.False(scope.IsTenantWide);
        Assert.Equal([NorthTeam, NorthSubTeam], scope.TeamIds);
        Assert.Contains(Rep, scope.UserIds);
        Assert.Contains(SubTeamRep, scope.UserIds);
        Assert.DoesNotContain(SouthRep, scope.UserIds);
        Assert.DoesNotContain(SouthTeam, scope.TeamIds);
    }

    /// <summary>An administrator holds the tenant plane, at the same rank threshold
    /// <c>PermissionHandler</c> already satisfies module permissions by. Two different answers to
    /// "what may this caller read" in one codebase is how they drift apart.</summary>
    [Fact]
    public async Task An_administrator_is_tenant_wide()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);

        var scope = await Resolve(context, RoleRanks.Admin).ResolveAsync(Supervisor, 1, Bu, Now);

        Assert.True(scope.IsTenantWide);
        Assert.Equal("tenant", scope.ScopeName);
    }

    // ── the read path depends on it ──────────────────────────────────────────

    /// <summary>
    /// THE requirement, pinned on the real repository. A rep on North reads North's accounts and
    /// not South's. This is the test that fails if the wiring is removed: delete the
    /// <c>InAccountScope</c> call from <c>CustomerRepository.GetAllAsync</c> and the rep sees the
    /// whole tenant again.
    /// </summary>
    [Fact]
    public async Task A_rep_reads_their_own_teams_accounts_and_not_another_teams()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        var (rows, total) = await new CustomerRepository(context)
            .GetAllAsync(1, 50, null, null, null, null, null, Bu, scope);

        var names = rows.Select(r => r.Name).ToArray();
        Assert.Contains("North Account", names);
        Assert.DoesNotContain("South Account", names);
        // The COUNT is scoped too. Counting the tenant and paging a subset would still disclose
        // how many records exist outside the scope.
        Assert.Equal(names.Length, total);
    }

    /// <summary>
    /// A customer nobody has put in a book is not "restricted to nobody" — there is no team to
    /// restrict it to. It stays readable, exactly as it was before the column existed, and the row
    /// says so out loud rather than rendering a blank that reads like a loading state.
    /// </summary>
    [Fact]
    public async Task A_customer_with_no_account_team_stays_readable_and_says_so()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        var (rows, _) = await new CustomerRepository(context)
            .GetAllAsync(1, 50, null, null, null, null, null, Bu, scope);

        var unassigned = Assert.Single(rows, r => r.Name == "Unassigned Account");
        Assert.Null(unassigned.AccountTeamId);
        Assert.Null(unassigned.AccountTeamName);

        var owned = Assert.Single(rows, r => r.Name == "North Account");
        Assert.Equal(NorthTeam, owned.AccountTeamId);
        Assert.Equal("North", owned.AccountTeamName);
    }

    /// <summary>
    /// Reading a customer outside the scope by its id raises the SAME exception as reading one that
    /// does not exist. A distinct "forbidden" would confirm the record to somebody who may not open
    /// it, which is an enumeration oracle wearing a 403.
    /// </summary>
    [Fact]
    public async Task Reading_an_out_of_scope_customer_by_id_is_indistinguishable_from_not_found()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        var repository = new CustomerRepository(context);

        var outOfScope = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => repository.GetByIdAsync(8_332, Bu, scope));
        var absent = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => repository.GetByIdAsync(999_999, Bu, scope));

        Assert.Equal(
            absent.Message.Replace("999999", "8332"),
            outOfScope.Message);
    }

    /// <summary>
    /// A named owner keeps their account even when it sits in another team's book. That assignment
    /// is a deliberate act by a manager, and revoking read access to an account somebody has been
    /// made responsible for would break the routing they were assigned by.
    /// </summary>
    [Fact]
    public async Task A_named_owner_keeps_an_account_that_belongs_to_another_team()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);
        context.Set<CustomerOwnership>().Add(new CustomerOwnership
        {
            Id = 1, BusinessUnitId = Bu, CustomerId = 8_332, PrimaryUserId = Rep,
            Scope = OwnershipScope.CustomerException, IsActive = true,
            EffectiveFrom = Now.AddDays(-30), Source = "test"
        });
        await context.SaveChangesAsync();

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        var (rows, _) = await new CustomerRepository(context)
            .GetAllAsync(1, 50, null, null, null, null, null, Bu, scope);

        Assert.Contains(rows, r => r.Name == "South Account");
    }

    /// <summary>An ownership row whose window has closed grants nothing.</summary>
    [Fact]
    public async Task An_expired_ownership_row_does_not_keep_the_account_readable()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);
        context.Set<CustomerOwnership>().Add(new CustomerOwnership
        {
            Id = 1, BusinessUnitId = Bu, CustomerId = 8_332, PrimaryUserId = Rep,
            Scope = OwnershipScope.CustomerException, IsActive = true,
            EffectiveFrom = Now.AddDays(-30), EffectiveTo = Now.AddDays(-1), Source = "test"
        });
        await context.SaveChangesAsync();

        var scope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        var (rows, _) = await new CustomerRepository(context)
            .GetAllAsync(1, 50, null, null, null, null, null, Bu, scope);

        Assert.DoesNotContain(rows, r => r.Name == "South Account");
    }

    /// <summary>
    /// The supervisor tier is genuinely wider than the rep's and genuinely narrower than the
    /// tenant's. Both halves matter: without the first it is not a supervisor, without the second
    /// it is not a tier.
    /// </summary>
    [Fact]
    public async Task A_supervisor_reads_more_than_a_rep_and_less_than_the_tenant()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);
        var repository = new CustomerRepository(context);

        var repScope = await Resolve(context, RoleRanks.Member).ResolveAsync(Rep, 1, Bu, Now);
        var supervisorScope = await Resolve(context, RoleRanks.Manager).ResolveAsync(Supervisor, 1, Bu, Now);

        var (repRows, _) = await repository.GetAllAsync(1, 50, null, null, null, null, null, Bu, repScope);
        var (supervisorRows, _) = await repository.GetAllAsync(1, 50, null, null, null, null, null, Bu, supervisorScope);

        Assert.DoesNotContain(repRows, r => r.Name == "Sub-team Account");
        Assert.Contains(supervisorRows, r => r.Name == "Sub-team Account");
        Assert.DoesNotContain(supervisorRows, r => r.Name == "South Account");
    }

    /// <summary>
    /// The account filter is an INTRA-tenant control and never a substitute for the tenant one.
    /// Another tenant's customer sitting in a team with the same id must not become readable
    /// because the ids happen to collide.
    /// </summary>
    [Fact]
    public async Task The_account_filter_never_reaches_across_a_tenant_boundary()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            await SeedOrganisationAsync(seed);
            await SeedCustomersAsync(seed);
            Seed.EnsureBusinessUnit(seed, OtherBu);
            seed.Teams.Add(new Team
            {
                Id = 8_399, TeamName = "North", BusinessUnitId = OtherBu,
                CreatedBy = "seed", CreatedOn = Now
            });
            var foreign = Seed.Customer(seed, 8_398, OtherBu, "Other Tenant Account");
            await seed.SaveChangesAsync();
            foreign.AccountTeamId = 8_399;
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var scope = await Resolve(context, RoleRanks.Admin).ResolveAsync(Supervisor, 1, Bu, Now);
        var (rows, _) = await new CustomerRepository(context)
            .GetAllAsync(1, 50, null, null, null, null, null, Bu, scope);

        Assert.DoesNotContain(rows, r => r.Name == "Other Tenant Account");
    }

    // ── FR-DSH-05: the dashboard reads the same scope ────────────────────────

    /// <summary>
    /// The dashboard's account tier. A supervisor's release-01 figures include the work of the
    /// people they manage AND the accounts their teams hold — the third clause is what makes it a
    /// real middle tier, because a lead on a team account that nobody has been assigned yet is
    /// precisely the work a supervisor is there to see.
    /// </summary>
    [Fact]
    public async Task The_dashboard_counts_a_team_account_lead_that_is_assigned_to_nobody()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);

        // One lead on North's account, assigned to nobody; one on South's, likewise.
        var northLead = Seed.Lead(context, 8_340, Bu, buyersName: "North Buyer");
        var southLead = Seed.Lead(context, 8_341, Bu, buyersName: "South Buyer");
        await context.SaveChangesAsync();
        northLead.ResolveCommercialIdentity(8_331, null, "CONFIRMED");
        southLead.ResolveCommercialIdentity(8_332, null, "CONFIRMED");
        await context.SaveChangesAsync();

        var supervisorScope = await Resolve(context, RoleRanks.Manager).ResolveAsync(Supervisor, 1, Bu, Now);
        var result = await new DashboardRepository(context).GetRelease01Async(
            Bu, supervisorScope, Now.AddDays(-30), Now, Now.AddMinutes(1));

        Assert.Equal("managed_scope", result.RoleScope.Scope);
        Assert.Equal([NorthTeam, NorthSubTeam], result.RoleScope.AccountTeamIds);

        var received = result.Kpis.Single(k => k.Key == "leads_received");
        // Both leads exist in the tenant; only the one on a team account in scope is in the
        // denominator. A tenant-wide read would report two.
        Assert.Equal(1, received.Denominator);
    }

    /// <summary>
    /// The tenant tier is not scoped by team at all, and says so — an empty team list on the
    /// tenant tier means "not applicable", which is a different fact from a caller who is on no
    /// team, and the two are distinguished by the tier name rather than by the list's length.
    /// </summary>
    [Fact]
    public async Task The_tenant_tier_states_that_it_is_not_scoped_by_team()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedOrganisationAsync(context);
        await SeedCustomersAsync(context);
        var lead = Seed.Lead(context, 8_342, Bu, buyersName: "North Buyer");
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(8_331, null, "CONFIRMED");
        await context.SaveChangesAsync();

        var result = await new DashboardRepository(context).GetRelease01Async(
            Bu, AccountTeamScope.TenantWide(Supervisor), Now.AddDays(-30), Now, Now.AddMinutes(1));

        Assert.Equal("tenant", result.RoleScope.Scope);
        Assert.Null(result.RoleScope.OwnerUserId);
        Assert.Empty(result.RoleScope.AccountTeamIds);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AccountTeamScopeResolver Resolve(ErpRfqAutomationContext context, short rank)
        => new(context, new FixedRankRoleGate(rank));

    private static async Task SeedOrganisationAsync(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Bu);

        // Real Users rows: Team.ManagerId carries a foreign key onto Users, so a supervisor who
        // exists only as an integer would make the whole managed tier untestable — and would hide
        // that the resolver reads a genuine manager relationship rather than a loose number.
        context.Users.AddRange(
            User(Rep), User(SouthRep), User(Supervisor), User(SubTeamRep));
        await context.SaveChangesAsync();

        context.Teams.AddRange(
            new Team { Id = NorthTeam, TeamName = "North", BusinessUnitId = Bu, ManagerId = Supervisor, CreatedBy = "seed", CreatedOn = Now },
            new Team { Id = SouthTeam, TeamName = "South", BusinessUnitId = Bu, CreatedBy = "seed", CreatedOn = Now });
        await context.SaveChangesAsync();

        // North's sub-team. Team.SubTeamId points at the PARENT — TeamRepository reads a team's
        // children as Teams.Any(t => t.SubTeamId == id) — so this row hangs beneath North.
        context.Teams.Add(new Team
        {
            Id = NorthSubTeam, TeamName = "North Field", BusinessUnitId = Bu,
            SubTeamId = NorthTeam, CreatedBy = "seed", CreatedOn = Now
        });
        await context.SaveChangesAsync();

        // Membership is Users.TeamID — the column the Users screen writes and the ONLY one the
        // resolver reads. It is set after the teams exist because it carries a foreign key onto
        // them, which is the same two-step the customers below use for Customer.AccountTeamId.
        //
        // This seed deliberately writes NO SalesTeamMembership rows. Production has none and has
        // no writer that could produce one, so a fixture that seeded them would be green on a
        // shape the product never emits — which is exactly how the dead middle tier survived.
        await AssignTeamAsync(context, (Rep, NorthTeam), (SouthRep, SouthTeam), (SubTeamRep, NorthSubTeam));
    }

    /// <summary>Puts users on teams the way the Users screen does, and nothing else.</summary>
    private static async Task AssignTeamAsync(
        ErpRfqAutomationContext context, params (long UserId, long? TeamId)[] assignments)
    {
        foreach (var (id, team) in assignments)
        {
            var user = await context.Users.SingleAsync(u => u.Id == id);
            user.TeamId = team;
        }
        await context.SaveChangesAsync();
    }

    private static User User(long id) => new()
    {
        Id = id, FirstName = "Synthetic", LastName = $"User {id}",
        Email = $"user{id}@example.test", PasswordHash = "x", ImageUrl = "n/a",
        Buid = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = Now
    };

    private static async Task SeedCustomersAsync(ErpRfqAutomationContext context)
    {
        Seed.Customer(context, 8_331, Bu, "North Account");
        Seed.Customer(context, 8_332, Bu, "South Account");
        Seed.Customer(context, 8_333, Bu, "Sub-team Account");
        Seed.Customer(context, 8_334, Bu, "Unassigned Account");
        await context.SaveChangesAsync();

        foreach (var (id, team) in new[]
                 {
                     (8_331L, NorthTeam), (8_332L, SouthTeam), (8_333L, NorthSubTeam)
                 })
        {
            var customer = await context.Customers.SingleAsync(c => c.Id == id);
            customer.AccountTeamId = team;
        }
        await context.SaveChangesAsync();
    }

    /// <summary>A role gate whose only job is to state a rank. The rank rules themselves belong to
    /// RoleGate and are certified where they live; stubbing it here keeps these tests about the
    /// tiers rather than about Setup_Master rows.</summary>
    private sealed class FixedRankRoleGate(short rank) : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId)
            => Task.FromResult(rank >= RoleRanks.Owner);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId)
            => Task.FromResult(rank >= RoleRanks.Manager);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) => Task.FromResult(rank);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId)
            => Task.FromResult(false);
    }
}

using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Search;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-DSH-04 — top-bar quick search across customer, supplier, product, date range and status.
///
/// <para><b>What this replaces.</b> A ten-entry keyword→route table in <c>Navbar.tsx</c> that made
/// no network request and, on no match, navigated silently to <c>/dashboard</c>. A search for a
/// customer that does not exist was indistinguishable from a search for one that does — wiring
/// contract failure #7, a control that reports success while doing nothing.</para>
///
/// <para>Every value in this file is synthetic. No real company name, CR number or VAT registration
/// number appears.</para>
/// </summary>
public sealed class Gate8GlobalSearchTests
{
    private const long Bu = 8_400;
    private const long OtherBu = 8_490;
    private const long RoleId = 5;

    private static readonly DateTime Now = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The point of the whole endpoint: a term finds RECORDS, across families, in one call. The old
    /// control could not have passed this at all — it never issued a request.
    /// </summary>
    [Fact]
    public async Task One_term_finds_records_across_several_families()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var response = await Search(context, "meridian");

        Assert.Contains(response.Hits, h => h.Entity == SearchEntities.Customer && h.Title == "Meridian Trading");
        Assert.Contains(response.Hits, h => h.Entity == SearchEntities.Supplier && h.Title == "Meridian Supply");
        Assert.Contains(response.Hits, h => h.Entity == SearchEntities.Product);
    }

    /// <summary>
    /// A term nobody matches returns NOTHING and says so. It does not navigate anywhere, and it
    /// does not fall back to a route. This is the assertion the old keyword router could never
    /// satisfy: it always "succeeded".
    /// </summary>
    [Fact]
    public async Task A_term_that_matches_nothing_returns_no_hits_rather_than_a_destination()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var response = await Search(context, "zzzznotpresent");

        Assert.Empty(response.Hits);
        Assert.Equal(SearchEntities.All.Length, response.SearchedEntities.Count);
        Assert.Empty(response.DeniedEntities);
    }

    /// <summary>
    /// Every hit says which field matched. Without it a hit on a VAT number next to a name search
    /// looks like a bug, and a user who cannot explain a result learns to distrust the whole box.
    /// </summary>
    [Fact]
    public async Task A_hit_states_which_field_the_term_matched()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        // Synthetic KSA-shaped VAT number: 15 digits, leading and trailing 3.
        var response = await Search(context, "300000000000003");

        var hit = Assert.Single(response.Hits, h => h.Entity == SearchEntities.Customer);
        Assert.Equal("VAT number", hit.MatchedOn);
    }

    /// <summary>The customer hit names the account team, so an unassigned account is visibly
    /// unassigned rather than looking like every other row.</summary>
    [Fact]
    public async Task A_customer_hit_names_its_account_team_or_states_that_it_has_none()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var response = await Search(context, "meridian");

        var hit = Assert.Single(response.Hits, h => h.Entity == SearchEntities.Customer);
        Assert.Contains("No account team", hit.Subtitle);
    }

    // ── the filters FR-DSH-04 names ──────────────────────────────────────────

    /// <summary>
    /// The date filter is real and half-open, matching every other window in the codebase. An
    /// inclusive upper bound is how a "to 30 June" filter silently drops everything recorded during
    /// 30 June.
    /// </summary>
    [Fact]
    public async Task The_date_range_filter_is_half_open_and_actually_excludes()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var boundary = Now.AddDays(-10);
        var recent = await Search(context, "meridian", from: boundary, to: Now.AddDays(1));
        var older = await Search(context, "meridian", from: Now.AddDays(-90), to: boundary);

        // The customer was created 30 days ago; the supplier 5 days ago.
        Assert.Contains(recent.Hits, h => h.Entity == SearchEntities.Supplier);
        Assert.DoesNotContain(recent.Hits, h => h.Entity == SearchEntities.Customer);
        Assert.Contains(older.Hits, h => h.Entity == SearchEntities.Customer);
        Assert.DoesNotContain(older.Hits, h => h.Entity == SearchEntities.Supplier);
    }

    /// <summary>
    /// A status nobody has configured matches NOTHING. The tempting alternative — treat an
    /// unresolvable status as "no filter" — turns a typo into a search that quietly returns every
    /// record, and the note says out loud that the filter was applied rather than ignored.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_status_matches_nothing_and_says_so()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var response = await Search(context, "meridian", status: "NOT_A_STATUS");

        Assert.DoesNotContain(response.Hits, h => h.Entity == SearchEntities.Lead);
        Assert.Contains(response.Notes, n => n.Contains("No status called"));
    }

    /// <summary>
    /// The status filter selects. Filtering to the seeded lead status finds the lead; filtering to
    /// a different configured status does not.
    /// </summary>
    [Fact]
    public async Task The_status_filter_selects_rather_than_decorating()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var matching = await Search(context, "meridian", status: "QUALIFIED");
        var other = await Search(context, "meridian", status: "REJECTED");

        Assert.Contains(matching.Hits, h => h.Entity == SearchEntities.Lead);
        Assert.DoesNotContain(other.Hits, h => h.Entity == SearchEntities.Lead);
    }

    /// <summary>
    /// A family with no status concept is EXCLUDED and named, rather than being returned as though
    /// the filter did not apply to it. Silently ignoring a filter for some families is how a user
    /// concludes the filter does not work.
    /// </summary>
    [Fact]
    public async Task A_family_with_no_status_is_excluded_and_the_gap_is_stated()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var response = await Search(context, "meridian", status: "QUALIFIED");

        Assert.DoesNotContain(response.Hits, h => h.Entity == SearchEntities.Customer);
        Assert.Contains(response.Notes, n => n.Contains("Customers carry no status column"));
    }

    // ── boundaries ───────────────────────────────────────────────────────────

    /// <summary>Tenant isolation. A term that matches another tenant's records finds none of them.</summary>
    [Fact]
    public async Task Search_never_crosses_a_tenant_boundary()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            await SeedAsync(seed);
            Seed.Customer(seed, 8_492, OtherBu, "Meridian Trading Other");
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var response = await Search(context, "meridian");

        Assert.DoesNotContain(response.Hits, h => h.Title.Contains("Other"));
    }

    /// <summary>
    /// FR-CST-02 is enforced HERE too. Quick search is not a side door around the account-team
    /// filter, and this is the test that fails if somebody drops the <c>InAccountScope</c> call
    /// from the customer family.
    /// </summary>
    [Fact]
    public async Task Quick_search_applies_the_same_account_scope_as_the_customer_list()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);
        context.Teams.Add(new Team
        {
            Id = 8_450, TeamName = "Strategic", BusinessUnitId = Bu, CreatedBy = "seed", CreatedOn = Now
        });
        await context.SaveChangesAsync();
        var customer = await context.Customers.SingleAsync(c => c.Id == 8_401);
        customer.AccountTeamId = 8_450;
        await context.SaveChangesAsync();

        // A caller on no team: the account-owned customer is out of scope.
        var scoped = new AccountTeamScope(AccountScopeTier.AssignedAccounts, 99, [], [99]);
        var response = await Search(context, "meridian", scope: scoped);

        Assert.DoesNotContain(response.Hits, h => h.Entity == SearchEntities.Customer);
        // The families the caller CAN read are unaffected — the scope narrows customers, not
        // everything.
        Assert.Contains(response.Hits, h => h.Entity == SearchEntities.Supplier);
    }

    /// <summary>
    /// A family the caller may not read is NAMED, not silently dropped. A shorter answer with no
    /// explanation is indistinguishable from "nothing matched", which is the failure mode this
    /// whole endpoint exists to end.
    /// </summary>
    [Fact]
    public async Task A_family_the_caller_may_not_read_is_reported_rather_than_dropped()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        var response = await Search(context, "meridian",
            permissions: new StubPermissions(denied: "Suppliers"));

        Assert.Contains(SearchEntities.Supplier, response.DeniedEntities);
        Assert.DoesNotContain(SearchEntities.Supplier, response.SearchedEntities);
        Assert.DoesNotContain(response.Hits, h => h.Entity == SearchEntities.Supplier);
        Assert.Contains(response.Hits, h => h.Entity == SearchEntities.Customer);
    }

    /// <summary>A one-character term is refused rather than served slowly and uselessly.</summary>
    [Fact]
    public async Task A_term_shorter_than_the_minimum_is_refused()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);

        await Assert.ThrowsAsync<ArgumentException>(() => Search(context, "m"));
    }

    /// <summary>
    /// A truncated family says so, so "5 results" is never read as "5 matching records exist".
    /// </summary>
    [Fact]
    public async Task A_truncated_family_is_named_so_a_count_is_not_mistaken_for_a_total()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);
        for (var i = 0; i < 4; i++)
            Seed.Customer(context, 8_460 + i, Bu, $"Meridian Branch {i}");
        await context.SaveChangesAsync();

        var response = await Search(context, "meridian", limit: 2);

        Assert.Contains(SearchEntities.Customer, response.Truncated);
        Assert.Equal(2, response.Hits.Count(h => h.Entity == SearchEntities.Customer));
    }

    /// <summary>
    /// The other half, and the reason the service probes for one extra row: a family whose results
    /// land EXACTLY on the limit is not truncated, and must not claim to be. "More exist" is a
    /// statement of fact, not an inference from a full page.
    /// </summary>
    [Fact]
    public async Task A_family_whose_results_land_exactly_on_the_limit_does_not_claim_more_exist()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);
        Seed.Customer(context, 8_470, Bu, "Meridian Branch A");
        await context.SaveChangesAsync();

        // Two matching customers, limit two.
        var response = await Search(context, "meridian", limit: 2);

        Assert.Equal(2, response.Hits.Count(h => h.Entity == SearchEntities.Customer));
        Assert.DoesNotContain(SearchEntities.Customer, response.Truncated);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Task<GlobalSearchResponse> Search(
        ErpRfqAutomationContext context,
        string query,
        DateTime? from = null,
        DateTime? to = null,
        string? status = null,
        int limit = 5,
        AccountTeamScope? scope = null,
        IRolePermissionRepository? permissions = null)
    {
        var service = new GlobalSearchService(
            context, permissions ?? new StubPermissions(), new FixedRankRoleGate(RoleRanks.Member));
        return service.SearchAsync(new GlobalSearchRequest(
            query, null, from, to, status, limit, Bu, 77, RoleId,
            scope ?? AccountTeamScope.TenantWide(77)));
    }

    private static async Task SeedAsync(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Bu);
        Seed.EnsureBusinessUnit(context, OtherBu);

        var customer = Seed.Customer(context, 8_401, Bu, "Meridian Trading");
        customer.CreatedOn = Now.AddDays(-30);
        // Synthetic KSA-shaped VAT number. It identifies no company.
        customer.TaxRegistrationNumber = "300000000000003";

        context.Suppliers.Add(new Supplier
        {
            Id = 8_402, Buid = Bu, Name = "Meridian Supply", ImageUrl = "n/a",
            IsActive = true, CreatedBy = "seed", CreatedOn = Now.AddDays(-5)
        });
        context.Products.Add(new Product
        {
            Id = 8_403, Buid = Bu, PartNo = "MERIDIAN-100", ProductName = "Meridian Valve",
            IsActive = true, CreatedBy = "seed", CreatedOn = Now.AddDays(-5)
        });

        Seed.LeadStatus(context, 8_404, Bu, "QUALIFIED");
        Seed.LeadStatus(context, 8_405, Bu, "REJECTED");
        await context.SaveChangesAsync();

        var lead = Seed.Lead(context, 8_406, Bu, leadStatusId: 8_404, buyersName: "Meridian Trading");
        lead.CreatedDate = Now.AddDays(-3);
        await context.SaveChangesAsync();
    }

    private sealed class StubPermissions(string? denied = null) : IRolePermissionRepository
    {
        public Task<bool> CheckPermissionAsync(long roleId, string moduleName, string action, long businessUnitId)
            => Task.FromResult(!string.Equals(moduleName, denied, StringComparison.Ordinal));

        public Task<(IEnumerable<RolePermission>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, long? roleId, long? moduleId, long businessUnitId)
            => throw new NotSupportedException();
        public Task<RolePermission> GetByIdAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task AddAsync(RolePermission rolePermission) => throw new NotSupportedException();
        public Task UpdateAsync(RolePermission rolePermission) => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new NotSupportedException();
    }

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

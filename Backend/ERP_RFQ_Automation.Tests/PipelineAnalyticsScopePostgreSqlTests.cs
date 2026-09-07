using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Migrations;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// GET /api/dashboard/pipeline-analytics is the sole source of the funnel, Won, the weighted
/// pipeline and waiting-on-customer. It used to take a business unit and nothing else, so every
/// predicate in it was tenant-wide and the only thing between a sales representative and the whole
/// company's money was <c>[RequireManagerRole]</c> on the action — an attribute that either
/// refused the rep their own funnel or, once removed, served them everybody's revenue under a
/// heading that says "your accounts".
///
/// <para>These tests seed TWO reps in ONE tenant with quotes owned by each and assert that rep A's
/// counts and money exclude rep B's. Against the unscoped repository they fail: the quoted stage
/// reads 6 instead of 2 and Won reads 10,000 instead of 1,000, which is exactly the leak.</para>
///
/// <para>PostgreSQL rather than SQLite because the scope is decided inside the query — the account
/// predicate is a correlated subquery over Customers and CustomerOwnership — and a filter that
/// silently falls back to client evaluation is the way this class of defect survives a green
/// suite. The production dialect is the one that has to translate it.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PipelineAnalyticsScopePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 97_100;
    private const long OtherTenant = 97_101;

    private const long BaseCurrencyId = 97_110;

    private const long NorthTeam = 97_120;
    private const long SouthTeam = 97_121;

    private const long RepA = 97_130;
    private const long RepB = 97_131;

    private const long NorthCustomer = 97_140;
    private const long SouthCustomer = 97_141;

    private const long SentStatusId = 97_150;
    private const long AcceptedStatusId = 97_151;
    private const long RejectedStatusId = 97_152;
    private const long ExpiredStatusId = 97_153;

    private const long PriceReasonId = 97_160;
    private const long AutoExpiredReasonId = 97_161;

    private const long RepALead = 97_170;
    private const long RepBLead = 97_171;

    // Rep A's book.
    private const long WonByA = 97_180;      // ACCEPTED, 1,000
    private const long SentByA = 97_181;     // SENT, awaiting a response, 500
    // Rep B's book — none of it may reach rep A.
    private const long WonByB = 97_182;      // ACCEPTED, 9,000
    private const long LostByB = 97_183;     // REJECTED, "price too high", 7,000
    private const long ExpiredByB = 97_184;  // EXPIRED by the sweep, 400
    // On rep A's account, owned by nobody.
    private const long UnownedOnAAccount = 97_185; // REJECTED, no reason, 300

    private static readonly DateTime Early = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Recent = new(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowFrom = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The important one. A rep sees their own funnel and their own money, and rep B's larger
    /// numbers are absent from every figure — the counts, the stage values, the open pipeline and
    /// the loss list alike.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_reps_funnel_counts_and_money_exclude_the_other_reps_quotes()
    {
        await SeedAsync();
        {
            await using var context = database.ContextFor(Tenant);
            var scope = await ScopeFor(context, RepA, RoleRanks.Member);
            Assert.Equal(AccountScopeTier.AssignedAccounts, scope.Tier);

            var result = await new DashboardRepository(context).GetPipelineAnalyticsAsync(Tenant, scope);

            var quoted = Stage(result, "quoted");
            Assert.Equal(2, quoted.Count);
            Assert.Equal(1_500m, quoted.Value);

            var won = Stage(result, "won");
            Assert.Equal(1, won.Count);
            Assert.Equal(1_000m, won.Value);

            // Leads carry their own scope (assignee or account), so the top of the funnel has to
            // narrow with the bottom or the conversion rates the screen draws are nonsense.
            Assert.Equal(1, Stage(result, "leads").Count);

            Assert.Equal(1, result.AwaitingResponseQuotes);
            Assert.Equal(500m, result.AwaitingResponseValue);

            // Rep B's loss is rep B's business.
            Assert.DoesNotContain(result.LossReasons, reason => reason.Code == "PRICE");
            Assert.DoesNotContain(result.LossReasons, reason => reason.Code == "AUTO_EXPIRED");

            Assert.Equal("assigned_accounts", result.RoleScope.Scope);
            Assert.Equal(RepA, result.RoleScope.OwnerUserId);
            Assert.Equal([NorthTeam], result.RoleScope.AccountTeamIds);
            Assert.Equal([RepA], result.RoleScope.ScopedUserIds);
        }
    }

    /// <summary>
    /// An unowned quote on the reader's own account is excluded and SAID to be excluded. Folding
    /// it in would attribute money to a person the record does not name; dropping it silently
    /// would hide a gap the rep is the right person to close.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_unowned_quote_on_the_readers_account_is_excluded_and_disclosed()
    {
        await SeedAsync();
        {
            await using var context = database.ContextFor(Tenant);
            var repository = new DashboardRepository(context);

            var repView = await repository.GetPipelineAnalyticsAsync(
                Tenant, await ScopeFor(context, RepA, RoleRanks.Member));

            Assert.Equal(1, repView.UnownedQuotesExcluded);
            Assert.NotNull(repView.UnownedQuotesExcludedReason);
            Assert.DoesNotContain(repView.LossReasons,
                reason => reason.Code == PipelineLossReasonDTO.UnrecordedCode);

            // Tenant scope excludes nothing, so it has nothing to disclose — and the unowned
            // quote is one of the six it counts.
            var tenantView = await repository.GetPipelineAnalyticsAsync(
                Tenant, AccountTeamScope.TenantWide(RepA));

            Assert.Equal(0, tenantView.UnownedQuotesExcluded);
            Assert.Null(tenantView.UnownedQuotesExcludedReason);
            Assert.Equal(6, Stage(tenantView, "quoted").Count);
            Assert.Equal(2, Stage(tenantView, "won").Count);
            Assert.Equal(10_000m, Stage(tenantView, "won").Value);
        }
    }

    /// <summary>
    /// Loss reasons carry a stable code and a server-set group. The grouping is the point: an
    /// automatic expiry ranked beside "price too high" reads as the market rejecting our prices
    /// when it means nobody followed the quote up.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Loss_reasons_separate_what_the_customer_told_us_from_what_we_never_found_out()
    {
        await SeedAsync();
        {
            await using var context = database.ContextFor(Tenant);

            var result = await new DashboardRepository(context)
                .GetPipelineAnalyticsAsync(Tenant, AccountTeamScope.TenantWide(RepA));

            var stated = Assert.Single(result.LossReasons, reason => reason.Code == "PRICE");
            Assert.Equal(PipelineLossReasonDTO.CustomerStatedGroup, stated.Group);
            Assert.Equal("Price too high", stated.Reason);
            Assert.Equal(7_000m, stated.Value);

            var expired = Assert.Single(result.LossReasons, reason => reason.Code == "AUTO_EXPIRED");
            Assert.Equal(PipelineLossReasonDTO.NeverEstablishedGroup, expired.Group);

            var unrecorded = Assert.Single(result.LossReasons,
                reason => reason.Code == PipelineLossReasonDTO.UnrecordedCode);
            Assert.Equal(PipelineLossReasonDTO.NeverEstablishedGroup, unrecorded.Group);

            Assert.All(result.LossReasons, reason => Assert.Contains(reason.Group,
                new[] { PipelineLossReasonDTO.CustomerStatedGroup, PipelineLossReasonDTO.NeverEstablishedGroup }));
        }
    }

    /// <summary>
    /// A window is applied only when both ends are given, and the payload says which of the two
    /// answers it produced — the screen draws its "period applied" seal from that, so a window the
    /// server ignored must not be presentable as one it honoured.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_funnel_states_whether_it_applied_a_window()
    {
        await SeedAsync();
        {
            await using var context = database.ContextFor(Tenant);
            var repository = new DashboardRepository(context);
            var scope = AccountTeamScope.TenantWide(RepA);

            var allTime = await repository.GetPipelineAnalyticsAsync(Tenant, scope);
            Assert.Equal(PipelineAnalyticsDTO.AllTimeScope, allTime.FunnelScope);
            Assert.Null(allTime.WindowFrom);
            Assert.Null(allTime.WindowTo);
            Assert.Equal(6, Stage(allTime, "quoted").Count);

            var windowed = await repository.GetPipelineAnalyticsAsync(Tenant, scope, WindowFrom, WindowTo);
            Assert.Equal(PipelineAnalyticsDTO.WindowScope, windowed.FunnelScope);
            Assert.Equal(WindowFrom, windowed.WindowFrom);
            Assert.Equal(WindowTo, windowed.WindowTo);

            // WonByA was created in March; everything else in June.
            Assert.Equal(5, Stage(windowed, "quoted").Count);
            Assert.Equal(1, Stage(windowed, "won").Count);
            Assert.Equal(9_000m, Stage(windowed, "won").Value);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.GetPipelineAnalyticsAsync(Tenant, scope, WindowFrom, null));
        }
    }

    /// <summary>
    /// The tenant predicate still comes first: another tenant's quote is not rep A's, and is not
    /// this tenant's either.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Another_tenants_quotes_never_reach_this_tenants_funnel()
    {
        await SeedAsync();
        {
            await using var context = database.ContextFor(Tenant);

            var result = await new DashboardRepository(context)
                .GetPipelineAnalyticsAsync(Tenant, AccountTeamScope.TenantWide(RepA));

            Assert.Equal(6, Stage(result, "quoted").Count);
            Assert.Equal(10_000m, Stage(result, "won").Value);
        }
    }

    /// <summary>
    /// The migration's own backfill statement, executed verbatim. It claims a quote for a user
    /// only when the free text names exactly one of them, and leaves everything else NULL — a
    /// service actor, a stranger's address, and a string two people answer to.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_backfill_claims_only_the_quotes_whose_actor_names_exactly_one_user()
    {
        // Its own tenant, because this test writes quotes that would otherwise land in the
        // funnel fixture's counts.
        const long tenant = 97_102;
        const long onlySam = 97_133;
        const long twinOne = 97_134;
        const long twinTwo = 97_135;

        await using var context = database.ContextFor(null);
        try
        {
            Seed.EnsureBusinessUnit(context, tenant);
            await context.SaveChangesAsync();

            context.Users.AddRange(
                BackfillUser(onlySam, tenant, "Sam", "Solo", "sam.solo@nexora.invalid"),
                // Two people who answer to the same display name. Nothing stops a tenant from
                // having them, and the free-text rule attributed the quote to whichever the
                // enumeration reached first.
                BackfillUser(twinOne, tenant, "Twin", "Person", "twin.one@nexora.invalid"),
                BackfillUser(twinTwo, tenant, "Twin", "Person", "twin.two@nexora.invalid"));
            await context.SaveChangesAsync();

            foreach (var (id, createdBy) in new[]
                     {
                         (97_190L, "SAM.SOLO@nexora.invalid"), // the email, in the wrong case
                         (97_191L, "  Sam Solo  "),            // the display name, padded
                         (97_192L, "Twin Person"),             // two users answer to it
                         (97_193L, "System"),                  // names nobody
                         (97_194L, "   ")                      // names nothing at all
                     })
                context.Quotes.Add(new Quote
                {
                    Id = id, QuoteNo = $"QT-{id}", BusinessUnitId = tenant, TotalAmount = 1m,
                    QuoteDate = Recent, CreatedBy = createdBy, CreatedDate = Recent
                });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync(QuoteOwnership.BackfillOwnerFromCreatedBySql);

            context.ChangeTracker.Clear();
            var owners = await context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == tenant)
                .ToDictionaryAsync(q => q.Id, q => q.OwnerUserId);

            Assert.Equal(onlySam, owners[97_190]);
            Assert.Equal(onlySam, owners[97_191]);
            Assert.Null(owners[97_192]);
            Assert.Null(owners[97_193]);
            Assert.Null(owners[97_194]);
        }
        finally
        {
            context.ChangeTracker.Clear();
            context.Quotes.RemoveRange(context.Quotes.Where(q => q.BusinessUnitId == tenant));
            await context.SaveChangesAsync();
            context.Users.RemoveRange(context.Users.Where(u => u.Buid == tenant));
            await context.SaveChangesAsync();
            // The business unit itself stays: provisioning hangs its own rows off it, and an empty
            // tenant shell is inert. The rows this test wrote are the ones it takes back.
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static User BackfillUser(long id, long tenant, string first, string last, string email) => new()
    {
        Id = id, FirstName = first, LastName = last, Email = email, PasswordHash = "x",
        ImageUrl = "n/a", Buid = tenant, IsActive = true, CreatedBy = "tests", CreatedOn = Recent
    };

    private static PipelineStageDTO Stage(PipelineAnalyticsDTO result, string key)
        => Assert.Single(result.Funnel, stage => stage.Key == key);

    /// <summary>
    /// The REAL resolver against real membership rows, not a hand-made scope: the defect this
    /// suite covers is that the repository never received one, and a fabricated scope object would
    /// not prove the resolver and the repository agree about who a rep is.
    /// </summary>
    private static async Task<AccountTeamScope> ScopeFor(
        ErpRfqAutomationContext context, long userId, short rank)
        => await new AccountTeamScopeResolver(context, new FixedRankRoleGate(rank))
            .ResolveAsync(userId, roleId: 1, Tenant, Recent);

    /// <summary>
    /// Seeds once for the whole class and then leaves the rows in place. Nothing here is torn
    /// down, and that is deliberate rather than lazy: a seeded Lead acquires a status-history row
    /// the database refuses to delete ("Lead status history is append-only", a guard this fixture
    /// has no business defeating), so the fixture is written to be re-enterable instead. The ids
    /// belong to this class alone and the collection is serialized.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var seed = database.ContextFor(null);
        if (await seed.BusinessUnits.AnyAsync(b => b.Id == Tenant)) return;

        Seed.EnsureBusinessUnit(seed, Tenant);
        Seed.EnsureBusinessUnit(seed, OtherTenant);
        await seed.SaveChangesAsync();

        seed.Currencies.Add(new Currency
        {
            Id = BaseCurrencyId, BusinessUnitId = Tenant, Code = "USD", CurrencyName = "US Dollar",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "tests", CreatedOn = Recent
        });

        foreach (var (id, code, value) in new[]
                 {
                     (SentStatusId, "SENT", "Sent"),
                     (AcceptedStatusId, "ACCEPTED", "Accepted"),
                     (RejectedStatusId, "REJECTED", "Rejected"),
                     (ExpiredStatusId, "EXPIRED", "Expired")
                 })
            seed.SetupMasters.Add(new SetupMaster
            {
                SetupId = id, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = code,
                SetupValue = value, IsActive = true, CreatedBy = "tests", CreatedOn = Recent
            });

        // The reason catalogue is governed per-tenant data, which is why the grouping can be the
        // server's decision at all — these are SetupMaster rows, not free text on the quote.
        foreach (var (id, code, label) in new[]
                 {
                     (PriceReasonId, "PRICE", "Price too high"),
                     (AutoExpiredReasonId, "AUTO_EXPIRED", "Expired automatically")
                 })
            seed.SetupMasters.Add(new SetupMaster
            {
                SetupId = id, BusinessUnitId = Tenant, SetupType = "QuoteOutcomeReason", SetupCode = code,
                SetupValue = label, Description = label, IsActive = true, CreatedBy = "tests", CreatedOn = Recent
            });

        seed.Users.AddRange(
            new User
            {
                Id = RepA, FirstName = "Sam", LastName = "North", Email = "rep.a@nexora.invalid",
                PasswordHash = "x", ImageUrl = "n/a", Buid = Tenant, IsActive = true,
                CreatedBy = "tests", CreatedOn = Recent
            },
            new User
            {
                Id = RepB, FirstName = "Bella", LastName = "South", Email = "rep.b@nexora.invalid",
                PasswordHash = "x", ImageUrl = "n/a", Buid = Tenant, IsActive = true,
                CreatedBy = "tests", CreatedOn = Recent
            });
        await seed.SaveChangesAsync();

        seed.Teams.AddRange(
            new Team { Id = NorthTeam, TeamName = "North", BusinessUnitId = Tenant, CreatedBy = "tests", CreatedOn = Recent },
            new Team { Id = SouthTeam, TeamName = "South", BusinessUnitId = Tenant, CreatedBy = "tests", CreatedOn = Recent });
        await seed.SaveChangesAsync();

        // Membership is Users.TeamID, the column the Users screen writes and the only one the
        // resolver reads.
        foreach (var (userId, teamId) in new[] { (RepA, NorthTeam), (RepB, SouthTeam) })
            (await seed.Users.SingleAsync(u => u.Id == userId)).TeamId = teamId;

        Seed.Customer(seed, NorthCustomer, Tenant, "North Account");
        Seed.Customer(seed, SouthCustomer, Tenant, "South Account");
        await seed.SaveChangesAsync();

        foreach (var (customerId, teamId) in new[] { (NorthCustomer, NorthTeam), (SouthCustomer, SouthTeam) })
            (await seed.Customers.SingleAsync(c => c.Id == customerId)).AccountTeamId = teamId;
        await seed.SaveChangesAsync();

        var repALead = Seed.Lead(seed, RepALead, Tenant);
        repALead.AssignTo = RepA;
        repALead.ResolveCommercialIdentity(NorthCustomer, null, "MATCHED");
        var repBLead = Seed.Lead(seed, RepBLead, Tenant);
        repBLead.AssignTo = RepB;
        repBLead.ResolveCommercialIdentity(SouthCustomer, null, "MATCHED");
        await seed.SaveChangesAsync();

        seed.Quotes.AddRange(
            Quote(WonByA, "Sam North", RepA, NorthCustomer, AcceptedStatusId, 1_000m, Early),
            Quote(SentByA, "Sam North", RepA, NorthCustomer, SentStatusId, 500m, Recent),
            Quote(WonByB, "Bella South", RepB, SouthCustomer, AcceptedStatusId, 9_000m, Recent),
            Quote(LostByB, "Bella South", RepB, SouthCustomer, RejectedStatusId, 7_000m, Recent,
                outcomeReasonId: PriceReasonId),
            Quote(ExpiredByB, "Bella South", RepB, SouthCustomer, ExpiredStatusId, 400m, Recent,
                outcomeReasonId: AutoExpiredReasonId),
            Quote(UnownedOnAAccount, "System", null, NorthCustomer, RejectedStatusId, 300m, Recent));

        // The other tenant's money. It shares a status id space with nothing here on purpose:
        // this row exists to prove the tenant predicate still runs first.
        seed.Currencies.Add(new Currency
        {
            Id = BaseCurrencyId + 1, BusinessUnitId = OtherTenant, Code = "USD", CurrencyName = "US Dollar",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "tests", CreatedOn = Recent
        });
        seed.Quotes.Add(new Quote
        {
            Id = 97_199, QuoteNo = "QT-OTHER-TENANT", BusinessUnitId = OtherTenant,
            StatusId = AcceptedStatusId, CurrencyId = BaseCurrencyId + 1, TotalAmount = 999_000m,
            QuoteDate = Recent, CreatedBy = "someone else", CreatedDate = Recent
        });
        await seed.SaveChangesAsync();
    }

    private static Quote Quote(
        long id, string createdBy, long? ownerUserId, long customerId, long statusId, decimal total,
        DateTime createdOn, long? outcomeReasonId = null) => new()
    {
        Id = id,
        QuoteNo = $"QT-{id}",
        BusinessUnitId = Tenant,
        CustomerId = customerId,
        OwnerUserId = ownerUserId,
        StatusId = statusId,
        CurrencyId = BaseCurrencyId,
        TotalAmount = total,
        OutcomeReasonId = outcomeReasonId,
        QuoteDate = createdOn,
        CreatedBy = createdBy,
        CreatedDate = createdOn
    };

    /// <summary>States a rank and nothing else; the rank rules are certified where RoleGate lives.</summary>
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

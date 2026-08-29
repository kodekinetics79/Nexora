using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What <see cref="GoldenCommercialJourneySeeder"/> does when its configuration is incomplete, and
/// how loudly it says so.
///
/// <para><b>The defect these tests pin.</b> With <c>GoldenJourneySeed:Enabled</c> true and
/// <c>GoldenJourneySeed:Password</c> unset, <c>EnsureAsync</c> seeded nothing and emitted ONE
/// <c>LogWarning</c>. The refusal is CORRECT — a seeder that provisions logins must never invent or
/// generate a default credential — but the single warning never made it into the aggregated log
/// output, so the observable symptom was "0 business units, 0 users, 0 mailboxes" with no stated
/// reason. An operator read that as a silent no-op and lost hours to it. So there are two things to
/// hold still, and they pull in opposite directions: the refusal must not be softened into a default
/// password, and it must not be quiet. Both are asserted below — the second on the captured log
/// record, because "nothing was written" is exactly the evidence that already failed to explain
/// itself.</para>
///
/// <para><b>Why the collaborators stop the run.</b> Beyond the configuration gate the seeder walks
/// the real identity pipeline, and revision lineage, the occurrence link and the append-only guards
/// are database-enforced — that is why
/// <see cref="GoldenJourneyIdentitySeedPostgreSqlTests"/> exists on its own PostgreSQL lane. None of
/// that is what these tests are about. <see cref="StopAtTheCollaborators"/> therefore satisfies the
/// three service resolutions and throws on the first CALL, which lands immediately after the whole
/// relational half of the seed — tenants, roles, users, AI policies, lifecycle statuses, customers,
/// products — has been committed. That is precisely the ground the operator saw empty, so it is the
/// ground worth asserting, and it is asserted on the portable SQLite lane where it is deterministic.</para>
///
/// <para>Companion to <see cref="GoldenJourneyGovernedTenantTests"/>, which pins the Production
/// refusal against a provider that carries no collaborators at all. The Production test here is the
/// stronger form of the same guard: it uses the provider this file has just PROVEN can seed, so a
/// zero-row assertion under Production cannot pass merely because the provider was too thin to
/// write anything.</para>
/// </summary>
public sealed class GoldenJourneySeederConfigurationTests : IDisposable
{
    // Obviously synthetic, local-only, and never a value any environment would hold. It exists to
    // prove the gate opens, not to authenticate anything.
    private const string SyntheticPassword = "GoldenSeederLocalOnly!1";

    private readonly TestDb _db = new();
    private readonly List<LogRecord> _log = [];

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Each_governed_outcome_gets_a_stable_but_independent_document_batch()
    {
        var partial = GoldenCommercialJourneySeeder.DeterministicBatchId(42, "E2E-GOLDEN-A-PARTIAL");
        var full = GoldenCommercialJourneySeeder.DeterministicBatchId(42, "E2E-GOLDEN-A-FULL");
        var noBid = GoldenCommercialJourneySeeder.DeterministicBatchId(42, "E2E-GOLDEN-A-NOBID");

        Assert.Equal(partial,
            GoldenCommercialJourneySeeder.DeterministicBatchId(42, "E2E-GOLDEN-A-PARTIAL"));
        Assert.Equal(3, new[] { partial, full, noBid }.Distinct().Count());
        Assert.NotEqual(partial,
            GoldenCommercialJourneySeeder.DeterministicBatchId(43, "E2E-GOLDEN-A-PARTIAL"));
    }

    [Fact]
    public void A_used_current_revision_cannot_be_certified_as_a_clean_browser_start()
    {
        Assert.Empty(GoldenCommercialJourneySeeder.StartingStateDecisionProblems(0, 0));

        var problems = GoldenCommercialJourneySeeder.StartingStateDecisionProblems(2, 3);

        Assert.Contains(problems, problem => problem.Contains("fit assessment", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("participation decision", StringComparison.Ordinal));
    }

    [Fact]
    public void Golden_line_records_exact_identity_quantity_and_uom_provenance()
    {
        var line = new LeadItem
        {
            ItemMaterialCode = "GOLD-NOQT-0005",
            ProductShortDescription = "Intended no-catalog warning line",
            Quantity = 12.5m,
            UnitOfMeasure = "EA"
        };

        var fields = GoldenCommercialJourneySeeder.GoldenEvidenceFields(line);

        Assert.Equal(3, fields.Count);
        Assert.Contains(fields, x => x.Name == "ItemMaterialCode"
            && x.Raw == "GOLD-NOQT-0005" && x.Normalized == "GOLD-NOQT-0005");
        Assert.Contains(fields, x => x.Name == "Quantity" && x.Raw == "12.5" && x.Normalized == "12.5");
        Assert.Contains(fields, x => x.Name == "UnitOfMeasure" && x.Raw == "EA" && x.Normalized == "EA");
    }

    [Fact]
    public void Golden_current_revision_must_freeze_the_live_customer_and_retain_source_lineage()
    {
        Assert.Empty(GoldenCommercialJourneySeeder.CurrentRevisionIdentityProblems(
            leadCustomerId: 101, leadContactId: 202,
            revisionCustomerId: 101, revisionContactId: 202,
            hasSourceLineage: true));

        var stale = GoldenCommercialJourneySeeder.CurrentRevisionIdentityProblems(
            leadCustomerId: 101, leadContactId: 202,
            revisionCustomerId: null, revisionContactId: null,
            hasSourceLineage: false);
        Assert.Contains(stale, x => x.Contains("customer identity", StringComparison.Ordinal));
        Assert.Contains(stale, x => x.Contains("contact identity", StringComparison.Ordinal));
        Assert.Contains(stale, x => x.Contains("source-document lineage", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the defect, stated

    /// <summary>
    /// Enabled, no password: nothing is seeded — and the refusal is on the record at Error level,
    /// naming the key that is missing. The row counts alone were the original symptom and explained
    /// nothing; the assertion that matters is the one on the message.
    /// </summary>
    [Fact]
    public async Task Enabled_without_a_password_seeds_nothing_and_says_why_at_error_level()
    {
        await GoldenCommercialJourneySeeder.EnsureAsync(
            SeedingProvider(), Config(password: null), new DevelopmentEnvironment());

        await AssertNothingWasSeededAsync();

        // Exactly one record, because a refusal that arrives buried in other output is the failure
        // mode being fixed, not a second-best outcome.
        var record = Assert.Single(_log);

        // Error, not Warning: this is a misconfiguration that disables the whole facility, and the
        // one warning it used to emit is the line that never reached the operator.
        Assert.Equal(LogLevel.Error, record.Level);

        // The exact key, so the message carries its own fix.
        Assert.Contains("GoldenJourneySeed:Password", record.Message, StringComparison.Ordinal);
        Assert.Contains("GoldenJourneySeed:Enabled", record.Message, StringComparison.Ordinal);

        // The symptom, named in the message, so the empty database and the log line can be matched
        // to each other without a code read.
        Assert.Contains("seeded NOTHING", record.Message, StringComparison.Ordinal);

        // And the reasoning survives verbatim. This is the half a future "fix" would be tempted to
        // delete on its way to a default password.
        Assert.Contains("No default credential will ever be seeded", record.Message, StringComparison.Ordinal);
        Assert.Contains("none will be generated", record.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whitespace password is an unset password. Left to <c>string.IsNullOrWhiteSpace</c> in the
    /// seeder, but pinned here because "" and " " are what an operator actually leaves behind when a
    /// secret fails to substitute — and a blank BCrypt hash would be a seeded credential of the
    /// worst kind.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_password_is_treated_as_unset(string password)
    {
        await GoldenCommercialJourneySeeder.EnsureAsync(
            SeedingProvider(), Config(password), new DevelopmentEnvironment());

        await AssertNothingWasSeededAsync();
        Assert.Equal(LogLevel.Error, Assert.Single(_log).Level);
    }

    // ---------------------------------------------------------------- the refusal is not vacuous

    /// <summary>
    /// The control for every "seeds nothing" assertion above: the IDENTICAL provider, environment
    /// and code path with a password present does seed. Without this, a seeder that had been broken
    /// into seeding nothing under any configuration would pass the refusal tests perfectly.
    /// </summary>
    [Fact]
    public async Task Enabled_with_a_password_seeds_the_business_units_and_the_users()
    {
        await Assert.ThrowsAsync<StopAtTheCollaborators.Reached>(() =>
            GoldenCommercialJourneySeeder.EnsureAsync(
                SeedingProvider(), Config(SyntheticPassword), new DevelopmentEnvironment()));

        await using var ctx = _db.ContextFor(null);

        // The two governed tenants the journey needs…
        var businessUnits = await ctx.BusinessUnits.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(2, businessUnits.Count);
        Assert.Contains(businessUnits, b => b.BusinessUnitCode == GoldenCommercialJourneySeeder.TenantACode);
        Assert.Contains(businessUnits, b => b.BusinessUnitCode == GoldenCommercialJourneySeeder.TenantBCode);
        Assert.Equal(2, await ctx.Set<Tenant>().IgnoreQueryFilters().CountAsync());

        // …and the five logins, which are the reason the password is demanded in the first place.
        var users = await ctx.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(5, users.Count);
        foreach (var email in new[]
                 {
                     "golden.admin@e2e.local", "golden.sales@e2e.local", "golden.manager@e2e.local",
                     "golden.denied@e2e.local", "golden.outsider@e2e.local"
                 })
        {
            var user = Assert.Single(users, u => u.Email == email);
            // The supplied password is what was hashed — not a substitute, and not a blank.
            Assert.True(BCrypt.Net.BCrypt.Verify(SyntheticPassword, user.PasswordHash));
        }

        var sales = Assert.Single(users, user => user.Email == "golden.sales@e2e.local");
        var manager = Assert.Single(users, user => user.Email == "golden.manager@e2e.local");
        var denied = Assert.Single(users, user => user.Email == "golden.denied@e2e.local");
        var outsider = Assert.Single(users, user => user.Email == "golden.outsider@e2e.local");
        var roles = await ctx.SetupMasters.IgnoreQueryFilters().AsNoTracking()
            .Where(role => role.SetupType == "Role")
            .ToDictionaryAsync(role => role.SetupId);

        Assert.Equal("SALES_REP", roles[sales.RoleId!.Value].SetupCode);
        Assert.Equal(RoleRanks.Member, roles[sales.RoleId.Value].RoleRank);
        Assert.Equal("SALES_MANAGER", roles[manager.RoleId!.Value].SetupCode);
        Assert.Equal(RoleRanks.Manager, roles[manager.RoleId.Value].RoleRank);
        Assert.Equal("E2E_DENIED", roles[denied.RoleId!.Value].SetupCode);
        Assert.Equal(RoleRanks.Member, roles[denied.RoleId.Value].RoleRank);
        Assert.Equal("SALES_REP", roles[outsider.RoleId!.Value].SetupCode);
        Assert.Equal(RoleRanks.Member, roles[outsider.RoleId.Value].RoleRank);

        var team = await ctx.Teams.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(value => value.TeamName == "Golden Sales Team");
        Assert.Equal(manager.Id, team.ManagerId);
        Assert.Equal(team.Id, sales.TeamId);
        Assert.Equal(manager.Id, sales.ManagerId);
        Assert.Equal(team.Id, manager.TeamId);

        Assert.True(await ctx.RolePermissions.IgnoreQueryFilters()
            .AnyAsync(permission => permission.RoleId == sales.RoleId));
        Assert.True(await ctx.RolePermissions.IgnoreQueryFilters()
            .AnyAsync(permission => permission.RoleId == manager.RoleId));
        Assert.False(await ctx.RolePermissions.IgnoreQueryFilters()
            .AnyAsync(permission => permission.RoleId == denied.RoleId));

        // The success path stays quiet at Error level; noise here would devalue the refusal.
        Assert.DoesNotContain(_log, r => r.Level >= LogLevel.Error);
    }

    // ---------------------------------------------------------------- the Production boundary

    /// <summary>
    /// Production refuses a fully configured, explicitly enabled seed.
    ///
    /// <para>The provider here is the one <see cref="Enabled_with_a_password_seeds_the_business_units_and_the_users"/>
    /// has just shown will write rows, so zero rows can only be the environment guard holding. That
    /// distinction is the whole value of this test: a refusal proven against a provider too thin to
    /// seed proves nothing about the guard.</para>
    /// </summary>
    [Fact]
    public async Task Production_refuses_a_fully_configured_seed()
    {
        await GoldenCommercialJourneySeeder.EnsureAsync(
            SeedingProvider(), Config(SyntheticPassword), new ProductionEnvironment());

        await AssertNothingWasSeededAsync();

        var record = Assert.Single(_log);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Contains("Production", record.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The environment is checked BEFORE the password, and must stay that way. If the order ever
    /// inverted, a production host with no password configured would be refused for the wrong
    /// reason and report the wrong fix — "set GoldenJourneySeed:Password" — which is an instruction
    /// to move a local E2E seeder one configuration key closer to running against production data.
    /// </summary>
    [Fact]
    public async Task Under_production_the_environment_is_the_stated_reason_even_with_no_password()
    {
        await GoldenCommercialJourneySeeder.EnsureAsync(
            SeedingProvider(), Config(password: null), new ProductionEnvironment());

        await AssertNothingWasSeededAsync();

        var record = Assert.Single(_log);
        Assert.Contains("Production", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GoldenJourneySeed:Password", record.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- disabled

    /// <summary>
    /// Off is off, and silently so: an absent <c>GoldenJourneySeed:Enabled</c> is the normal state of
    /// every host that is not running the golden journey, so it must not spend an Error on it. This
    /// is what keeps the Error above meaningful.
    /// </summary>
    [Fact]
    public async Task Not_enabled_seeds_nothing_and_logs_nothing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["GoldenJourneySeed:Password"] = SyntheticPassword }).Build();

        await GoldenCommercialJourneySeeder.EnsureAsync(
            SeedingProvider(), config, new DevelopmentEnvironment());

        await AssertNothingWasSeededAsync();
        Assert.Empty(_log);
    }

    // ---------------------------------------------------------------- helpers

    private static IConfiguration Config(string? password)
    {
        var values = new Dictionary<string, string?> { ["GoldenJourneySeed:Enabled"] = "true" };
        if (password is not null) values["GoldenJourneySeed:Password"] = password;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// The empty database the operator actually saw, stated as the three counts they read off it.
    /// Mailboxes are included because "0 mailboxes" was the third of the three numbers in the report,
    /// and the seeder must not be able to half-run.
    /// </summary>
    private async Task AssertNothingWasSeededAsync()
    {
        await using var ctx = _db.ContextFor(null);
        Assert.Equal(0, await ctx.BusinessUnits.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await ctx.Users.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await ctx.Set<Tenant>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await ctx.Set<Plan>().CountAsync());
        Assert.Equal(0, await ctx.Customers.IgnoreQueryFilters().CountAsync());
    }

    /// <summary>
    /// Everything <c>EnsureAsync</c> resolves, so the run is stopped by a decision rather than by a
    /// missing registration — the difference between proving a guard and proving a thin provider.
    /// </summary>
    private IServiceProvider SeedingProvider()
    {
        var stop = new StopAtTheCollaborators();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new RecordingLoggerProvider(_log));
        });
        services.AddScoped(_ => _db.ContextFor(null));
        services.AddScoped<ITenantBaselineSeeder, TenantBaselineSeeder>();
        services.AddSingleton<ISalesApplicationService>(stop);
        services.AddSingleton<ICommercialRoutingApplicationService>(stop);
        services.AddSingleton<ILeadIdentityApplicationService>(stop);
        return services.BuildServiceProvider();
    }

    private sealed record LogRecord(LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider(List<LogRecord> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(sink);
        public void Dispose() { }

        private sealed class RecordingLogger(List<LogRecord> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Resolvable, and a hard stop on first use.
    ///
    /// <para>The seeder resolves all three application services up front and calls the first of them
    /// — <see cref="ISalesApplicationService.UpsertProfileAsync"/> — only after the entire relational
    /// half of the seed is committed. Throwing there draws the line exactly where the portable lane
    /// stops being able to speak for the product: everything before it is ordinary EF that SQLite
    /// executes identically, everything after it is the identity/routing pipeline whose guarantees
    /// are database-enforced and are certified on the PostgreSQL lane instead.</para>
    ///
    /// <para>A distinct exception type, not <see cref="NotImplementedException"/>, so a test that
    /// stops for some unrelated reason cannot be mistaken for one that reached the boundary.</para>
    /// </summary>
    private sealed class StopAtTheCollaborators
        : ISalesApplicationService, ICommercialRoutingApplicationService, ILeadIdentityApplicationService
    {
        public sealed class Reached() : Exception(
            "The golden seeder reached its application-service collaborators; the relational half of "
            + "the seed is committed and is what this lane asserts.");

        private static Reached Stop() => new();

        // ISalesApplicationService
        public WeightedRoutingResult ScoreNewCustomer(NewCustomerRoutingRequest request) => throw Stop();
        public Task<SalesRepProfile> UpsertProfileAsync(long businessUnitId, UpsertSalesRepProfileCommand command, CancellationToken ct) => throw Stop();
        public Task<CommercialActivity> AppendActivityAsync(long businessUnitId, AppendCommercialActivityCommand command, CancellationToken ct) => throw Stop();
        public Task<FollowUpTask> CreateFollowUpAsync(long businessUnitId, CreateFollowUpTaskCommand command, CancellationToken ct) => throw Stop();
        public Task<FollowUpTransitionEvent> TransitionFollowUpAsync(long businessUnitId, long taskId, TransitionFollowUpTaskCommand command, CancellationToken ct) => throw Stop();
        public Task<SalesContribution> RecordContributionAsync(long businessUnitId, RecordSalesContributionCommand command, CancellationToken ct) => throw Stop();
        public Task<IReadOnlyList<SalesRepPerformance>> GetPerformanceAsync(long businessUnitId, SalesPerformanceQuery query, CancellationToken ct) => throw Stop();

        // ICommercialRoutingApplicationService
        public Task<RoutingDecisionResponse> RouteLeadAsync(long businessUnitId, RouteLeadCommand command, CancellationToken ct) => throw Stop();
        public Task<RoutingDecisionResponse> AssignLeadAsync(long businessUnitId, ManualAssignLeadCommand command, CancellationToken ct) => throw Stop();
        public Task<LeadOwnershipResponse> ChangeLeadOwnershipAsync(long businessUnitId, ChangeLeadOwnershipCommand command, CancellationToken ct) => throw Stop();
        public Task<IReadOnlyList<RoutingOwnerOptionResponse>> GetOwnerOptionsAsync(long businessUnitId, CancellationToken ct) => throw Stop();
        public Task<QueuePageResponse> GetQueueAsync(long businessUnitId, WorkItemStatus? status, string? search, bool overdueOnly, int pageNumber, int pageSize, CancellationToken ct) => throw Stop();
        public Task<UnassignedQueueItemResponse> ClaimAsync(long businessUnitId, long workItemId, QueueLeaseCommand command, CancellationToken ct) => throw Stop();
        public Task<UnassignedQueueItemResponse> ReleaseAsync(long businessUnitId, long workItemId, QueueReleaseCommand command, CancellationToken ct) => throw Stop();
        public Task<RoutingDecisionResponse> AssignQueueItemAsync(long businessUnitId, long workItemId, AssignQueueItemCommand command, CancellationToken ct) => throw Stop();
        public Task<IReadOnlyList<BulkQueueAssignmentResult>> BulkAssignQueueAsync(long businessUnitId, BulkAssignQueueCommand command, CancellationToken ct) => throw Stop();
        public Task<CustomerIdentifier> UpsertIdentifierAsync(long businessUnitId, UpsertCustomerIdentifierCommand command, CancellationToken ct) => throw Stop();
        public Task<CustomerOwnership> CreateOwnershipAsync(long businessUnitId, CreateCustomerOwnershipCommand command, CancellationToken ct) => throw Stop();
        public Task<CustomerRoutingProfileResponse?> GetCustomerProfileAsync(long businessUnitId, long customerId, CancellationToken ct) => throw Stop();
        public Task<DefaultLeadOwnerResponse> GetDefaultOwnerAsync(long businessUnitId, CancellationToken ct) => throw Stop();
        public Task<DefaultLeadOwnerResponse> SetDefaultOwnerAsync(long businessUnitId, SetDefaultLeadOwnerCommand command, CancellationToken ct) => throw Stop();

        // ILeadIdentityApplicationService
        public Task<LeadReconciliationResult> ReconcileAsync(Lead candidate, LeadIntakeDescriptor intake, CancellationToken ct = default) => throw Stop();
        public Task<BatchReconciliationDto?> GetBatchAsync(long businessUnitId, Guid batchId, CancellationToken ct = default) => throw Stop();
        public Task<IReadOnlyList<PossibleMatchQueueItemDto>> GetPossibleMatchesAsync(long businessUnitId, CancellationToken ct = default) => throw Stop();
        public Task<IReadOnlyList<DuplicateUploadDto>> GetDuplicateUploadsAsync(long businessUnitId, CancellationToken ct = default) => throw Stop();
        public Task<IReadOnlyList<LeadRevisionDto>> GetRevisionsAsync(long businessUnitId, long leadId, CancellationToken ct = default) => throw Stop();
        public Task<LeadReconciliationResult> DecideMatchAsync(long businessUnitId, long occurrenceId, MatchDecisionRequest request, string actorId, CancellationToken ct = default) => throw Stop();
        public Task<LeadIdentityAnalyticsDto> GetAnalyticsAsync(long businessUnitId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw Stop();
        public Task<LeadReconciliationResult> EstablishBaselineRevisionAsync(long businessUnitId, long leadId, LeadIdentityBaselineRequest request, CancellationToken ct = default) => throw Stop();
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

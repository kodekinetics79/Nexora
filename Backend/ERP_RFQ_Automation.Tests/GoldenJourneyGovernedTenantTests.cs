using System.Collections.Concurrent;
using System.Data.Common;
using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The golden E2E seeder must produce a GOVERNED tenant.
///
/// <para><b>The defect these tests pin.</b> <see cref="GoldenCommercialJourneySeeder"/> created two
/// BusinessUnits and no control-plane record at all, so <c>TenantAccessService</c> resolved no
/// entitlements and every <c>[RequiresEntitlement]</c> action refused. Reproduced against
/// PostgreSQL on 2026-08-12: sign in as the seeded golden salesperson, <c>GET /api/Rfq</c> → 403
/// <i>"Entitlement 'module.rfq' is unavailable because no governed platform tenant owns this
/// business unit"</i>. That is roughly thirty-five client-portal routes — the whole RFQ → quote →
/// order journey — so the browser scenario could not walk the journey it exists to prove.</para>
///
/// <para><b>The entitlement check itself is untouched.</b>
/// <see cref="Refusal_of_an_ungoverned_business_unit_is_unchanged"/> asserts the refusal verbatim,
/// so these tests fail if anybody ever "fixes" the 403 by weakening
/// <c>EntitlementService.CheckFeatureAsync</c> instead of seeding the data it asks for. Every
/// permit below is the production check answering yes to production data.</para>
///
/// <para>Portable lane: the control-plane resolution is ordinary relational reads, so SQLite
/// exercises the same query the API does. Nothing here depends on RLS or a PostgreSQL dialect.</para>
/// </summary>
public sealed class GoldenJourneyGovernedTenantTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// The real check, over the real resolution. A fresh <see cref="MemoryCache"/> per call so a
    /// 60-second snapshot cached before the seed cannot answer a question asked after it.
    /// </summary>
    private static IEntitlementService Entitlements(ErpRfqAutomationContext ctx)
        => new EntitlementService(
            new TenantAccessService(ctx, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance),
            ctx);

    private async Task<EntitlementDecision> CheckRfqAsync(long businessUnitId)
    {
        await using var ctx = _db.ContextFor(null);
        return await Entitlements(ctx).CheckFeatureAsync(businessUnitId, TypedEntitlementCatalog.Rfq);
    }

    private async Task SeedAsync()
    {
        await using var ctx = _db.ContextFor(null);
        await GoldenCommercialJourneySeeder.EnsureGovernedTenantsAsync(
            ctx, NullLogger.Instance, DateTime.UtcNow);
    }

    /// <summary>Seeds through an interceptor so the SQL the seeder issues can be inspected.</summary>
    private async Task SeedAsync(SqlRecorder recorder)
    {
        await using var ctx = _db.ContextFor(null, recorder);
        await GoldenCommercialJourneySeeder.EnsureGovernedTenantsAsync(
            ctx, NullLogger.Instance, DateTime.UtcNow);
    }

    private async Task<BusinessUnit> BusinessUnitAsync(string code)
    {
        await using var ctx = _db.ContextFor(null);
        return await ctx.BusinessUnits.IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(b => b.BusinessUnitCode == code);
    }

    private async Task<Tenant> TenantForAsync(long businessUnitId)
    {
        await using var ctx = _db.ContextFor(null);
        return await ctx.Set<Tenant>().IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(t => t.PrimaryBusinessUnitId == businessUnitId);
    }

    // ---------------------------------------------------------------- the defect, stated

    /// <summary>
    /// The check is the control, not the bug. A business unit with no platform tenant is refused
    /// with the exact message the browser journey hit — and that refusal must survive this change
    /// unaltered, because the fix is to seed the tenant, never to relax the guard.
    /// </summary>
    [Fact]
    public async Task Refusal_of_an_ungoverned_business_unit_is_unchanged()
    {
        await using (var ctx = _db.ContextFor(null))
        {
            ctx.BusinessUnits.Add(new BusinessUnit
            {
                Id = 903_100,
                BusinessUnitCode = "UNGOVERNED",
                BusinessUnitName = "Ungoverned",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var decision = await CheckRfqAsync(903_100);

        Assert.False(decision.Allowed);
        Assert.Equal(
            "Entitlement 'module.rfq' is unavailable because no governed platform tenant owns this business unit.",
            decision.Reason);
    }

    // ---------------------------------------------------------------- the fix

    [Fact]
    public async Task Both_golden_business_units_resolve_to_an_active_tenant_with_a_plan()
    {
        await SeedAsync();

        foreach (var code in new[]
                 {
                     GoldenCommercialJourneySeeder.TenantACode,
                     GoldenCommercialJourneySeeder.TenantBCode
                 })
        {
            var businessUnit = await BusinessUnitAsync(code);
            var tenant = await TenantForAsync(businessUnit.Id);

            Assert.Equal(TenantStatus.Active, tenant.Status);
            Assert.NotNull(tenant.PlanId);
            Assert.Equal(businessUnit.BusinessUnitCode.ToLowerInvariant(), tenant.Slug);

            // A non-Billable tenant with no recorded reason is an oversight wearing a decision's
            // clothes — UnplannedTenantAllowance reads this column to tell the two apart.
            Assert.Equal(TenantBillingMode.Internal, tenant.BillingMode);
            Assert.False(string.IsNullOrWhiteSpace(tenant.BillingModeReason));

            // The 403 that stopped the journey, now a permit — from the production check.
            var decision = await CheckRfqAsync(businessUnit.Id);
            Assert.True(decision.Allowed, decision.Reason);
        }
    }

    /// <summary>
    /// Tenant B exists ONLY to prove cross-tenant refusal, so it must be entitled. An ungoverned
    /// tenant B would let a cross-tenant test pass because the tenant is unentitled rather than
    /// because isolation held — a false pass, which is worse than a failure because nobody looks
    /// at it again.
    /// </summary>
    [Fact]
    public async Task Tenant_b_is_entitled_so_a_cross_tenant_refusal_can_only_be_isolation()
    {
        await SeedAsync();

        var tenantB = await BusinessUnitAsync(GoldenCommercialJourneySeeder.TenantBCode);

        foreach (var key in new[]
                 {
                     TypedEntitlementCatalog.Rfq,
                     TypedEntitlementCatalog.Quotes,
                     TypedEntitlementCatalog.Orders,
                     TypedEntitlementCatalog.Procurement
                 })
        {
            await using var ctx = _db.ContextFor(null);
            var decision = await Entitlements(ctx).CheckFeatureAsync(tenantB.Id, key);
            Assert.True(decision.Allowed, $"{key}: {decision.Reason}");
        }
    }

    /// <summary>
    /// The plan advertises exactly what the server can execute. A packaging flag turned on for an
    /// unimplemented capability would describe a surface that does not exist, and runtime
    /// authorization denies it anyway.
    /// </summary>
    [Fact]
    public async Task The_seeded_plan_enables_only_runtime_available_entitlements()
    {
        await SeedAsync();

        await using var ctx = _db.ContextFor(null);
        var plan = await ctx.Set<Plan>().AsNoTracking()
            .SingleAsync(p => p.Code == GoldenCommercialJourneySeeder.GoldenPlanCode);

        Assert.True(TypedEntitlementCatalog.TryParse(plan.Features, out var features, out var error), error);
        // Every catalogue key present, so an absent key can never read as an intentional grant.
        Assert.Equal(TypedEntitlementCatalog.Keys.Count, features.Count);
        foreach (var key in TypedEntitlementCatalog.Keys)
            Assert.Equal(TypedEntitlementCatalog.IsRuntimeAvailable(key), features[key]);
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task Re_running_the_seed_creates_no_second_plan_or_tenant()
    {
        await SeedAsync();
        await SeedAsync();
        await SeedAsync();

        await using var ctx = _db.ContextFor(null);
        Assert.Equal(1, await ctx.Set<Plan>()
            .CountAsync(p => p.Code == GoldenCommercialJourneySeeder.GoldenPlanCode));
        Assert.Equal(2, await ctx.Set<Tenant>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(2, await ctx.BusinessUnits.IgnoreQueryFilters().CountAsync());
    }

    /// <summary>
    /// By the second run an operator may have narrowed the plan deliberately. Re-imposing the
    /// default over that would be an unlogged reversal of their decision.
    /// </summary>
    [Fact]
    public async Task Re_running_the_seed_never_overwrites_an_existing_plans_features()
    {
        const string narrowed = """{"module.rfq":false}""";

        await using (var ctx = _db.ContextFor(null))
        {
            ctx.Set<Plan>().Add(new Plan
            {
                Code = GoldenCommercialJourneySeeder.GoldenPlanCode,
                Name = "Operator narrowed",
                Features = narrowed,
                MaxSeats = 1
            });
            await ctx.SaveChangesAsync();
        }

        await SeedAsync();

        await using (var ctx = _db.ContextFor(null))
        {
            var plan = await ctx.Set<Plan>().AsNoTracking()
                .SingleAsync(p => p.Code == GoldenCommercialJourneySeeder.GoldenPlanCode);
            Assert.Equal(narrowed, plan.Features);
            Assert.Equal(1, plan.MaxSeats);
            Assert.Equal("Operator narrowed", plan.Name);
        }

        // And the narrowing is honoured downstream: the seeder did not buy back the entitlement.
        var tenantA = await BusinessUnitAsync(GoldenCommercialJourneySeeder.TenantACode);
        var decision = await CheckRfqAsync(tenantA.Id);
        Assert.False(decision.Allowed);
    }

    /// <summary>
    /// A suspended tenant is a decision somebody made and recorded. A restart of the API — which is
    /// all a re-seed is — must not quietly resume it.
    /// </summary>
    [Fact]
    public async Task A_suspended_golden_tenant_is_left_suspended()
    {
        await SeedAsync();
        var tenantA = await BusinessUnitAsync(GoldenCommercialJourneySeeder.TenantACode);

        await using (var ctx = _db.ContextFor(null))
        {
            var tenant = await ctx.Set<Tenant>().IgnoreQueryFilters()
                .SingleAsync(t => t.PrimaryBusinessUnitId == tenantA.Id);
            tenant.Status = TenantStatus.Suspended;
            tenant.StatusReason = "Suspended by an operator during the pilot.";
            await ctx.SaveChangesAsync();
        }

        await SeedAsync();

        var after = await TenantForAsync(tenantA.Id);
        Assert.Equal(TenantStatus.Suspended, after.Status);
        Assert.Equal("Suspended by an operator during the pilot.", after.StatusReason);
    }

    /// <summary>
    /// The one exception to "never overwrite": a Tenant row with no plan at all is not a decision,
    /// it is the gap — it can be entitled to nothing.
    /// </summary>
    [Fact]
    public async Task An_existing_tenant_with_no_plan_is_given_one()
    {
        await using (var ctx = _db.ContextFor(null))
        {
            var businessUnit = new BusinessUnit
            {
                BusinessUnitCode = GoldenCommercialJourneySeeder.TenantACode,
                BusinessUnitName = "E2E Golden Tenant A",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            };
            ctx.BusinessUnits.Add(businessUnit);
            await ctx.SaveChangesAsync();

            ctx.Set<Tenant>().Add(new Tenant
            {
                Name = "E2E Golden Tenant A",
                Slug = "e2e-golden-a",
                Status = TenantStatus.Active,
                PlanId = null,
                PrimaryBusinessUnitId = businessUnit.Id,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        await SeedAsync();

        var tenantA = await BusinessUnitAsync(GoldenCommercialJourneySeeder.TenantACode);
        var tenant = await TenantForAsync(tenantA.Id);
        Assert.NotNull(tenant.PlanId);
        Assert.True((await CheckRfqAsync(tenantA.Id)).Allowed);
    }

    /// <summary>
    /// The seeder repairs a stable canonical row while runtime authorization rejects ambiguity.
    ///
    /// <para>Nothing keeps a business unit to one Tenant row: there is no unique index on
    /// <c>PrimaryBusinessUnitId</c>. The development fixture seeder still needs deterministic
    /// repair semantics, so it attaches the plan to the lowest-id row rather than whichever row
    /// the engine happens to return. That does not confer runtime authority: TenantAccessService
    /// observes both owners and refuses the ambiguous mapping.
    /// </para>
    ///
    /// <para><b>Why the SQL is asserted as well as the outcome.</b> On SQLite a <c>bigint</c> primary
    /// key is an alias for the rowid, so an unordered scan is ASCENDING BY ID by construction and
    /// both the fixed and the broken seeder land on the same row here — the row assertion below is a
    /// true statement about behaviour but it cannot fail. PostgreSQL has no such guarantee: a
    /// sequential scan returns physical order, and the row inserted first wins. The ordering is
    /// therefore also asserted where it is actually decided — in the query — so this test fails if
    /// the <c>OrderBy</c> is ever removed.</para>
    /// </summary>
    [Fact]
    public async Task The_plan_lands_on_the_tenant_row_the_entitlement_check_reads()
    {
        const long businessUnitId = 903_200;
        const long lowerTenantId = 903_301;
        const long higherTenantId = 903_302;

        await using (var ctx = _db.ContextFor(null))
        {
            ctx.BusinessUnits.Add(new BusinessUnit
            {
                Id = businessUnitId,
                BusinessUnitCode = GoldenCommercialJourneySeeder.TenantACode,
                BusinessUnitName = "E2E Golden Tenant A",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            // Written HIGHER id first, so physical order and id order disagree on any engine that
            // returns the former.
            foreach (var (id, slug) in new[]
                     {
                         (higherTenantId, "e2e-golden-a-duplicate"),
                         (lowerTenantId, "e2e-golden-a")
                     })
            {
                ctx.Set<Tenant>().Add(new Tenant
                {
                    Id = id,
                    Name = "E2E Golden Tenant A",
                    Slug = slug,
                    Status = TenantStatus.Active,
                    PlanId = null,
                    PrimaryBusinessUnitId = businessUnitId,
                    CreatedBy = "tests",
                    CreatedOn = DateTime.UtcNow
                });
                await ctx.SaveChangesAsync();
            }
        }

        var recorder = new SqlRecorder();
        await SeedAsync(recorder);

        await using (var ctx = _db.ContextFor(null))
        {
            var rows = await ctx.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.PrimaryBusinessUnitId == businessUnitId)
                .OrderBy(t => t.Id)
                .ToListAsync();

            Assert.Equal(2, rows.Count);
            // The plan is on the stable canonical repair row…
            Assert.Equal(lowerTenantId, rows[0].Id);
            Assert.NotNull(rows[0].PlanId);
            // …and the seeder did not wander off and repair an insertion-order-dependent row.
            Assert.Null(rows[1].PlanId);
        }

        // Repair and runtime authority are deliberately different contracts. The fixture seeder
        // remains deterministic so it can repair the canonical row, but two tenant owners are
        // still ambiguous at the authorization boundary and therefore fail closed.
        var access = await CheckRfqAsync(businessUnitId);
        Assert.False(access.Allowed);
        Assert.Contains("could not be confirmed", access.Reason, StringComparison.OrdinalIgnoreCase);

        // And the ordering is in the query, not in the engine's goodwill.
        var lookups = recorder.Statements.Where(sql =>
            sql.StartsWith("SELECT", StringComparison.Ordinal)
            && sql.Contains("Tenants", StringComparison.Ordinal)
            && sql.Contains("PrimaryBusinessUnitId", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lookups.Count); // one per golden business unit
        Assert.All(lookups, sql => Assert.Contains("ORDER BY", sql, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- what the row says it is

    /// <summary>
    /// A seeded tenant must not read as a real customer that passed every control.
    ///
    /// <para>These rows are written straight to Active — correctly, because Provisioning would deny
    /// the access the seed exists to grant — and Active with the default PRODUCTION profile is
    /// exactly the shape of a customer whose activation controls all went green. On a shared
    /// platform console the two are indistinguishable, and the mislabelling is PERMANENT: the
    /// audited endpoint refuses to move a tenant off PRODUCTION once it has left Provisioning, so a
    /// row created unlabelled can never acquire the label afterwards. Hence at creation, and only at
    /// creation.</para>
    ///
    /// <para>LOCAL_TEST relaxes nothing that matters here: the profile is read by the activation
    /// surface alone, every deferred prerequisite stays a production blocker, and the tenant stays
    /// uncertifiable.</para>
    /// </summary>
    [Fact]
    public async Task A_created_tenant_is_labelled_local_test_with_a_reason()
    {
        await SeedAsync();

        foreach (var code in new[]
                 {
                     GoldenCommercialJourneySeeder.TenantACode,
                     GoldenCommercialJourneySeeder.TenantBCode
                 })
        {
            var businessUnit = await BusinessUnitAsync(code);
            var tenant = await TenantForAsync(businessUnit.Id);

            Assert.Equal(TenantDeploymentProfile.LocalTest, tenant.DeploymentProfile);
            Assert.False(string.IsNullOrWhiteSpace(tenant.DeploymentProfileReason));
            // Active stays Active — the label is a statement about what this workspace IS, not a
            // downgrade of what it can do.
            Assert.Equal(TenantStatus.Active, tenant.Status);
            Assert.True((await CheckRfqAsync(businessUnit.Id)).Allowed);
        }
    }

    /// <summary>
    /// The label is written on CREATION only. An existing row's profile is somebody's decision —
    /// possibly an Owner's, through the audited endpoint — and a restart of the API must not
    /// overwrite it, exactly as it must not overwrite a suspension or a narrowed plan.
    /// </summary>
    [Fact]
    public async Task An_existing_tenants_deployment_profile_is_never_rewritten()
    {
        await using (var ctx = _db.ContextFor(null))
        {
            var businessUnit = new BusinessUnit
            {
                BusinessUnitCode = GoldenCommercialJourneySeeder.TenantACode,
                BusinessUnitName = "E2E Golden Tenant A",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            };
            ctx.BusinessUnits.Add(businessUnit);
            await ctx.SaveChangesAsync();

            ctx.Set<Tenant>().Add(new Tenant
            {
                Name = "E2E Golden Tenant A",
                Slug = "e2e-golden-a",
                Status = TenantStatus.Active,
                PlanId = null,
                PrimaryBusinessUnitId = businessUnit.Id,
                DeploymentProfile = TenantDeploymentProfile.Production,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        await SeedAsync();

        var tenantA = await BusinessUnitAsync(GoldenCommercialJourneySeeder.TenantACode);
        var tenant = await TenantForAsync(tenantA.Id);

        // The gap — no plan — is closed, because a tenant entitled to nothing is not a decision.
        Assert.NotNull(tenant.PlanId);
        // The profile is left exactly as it was found.
        Assert.Equal(TenantDeploymentProfile.Production, tenant.DeploymentProfile);
        Assert.Null(tenant.DeploymentProfileReason);
        Assert.False(DeploymentProfilePolicy.PermitsDeferral(tenant));
    }

    // ---------------------------------------------------------------- the Production refusal

    /// <summary>
    /// The guard is the whole boundary, so it gets a test of its own.
    ///
    /// <para>This seeder is now a CONTROL-PLANE WRITER: it creates a <c>platform.Plans</c> row that
    /// turns on every runtime-available entitlement and <c>platform.Tenants</c> rows that are Active
    /// against it. The other tests in this file call the internal
    /// <c>EnsureGovernedTenantsAsync</c> directly, which carries no environment guard at all — so
    /// until now nothing exercised the one refusal standing between that plan and a production
    /// database. The counts below are the whole point: not just "no logins", but explicitly no plan
    /// and no tenant.</para>
    /// </summary>
    [Fact]
    public async Task The_golden_seeder_writes_no_control_plane_row_under_production()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GoldenJourneySeed:Enabled"] = "true",
            ["GoldenJourneySeed:Password"] = "GoldenFirstRunSecret!1"
        }).Build();

        // Fully configured and explicitly enabled — Production must still refuse.
        await GoldenCommercialJourneySeeder.EnsureAsync(
            MinimalProvider(), config, new ProductionEnvironment());

        await using var ctx = _db.ContextFor(null);
        Assert.Equal(0, await ctx.BusinessUnits.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await ctx.Users.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await ctx.Set<Plan>().CountAsync());
        Assert.Equal(0, await ctx.Set<Tenant>().IgnoreQueryFilters().CountAsync());
    }

    /// <summary>
    /// …and the refusal above is not vacuous. The IDENTICAL configuration under Development gets
    /// past the environment guard and goes on to resolve the seeder's collaborators — which the
    /// deliberately minimal provider does not carry, so it throws. Only the environment differs
    /// between the two tests, so a guard that stopped being reached would show up here as a pass
    /// where a throw is expected rather than as a silently green refusal test.
    /// </summary>
    [Fact]
    public async Task Only_the_environment_stops_it_the_same_configuration_proceeds_in_development()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GoldenJourneySeed:Enabled"] = "true",
            ["GoldenJourneySeed:Password"] = "GoldenFirstRunSecret!1"
        }).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GoldenCommercialJourneySeeder.EnsureAsync(
                MinimalProvider(), config, new DevelopmentEnvironment()));
    }

    /// <summary>
    /// Logging and a context, and nothing else: enough for the environment guard and the password
    /// check, and deliberately short of what the seeder needs to write anything.
    /// </summary>
    private IServiceProvider MinimalProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.ContextFor(null));
        return services.BuildServiceProvider();
    }

    // ---------------------------------------------------------------- the other caller

    /// <summary>
    /// The governed-tenant seeding was lifted out of <see cref="DemoUserSeeder"/> so both seeders
    /// share ONE implementation — two divergent copies of a control-plane seeder is the class of
    /// defect this codebase keeps paying for. This asserts the demo tenant kept every property the
    /// lift was supposed to preserve.
    /// </summary>
    [Fact]
    public async Task The_demo_seeder_still_governs_its_own_business_unit()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.ContextFor(null));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Password"] = "FirstRunSecret!1",
            ["PlatformOwner:Password"] = "FirstRunSecret!1"
        }).Build();

        await DemoUserSeeder.EnsureAsync(services.BuildServiceProvider(), config, new DevelopmentEnvironment());

        var demo = await BusinessUnitAsync("CUSTOMER-POC");
        var tenant = await TenantForAsync(demo.Id);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.NotNull(tenant.PlanId);
        Assert.True((await CheckRfqAsync(demo.Id)).Allowed);

        await using var ctx = _db.ContextFor(null);
        Assert.Equal(1, await ctx.Set<Plan>().CountAsync(p => p.Code == DemoUserSeeder.DemoPlanCode));
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

    /// <summary>Captures every statement a context issues, so a test can assert on query shape.</summary>
    private sealed class SqlRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _statements = new();

        public IReadOnlyCollection<string> Statements => _statements;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _statements.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _statements.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

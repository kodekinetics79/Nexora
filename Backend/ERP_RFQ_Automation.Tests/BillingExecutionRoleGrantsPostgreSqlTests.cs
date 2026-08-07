using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification of the execution-role GRANTS that entitlement
/// enforcement and billing depend on. The portable (SQLite) suite structurally cannot
/// cover this: SQLite has neither roles nor column privileges, so every query passes
/// there regardless of what the production grants allow.
///
/// <para><b>The defect class.</b>
/// 20260805105320_HardenPlatformGrantsAndBillingImmutability narrowed nexora_tenant_app
/// and nexora_identity_app from table-level SELECT on platform."Tenants"/"Plans" to a
/// handful of columns. An EF query that materialises the whole <c>Tenant</c> entity asks
/// for all 42 of its columns and PostgreSQL answers 42501. In
/// <see cref="TenantAccessService"/> that exception lands in a CONTRACTED FAIL-OPEN, so it
/// does not surface as an error — it silently disables tenant-suspension enforcement and
/// every plan limit for that business unit, in production, while the whole test suite
/// stays green. These tests execute the real resolution under the real role, which is the
/// only place that failure is observable.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class BillingExecutionRoleGrantsPostgreSqlTests
{
    private const string TenantRole = "nexora_tenant_app";
    private const string PipelineRole = "nexora_pipeline_app";

    private const long BusinessUnitId = 947_101;
    private const long TenantId = 947_201;
    private const long PlanId = 947_301;
    private const long RateCardId = 947_401;

    private readonly PostgreSqlTestDatabase _database;

    public BillingExecutionRoleGrantsPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    // ------------------------------------------------- the defect, stated head-on

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Materialising_a_whole_tenant_entity_under_the_tenant_role_is_refused_by_the_column_grants()
    {
        await SeedAsync();
        try
        {
            await using var context = _database.TenantContextWithRls(BusinessUnitId);

            // This is the shape TenantAccessService used to have. It is not a hypothetical:
            // it ran on every authenticated request through TenantStatusGuardMiddleware.
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(t => t.PrimaryBusinessUnitId == BusinessUnitId));

            Assert.Equal("42501", failure.SqlState);
            Assert.True(TenantAccessService.IsInsufficientPrivilege(failure));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Reading_a_plan_name_under_the_tenant_role_is_refused_which_is_why_quota_messages_name_the_code()
    {
        await SeedAsync();
        try
        {
            await using var context = _database.TenantContextWithRls(BusinessUnitId);

            // Plans.Name is outside the grant. A quota denial that printed the plan's display
            // name would take the whole resolution down with it — and the fail-open would then
            // remove the very limit the message was describing.
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Set<Plan>().AsNoTracking()
                    .Where(p => p.Id == PlanId)
                    .Select(p => p.Name)
                    .FirstOrDefaultAsync());
            Assert.Equal("42501", failure.SqlState);

            // Code is granted, and is what PlanSnapshot carries.
            await using var reader = _database.TenantContextWithRls(BusinessUnitId);
            var code = await reader.Set<Plan>().AsNoTracking()
                .Where(p => p.Id == PlanId).Select(p => p.Code).FirstOrDefaultAsync();
            Assert.Equal("grants-plan", code);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ------------------------------------------- the fix: enforcement actually runs

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tenant_status_and_plan_limits_resolve_under_the_tenant_role_instead_of_failing_open()
    {
        await SeedAsync();
        try
        {
            await using var context = _database.TenantContextWithRls(BusinessUnitId);
            var access = await AccessService(context).GetAccessAsync(BusinessUnitId);

            // The fail-open snapshot is (bu, null, null, null). Anything else proves the
            // resolution genuinely reached the platform plane under a column-scoped role.
            Assert.True(access.HasTenant);
            Assert.Equal(TenantId, access.TenantId);
            Assert.Equal(TenantStatus.Suspended, access.Status);
            Assert.True(access.IsAccessDenied);
            Assert.NotNull(access.Plan);
            Assert.Equal("grants-plan", access.Plan!.Code);
            Assert.Equal(2, access.Plan.MaxSeats);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Plan_quotas_are_genuinely_enforced_under_the_tenant_role()
    {
        await SeedAsync(seatCount: 2, documentCount: 3);
        try
        {
            await using var context = _database.TenantContextWithRls(BusinessUnitId);
            var entitlements = new EntitlementService(AccessService(context), context);

            var seats = await entitlements.CheckSeatAvailabilityAsync(BusinessUnitId);
            Assert.False(seats.Allowed);
            Assert.Equal(2, seats.Limit);

            var docs = await entitlements.CheckDocumentQuotaAsync(BusinessUnitId);
            Assert.False(docs.Allowed);
            Assert.Equal(3, docs.Limit);
            Assert.Equal(3, docs.Current);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ------------------------- the pending grant: degradation now, floor once granted

    /// <summary>
    /// The three grant states the deployment can be in, proven in order. The MIDDLE one is
    /// what 20260807002456 actually ships — BillingMode and CreatedOn granted,
    /// BillingModeReason deliberately withheld — so it is the state that matters most, and
    /// the one a single combined probe would have broken: one 42501 on the withheld column
    /// would have latched the whole group dormant and left the floor off for plan-missing
    /// tenants, which is the case the grant was added for.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_capacity_floor_degrades_independently_for_each_of_the_three_grant_states()
    {
        // A plan-less tenant provisioned long ago (the floor's target) that is ALSO an
        // unrecorded exemption, so one row exercises both halves of the state.
        await SeedAsync(withPlan: false, createdOn: DateTime.UtcNow.AddDays(-120));
        await SetBillingModeAsync(TenantBillingMode.Internal, reason: null);
        try
        {
            // ---- state 1: neither granted -------------------------------------------
            await RevokeBillingTermColumnsAsync();
            await RevokeExemptionReasonColumnAsync();
            TenantAccessService.ResetPrivilegeProbes();

            await using (var none = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(none).GetAccessAsync(BusinessUnitId);

                // Degraded, NOT failed open: status still resolves, so suspension enforcement
                // keeps working. Only the floor is inactive, and every unknown answers "no limit".
                Assert.Equal(TenantStatus.Suspended, access.Status);
                Assert.Null(access.BillingMode);
                Assert.Null(access.CreatedOn);
                Assert.Null(access.ExemptionRecorded);
                Assert.False(access.CommercialConfigurationRequired);
                Assert.False(UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow));
            }

            // ---- state 2: the shipped grant — mode + age, but NOT the reason ---------
            await GrantBillingTermColumnsAsync();
            TenantAccessService.ResetPrivilegeProbes();

            await using (var shipped = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(shipped).GetAccessAsync(BusinessUnitId);

                // The withheld column no longer vetoes the granted ones.
                Assert.Equal(TenantBillingMode.Internal, access.BillingMode);
                Assert.NotNull(access.CreatedOn);
                Assert.Null(access.ExemptionRecorded);

                // The documented, bounded gap: an unrecorded exemption is NOT floored here,
                // because the tenant plane cannot see whether a reason exists. It stays
                // visible on the platform-plane board and in the billing run's log instead.
                Assert.False(access.CommercialConfigurationRequired);
                Assert.False(UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow));
            }

            // ...and the case the grant was actually added for DOES bind in this state.
            await SetBillingModeAsync(TenantBillingMode.Billable, reason: null);
            TenantAccessService.ResetPrivilegeProbes();

            await using (var planMissing = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(planMissing).GetAccessAsync(BusinessUnitId);
                Assert.Equal(TenantBillingMode.Billable, access.BillingMode);
                Assert.True(access.CommercialConfigurationRequired);
                Assert.True(UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow));
            }

            // ---- state 3: a deployment that also grants the reason -------------------
            await SetBillingModeAsync(TenantBillingMode.Internal, reason: null);
            await GrantExemptionReasonColumnAsync();
            TenantAccessService.ResetPrivilegeProbes();

            await using (var all = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(all).GetAccessAsync(BusinessUnitId);
                Assert.False(access.ExemptionRecorded); // resolved, and it is false
                Assert.True(access.CommercialConfigurationRequired);
                Assert.True(UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow));
            }

            // Writing the reason down clears it, with the same grant in place.
            await SetBillingModeAsync(TenantBillingMode.Internal,
                reason: "Support sandbox owned by platform ops; cost approved in FY26 planning.");
            TenantAccessService.ResetPrivilegeProbes();

            await using (var recorded = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(recorded).GetAccessAsync(BusinessUnitId);
                Assert.True(access.ExemptionRecorded);
                Assert.False(access.CommercialConfigurationRequired);
            }
        }
        finally
        {
            await RestoreMigrationGrantsAsync();
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_floor_binds_a_plan_less_tenant_under_the_grant_the_migration_ships()
    {
        await SeedAsync(withPlan: false, seatCount: UnplannedTenantAllowance.MaxSeats,
            createdOn: DateTime.UtcNow.AddDays(-120));
        try
        {
            // No test-local grant: this is the privilege surface the migrations leave behind.
            TenantAccessService.ResetPrivilegeProbes();

            await using var context = _database.TenantContextWithRls(BusinessUnitId);
            var entitlements = new EntitlementService(AccessService(context), context);
            var seats = await entitlements.CheckSeatAvailabilityAsync(BusinessUnitId);

            Assert.False(seats.Allowed);
            Assert.Equal(UnplannedTenantAllowance.MaxSeats, seats.Limit);
            Assert.Contains("No plan is assigned", seats.Reason);
        }
        finally
        {
            TenantAccessService.ResetPrivilegeProbes();
            await CleanupAsync();
        }
    }

    // ------------------------------------------------- the privilege latch (R9)

    /// <summary>
    /// The latch must expire. A refusal that outlives the condition that caused it turns one
    /// transient 42501 into a capacity floor that stays off until somebody restarts the
    /// process — and nothing in the product would say so.
    ///
    /// <para>This test deliberately never calls <c>ResetPrivilegeProbes</c> between the revoke
    /// and the re-grant. That call is what every other test in this file uses at a state
    /// transition, and leaning on it here would prove only that the reset works.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_grant_restored_without_a_restart_is_picked_up_once_the_refusal_expires()
    {
        await SeedAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        try
        {
            TenantAccessService.ResetPrivilegeProbes();
            await RevokeBillingTermColumnsAsync();

            await using (var refused = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(refused, clock).GetAccessAsync(BusinessUnitId);
                Assert.Null(access.BillingMode); // refused, and now remembered
            }

            // The grant comes back — a migration ran, or an operator fixed it by hand.
            await GrantBillingTermColumnsAsync();

            // Still inside the refusal window: the column is readable but not retried yet.
            await using (var stillLatched = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(stillLatched, clock).GetAccessAsync(BusinessUnitId);
                Assert.Null(access.BillingMode);
            }

            clock.Advance(TenantAccessService.PrivilegeRefusalTtl + TimeSpan.FromSeconds(1));

            await using (var recovered = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(recovered, clock).GetAccessAsync(BusinessUnitId);
                Assert.Equal(TenantBillingMode.Billable, access.BillingMode);
                Assert.NotNull(access.CreatedOn);
            }
        }
        finally
        {
            await RestoreMigrationGrantsAsync();
            await CleanupAsync();
        }
    }

    /// <summary>
    /// A refusal is evidence about ONE role's privileges, never about another's.
    ///
    /// <para>The tenant plane genuinely cannot read <c>BillingModeReason</c> and refuses every
    /// time, which is the shipped configuration. A process-wide latch would let that permanent,
    /// expected refusal also silence the pipeline role — which holds table-level SELECT and
    /// reads the column perfectly well — so the extraction worker would lose the capacity floor
    /// for unrecorded exemptions without anything having gone wrong.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_refusal_in_the_tenant_scope_does_not_disable_the_platform_scope()
    {
        // Provisioned long ago, so the capacity floor is past its grace window and the
        // difference between the two scopes shows up as a real enforcement difference.
        await SeedAsync(createdOn: DateTime.UtcNow.AddDays(-120));
        await SetBillingModeAsync(TenantBillingMode.Internal, reason: null);
        try
        {
            TenantAccessService.ResetPrivilegeProbes();

            // Tenant scope: BillingModeReason is not granted, so the exemption probe refuses
            // and the refusal is remembered for that scope.
            await using (var tenantScope = _database.TenantContextWithRls(BusinessUnitId))
            {
                var access = await AccessService(tenantScope).GetAccessAsync(BusinessUnitId);
                Assert.Equal(TenantBillingMode.Internal, access.BillingMode);
                Assert.Null(access.ExemptionRecorded);
            }

            // Platform scope, same process, same latch store: the column IS readable here, and
            // the tenant plane's refusal must not have spoken for it.
            await using (var platformScope = _database.ContextFor(null))
            {
                var access = await AccessService(platformScope).GetAccessAsync(BusinessUnitId);
                Assert.Equal(TenantBillingMode.Internal, access.BillingMode);
                Assert.False(access.ExemptionRecorded);          // resolved, and it is false
                Assert.True(access.CommercialConfigurationRequired);
                Assert.True(UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow));
            }
        }
        finally
        {
            TenantAccessService.ResetPrivilegeProbes();
            await CleanupAsync();
        }
    }

    // ------------------------------------- the platform plane runs as the pipeline role

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_revenue_readout_and_the_billing_run_succeed_under_the_pipeline_role()
    {
        await SeedAsync();
        try
        {
            // Every /api/platform route without a tenant scope, and every background worker
            // with no HttpContext, resolves to nexora_pipeline_app — which holds table-level
            // SELECT/INSERT/UPDATE on the platform schema. That is what lets the readout and
            // the billing run materialise whole Tenant/RateCard/BillingStatement entities.
            await using var context = await ContextAsRoleAsync(PipelineRole);
            try
            {
                var service = new BillingStatementService(
                    context, NullLogger<BillingStatementService>.Instance);

                var readout = await service.GetRevenueRiskAsync(includeArchived: true);
                var row = Assert.Single(readout, r => r.TenantId == TenantId);
                Assert.Equal("Billable", row.BillingMode);
                Assert.Equal(CommercialConfigurationStates.Complete, row.CommercialConfigurationState);

                // The compute path writes platform."BillingStatements" under the same role.
                var statement = await service.ComputeStatementAsync(
                    TenantId, BillingPeriod.Containing(DateTime.UtcNow));
                Assert.Equal(120.00m, statement.TotalAmount);
                Assert.Equal(BillingStatementStatus.Draft, statement.Status);
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync("RESET ROLE");
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Billing_tables_stay_unreadable_to_the_tenant_role_so_a_customer_request_can_never_reach_them()
    {
        await SeedAsync();
        try
        {
            await using var context = _database.TenantContextWithRls(BusinessUnitId);

            // No migration ever granted the tenant role anything on the billing tables. That is
            // the isolation those tables rely on — platform.BillingStatements carries no RLS
            // policy at all, so the grant IS the boundary.
            foreach (var query in new Func<Task>[]
                     {
                         () => context.Set<BillingStatement>().AsNoTracking().CountAsync(),
                         () => context.Set<RateCard>().AsNoTracking().CountAsync()
                     })
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(query);
                Assert.Equal("42501", failure.SqlState);
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // =================================================================== support

    private static TenantAccessService AccessService(
        ErpRfqAutomationContext context, TimeProvider? time = null)
        => new(context, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAccessService>.Instance, metrics: null, time);

    /// <summary>
    /// A clock the test moves by hand, so the privilege latch's expiry can be crossed without
    /// the suite sleeping for it — and, more importantly, without calling
    /// <c>ResetPrivilegeProbes</c>, which would test the reset instead of the recovery.
    /// </summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// A context whose connection is held open with <c>SET ROLE</c> applied, so EF's own
    /// queries run under that role. <c>TenantContextWithRls</c> cannot express this: its
    /// interceptor resolves the tenant role whenever a business unit is present and issues
    /// no role at all when one is not.
    /// </summary>
    private async Task<ErpRfqAutomationContext> ContextAsRoleAsync(string role)
    {
        var context = _database.ContextForConnectionString(_database.ConnectionString, null);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync($"SET ROLE {role}");
        return context;
    }

    private Task GrantBillingTermColumnsAsync()
        => ExecuteAsync("""
            GRANT SELECT ("BillingMode", "CreatedOn")
                ON TABLE platform."Tenants" TO nexora_tenant_app, nexora_identity_app;
            """);

    private Task RevokeBillingTermColumnsAsync()
        => ExecuteAsync("""
            REVOKE SELECT ("BillingMode", "CreatedOn")
                ON TABLE platform."Tenants" FROM nexora_tenant_app, nexora_identity_app;
            """);

    private Task GrantExemptionReasonColumnAsync()
        => ExecuteAsync("""
            GRANT SELECT ("BillingModeReason")
                ON TABLE platform."Tenants" TO nexora_tenant_app, nexora_identity_app;
            """);

    private Task RevokeExemptionReasonColumnAsync()
        => ExecuteAsync("""
            REVOKE SELECT ("BillingModeReason")
                ON TABLE platform."Tenants" FROM nexora_tenant_app, nexora_identity_app;
            """);

    /// <summary>
    /// Puts the privilege surface back exactly as the migrations leave it: BillingMode and
    /// CreatedOn granted (20260807002456), BillingModeReason withheld. Privileges are shared
    /// state in this container and PlatformControlPlaneHardeningPostgreSqlTests asserts that
    /// surface is EXACTLY what the migrations define, so a grant left behind here would be
    /// read there as a widened surface.
    /// </summary>
    private async Task RestoreMigrationGrantsAsync()
    {
        await GrantBillingTermColumnsAsync();
        await RevokeExemptionReasonColumnAsync();
        TenantAccessService.ResetPrivilegeProbes();
    }

    /// <summary>Rewrites the seeded tenant's commercial terms out-of-band, as a legacy row would carry them.</summary>
    private async Task SetBillingModeAsync(TenantBillingMode mode, string? reason)
    {
        await using var context = _database.ContextFor(null);
        var tenant = await context.Set<Tenant>().IgnoreQueryFilters().FirstAsync(t => t.Id == TenantId);
        tenant.BillingMode = mode;
        tenant.BillingModeReason = reason;
        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedAsync(
        bool withPlan = true, int seatCount = 0, int documentCount = 0, DateTime? createdOn = null)
    {
        await CleanupAsync();
        await using var context = _database.ContextFor(null);

        // The business unit is created once and left in place. Other tables acquire rows
        // that reference it on their own (an AI processing policy, queue state), so tearing
        // it down would mean chasing every dependent this suite never created.
        if (!await context.BusinessUnits.AnyAsync(b => b.Id == BusinessUnitId))
        {
            context.Add(new BusinessUnit
            {
                Id = BusinessUnitId,
                BusinessUnitCode = $"GR{BusinessUnitId}",
                BusinessUnitName = "Grants BU",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        if (!await context.Set<RateCard>().AnyAsync(c => c.Id == RateCardId))
        {
            context.Add(new RateCard
            {
                Id = RateCardId,
                Code = "grants-card",
                Currency = "USD",
                EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                CreatedBy = "tests",
                Lines =
                {
                    new RateCardLine
                    {
                        MeterKey = BillingMeterKeys.Documents,
                        IncludedQuantity = 0m,
                        UnitPrice = 1.50m,
                        Unit = "document"
                    }
                }
            });
            await context.SaveChangesAsync();
        }

        if (withPlan)
        {
            context.Add(new Plan
            {
                Id = PlanId,
                Code = "grants-plan",
                Name = "Grants Plan",
                MonthlyPriceUsd = 120.00m,
                MaxSeats = 2,
                MaxDocsPerMonth = 3
            });
            await context.SaveChangesAsync();
        }

        context.Add(new Tenant
        {
            Id = TenantId,
            Name = "Grants Tenant",
            Slug = $"grants-{TenantId}",
            // Suspended so the status assertion proves a REAL value came back rather than a
            // default that a fail-open snapshot would also produce.
            Status = TenantStatus.Suspended,
            PlanId = withPlan ? PlanId : null,
            PrimaryBusinessUnitId = BusinessUnitId,
            RateCardId = RateCardId,
            BillingMode = TenantBillingMode.Billable,
            CreatedBy = "tests",
            CreatedOn = createdOn ?? DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        for (var i = 0; i < seatCount; i++)
            context.Users.Add(new User
            {
                FirstName = "Seat",
                LastName = $"Holder{i}",
                Email = $"grants-seat-{Guid.NewGuid():N}@nexora.test",
                PasswordHash = "hash",
                ImageUrl = string.Empty,
                Buid = BusinessUnitId,
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });

        for (var i = 0; i < documentCount; i++)
            context.Set<ExtractionJob>().Add(new ExtractionJob
            {
                BusinessUnitId = BusinessUnitId,
                BatchId = Guid.NewGuid(),
                SourceType = ExtractionSourceType.ManualUpload,
                Status = ExtractionStatus.Succeeded,
                ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                StoragePath = "/uploads/grants.pdf",
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });

        if (seatCount > 0 || documentCount > 0)
            await context.SaveChangesAsync();
    }

    /// <summary>
    /// Removes only the rows these tests create, through EF so the column names come from the
    /// model rather than from a hand-written guess that drifts with the next rename.
    /// </summary>
    private async Task CleanupAsync()
    {
        await using var context = _database.ContextFor(null);
        await context.Set<BillingStatementLine>()
            .Where(l => context.Set<BillingStatement>().Any(s => s.Id == l.BillingStatementId && s.TenantId == TenantId))
            .ExecuteDeleteAsync();
        await context.Set<BillingStatement>().Where(s => s.TenantId == TenantId).ExecuteDeleteAsync();
        await context.Set<Tenant>().IgnoreQueryFilters().Where(t => t.Id == TenantId).ExecuteDeleteAsync();
        await context.Set<Plan>().Where(p => p.Id == PlanId).ExecuteDeleteAsync();
        await context.Set<RateCardLine>().Where(l => l.RateCardId == RateCardId).ExecuteDeleteAsync();
        await context.Set<RateCard>().Where(c => c.Id == RateCardId).ExecuteDeleteAsync();
        await context.Set<ExtractionJob>().IgnoreQueryFilters()
            .Where(j => j.BusinessUnitId == BusinessUnitId).ExecuteDeleteAsync();
        await context.Users.IgnoreQueryFilters().Where(u => u.Buid == BusinessUnitId).ExecuteDeleteAsync();
    }
}

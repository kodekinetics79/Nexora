using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certification for 20260812120000_ExecutionRoleGrantsForMasterDataAuditAndLegalHoldFence — two
/// grants for reads and writes the application already performs and the database already refused
/// with 42501.
///
/// <para>Structurally uncoverable by the portable suite: SQLite has neither roles nor column
/// privileges, so both statements below succeed there no matter what production allows. Both defects
/// therefore reached a running system — one killed the API at startup, the other 500'd a tenant
/// screen — with a green suite behind them.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ExecutionRoleGrantsForAuditAndLegalHoldPostgreSqlTests
{
    private const string TenantRole = "nexora_tenant_app";
    private const string PipelineRole = "nexora_pipeline_app";

    private const long BusinessUnitId = 981_501;
    private const long TenantId = 981_601;

    private readonly PostgreSqlTestDatabase _database;

    public ExecutionRoleGrantsForAuditAndLegalHoldPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    /// <summary>
    /// The golden-journey seeder's failure, reduced to its mechanism.
    ///
    /// <para><c>GoldenCommercialJourneySeeder</c> runs at startup with no HttpContext, so
    /// <c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c> selects <c>nexora_pipeline_app</c>. Its
    /// first Customer insert also inserts the FR-MDM-05 audit rows that
    /// <c>ErpRfqAutomationContext.SaveChanges</c> captures — and that role had no privilege on
    /// <c>MasterDataChangeEvents</c>, so the save raised
    /// <c>42501: permission denied for table MasterDataChangeEvents</c> as an unhandled exception
    /// during startup. That is <c>scripts/e2e/run-phase1-base-journey.sh</c> and the documented local
    /// E2E data path, dead on arrival.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_master_data_write_under_the_pipeline_role_can_append_its_own_audit()
    {
        await SeedBusinessUnitAsync();
        try
        {
            await using var context = await ContextAsRoleAsync(PipelineRole, BusinessUnitId);
            try
            {
                context.Customers.Add(new Customer
                {
                    Name = "Pipeline Audit Customer",
                    Buid = BusinessUnitId,
                    ImageUrl = string.Empty,
                    IsActive = true,
                    CreatedBy = "system:tests",
                    CreatedOn = DateTime.UtcNow
                });

                await context.SaveChangesAsync();

                // The audit is the point: a save that "succeeded" without writing one would mean the
                // interceptor had been made bypassable, which is the opposite of the fix.
                var audited = await context.Set<MasterData.MasterDataChangeEvent>().AsNoTracking()
                    .CountAsync(row => row.BusinessUnitId == BusinessUnitId
                                       && row.EntityType == "Customer" && row.ChangeType == "CREATED");
                Assert.Equal(1, audited);
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

    /// <summary>
    /// <c>GET /api/platform-governance/evidence-retention</c> returned 500 for any tenant that is a
    /// governed platform tenant.
    ///
    /// <para>The endpoint runs as <c>nexora_tenant_app</c>;
    /// <c>EvidenceRetentionService.TenantHoldBlocksAsync</c> must prove no legal hold is in force
    /// before reporting anything reclaimable, and the tenant role had no privilege on
    /// <c>platform."TenantLegalHolds"</c>. The Tenant row in the fixture is not decoration: the fence
    /// returns early when a business unit has none, so WITHOUT it the hold table is never read and
    /// this test passes against the unfixed database.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_legal_hold_fence_is_readable_by_the_tenant_role_for_a_governed_tenant()
    {
        await SeedBusinessUnitAsync();
        await SeedPlatformTenantAsync();
        try
        {
            await using var context = await ContextAsRoleAsync(TenantRole, BusinessUnitId);
            try
            {
                var platformTenantId = await TenantLegalHoldFence.ResolvePlatformTenantIdAsync(
                    context, BusinessUnitId, default);
                Assert.Equal(TenantId, platformTenantId);

                // Before the grant this threw PostgresException 42501.
                Assert.False(await TenantLegalHoldFence.HasActiveAsync(context, TenantId, default));
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

    /// <summary>
    /// The grant is two columns, not the table. The operator's written justification for a hold —
    /// Reason, Authority, EvidenceReference — must stay out of the tenant plane, so this asserts the
    /// refusal as deliberately as the previous test asserts the permission.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_tenant_role_still_cannot_read_why_a_hold_was_placed()
    {
        await SeedBusinessUnitAsync();
        await SeedPlatformTenantAsync();
        try
        {
            await using var context = await ContextAsRoleAsync(TenantRole, BusinessUnitId);
            try
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        "SELECT \"Reason\" FROM platform.\"TenantLegalHolds\" LIMIT 1"));
                Assert.Equal("42501", failure.SqlState);
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

    // ---------------------------------------------------------------- plumbing

    private async Task<ErpRfqAutomationContext> ContextAsRoleAsync(string role, long? businessUnitId)
    {
        var context = _database.ContextForConnectionString(_database.ConnectionString, businessUnitId);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync($"SET ROLE {role}");
        if (businessUnitId is { } tenant)
            await context.Database.ExecuteSqlRawAsync(
                $"SELECT set_config('nexora.business_unit_id', '{tenant}', false)");
        return context;
    }

    private async Task SeedBusinessUnitAsync()
    {
        await CleanupAsync();
        await using var context = _database.ContextFor(null);
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = BusinessUnitId,
            BusinessUnitCode = "GRANTS-FENCE",
            BusinessUnitName = "Execution Role Grants Test Unit",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedPlatformTenantAsync()
    {
        await using var context = _database.ContextFor(null);
        context.Set<Tenant>().Add(new Tenant
        {
            Id = TenantId,
            Name = "Execution Role Grants Tenant",
            Slug = "grants-fence",
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = BusinessUnitId,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Removes only this fixture's rows.
    ///
    /// <para><c>session_replication_role = 'replica'</c> for the duration — what
    /// <c>TenantPurgeExecutor</c> does, for the same reasons. The master-data audit this fixture
    /// deliberately provokes is append-only by trigger and is referenced by a foreign key back to
    /// the business unit; and inserting a Tenant seeds
    /// <c>platform."TenantMeterSourcePolicies"</c>, which holds a foreign key back to it. Suspending
    /// both classes of guard for this one session is the only way the fixture can undo itself.</para>
    /// </summary>
    private async Task CleanupAsync()
    {
        await using var context = _database.ContextFor(null);
        await context.Database.ExecuteSqlRawAsync(
            $"""
             SET session_replication_role = 'replica';
             DELETE FROM "MasterDataFieldChanges" WHERE "BusinessUnitId" = {BusinessUnitId};
             DELETE FROM "MasterDataChangeEvents" WHERE "BusinessUnitId" = {BusinessUnitId};
             DELETE FROM "Customers" WHERE "BUID" = {BusinessUnitId};
             DELETE FROM platform."TenantMeterSourcePolicies" WHERE "TenantId" = {TenantId};
             DELETE FROM platform."TenantLegalHolds" WHERE "TenantId" = {TenantId};
             DELETE FROM platform."Tenants" WHERE "Id" = {TenantId};
             DELETE FROM "BusinessUnits" WHERE "ID" = {BusinessUnitId};
             SET session_replication_role = 'origin';
             """);
    }
}

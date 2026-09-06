using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The grant half of 20260906130000_DeploymentDescribesItsOwnDatabase.
///
/// <para><b>The failure this exists to stop happening twice.</b> A new table in this database is
/// readable by NOBODY until somebody says so. Every table in the <c>platform</c> schema is granted
/// per table in <c>09_privileges.sql</c>, and the application serves under the least-privilege
/// runtime roles, never the owner. So a migration that creates a table and stops leaves a table
/// the application cannot read — and none of the portable SQLite lanes can see it, because SQLite
/// has no roles: they connect as everything. The whole unit suite passes and the first request in
/// a real deployment fails with 42501.</para>
///
/// <para>Asserted against a real PostgreSQL, through the real migration chain, as the real role.</para>
/// </summary>
public sealed class PlatformDataBoundarySettingsGrantPostgreSqlTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_control_plane_role_can_read_and_write_the_row_and_can_never_remove_it()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_data_boundary_grants")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(container.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        await context.GetService<IMigrator>().MigrateAsync();

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        await using var privileges = connection.CreateCommand();
        privileges.CommandText = """
            SELECT has_table_privilege('nexora_pipeline_app', 'platform."PlatformDataBoundarySettings"', 'SELECT')
               AND has_table_privilege('nexora_pipeline_app', 'platform."PlatformDataBoundarySettings"', 'INSERT')
               AND has_table_privilege('nexora_pipeline_app', 'platform."PlatformDataBoundarySettings"', 'UPDATE')
               -- Corrected, never withdrawn. A deleted singleton is indistinguishable from a
               -- deployment that has never said where its customers' data lives, and the residency
               -- control would start blocking every tenant with no record of why.
               AND NOT has_table_privilege('nexora_pipeline_app', 'platform."PlatformDataBoundarySettings"', 'DELETE')
               AND NOT has_table_privilege('nexora_pipeline_app', 'platform."PlatformDataBoundarySettings"', 'TRUNCATE')
               -- Nothing on a tenant or identity path has any business knowing which database the
               -- platform runs on. Unlike PlatformEmailSettings, which they read column by column
               -- because outbound mail is composed on their paths.
               AND NOT has_table_privilege('nexora_tenant_app', 'platform."PlatformDataBoundarySettings"', 'SELECT')
               AND NOT has_table_privilege('nexora_identity_app', 'platform."PlatformDataBoundarySettings"', 'SELECT');
            """;
        Assert.True((bool)(await privileges.ExecuteScalarAsync())!,
            "nexora_pipeline_app serves the control plane and needs SELECT/INSERT/UPDATE on the "
            + "deployment's data-boundary row, and must hold neither DELETE nor TRUNCATE. The tenant "
            + "and identity roles must not see it at all.");

        // The singleton rule is the database's, not the application's: two rows would be two
        // answers to where a customer's data lives, and the failure mode is the auditor reading
        // one while the probe measures against the other.
        await using var second = connection.CreateCommand();
        second.CommandText = """
            INSERT INTO platform."PlatformDataBoundarySettings"
                ("Id", "OpaqueProviderReference", "Region", "BackupPolicyReference", "BackupPolicyVersion",
                 "Basis", "Reason", "RecordedBy", "RecordedOn", "Version")
            VALUES (2, 'neon-second', 'us-east-1', 'pitr-7d', 1, 'entered', 'a second answer', 'x', now(), 1);
            """;
        var refused = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
        Assert.Equal("23514", refused.SqlState);
    }
}

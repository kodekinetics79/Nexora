using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Release02CommercialBackbonePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Latest_schema_has_tenant_policies_least_privilege_and_immutable_lineage()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" = '20260726105111_Release02SupplierQuoteCommercialBackbone') = 1,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                   AND tablename = ANY(ARRAY[
                       'Suppliers', 'commercial_demand_lines', 'sourcing_cases',
                       'sourcing_case_candidates', 'commercial_document_classifications'])) = 5,
                has_table_privilege('nexora_tenant_app', 'public.commercial_demand_lines', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_demand_lines', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.sourcing_cases', 'SELECT,INSERT,UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.sourcing_cases', 'DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_document_classifications', 'SELECT,INSERT,UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_document_classifications', 'DELETE'),
                (SELECT count(*) FROM pg_trigger
                 WHERE NOT tgisinternal AND tgname = ANY(ARRAY[
                     'commercial_demand_lines_immutable', 'sourcing_cases_lineage_immutable',
                     'commercial_document_classifications_source_immutable'])) = 3;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 6; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_backfills_safe_supplier_state_and_DemandLine_without_rewriting_history()
    {
        var databaseName = $"release02_upgrade_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = true
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var context = database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            const string previous = "20260726064437_ServerAuthoritativeRfqNumbers";
            const string current = "20260726105111_Release02SupplierQuoteCommercialBackbone";
            await migrator.MigrateAsync(previous);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (98201, 'R02-UP', 'Release 02 upgrade', 'qa', now());
                INSERT INTO "Suppliers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES (98202, 'Legacy Supplier', 'legacy-r02@example.test', 'n/a', 98201, true, 'qa', now());
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "CreatedBy", "CreatedDate", "BusinessUnitID", "NexoraSerial")
                VALUES (98203, 'R02-RFQ', now(), 'qa', now(), 98201, 'NXR-R02-98203');
                INSERT INTO "RFQItems"
                    ("ID", "RFQID", "Quantity", "CreatedBy", "CreatedDate")
                VALUES (98204, 98203, 2, 'qa', now());
                """);

            await migrator.MigrateAsync(current);
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM commercial_demand_lines
                WHERE "BusinessUnitId" = 98201 AND "RfqId" = 98203
                  AND "RfqItemId" = 98204 AND "NexoraSerial" = 'NXR-R02-98203'
                """).SingleAsync());
            Assert.True(await context.Database.SqlQueryRaw<bool>("""
                SELECT "ConcurrencyToken" IS NOT NULL
                   AND "EffectiveFrom" IS NOT NULL
                   AND "GovernanceStatus" = 'UNVERIFIED'
                   AND "ReadinessStatus" = 'REVIEW_REQUIRED' AS "Value"
                FROM "Suppliers" WHERE "ID" = 98202
                """).SingleAsync());
            var immutable = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE commercial_demand_lines SET "NexoraSerial" = 'CHANGED'
                    WHERE "RfqItemId" = 98204
                    """));
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, immutable.SqlState);
            Assert.Equal(2, await context.Database.SqlQueryRaw<int>($"""
                SELECT count(*)::int AS "Value" FROM "__EFMigrationsHistory"
                WHERE "MigrationId" IN ('{previous}', '{current}')
                """).SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

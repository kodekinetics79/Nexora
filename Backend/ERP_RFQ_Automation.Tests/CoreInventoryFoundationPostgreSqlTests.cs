using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CoreInventoryFoundationPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public CoreInventoryFoundationPostgreSqlTests(PostgreSqlTestDatabase database) =>
        _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_preserves_ambiguous_identity_and_grants_usage_only_on_sequences()
    {
        var databaseName = $"core_inventory_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = databaseName };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var context = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260725205156_CoreCommercialSalesFoundation");

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO public."BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (95801, 'INV-FOUND', 'Inventory foundation migration', 'tests', now());

                INSERT INTO public."Warehouses"
                    ("ID", "WarehouseCode", "WarehouseName", "BusinessUnitID", "CreatedBy", "CreatedOn")
                VALUES (95801, 'WH-FOUND', 'Foundation warehouse', 95801, 'tests', now());

                -- Raw part numbers remain unique, but the first two normalize to the
                -- same commercial identity and therefore require later human review.
                INSERT INTO public."Products"
                    ("ID", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "BUID", "CreatedBy", "CreatedOn")
                VALUES (95801, 'Ambiguous one', 'DUP-100', 0, 0, 95801, 'tests', now()),
                       (95802, 'Ambiguous two', 'DUP100', 0, 0, 95801, 'tests', now()),
                       (95803, 'Unique product', 'SAFE-200', 0, 0, 95801, 'tests', now());

                INSERT INTO public."Inventory"
                    ("Id", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "WarehouseId", "CreatedBy", "CreatedOn", "Buid")
                VALUES (95801, 'Ambiguous stock', 'DUP 100', 5, 0, 95801, 'tests', now(), 95801),
                       (95803, 'Unique stock', 'SAFE200', 7, 0, 95801, 'tests', now(), 95801);
                """);

            await migrator.MigrateAsync("20260725205811_CoreProductInventoryFoundation");

            var ambiguousProduct = await context.Database.SqlQueryRaw<long?>("""
                SELECT "ProductId" AS "Value" FROM public."Inventory" WHERE "Id" = 95801
                """).SingleAsync();
            var uniqueProduct = await context.Database.SqlQueryRaw<long?>("""
                SELECT "ProductId" AS "Value" FROM public."Inventory" WHERE "Id" = 95803
                """).SingleAsync();
            Assert.Null(ambiguousProduct);
            Assert.Equal(95803, uniqueProduct);

            var aliases = await context.Database.SqlQueryRaw<string>("""
                SELECT "NormalizedValue" AS "Value"
                FROM public.product_aliases
                WHERE "BusinessUnitId" = 95801
                ORDER BY "NormalizedValue"
                """).ToListAsync();
            Assert.Equal(["SAFE200"], aliases);

            var sequencePrivileges = await context.Database.SqlQueryRaw<SequencePrivilege>("""
                WITH governed(table_name) AS (
                    VALUES ('incoming_inventory'), ('inventory_movements'),
                           ('product_aliases'), ('product_supersessions')
                ), resolved AS (
                    SELECT table_name,
                           pg_get_serial_sequence(format('public.%I', table_name), 'Id') AS sequence_name
                    FROM governed
                )
                SELECT table_name AS "TableName", sequence_name AS "SequenceName",
                       has_sequence_privilege('nexora_tenant_app', sequence_name, 'USAGE') AS "Usage",
                       has_sequence_privilege('nexora_tenant_app', sequence_name, 'SELECT') AS "Select",
                       has_sequence_privilege('nexora_tenant_app', sequence_name, 'UPDATE') AS "Update"
                FROM resolved
                ORDER BY table_name
                """).ToListAsync();

            Assert.Equal(4, sequencePrivileges.Count);
            Assert.All(sequencePrivileges, privilege =>
            {
                Assert.True(privilege.Usage);
                Assert.False(privilege.Select);
                Assert.False(privilege.Update);
            });

            await migrator.MigrateAsync();
            var triggerDefinition = await context.Database.SqlQueryRaw<string>("""
                SELECT pg_get_functiondef('public.nexora_validate_commercial_line_resolution()'::regprocedure) AS "Value"
                """).SingleAsync();
            Assert.Contains("public.\"RFQ\"", triggerDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("public.\"RFQs\"", triggerDefinition, StringComparison.Ordinal);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO public."BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (95802, 'INV-OTHER', 'Other inventory tenant', 'tests', now());
                INSERT INTO public."Warehouses"
                    ("ID", "WarehouseCode", "WarehouseName", "BusinessUnitID", "CreatedBy", "CreatedOn")
                VALUES (95802, 'WH-OTHER', 'Other tenant warehouse', 95802, 'tests', now());
                """);
            var crossTenantWarehouse = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE public."Inventory" SET "WarehouseId" = 95802 WHERE "Id" = 95803
                    """));
            Assert.Equal("P0001", crossTenantWarehouse.SqlState);
            Assert.Contains("inventory warehouse must belong to the same tenant", crossTenantWarehouse.MessageText);
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

    private sealed record SequencePrivilege(
        string TableName, string SequenceName, bool Usage, bool Select, bool Update);
}

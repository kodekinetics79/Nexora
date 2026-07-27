using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Inventory.Commercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V1Gate02CommercialIntegrityPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Commercial_inventory_foreign_keys_are_tenant_qualified_and_reject_cross_tenant_rows()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantOne = 9_700_001L;
        var tenantTwo = 9_700_002L;
        var productOne = 9_710_001L;
        var inventoryOne = 9_720_001L;

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO public."BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES ({tenantOne}, 'G2A-{suffix}', 'Gate 2 tenant A', 'tests', now()),
                   ({tenantTwo}, 'G2B-{suffix}', 'Gate 2 tenant B', 'tests', now());
            INSERT INTO public."Products"
                ("ID", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "BUID", "CreatedBy", "CreatedOn")
            VALUES ({productOne}, 'Gate 2 product', 'G2P-{suffix}', 0, 0, {tenantOne}, 'tests', now());
            INSERT INTO public."Inventory"
                ("Id", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "Buid", "CreatedBy", "CreatedOn")
            VALUES ({inventoryOne}, 'Gate 2 inventory', 'G2I-{suffix}', 10, 0, {tenantOne}, 'tests', now());
            """);

        var aliasViolation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, $"""
            INSERT INTO public.product_aliases
                ("BusinessUnitId", "ProductId", "Kind", "Value", "NormalizedValue", "IsActive", "CreatedOn", "CreatedBy")
            VALUES ({tenantTwo}, {productOne}, 'ManufacturerPartNumber', 'BAD', 'BAD', true, now(), 'tests');
            """));
        Assert.Contains(aliasViolation.SqlState,
            new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RaiseException });

        await transaction.RollbackAsync();
        await using var secondTransaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, secondTransaction, $"""
            INSERT INTO public."BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES ({tenantOne}, 'G2A-{suffix}', 'Gate 2 tenant A', 'tests', now()),
                   ({tenantTwo}, 'G2B-{suffix}', 'Gate 2 tenant B', 'tests', now());
            INSERT INTO public."Inventory"
                ("Id", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "Buid", "CreatedBy", "CreatedOn")
            VALUES ({inventoryOne}, 'Gate 2 inventory', 'G2I-{suffix}', 10, 0, {tenantOne}, 'tests', now());
            """);
        var reservationViolation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            secondTransaction, $"""
                INSERT INTO public.stock_reservations
                    ("BusinessUnitId", "InventoryId", "Quantity", "Status", "IdempotencyKey", "CreatedBy", "CreatedOn", "Version")
                VALUES ({tenantTwo}, {inventoryOne}, 1, 'Active', 'cross-{suffix}', 'tests', now(), 1);
                """));
        Assert.Contains(reservationViolation.SqlState,
            new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RaiseException });
        await secondTransaction.RollbackAsync();

        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT
                (SELECT count(*) FROM pg_constraint
                 WHERE contype = 'f' AND conrelid = ANY(ARRAY[
                     'public.product_aliases'::regclass,
                     'public.product_supersessions'::regclass,
                     'public.inventory_movements'::regclass,
                     'public.incoming_inventory'::regclass,
                     'public.stock_reservations'::regclass,
                     'public.supplier_purchase_order_lines'::regclass,
                     'public."SupplierQuotedItems"'::regclass])
                   AND array_length(conkey, 1) = 2) >= 11,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                   AND tablename = ANY(ARRAY[
                       'product_aliases', 'product_supersessions', 'inventory_movements',
                       'incoming_inventory', 'stock_reservations'])) = 5;
            """;
        await using var reader = await schema.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Runtime_role_rls_hides_other_tenant_commercial_inventory_rows()
    {
        const long tenantOne = 9_927_101;
        const long tenantTwo = 9_927_102;
        await using var owner = await database.OpenConnectionAsync();
        await using (var seed = owner.CreateCommand())
        {
            seed.CommandText = $"""
                INSERT INTO public."BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES ({tenantOne}, 'G2-RLS-A', 'Gate 2 RLS A', 'tests', now()),
                       ({tenantTwo}, 'G2-RLS-B', 'Gate 2 RLS B', 'tests', now())
                ON CONFLICT ("ID") DO NOTHING;
                INSERT INTO public."Products"
                    ("ID", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "BUID", "CreatedBy", "CreatedOn")
                VALUES ({tenantOne}, 'Gate 2 RLS A', 'G2-RLS-A', 0, 0, {tenantOne}, 'tests', now()),
                       ({tenantTwo}, 'Gate 2 RLS B', 'G2-RLS-B', 0, 0, {tenantTwo}, 'tests', now())
                ON CONFLICT ("ID") DO NOTHING;
                INSERT INTO public.product_aliases
                    ("BusinessUnitId", "ProductId", "Kind", "Value", "NormalizedValue", "IsActive", "CreatedOn", "CreatedBy")
                SELECT v.tenant_id, v.tenant_id, 'ManufacturerPartNumber', v.alias, v.alias, true, now(), 'tests'
                FROM (VALUES ({tenantOne}, 'G2RLSA'), ({tenantTwo}, 'G2RLSB')) AS v(tenant_id, alias)
                WHERE NOT EXISTS (SELECT 1 FROM public.product_aliases a
                    WHERE a."BusinessUnitId" = v.tenant_id AND a."NormalizedValue" = v.alias);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using var tenantOneContext = database.TenantContextWithRls(tenantOne);
        var tenantOneRows = await tenantOneContext.Set<ProductAlias>().IgnoreQueryFilters().
            Where(x => x.BusinessUnitId == tenantOne || x.BusinessUnitId == tenantTwo).ToListAsync();
        Assert.Single(tenantOneRows);
        Assert.Equal(tenantOne, tenantOneRows[0].BusinessUnitId);

        await using var tenantTwoContext = database.TenantContextWithRls(tenantTwo);
        var tenantTwoRows = await tenantTwoContext.Set<ProductAlias>().IgnoreQueryFilters().
            Where(x => x.BusinessUnitId == tenantOne || x.BusinessUnitId == tenantTwo).ToListAsync();
        Assert.Single(tenantTwoRows);
        Assert.Equal(tenantTwo, tenantTwoRows[0].BusinessUnitId);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_downgrade_reupgrade_and_restored_clone_preserve_data()
    {
        const string previousMigration = "20260726205812_Release02ProcurementHandoffHardening";
        const string currentMigration = "20260727042452_V1Gate02CommercialIntelligenceIntegrity";
        var sourceName = $"gate2_source_{Guid.NewGuid():N}";
        var restoredName = $"gate2_restore_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" };
        var sourceBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = sourceName };
        var restoredBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = restoredName };

        await ExecuteAdminAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{sourceName}\"");
        try
        {
            await using (var source = database.ContextForConnectionString(sourceBuilder.ConnectionString, null))
            {
                var migrator = source.GetService<IMigrator>();
                await migrator.MigrateAsync(previousMigration);
                await source.Database.ExecuteSqlRawAsync("""
                    INSERT INTO public."BusinessUnits"
                        ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                    VALUES (9927201, 'G2-UP', 'Gate 2 populated upgrade', 'tests', now());
                    INSERT INTO public."Products"
                        ("ID", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "BUID", "CreatedBy", "CreatedOn")
                    VALUES (9927201, 'Gate 2 product', 'G2-UP-PART', 0, 0, 9927201, 'tests', now());
                    INSERT INTO public."Inventory"
                        ("Id", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "ProductId", "Buid", "CreatedBy", "CreatedOn")
                    VALUES (9927201, 'Gate 2 stock', 'G2-UP-PART', 12, 0, 9927201, 9927201, 'tests', now());
                    """);
            }

            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(adminBuilder.ConnectionString,
                $"CREATE DATABASE \"{restoredName}\" TEMPLATE \"{sourceName}\"");

            await using (var source = database.ContextForConnectionString(sourceBuilder.ConnectionString, null))
            {
                var migrator = source.GetService<IMigrator>();
                await migrator.MigrateAsync();
                Assert.Equal(12m, await source.Set<ERP_RFQ_Automation.Models.Inventory>()
                    .Where(x => x.Id == 9927201 && x.Buid == 9927201).Select(x => x.QtyOnHand).SingleAsync());
                Assert.Contains(currentMigration, await source.Database.SqlQueryRaw<string>("""
                    SELECT "MigrationId" AS "Value" FROM public."__EFMigrationsHistory"
                    """).ToListAsync());
                await migrator.MigrateAsync(previousMigration);
                Assert.Equal(12m, await source.Set<ERP_RFQ_Automation.Models.Inventory>().IgnoreQueryFilters()
                    .Where(x => x.Id == 9927201).Select(x => x.QtyOnHand).SingleAsync());
                Assert.DoesNotContain(currentMigration, await source.Database.SqlQueryRaw<string>("""
                    SELECT "MigrationId" AS "Value" FROM public."__EFMigrationsHistory"
                    """).ToListAsync());
                await migrator.MigrateAsync();
                Assert.Equal(12m, await source.Set<ERP_RFQ_Automation.Models.Inventory>()
                    .Where(x => x.Id == 9927201 && x.Buid == 9927201).Select(x => x.QtyOnHand).SingleAsync());
            }

            await using (var restored = database.ContextForConnectionString(restoredBuilder.ConnectionString, null))
            {
                await restored.GetService<IMigrator>().MigrateAsync();
                Assert.Equal(12m, await restored.Set<ERP_RFQ_Automation.Models.Inventory>()
                    .Where(x => x.Id == 9927201 && x.Buid == 9927201).Select(x => x.QtyOnHand).SingleAsync());
                Assert.Contains(currentMigration, await restored.Database.SqlQueryRaw<string>("""
                    SELECT "MigrationId" AS "Value" FROM public."__EFMigrationsHistory"
                    """).ToListAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(adminBuilder.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{restoredName}\" WITH (FORCE)");
            await ExecuteAdminAsync(adminBuilder.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{sourceName}\" WITH (FORCE)");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

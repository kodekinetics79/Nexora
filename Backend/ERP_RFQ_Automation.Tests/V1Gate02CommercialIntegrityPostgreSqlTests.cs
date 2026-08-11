using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Inventory.Commercial;
using Microsoft.EntityFrameworkCore;
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

    // DELETED BY THE SQUASH — Populated_upgrade_downgrade_reupgrade_and_restored_clone_preserve_data
    //
    // It created an isolated database at 20260726205812_Release02ProcurementHandoffHardening, wrote
    // a business unit, a product and an inventory row, cloned the database as a restored backup,
    // then walked up to 20260727042452_V1Gate02CommercialIntelligenceIntegrity, back down and up
    // again, asserting after every step that QtyOnHand was still 12 and that the Gate 2 id was
    // present or absent as expected.
    //
    // Every assertion in it was migration identity or data-survives-a-walk. Both were erased by
    // 20260811033109_SquashedSchemaBaseline, not weakened: there is one migration, so there is no
    // previous migration to walk to, and no database will ever perform this upgrade again — a new
    // database starts at the baseline and an existing one is stamped past it without running any
    // DDL at all.
    //
    // WHAT STILL COVERS IT
    //   * The Gate 2 schema itself — the tenant-qualified composite foreign keys and the five
    //     commercial-inventory RLS policies the migration installed — is asserted against the live
    //     catalogue by Commercial_inventory_foreign_keys_are_tenant_qualified_and_reject_cross_tenant_rows
    //     above, which also drives real cross-tenant INSERTs through them.
    //   * Runtime_role_rls_hides_other_tenant_commercial_inventory_rows above proves the policies
    //     actually hide rows under the tenant execution role.
    //   * The surviving walk — that the baseline's Down reverts exactly what its Up creates, and
    //     that Up replays onto the ground Down leaves — is
    //     SquashedBaselineMigrationPostgreSqlTests.Baseline_down_reverts_exactly_what_its_up_creates_and_up_replays_identically.
    //   * Row-level durability across a restore is a PostgreSQL property, not a Nexora one, and was
    //     never what this file was for.

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

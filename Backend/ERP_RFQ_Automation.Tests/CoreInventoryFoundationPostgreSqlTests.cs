using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — this file used to be Populated_upgrade_preserves_ambiguous_identity_and_grants_usage_only_on_sequences.
///
/// That test created a database at 20260725205156_CoreCommercialSalesFoundation, seeded two
/// products whose raw part numbers ("DUP-100" and "DUP100") normalise to the same commercial
/// identity plus one that does not, then upgraded to 20260725205811_CoreProductInventoryFoundation
/// and asserted six things. 20260811033109_SquashedSchemaBaseline removed the ability to stand a
/// database up at an earlier migration, so the six were triaged rather than dropped wholesale:
///
///   RETIRED (data migration, unreachable now)
///     * Inventory.ProductId left NULL for the ambiguous pair and linked for the unambiguous row.
///     * product_aliases seeded with only the unambiguous normalised value.
///     * The Contacts identity sequence reconciled past the highest imported ID.
///       All three could only ever act on rows written before the columns and the sequence
///       reconciliation existed. A new database starts at the baseline with those columns already
///       present and its sequences already correct; an existing database is stamped past the
///       baseline and ran them once, years of deploys ago. There is no state left in which they
///       can run, and asserting they "would have" worked is not a test of anything shipped.
///
///   KEPT, and asserted here directly against the live catalogue and live writes
///     * USAGE-only sequence privileges on the four governed inventory tables.
///     * The commercial line resolution trigger function naming public."RFQ" and not "RFQs" —
///       the typo that made the guard silently unreachable.
///     * The cross-tenant warehouse guard actually refusing a cross-tenant move.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CoreInventoryFoundationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Governed_inventory_sequences_grant_usage_only()
    {
        await using var context = database.ContextFor(null);

        // USAGE lets a tenant session draw the next id. SELECT would let it read the allocation
        // — how many rows another tenant has written — and UPDATE would let it reset the counter
        // and collide with rows it cannot see.
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
            Assert.NotNull(privilege.SequenceName);
            Assert.True(privilege.Usage, $"{privilege.TableName}: tenant role cannot draw an id.");
            Assert.False(privilege.Select, $"{privilege.TableName}: tenant role can read the sequence.");
            Assert.False(privilege.Update, $"{privilege.TableName}: tenant role can reset the sequence.");
        });
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Commercial_line_resolution_guard_names_the_table_that_exists()
    {
        await using var context = database.ContextFor(null);

        // The guard once referenced public."RFQs", a table that has never existed, which made
        // every lookup inside it raise 42P01 the moment it was reached — a validation that could
        // only ever fail open or fail loudly, never validate. Asserted on the deployed function
        // body rather than on migration source, so a later edit is caught wherever it happens.
        var triggerDefinition = await context.Database.SqlQueryRaw<string>("""
            SELECT pg_get_functiondef('public.nexora_validate_commercial_line_resolution()'::regprocedure) AS "Value"
            """).SingleAsync();
        Assert.Contains("public.\"RFQ\"", triggerDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("public.\"RFQs\"", triggerDefinition, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Inventory_cannot_be_moved_into_another_tenants_warehouse()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO public."BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (95801, 'INV-FOUND', 'Inventory foundation', 'tests', now()),
                       (95802, 'INV-OTHER', 'Other inventory tenant', 'tests', now());
                INSERT INTO public."Warehouses"
                    ("ID", "WarehouseCode", "WarehouseName", "BusinessUnitID", "CreatedBy", "CreatedOn")
                VALUES (95801, 'WH-FOUND', 'Foundation warehouse', 95801, 'tests', now()),
                       (95802, 'WH-OTHER', 'Other tenant warehouse', 95802, 'tests', now());
                INSERT INTO public."Products"
                    ("ID", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "BUID", "CreatedBy", "CreatedOn")
                VALUES (95803, 'Unique product', 'SAFE-200', 0, 0, 95801, 'tests', now());
                INSERT INTO public."Inventory"
                    ("Id", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "ProductId", "WarehouseId",
                     "CreatedBy", "CreatedOn", "Buid")
                VALUES (95803, 'Unique stock', 'SAFE200', 7, 0, 95803, 95801, 'tests', now(), 95801);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var crossTenant = connection.CreateCommand())
        {
            crossTenant.Transaction = transaction;
            crossTenant.CommandText = """
                UPDATE public."Inventory" SET "WarehouseId" = 95802 WHERE "Id" = 95803;
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossTenant.ExecuteNonQueryAsync());
            Assert.Equal("P0001", error.SqlState);
            Assert.Contains("inventory warehouse must belong to the same tenant", error.MessageText);
        }

        await transaction.RollbackAsync();
    }

    private sealed record SequencePrivilege(
        string TableName, string SequenceName, bool Usage, bool Select, bool Update);
}

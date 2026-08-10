using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 6 — FR-INV-04, PostgreSQL lane.
///
/// <para><b>Why these two tests are here and not in the portable suite.</b> The portable lane runs
/// SQLite with <c>PRAGMA ignore_check_constraints = ON</c>, so every <c>CHECK</c> this gate adds is
/// unenforced there: a row that violates it is written silently and the suite goes green. Whether
/// the constraints actually refuse anything can only be certified against the production dialect,
/// which is what runs below. The same invariants are ALSO enforced in
/// <c>StockLedgerService.SetStockLevelsAsync</c> and <c>ReorderAlertService</c>, and those refusals
/// are what <c>Gate6ReorderAlertTests</c> exercises — belt and braces, in the two lanes that can
/// each only see one of them.</para>
///
/// <para><b>These tests fail until the migration is authored, and that is the intended signal.</b>
/// <c>inventory_reorder_alerts</c> and the two <c>Inventory</c> columns exist in the EF model only;
/// the portable lane builds its schema from the model with <c>EnsureCreated()</c> so they exist
/// there, while this lane runs <c>Migrate()</c> and will raise <c>42P01</c> (undefined table) or
/// <c>42703</c> (undefined column) until the consolidated migration lands. That is exactly the
/// failure <c>PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase</c>
/// exists to make loud, and it must not be silenced by weakening these assertions.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Gate6ReorderAlertPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 96_401;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_maximum_stock_level_below_the_minimum_is_refused_by_the_database()
    {
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        await using var connection = await database.OpenConnectionAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO public."Inventory"
                -- "Buid", not "BUID": Inventory declares the property with no HasColumnName
                -- override, and PostgreSQL quoted identifiers are case-sensitive, so the upper-case
                -- spelling is an undefined column (42703) and the row never reaches the CHECK.
                ("PartNo", "QtyOnHand", "ReorderPoint", "CreatedBy", "CreatedOn", "Buid",
                 "MinimumLevel", "MaximumLevel")
            VALUES ('CK-LEVELS', 0, 0, 'qa', now(), 96401, 40, 10)
            """;

        // No quantity satisfies both, so the row would be reported short AND overstocked forever.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("23514", refusal.SqlState);
        Assert.Contains("CK_Inventory_StockLevels", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_reorder_alert_acknowledged_with_no_name_or_reason_is_refused_by_the_database()
    {
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        await using var connection = await database.OpenConnectionAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO public.inventory_reorder_alerts
                ("BusinessUnitId", "InventoryId", "ProductId", "WarehouseId", "Kind", "Status",
                 "Severity", "OnHandQuantity", "AvailableQuantity", "IncomingQuantity",
                 "ProjectedQuantity", "ThresholdQuantity", "ShortfallQuantity", "RaisedOn",
                 "NotifiedCount", "AcknowledgedOn", "CreatedOn", "Version")
            VALUES (96401, 1, 1, 1, 'BELOW_MINIMUM', 'ACKNOWLEDGED', 'critical',
                    0, 0, 0, 0, 10, 10, now(), 0, now(), now(), 1)
            """;

        // A timestamp with no name and no reason behind it is a mute button, not an acknowledgement.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("23514", refusal.SqlState);
        Assert.Contains("CK_inventory_reorder_alerts_Acknowledgement", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_tenant_role_can_read_and_write_the_reorder_alert_ledger()
    {
        // The defect that shipped three times in one gate: a row-level-security policy with no
        // GRANT. PostgreSQL checks the grant FIRST and raises 42501 before evaluating any row
        // predicate, so the table is not "more isolated", it is unreadable — and a test that only
        // asserts a policy exists passes anyway. This asserts the grant.
        await using var connection = await database.OpenConnectionAsync();
        await using var privileges = connection.CreateCommand();
        privileges.CommandText = """
            SELECT has_table_privilege('nexora_tenant_app', 'public.inventory_reorder_alerts',
                                       'SELECT,INSERT,UPDATE')
            """;
        Assert.True((bool)(await privileges.ExecuteScalarAsync())!);
    }
}

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
/// A DATA-BEARING upgrade test for 20260812155257_Gate2SupplierQuoteWarrantyMonths.
///
/// <para><b>Why this migration needs one.</b> It does not merely add a nullable column. It DROPS
/// <c>CK_supplier_quote_lines_Values</c> and re-creates it, on a table that in any real estate
/// already holds every supplier offer the business has ever received. Between the DROP and the ADD
/// the table is unguarded, and the ADD re-states six clauses by hand — <c>LineNumber</c>,
/// <c>Quantity</c>, <c>UnitPrice</c>, <c>AvailableQuantity</c>, <c>MinimumOrderQuantity</c>,
/// <c>LeadTimeDays</c> — none of which the migration engine checks against what was there before.
/// A single typo in that re-statement silently retires a guard on the commercial record, and an
/// empty-database migration test cannot see it, because on an empty table
/// <c>ADD CONSTRAINT ... CHECK</c> validates nothing and every clause looks fine.</para>
///
/// <para>So the assertions are: rows written BEFORE the migration are still there afterwards, with
/// <c>WarrantyMonths</c> NULL and their other values untouched (NULL, never a backfilled zero —
/// zero asserts the supplier offered no warranty, which is a claim nobody made); the new bound
/// refuses 601 and refuses -1 while accepting the endpoints 0 and 600; and the pre-existing clauses
/// still bite after the rebuild, proved by inserts the ORIGINAL constraint would have refused.</para>
///
/// <para><b>The constraint bodies were compared, clause by clause, before this was written.</b> The
/// original lives in <c>MigrationsBaseline/Sql/03_tables_and_sequences.sql</c>; the rebuilt one in
/// the migration. They differ only by the added <c>WarrantyMonths</c> disjunction — the six original
/// clauses are identical in operator, operand and null-handling. Had they differed by anything else
/// this file would not exist and the divergence would have been reported instead.</para>
///
/// <para><b>Dedicated container, not the shared fixture.</b> The shared fixture is migrated to head
/// in <c>InitializeAsync</c>, so the pre-migration state this test needs does not exist there and
/// cannot be recovered without migrating the whole collection backwards.</para>
/// </summary>
public sealed class Gate2SupplierQuoteWarrantyMonthsUpgradePostgreSqlTests
{
    /// <summary>The last migration BEFORE the one under test.</summary>
    private const string PriorMigration = "20260812150000_CompleteTypedPlanEntitlements";

    private const string TargetMigration = "20260812155257_Gate2SupplierQuoteWarrantyMonths";

    private const string Constraint = "CK_supplier_quote_lines_Values";

    private const long Tenant = 96_100;
    private const long RevisionId = 96_110;
    private const long RfqItemId = 96_120;
    private const long DemandLineId = 96_130;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Warranty_months_lands_on_a_populated_table_without_disarming_the_row_guard()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_warranty_upgrade")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(container.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .EnableDetailedErrors()
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        var migrator = context.GetService<IMigrator>();

        // ---------------------------------------------------------------- before
        await migrator.MigrateAsync(PriorMigration);

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        Assert.False(await ColumnExistsAsync(connection),
            "WarrantyMonths already existed before the migration under test ran.");

        await SeedLineageAsync(connection);
        // Two offers captured before anybody had heard of a numeric warranty. One carries the free
        // text the column does NOT replace; one carries none at all.
        await ExecuteAsync(connection, InsertLine(
            id: 96_201, lineNumber: 1, quantity: "12", unitPrice: "1450.500000",
            leadTimeDays: "30", warranty: "'24 months on parts, 12 on labour'"));
        await ExecuteAsync(connection, InsertLine(
            id: 96_202, lineNumber: 2, quantity: "4", unitPrice: "0",
            leadTimeDays: "NULL", warranty: "NULL"));

        // ---------------------------------------------------------------- the upgrade
        await migrator.MigrateAsync(TargetMigration);

        // ---------------------------------------------------------------- existing rows survive
        Assert.True(await ColumnExistsAsync(connection));

        await using (var survivors = new NpgsqlCommand("""
            SELECT "Id", "LineNumber", "Quantity", "UnitPrice", "LeadTimeDays", "Warranty",
                   "WarrantyMonths"
            FROM supplier_quote_lines ORDER BY "LineNumber";
            """, connection))
        await using (var reader = await survivors.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(96_201L, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(12m, reader.GetDecimal(2));
            Assert.Equal(1450.5m, reader.GetDecimal(3));
            Assert.Equal(30, reader.GetInt32(4));
            Assert.Equal("24 months on parts, 12 on labour", reader.GetString(5));
            // NULL means "nobody captured this". A backfilled 0 would assert that this supplier
            // offered no warranty at all, about every historical offer in the estate.
            Assert.True(await reader.IsDBNullAsync(6));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(96_202L, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.True(await reader.IsDBNullAsync(4));
            Assert.True(await reader.IsDBNullAsync(5));
            Assert.True(await reader.IsDBNullAsync(6));

            Assert.False(await reader.ReadAsync());
        }

        // ---------------------------------------------------------------- the new bound bites
        // 601 months is the mistyped year ("2026" scaled down) the bound exists to refuse; without
        // it the longest warranty in the set silently dominates the ranking.
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_301, lineNumber: 11, warrantyMonths: "601"));
        // A negative warranty is not a shorter warranty; it is a typo that would score first.
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_302, lineNumber: 12, warrantyMonths: "-1"));

        // Both endpoints are inside. 0 is a real and different claim — "no warranty offered" — and
        // must be storable by an operator who means it.
        await ExecuteAsync(connection, InsertLine(id: 96_303, lineNumber: 13, warrantyMonths: "0"));
        await ExecuteAsync(connection, InsertLine(id: 96_304, lineNumber: 14, warrantyMonths: "600"));

        // ---------------------------------------------------------------- and the OLD clauses still bite
        // Each of these was refused by the constraint the migration dropped. The rebuild re-states
        // them by hand, so each is re-proved rather than assumed.
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_401, lineNumber: 21, quantity: "0"));                       // Quantity > 0
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_402, lineNumber: 22, leadTimeDays: "-1"));                  // LeadTimeDays >= 0
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_403, lineNumber: 23, unitPrice: "-0.000001"));              // UnitPrice >= 0
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_404, lineNumber: 0));                                       // LineNumber > 0
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_405, lineNumber: 25, availableQuantity: "-1"));             // AvailableQuantity >= 0
        await AssertRefusedAsync(connection, InsertLine(
            id: 96_406, lineNumber: 26, minimumOrderQuantity: "0"));           // MinimumOrderQuantity > 0

        // …while the nullable clauses still ACCEPT null, which is what "IS NULL OR" is for.
        await ExecuteAsync(connection, InsertLine(id: 96_407, lineNumber: 27,
            leadTimeDays: "NULL", availableQuantity: "NULL", minimumOrderQuantity: "NULL"));

        // Nothing was quietly dropped instead of rebuilt: exactly one constraint of this name, and
        // it names every column it is supposed to guard.
        var definition = await ScalarAsync<string>(connection, $"""
            SELECT pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            WHERE con.conname = '{Constraint}' AND c.relname = 'supplier_quote_lines';
            """);
        foreach (var column in new[]
                 {
                     "LineNumber", "Quantity", "UnitPrice", "AvailableQuantity",
                     "MinimumOrderQuantity", "LeadTimeDays", "WarrantyMonths"
                 })
            Assert.Contains(column, definition, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<bool> ColumnExistsAsync(NpgsqlConnection connection)
        => ScalarAsync<bool>(connection, """
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'public' AND table_name = 'supplier_quote_lines'
                             AND column_name = 'WarrantyMonths');
            """);

    /// <summary>
    /// One INSERT, parameterised only in the columns any assertion varies. Everything else is a
    /// legal value, so a refusal below can only be the clause the caller changed.
    /// </summary>
    private static string InsertLine(
        long id,
        int lineNumber,
        string quantity = "5",
        string unitPrice = "100.000000",
        string leadTimeDays = "14",
        string availableQuantity = "5",
        string minimumOrderQuantity = "1",
        string warranty = "NULL",
        string? warrantyMonths = null)
    {
        var warrantyMonthsColumn = warrantyMonths is null ? string.Empty : ", \"WarrantyMonths\"";
        var warrantyMonthsValue = warrantyMonths is null ? string.Empty : $", {warrantyMonths}";
        return $"""
            INSERT INTO supplier_quote_lines
                ("Id", "BusinessUnitId", "SupplierQuoteRevisionId", "LineNumber", "RfqItemId",
                 "CommercialDemandLineId", "Description", "Quantity", "AvailableQuantity",
                 "UnitOfMeasure", "UnitPrice", "MinimumOrderQuantity", "LeadTimeDays", "Warranty",
                 "IsAlternate"{warrantyMonthsColumn})
            VALUES ({id}, {Tenant}, {RevisionId}, {lineNumber}, {RfqItemId}, {DemandLineId},
                    'Pre-existing offer line', {quantity}, {availableQuantity}, 'EA', {unitPrice},
                    {minimumOrderQuantity}, {leadTimeDays}, {warranty}, false{warrantyMonthsValue});
            """;
    }

    private static async Task AssertRefusedAsync(NpgsqlConnection connection, string sql)
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.Equal("23514", failure.SqlState); // check_violation
        Assert.Equal(Constraint, failure.ConstraintName);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The minimum lineage a supplier quote line is allowed to exist behind: business unit →
    /// currency + supplier + RFQ → RFQ item → demand line → sourcing case → solicitation → quote →
    /// revision. Every foreign key on the table is real and none of it is stubbed out.
    /// </summary>
    private static async Task SeedLineageAsync(NpgsqlConnection connection)
        => await ExecuteAsync(connection, $"""
            INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy")
            VALUES ({Tenant}, 'WARRANTY-UPG', 'Warranty upgrade tenant', 'tests');

            INSERT INTO "Currency" ("ID", "Code", "CurrencyName", "BusinessUnitID", "CreatedBy", "CreatedOn")
            VALUES ({Tenant}, 'SAR', 'Saudi Riyal', {Tenant}, 'tests', now());

            INSERT INTO "Suppliers" ("ID", "Name", "ImageURL", "BUID", "CreatedBy", "CreatedOn")
            VALUES ({Tenant}, 'Warranty Upgrade Supplier', '', {Tenant}, 'tests', now());

            INSERT INTO "RFQ" ("ID", "RFQNo", "RecDate", "CreatedBy", "BusinessUnitID")
            VALUES ({Tenant}, 'RFQ-WARRANTY-UPG', now(), 'tests', {Tenant});

            INSERT INTO "RFQItems" ("ID", "RFQID", "Quantity", "CreatedBy")
            VALUES ({RfqItemId}, {Tenant}, 12, 'tests');

            INSERT INTO commercial_demand_lines
                ("Id", "BusinessUnitId", "RfqId", "RfqItemId", "NexoraSerial", "IdentityKey",
                 "CreatedOn", "CreatedBy")
            VALUES ({DemandLineId}, {Tenant}, {Tenant}, {RfqItemId}, 'NX-WARRANTY-UPG',
                    'warranty-upgrade-line', now(), 'tests');

            INSERT INTO sourcing_cases
                ("Id", "BusinessUnitId", "CommercialDemandLineId", "RfqId", "RfqItemId",
                 "NexoraSerial", "Description", "RequestedQuantity", "StockQuantity",
                 "UnfulfilledQuantity", "SearchLimit", "Priority", "Status", "NextAction",
                 "ShortageDecisionKey", "IdempotencyKey", "RequestHash", "CreatedOn", "CreatedBy",
                 "UpdatedOn", "UpdatedBy")
            VALUES ({Tenant}, {Tenant}, {DemandLineId}, {Tenant}, {RfqItemId}, 'NX-WARRANTY-UPG',
                    'Warranty upgrade sourcing case', 12, 0, 12, 10, 'NORMAL', 'DRAFT',
                    'Await supplier offers', 'warranty-upgrade-shortage', 'warranty-upgrade-case',
                    repeat('a', 64), now(), 'tests', now(), 'tests');

            INSERT INTO "SupplierSolicitations"
                ("Id", "BusinessUnitId", "RfqId", "SupplierId", "Status", "SentOn", "Channel",
                 "CommercialDemandLineId", "SourcingCaseId")
            VALUES ({Tenant}, {Tenant}, {Tenant}, {Tenant}, 'Sent', now(), 'Email',
                    {DemandLineId}, {Tenant});

            INSERT INTO supplier_quotes
                ("Id", "BusinessUnitId", "SupplierId", "SupplierSolicitationId", "SourcingCaseId",
                 "RfqId", "NexoraSerial", "SupplierQuoteReference", "CurrentRevisionNumber",
                 "InboxStatus", "Version", "CreatedOn", "CreatedBy", "UpdatedOn", "UpdatedBy")
            VALUES ({Tenant}, {Tenant}, {Tenant}, {Tenant}, {Tenant}, {Tenant}, 'NX-WARRANTY-UPG',
                    'SQ-WARRANTY-UPG', 1, 'READY_FOR_COMPARISON', 1, now(), 'tests', now(), 'tests');

            INSERT INTO supplier_quote_revisions
                ("Id", "BusinessUnitId", "SupplierQuoteId", "RevisionNumber", "CaptureChannel",
                 "SourceIdentity", "SourceSha256", "CurrencyId", "FreightAmount", "TaxAmount",
                 "RequiresReview", "IdempotencyKey", "RequestHash", "CapturedOn", "CapturedBy",
                 "CorrelationId")
            VALUES ({RevisionId}, {Tenant}, {Tenant}, 1, 'Manual', 'warranty-upgrade',
                    repeat('b', 64), {Tenant}, 0, 0, false, 'warranty-upgrade-revision',
                    repeat('c', 64), now(), 'tests', 'warranty-upgrade-correlation');
            """);
}

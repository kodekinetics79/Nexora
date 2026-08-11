using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — this file used to be
/// Upgrade_backfills_shipments_from_their_order_and_supplier_orders_from_their_RFQ.
///
/// It seeded a fully migrated database, migrated 20260809175352_Gate3CaseContinuityAndLeadOutcome
/// back DOWN so the two continuity columns disappeared, then migrated up again so the assertion sat
/// on the migration's own SQL running against real rows: a shipment recovered its case from its
/// order, a customer-demand purchase order recovered its case from its RFQ, a STOCK purchase order
/// was correctly left blank (replenishment has no case, and a reference there would be an
/// invention), and a shipment whose order never came from a lead stayed an honest gap.
///
/// 20260811033109_SquashedSchemaBaseline erased that id, so there is no longer a migration to walk
/// down to. The BACKFILL itself is retired: it repaired documents written before the columns
/// existed, and no database can be in that state again.
///
/// WHAT REPLACED IT
///   * Creation-time population — the forward half the backfill's own summary called "fixing the
///     future" — is CommercialCaseContinuityTests, which drives Shipment.InheritCommercialIdentity
///     and SupplierPurchaseOrder.InheritCommercialIdentity directly, including the cross-tenant
///     refusal.
///   * What no other test covers, and what is asserted below, is the DATABASE half: the two
///     continuity columns are NULLABLE, so an unresolvable document stays an honest gap instead of
///     being forced to invent a case; and they are backed by TENANT-QUALIFIED foreign keys, so a
///     document cannot be stamped with another tenant's case even from raw SQL with the guard
///     objects bypassed. The backfill's most important property was that it refused to fabricate a
///     reference; this is the constraint that makes fabrication impossible for everyone else too.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Gate3CaseContinuityBackfillPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantA = 96_951;
    private const long TenantB = 96_952;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Case_continuity_columns_are_optional_and_cannot_cite_another_tenants_case()
    {
        await using var connection = await database.OpenConnectionAsync();

        // Nullable on purpose. A NOT NULL column here would have forced the backfill — and forces
        // every writer since — to put SOMETHING in it, which is how invented lineage gets created.
        await using (var nullable = connection.CreateCommand())
        {
            nullable.CommandText = """
                SELECT count(*)::int FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND ((table_name = 'Shipments' AND column_name IN ('CommercialCaseId', 'NexoraSerial'))
                    OR (table_name = 'supplier_purchase_orders' AND column_name IN ('CommercialCaseId', 'NexoraSerial')))
                  AND is_nullable = 'YES';
                """;
            Assert.Equal(4, Convert.ToInt32(await nullable.ExecuteScalarAsync()));
        }

        // Both references carry the tenant in the key. A single-column FK to "CommercialCases"
        // would satisfy PostgreSQL while pointing a shipment at another tenant's case.
        await using (var tenantQualified = connection.CreateCommand())
        {
            tenantQualified.CommandText = """
                SELECT count(*)::int FROM pg_constraint
                WHERE contype = 'f'
                  AND confrelid = 'public."CommercialCases"'::regclass
                  AND conrelid IN ('public."Shipments"'::regclass, 'public.supplier_purchase_orders'::regclass)
                  AND array_length(conkey, 1) = 2;
                """;
            Assert.Equal(2, Convert.ToInt32(await tenantQualified.ExecuteScalarAsync()));
        }

        await using var transaction = await connection.BeginTransactionAsync();
        var foreignCaseId = await SeedAsync(connection, transaction);

        // The stamp that the backfill would have made, made by hand and pointed at the wrong
        // tenant. It is refused by the FOREIGN KEY itself — not by an application trigger a bulk
        // repair could switch off — which is why the tenant has to be IN the key.
        await using (var crossTenant = connection.CreateCommand())
        {
            crossTenant.Transaction = transaction;
            crossTenant.CommandText = $"""
                UPDATE public."Shipments" SET "CommercialCaseId" = {foreignCaseId} WHERE "ID" = 96959;
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Two tenants, each with a lead — the commercial case is allocated by trigger on the lead —
    /// and one order and shipment in tenant A. Returns tenant B's case id.
    /// </summary>
    private static async Task<long> SeedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO public."BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES ({TenantA}, 'G3-CC-A', 'Case continuity A', 'qa', now()),
                       ({TenantB}, 'G3-CC-B', 'Case continuity B', 'qa', now());
                INSERT INTO public."Customers"
                    ("ID", "Name", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn",
                     "ConcurrencyToken")
                VALUES (96953, 'Case continuity customer', '', {TenantA}, true, 'qa', now(),
                        gen_random_uuid());
                INSERT INTO public."Setup_Master"
                    ("SetupID", "SetupType", "SetupCode", "SetupValue", "BusinessUnitID", "IsActive",
                     "CreatedBy", "CreatedOn")
                VALUES (96954, 'OrderStatus', 'OPEN', 'OPEN', {TenantA}, true, 'qa', now()),
                       (96955, 'ShipmentStatus', 'READY', 'READY', {TenantA}, true, 'qa', now());
                INSERT INTO public."Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID")
                VALUES (96956, 'RFQ-CONTINUITY-A', now(), 'Tests', 'qa', now(), {TenantA}),
                       (96957, 'RFQ-CONTINUITY-B', now(), 'Tests', 'qa', now(), {TenantB});
                INSERT INTO public."Orders"
                    ("ID", "OrderNo", "CustomerID", "BusinessUnitID", "StatusID", "SourceType",
                     "TotalAmount", "PaidAmount", "OrderDate", "CreatedBy", "CreatedOn")
                VALUES (96958, 'SO-CONTINUITY', 96953, {TenantA}, 96954, 'MANUAL',
                        40, 0, now(), 'qa', now());
                INSERT INTO public."Shipments"
                    ("ID", "ShipmentNo", "OrderID", "BusinessUnitID", "StatusID", "ShipmentDate",
                     "CreatedBy", "CreatedOn")
                VALUES (96959, 'SH-CONTINUITY', 96958, {TenantA}, 96955, now(), 'qa', now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using var foreignCase = connection.CreateCommand();
        foreignCase.Transaction = transaction;
        foreignCase.CommandText = """SELECT "CommercialCaseId" FROM public."Leads" WHERE "ID" = 96957;""";
        var value = await foreignCase.ExecuteScalarAsync();
        Assert.NotNull(value);
        Assert.IsNotType<DBNull>(value);
        return Convert.ToInt64(value);
    }
}

using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
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
        // Squash note: dropped the leading id check for
        // '20260726105111_Release02SupplierQuoteCommercialBackbone'.
        // 20260811033109_SquashedSchemaBaseline erased that id. The policies, grants and
        // immutability triggers it installed are asserted against pg_catalog below.
        command.CommandText = """
            SELECT
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
        for (var index = 0; index < 5; index++)
            Assert.True(reader.GetBoolean(index), $"Commercial backbone schema assertion {index + 1} failed.");
    }

    /// <summary>
    /// SQUASH NOTE — this replaces Populated_upgrade_backfills_safe_supplier_state_and_DemandLine_without_rewriting_history.
    ///
    /// That test built a database at 20260726064437_ServerAuthoritativeRfqNumbers, inserted a legacy
    /// supplier and RFQ line, upgraded to 20260726105111_Release02SupplierQuoteCommercialBackbone
    /// and asserted three things: the demand line was backfilled from the RFQ item, the legacy
    /// supplier landed on FAIL-CLOSED governance state rather than an approved one, and the demand
    /// line was immutable afterwards. Then it counted two migration ids.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased the ids and the backfill alike. The BACKFILL is
    /// genuinely retired — it could only ever run against rows written before the columns existed,
    /// and no database can be in that state again — but the two properties the backfill existed to
    /// establish are not retired at all, and are asserted here directly and, for the supplier, more
    /// strongly than before:
    ///
    ///   * A supplier written today lands on UNVERIFIED / REVIEW_REQUIRED, bounded by CHECK
    ///     constraints, and carries a concurrency token and an effective-from date.
    ///   * A commercial demand line cannot be rewritten or deleted once written.
    ///
    /// CORRECTION. An earlier revision of this file dropped two conjuncts from the supplier
    /// assertion — "ConcurrencyToken" IS NOT NULL and "EffectiveFrom" IS NOT NULL — while claiming
    /// the replacement was stronger. It was not; it was strictly weaker, and the claim has been
    /// removed. Both conjuncts are restored below, and the reason they could not simply be pasted
    /// onto a raw INSERT is worth recording: BOTH COLUMNS ARE NULLABLE WITH NO STORE DEFAULT AND NO
    /// TRIGGER.
    ///
    /// UPDATE. The gap that note recorded turned out to be wider than "a raw INSERT": the columns
    /// were populated by SupplierRepository.AddAsync and by nothing else, so the bulk Excel
    /// importer — which never touches the repository — wrote suppliers with both NULL and made
    /// them permanently ungovernable. The assignment now lives at the single point every write
    /// path passes through (SupplierGovernanceIdentityRules.Stamp, called from
    /// ErpRfqAutomationContext.SaveChanges), so this test would now hold whichever creation path
    /// it used; it keeps writing through the repository because that remains the real screen path.
    /// A raw INSERT that bypasses SaveChanges entirely still yields NULL for both, which is why
    /// 20260811233000_BackfillSupplierGovernanceIdentity repairs the rows already written that way.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Supplier_governance_state_is_fail_closed_and_demand_lines_are_immutable()
    {
        // The supplier goes in through the repository — the real screen path. EffectiveFrom and
        // ConcurrencyToken are stamped at SaveChanges; everything else is raw SQL on the same
        // database.
        long supplierId;
        await using (var context = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, 98_201);
            await context.SaveChangesAsync();
            var supplier = new Supplier
            {
                Name = "Release 02 backbone supplier",
                ContactEmail = "new-r02@example.test",
                ImageUrl = "n/a",
                Buid = 98_201,
                IsActive = true,
                CreatedBy = "qa",
                CreatedOn = DateTime.UtcNow
            };
            await new SupplierRepository(context).AddAsync(supplier);
            supplierId = supplier.Id;
        }

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "CreatedBy", "CreatedDate", "BusinessUnitID", "NexoraSerial")
                VALUES (98203, 'R02-RFQ', now(), 'qa', now(), 98201, 'NXR-R02-98203');
                INSERT INTO "RFQItems"
                    ("ID", "RFQID", "Quantity", "CreatedBy", "CreatedDate")
                VALUES (98204, 98203, 2, 'qa', now());
                INSERT INTO commercial_demand_lines
                    ("Id", "BusinessUnitId", "RfqId", "RfqItemId", "NexoraSerial", "IdentityKey",
                     "CreatedOn", "CreatedBy")
                VALUES (98205, 98201, 98203, 98204, 'NXR-R02-98203', '98201:98203:98204', now(), 'qa');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // Fail-closed by DEFAULT, not by application code: a supplier row that names none of the
        // governance columns is UNVERIFIED and REVIEW_REQUIRED, never approved or ready.
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = $"""
                SELECT "GovernanceStatus" = 'UNVERIFIED'
                   AND "ReadinessStatus" = 'REVIEW_REQUIRED'
                   AND "ConcurrencyToken" IS NOT NULL
                   AND "EffectiveFrom" IS NOT NULL
                FROM "Suppliers" WHERE "ID" = {supplierId};
                """;
            Assert.True((bool)(await state.ExecuteScalarAsync())!);
        }

        // …and the pair of statuses is bounded by CHECK constraints, so no writer can invent a
        // value outside the governed vocabulary and no default can drift to an approved one.
        await using (var bounds = connection.CreateCommand())
        {
            bounds.Transaction = transaction;
            bounds.CommandText = """
                SELECT count(*)::int FROM pg_constraint
                WHERE conrelid = 'public."Suppliers"'::regclass AND contype = 'c'
                  AND conname IN ('CK_Suppliers_GovernanceStatus', 'CK_Suppliers_ReadinessStatus')
                  AND convalidated;
                """;
            Assert.Equal(2, Convert.ToInt32(await bounds.ExecuteScalarAsync()));
        }

        await using (var immutable = connection.CreateCommand())
        {
            immutable.Transaction = transaction;
            immutable.CommandText = """
                UPDATE commercial_demand_lines SET "NexoraSerial" = 'CHANGED' WHERE "RfqItemId" = 98204;
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The delete half of the same immutability guard. Kept separate because the UPDATE above
    /// aborts its transaction, and a guard that rejects rewrites while permitting deletes would
    /// leave the demand line erasable.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Demand_lines_cannot_be_deleted()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (98211, 'R02-DEL', 'Release 02 backbone delete', 'qa', now());
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "CreatedBy", "CreatedDate", "BusinessUnitID", "NexoraSerial")
                VALUES (98213, 'R02-RFQ-DEL', now(), 'qa', now(), 98211, 'NXR-R02-98213');
                INSERT INTO "RFQItems"
                    ("ID", "RFQID", "Quantity", "CreatedBy", "CreatedDate")
                VALUES (98214, 98213, 2, 'qa', now());
                INSERT INTO commercial_demand_lines
                    ("Id", "BusinessUnitId", "RfqId", "RfqItemId", "NexoraSerial", "IdentityKey",
                     "CreatedOn", "CreatedBy")
                VALUES (98215, 98211, 98213, 98214, 'NXR-R02-98213', '98211:98213:98214', now(), 'qa');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """DELETE FROM commercial_demand_lines WHERE "Id" = 98215;""";
            var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, error.SqlState);
        }

        await transaction.RollbackAsync();
    }
}

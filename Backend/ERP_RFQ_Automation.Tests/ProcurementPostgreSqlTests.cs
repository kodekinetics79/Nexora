using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ProcurementPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private readonly PostgreSqlTestDatabase _database = database;

    /// <summary>
    /// SQUASH NOTE — this replaces two tests that no longer have a migration to walk.
    ///
    /// Procurement_data_and_history_survive_latest_restore_and_reupgrade built an isolated
    /// database, drove a complete procurement round through the application services, cloned and
    /// restored it, walked back to 20260726052445_GovernSupplierSourcingAndProcurement and forward
    /// again, asserting the goods receipt survived and — the part that was about the SCHEMA rather
    /// than about EF — that the tenant role held DELETE on the three sourcing tables at the early
    /// migration and had it REVOKED by the end of the chain.
    ///
    /// Populated_supplier_quote_upgrade_reconciles_unique_lineage_or_fails_with_row_evidence (four
    /// scenarios) built a database at
    /// 20260725232839_AppendCommercialResolutionSnapshotsAndOwnershipIdempotency, planted a legacy
    /// supplier quote or sourcing award in one of four states — correctly linked, missing its
    /// tenant, missing its RFQ lineage, or with an award price matching no quote — and asserted the
    /// upgrade either reconciled it or ABORTED naming the offending row id.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased those ids. Reconciliation-or-abort is
    /// migration-time behaviour and cannot survive a squash. But all four legacy shapes existed
    /// because the columns that would have prevented them were added late, and every one of them is
    /// now UNWRITABLE — which is a stronger guarantee than "the migration would have caught it".
    /// That, and the revoked DELETE, is what is asserted here.
    ///
    /// ONE ASSERTION IS RETIRED AND NOT REPLACED, and it is called out because an earlier revision
    /// of this file dropped it silently: nextval('public.nexora_rfq_number_seq') &gt; 900, which
    /// proved 20260726064437_ServerAuthoritativeRfqNumbers had setval'd the sequence past every RFQ
    /// number already issued. That is the identical retirement already disclosed for the PO
    /// sequence lower in this file — a one-time high-water reconciliation for numbers issued before
    /// the sequence existed, which no database can need again. What is NOT retired is that the
    /// sequence exists, is schema-qualified and is USAGE-only to the tenant role; that moved into
    /// Rfq_and_po_document_numbers_come_from_governed_database_sequences below, and the sequence is
    /// additionally drawn from under the tenant role by
    /// PostgreSqlProductionDialectTests and RfqTenantRoleCreatePostgreSqlTests.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Sourcing_evidence_is_undeletable_by_the_tenant_role_and_lineage_is_tenant_bound()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                -- The end state of the chain: the runtime role may write sourcing evidence and may
                -- never remove it. The early migration granted DELETE; nothing does now.
                NOT bool_or(has_table_privilege('nexora_tenant_app', format('public.%I', table_name), 'DELETE'))
                FROM unnest(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems']) table_name;
            """;
        Assert.True((bool)(await command.ExecuteScalarAsync())!);

        await using var rest = connection.CreateCommand();
        rest.CommandText = """
            SELECT
                (SELECT count(*)::int FROM pg_policies
                 WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                   AND tablename = ANY(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'])) = 3,
                (SELECT bool_and(c.relrowsecurity)
                 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'public'
                   AND c.relname = ANY(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'])),
                -- "missing_tenant": a supplier quote with no BusinessUnitId was the legacy shape the
                -- upgrade had to reconcile. It is now simply unwritable.
                (SELECT count(*)::int FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name = 'SupplierQuotedItems'
                   AND column_name = 'BusinessUnitId' AND is_nullable = 'NO') = 1,
                (SELECT count(*)::int FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name = 'SourcingAwards'
                   AND column_name IN ('BusinessUnitId', 'RfqId') AND is_nullable = 'NO') = 2,
                -- "unlinked_quote" / "unlinked_award": every lineage reference out of these two
                -- tables carries the tenant in the key, so a quote or an award cannot point at
                -- another tenant's RFQ, product, solicitation or quote line.
                (SELECT bool_and(array_length(conkey, 1) = 2)
                 FROM pg_constraint
                 WHERE contype = 'f'
                   AND conrelid IN ('public."SupplierQuotedItems"'::regclass, 'public."SourcingAwards"'::regclass)
                   AND confrelid IN ('public."RFQ"'::regclass, 'public."RFQItems"'::regclass,
                                     'public."Products"'::regclass, 'public."SupplierSolicitations"'::regclass,
                                     'public."SupplierQuotedItems"'::regclass,
                                     'public.commercial_demand_lines'::regclass));
            """;
        await using var reader = await rest.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 5; index++)
            Assert.True(reader.GetBoolean(index), $"Sourcing lineage assertion {index + 1} failed.");
    }

    /// <summary>
    /// Both server-authoritative document sequences, asserted on the live catalogue.
    ///
    /// SQUASH NOTE: this replaces
    /// SupplierPurchaseHistoryRepositoryTests.Po_document_sequence_migration_reconciles_the_persisted_high_water_mark,
    /// which read the text of Migrations/20260804180919_Module05ProcurementDeadlineAndPoNumberAuthority.cs
    /// off disk and looked for three substrings. It was the last thing in the suite reading a file
    /// under Migrations\, and it could not have caught a hand edit to a deployed database.
    ///
    /// It also carries the surviving half of the retired RFQ-number assertion (see the note on
    /// Sourcing_evidence_is_undeletable_by_the_tenant_role_and_lineage_is_tenant_bound above): the
    /// existence, schema qualification and USAGE-only grant of nexora_rfq_number_seq. Only the
    /// one-time setval high-water reconciliation is gone, for both sequences, and for the same
    /// reason — it carried numbers issued before the sequences existed.
    ///
    /// USAGE without SELECT or UPDATE is the point: a tenant session may draw the next number, and
    /// may neither read how many have been issued nor reset the counter onto numbers already used.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Rfq_and_po_document_numbers_come_from_governed_database_sequences()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                to_regclass('public.nexora_supplier_po_doc_seq') IS NOT NULL,
                has_sequence_privilege('nexora_tenant_app', 'public.nexora_supplier_po_doc_seq', 'USAGE'),
                NOT has_sequence_privilege('nexora_tenant_app', 'public.nexora_supplier_po_doc_seq', 'SELECT'),
                NOT has_sequence_privilege('nexora_tenant_app', 'public.nexora_supplier_po_doc_seq', 'UPDATE'),
                to_regclass('public.nexora_rfq_number_seq') IS NOT NULL,
                has_sequence_privilege('nexora_tenant_app', 'public.nexora_rfq_number_seq', 'USAGE'),
                NOT has_sequence_privilege('nexora_tenant_app', 'public.nexora_rfq_number_seq', 'SELECT'),
                NOT has_sequence_privilege('nexora_tenant_app', 'public.nexora_rfq_number_seq', 'UPDATE');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 8; index++)
            Assert.True(reader.GetBoolean(index), $"Document sequence assertion {index + 1} failed.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    // Squash note: this method opened by asserting that
    // '20260726052445_GovernSupplierSourcingAndProcurement' was present in
    // "__EFMigrationsHistory". 20260811033109_SquashedSchemaBaseline erased that id. The six
    // tables, the two unique document-number indexes, the six tenant policies, the twelve grant
    // shapes and the append-only event trigger are all still asserted below against the live
    // catalogue, which is what the id was standing in for.
    public async Task Procurement_schema_has_tables_rls_grants_and_append_only_events()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using var tables = connection.CreateCommand();
        tables.CommandText = """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = ANY(ARRAY[
                'supplier_purchase_orders', 'supplier_purchase_order_lines',
                'goods_receipts', 'goods_receipt_lines', 'procurement_events', 'procurement_outbox'])
            """;
        Assert.Equal(6L, (long)(await tables.ExecuteScalarAsync())!);

        await using var canonicalIdentities = connection.CreateCommand();
        canonicalIdentities.CommandText = """
            SELECT count(*)
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND indexname = ANY(ARRAY[
                  'IX_supplier_purchase_orders_BusinessUnitId_PurchaseOrderNumber',
                  'IX_goods_receipts_BusinessUnitId_ReceiptNumber'])
              AND indexdef LIKE 'CREATE UNIQUE INDEX%'
            """;
        Assert.Equal(2L, (long)(await canonicalIdentities.ExecuteScalarAsync())!);

        await using var policies = connection.CreateCommand();
        policies.CommandText = """
            SELECT count(*) FROM pg_policies
            WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
              AND tablename = ANY(ARRAY[
                'supplier_purchase_orders', 'supplier_purchase_order_lines',
                'goods_receipts', 'goods_receipt_lines', 'procurement_events', 'procurement_outbox'])
            """;
        Assert.Equal(6L, (long)(await policies.ExecuteScalarAsync())!);

        await using var grants = connection.CreateCommand();
        grants.CommandText = """
            SELECT
                has_table_privilege('nexora_tenant_app', 'public.supplier_purchase_orders', 'SELECT,INSERT,UPDATE')
                AND NOT has_table_privilege('nexora_tenant_app', 'public.supplier_purchase_orders', 'DELETE')
                AND has_table_privilege('nexora_tenant_app', 'public.supplier_purchase_order_lines', 'SELECT,INSERT,UPDATE')
                AND NOT has_table_privilege('nexora_tenant_app', 'public.supplier_purchase_order_lines', 'DELETE')
                AND has_table_privilege('nexora_tenant_app', 'public.goods_receipts', 'SELECT,INSERT')
                AND NOT has_table_privilege('nexora_tenant_app', 'public.goods_receipts', 'UPDATE,DELETE')
                AND has_table_privilege('nexora_tenant_app', 'public.goods_receipt_lines', 'SELECT,INSERT')
                AND NOT has_table_privilege('nexora_tenant_app', 'public.goods_receipt_lines', 'UPDATE,DELETE')
                AND has_table_privilege('nexora_tenant_app', 'public.procurement_events', 'SELECT,INSERT')
                AND NOT has_table_privilege('nexora_tenant_app', 'public.procurement_events', 'UPDATE,DELETE')
                AND has_table_privilege('nexora_tenant_app', 'public.procurement_outbox', 'SELECT,INSERT,UPDATE')
                AND NOT has_table_privilege('nexora_tenant_app', 'public.procurement_outbox', 'DELETE')
            """;
        Assert.True((bool)(await grants.ExecuteScalarAsync())!);

        const long tenant = 97_001;
        await using (var seed = _database.ContextFor(null))
        {
            ProcurementTestData.SeedGraph(seed, tenant, 20_000);
            seed.ProcurementEvents.Add(new ProcurementEvent
            {
                BusinessUnitId = tenant, AggregateType = "QA", AggregateId = 1, AggregateVersion = 1,
                EventType = "QA_CREATED", Actor = "qa", CorrelationId = "pg-migration",
                IdempotencyKey = "pg-migration", PayloadJson = "{}", OccurredOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var immutable = connection.CreateCommand();
        immutable.CommandText = """
            UPDATE procurement_events SET "Actor" = 'forged'
            WHERE "BusinessUnitId" = 97001 AND "IdempotencyKey" = 'pg-migration'
            """;
        var immutableError = await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
        Assert.Equal("55000", immutableError.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Governed_sourcing_tables_and_inventory_links_reject_cross_tenant_access()
    {
        const long tenantA = 97_301;
        const long tenantB = 97_302;
        const long offsetA = 90_000;
        const long offsetB = 100_000;
        var poA = await CreatePurchaseOrderAsync(tenantA, offsetA, "pg-boundary-a", 6m);
        await CreatePurchaseOrderAsync(tenantB, offsetB, "pg-boundary-b", 6m);

        await using var connection = await _database.OpenConnectionAsync();
        await using (var policies = connection.CreateCommand())
        {
            policies.CommandText = """
                SELECT count(*) FROM pg_policies
                WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                  AND tablename = ANY(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'])
                """;
            Assert.Equal(3L, (long)(await policies.ExecuteScalarAsync())!);
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var hidden = connection.CreateCommand();
            hidden.Transaction = transaction;
            hidden.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{tenantA}';
                SELECT
                    (SELECT count(*) FROM "SupplierSolicitations" WHERE "BusinessUnitId" = {tenantB})
                  + (SELECT count(*) FROM "SourcingAwards" WHERE "BusinessUnitId" = {tenantB})
                  + (SELECT count(*) FROM "SupplierQuotedItems" WHERE "BusinessUnitId" = {tenantB});
                """;
            Assert.Equal(0L, (long)(await hidden.ExecuteScalarAsync())!);

            await using var forged = connection.CreateCommand();
            forged.Transaction = transaction;
            forged.CommandText = $"""
                INSERT INTO "SupplierQuotedItems"
                    ("SupplierId", "Quantity", "UnitPrice", "CreatedBy", "CreatedDate", "IsActive", "BusinessUnitId")
                VALUES ({ProcurementTestData.Supplier + offsetB}, 1, 1, 'qa', now(), true, {tenantB});
                """;
            var rlsError = await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rlsError.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var supplierLink = connection.CreateCommand())
        {
            supplierLink.CommandText = $"""
                UPDATE "SupplierQuotedItems"
                SET "SupplierId" = {ProcurementTestData.Supplier + offsetB}
                WHERE "BusinessUnitId" = {tenantA};
                """;
            var supplierError = await Assert.ThrowsAsync<PostgresException>(() => supplierLink.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, supplierError.SqlState);
        }

        await using (var quotedProductLink = connection.CreateCommand())
        {
            quotedProductLink.CommandText = $"""
                UPDATE "SupplierQuotedItems"
                SET "ProductId" = {ProcurementTestData.Product + offsetB}
                WHERE "BusinessUnitId" = {tenantA};
                """;
            var productError = await Assert.ThrowsAsync<PostgresException>(() => quotedProductLink.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, productError.SqlState);
        }

        await using (var quotedCurrencyLink = connection.CreateCommand())
        {
            quotedCurrencyLink.CommandText = $"""
                UPDATE "SupplierQuotedItems"
                SET "CurrencyId" = {ProcurementTestData.Currency + offsetB}
                WHERE "BusinessUnitId" = {tenantA};
                """;
            var currencyError = await Assert.ThrowsAsync<PostgresException>(() => quotedCurrencyLink.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, currencyError.SqlState);
        }

        await using (var awardCurrencyLink = connection.CreateCommand())
        {
            awardCurrencyLink.CommandText = $"""
                UPDATE "SourcingAwards"
                SET "CurrencyId" = {ProcurementTestData.Currency + offsetB}
                WHERE "BusinessUnitId" = {tenantA};
                """;
            var currencyError = await Assert.ThrowsAsync<PostgresException>(() => awardCurrencyLink.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, currencyError.SqlState);
        }

        await using (var inventoryLink = connection.CreateCommand())
        {
            inventoryLink.CommandText = $"""
                UPDATE supplier_purchase_order_lines
                SET "InventoryId" = {ProcurementTestData.Inventory + offsetB}
                WHERE "BusinessUnitId" = {tenantA} AND "SupplierPurchaseOrderId" = {poA.Id};
                """;
            var inventoryError = await Assert.ThrowsAsync<PostgresException>(() => inventoryLink.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, inventoryError.SqlState);
        }

        await using (var purchaseOrderProductLink = connection.CreateCommand())
        {
            purchaseOrderProductLink.CommandText = $"""
                UPDATE supplier_purchase_order_lines
                SET "ProductId" = {ProcurementTestData.Product + offsetB}
                WHERE "BusinessUnitId" = {tenantA} AND "SupplierPurchaseOrderId" = {poA.Id};
                """;
            var productError = await Assert.ThrowsAsync<PostgresException>(() => purchaseOrderProductLink.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, productError.SqlState);
        }

        await using (var productOwnership = connection.CreateCommand())
        {
            productOwnership.CommandText = $"""
                UPDATE "Products"
                SET "BUID" = {tenantB}
                WHERE "ID" = {ProcurementTestData.Product + offsetA};
                """;
            var ownershipError = await Assert.ThrowsAsync<PostgresException>(() => productOwnership.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ownershipError.SqlState);
            Assert.Equal("product tenant ownership is immutable while referenced by procurement", ownershipError.MessageText);
        }

        await using (var inventoryOwnership = connection.CreateCommand())
        {
            inventoryOwnership.CommandText = $"""
                UPDATE "Inventory"
                SET "Buid" = {tenantB}
                WHERE "Id" = {ProcurementTestData.Inventory + offsetA};
                """;
            var ownershipError = await Assert.ThrowsAsync<PostgresException>(() => inventoryOwnership.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ownershipError.SqlState);
            Assert.Equal("inventory tenant ownership is immutable while referenced by procurement", ownershipError.MessageText);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Rls_and_composite_keys_reject_cross_tenant_reads_writes_and_relationships()
    {
        const long tenantA = 97_101;
        const long tenantB = 97_102;
        const long offsetA = 40_000;
        const long offsetB = 50_000;
        var poA = await CreatePurchaseOrderAsync(tenantA, offsetA, "pg-rls-a", 2m);
        var poB = await CreatePurchaseOrderAsync(tenantB, offsetB, "pg-rls-b", 2m);
        var lineA = await PurchaseOrderLineIdAsync(tenantA, poA.Id);
        var lineB = await PurchaseOrderLineIdAsync(tenantB, poB.Id);
        await PostReceiptAsync(Receipt(tenantA, offsetA, poA.Id, lineA, 1m, poA.Version, "pg-rls-gr-a", "GR-RLS-A"));
        await PostReceiptAsync(Receipt(tenantB, offsetB, poB.Id, lineB, 1m, poB.Version, "pg-rls-gr-b", "GR-RLS-B"));

        var tenantTables = new[]
        {
            "commercial_demand_lines", "sourcing_cases", "sourcing_case_candidates",
            "SupplierSolicitations", "SourcingAwards", "SupplierQuotedItems",
            "supplier_purchase_orders", "supplier_purchase_order_lines",
            "goods_receipts", "goods_receipt_lines", "procurement_events", "procurement_outbox"
        };

        await using var connection = await _database.OpenConnectionAsync();
        foreach (var tableName in tenantTables)
        {
            await using var readTransaction = await connection.BeginTransactionAsync();
            await using var read = connection.CreateCommand();
            read.Transaction = readTransaction;
            read.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{tenantA}';
                SELECT count(*) FROM public."{tableName}" WHERE "BusinessUnitId" = {tenantB};
                """;
            Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
            await readTransaction.RollbackAsync();
        }

        foreach (var tableName in tenantTables)
        {
            await using var writeTransaction = await connection.BeginTransactionAsync();
            await using (var scope = connection.CreateCommand())
            {
                scope.Transaction = writeTransaction;
                scope.CommandText = $"""
                    SET LOCAL ROLE nexora_tenant_app;
                    SET LOCAL nexora.business_unit_id = '{tenantA}';
                    """;
                await scope.ExecuteNonQueryAsync();
            }
            await using var write = connection.CreateCommand();
            write.Transaction = writeTransaction;
            write.CommandText = $"""
                UPDATE public."{tableName}"
                SET "BusinessUnitId" = "BusinessUnitId"
                WHERE "BusinessUnitId" = {tenantB};
                """;
            try
            {
                Assert.Equal(0, await write.ExecuteNonQueryAsync());
            }
            catch (PostgresException writeError)
            {
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, writeError.SqlState);
            }
            await writeTransaction.RollbackAsync();
        }

        await using var forgedRelationship = connection.CreateCommand();
        forgedRelationship.CommandText = $"""
            INSERT INTO supplier_purchase_orders
                ("BusinessUnitId", "RfqId", "SupplierId", "CurrencyId", "PurchaseOrderNumber", "Status",
                 "TotalValue", "ExpectedOn", "IdempotencyKey", "RequestHash", "Version", "CreatedOn", "CreatedBy")
            VALUES ({tenantA}, {ProcurementTestData.Rfq + offsetB}, {ProcurementTestData.Supplier + offsetA},
                    {ProcurementTestData.Currency + offsetA}, 'PO-FORGED-TENANT', 'ISSUED', 1, current_date,
                    'pg-forged-relation', repeat('a', 64), 1, now(), 'qa');
            """;
        var relationshipError = await Assert.ThrowsAsync<PostgresException>(() => forgedRelationship.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, relationshipError.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_and_over_receipts_are_fenced_and_rollback_preserves_reconciliation()
    {
        const long raceTenant = 97_201;
        const long raceOffset = 70_000;
        var racePo = await CreatePurchaseOrderAsync(raceTenant, raceOffset, "pg-race", 8m);
        Assert.Equal($"PO-{DateTime.UtcNow:yyyy}-{racePo.Id:D10}", racePo.Number);
        var raceLineId = await PurchaseOrderLineIdAsync(raceTenant, racePo.Id);
        var first = Receipt(raceTenant, raceOffset, racePo.Id, raceLineId, 8m, racePo.Version, "pg-race-a", "GR-RACE-A");
        var second = Receipt(raceTenant, raceOffset, racePo.Id, raceLineId, 8m, racePo.Version, "pg-race-b", "GR-RACE-B");

        var outcomes = await Task.WhenAll(CaptureReceiptAsync(first), CaptureReceiptAsync(second));
        var winner = Assert.Single(outcomes, x => x.Result is not null);
        var loser = Assert.Single(outcomes, x => x.Error is not null).Error!;
        Assert.True(loser is ProcurementConflictException or DbUpdateConcurrencyException or PostgresException
            || loser.InnerException is PostgresException,
            $"Unexpected race failure: {loser.GetType().Name}: {loser.Message}");

        var replay = await PostReceiptAsync(winner.Command);
        Assert.True(replay.Replayed);
        await AssertReconciliationAsync(raceTenant, raceOffset, 1, 8m, ProcurementTestData.InitialOnHand + 8m);

        const long rollbackTenant = 97_202;
        const long rollbackOffset = 80_000;
        var rollbackPo = await CreatePurchaseOrderAsync(rollbackTenant, rollbackOffset, "pg-rollback", 8m);
        var rollbackLineId = await PurchaseOrderLineIdAsync(rollbackTenant, rollbackPo.Id);
        var over = Receipt(rollbackTenant, rollbackOffset, rollbackPo.Id, rollbackLineId, 9m, rollbackPo.Version,
            "pg-over", "GR-OVER");
        await Assert.ThrowsAsync<ProcurementValidationException>(() => PostReceiptAsync(over));
        await AssertReconciliationAsync(rollbackTenant, rollbackOffset, 0, 0m, ProcurementTestData.InitialOnHand);
    }

    private async Task<PurchaseOrderResult> CreatePurchaseOrderAsync(long tenant, long offset, string key, decimal quantity)
    {
        await using (var seed = _database.ContextFor(null))
        {
            ProcurementTestData.SeedGraph(seed, tenant, offset);
            await seed.SaveChangesAsync();
        }

        var solicitation = await Execute(tenant, service => service.CreateSolicitationAsync(new(
            tenant, ProcurementTestData.Rfq + offset, ProcurementTestData.Supplier + offset,
            [ProcurementTestData.RfqItem + offset], DateTime.UtcNow.AddDays(2), $"{key}-sol", "qa", $"corr-{key}-sol")));
        await using (var delivered = _database.ContextFor(tenant))
        {
            var solicitationRow = await delivered.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>()
                .SingleAsync(x => x.Id == solicitation.Id);
            var outbox = await delivered.ProcurementOutboxMessages
                .SingleAsync(x => x.SupplierSolicitationId == solicitation.Id);
            solicitationRow.Status = ERP_RFQ_Automation.Agent.Models.SolicitationStatus.Sent;
            solicitationRow.SentOn = DateTime.UtcNow;
            outbox.Status = ProcurementOutboxStatuses.Sent;
            outbox.ProviderReference = $"qa-provider:{key}";
            outbox.SentOn = DateTime.UtcNow;
            await delivered.SaveChangesAsync();
        }
        var quote = await Execute(tenant, service => service.CaptureSupplierQuoteAsync(new(
            tenant, solicitation.Id, $"SQ-{key}", 1, DateTime.UtcNow.AddDays(30), $"{key}-quote", "qa", $"corr-{key}-quote",
            [new(ProcurementTestData.RfqItem + offset, ProcurementTestData.Product + offset, quantity, 12m,
                ProcurementTestData.Currency + offset, 5, quantity, 10m, 2m, 1m, 0m, 0m, 1m, 95m)])));
        var award = await Execute(tenant, service => service.ApproveAwardAsync(new(
            tenant, Assert.Single(quote.LineIds), quantity, 1, $"{key}-award", "qa", $"corr-{key}-award", 42, "QA award")));
        var draft = await Execute(tenant, service => service.CreatePurchaseOrderAsync(new(
            tenant, ProcurementTestData.Rfq + offset, ProcurementTestData.Supplier + offset,
            ProcurementTestData.Currency + offset, ProcurementTestData.Warehouse + offset,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), [award.Id],
            $"{key}-po", "qa", $"corr-{key}-po")));
        // The award above is approved by user 42; the purchase order is approved by user 77, so
        // the segregation-of-duties default is satisfied rather than switched off.
        var approval = await Execute(tenant, service => service.ApprovePurchaseOrderAsync(new(
            tenant, draft.Id, 1, 77, $"{key}-approve", "qa", $"corr-{key}-approve")));
        return await Execute(tenant, service => service.IssuePurchaseOrderAsync(new(
            tenant, draft.Id, approval.Version, $"provider-receipt:{key}", $"{key}-issue", "qa",
            $"corr-{key}-issue", new string('a', 64), DateTime.UtcNow)));
    }

    private async Task<T> Execute<T>(long tenant, Func<ProcurementApplicationService, Task<T>> operation)
    {
        await using var context = _database.ContextFor(tenant);
        return await operation(new ProcurementApplicationService(context));
    }

    private async Task<long> PurchaseOrderLineIdAsync(long tenant, long purchaseOrderId)
    {
        await using var context = _database.ContextFor(tenant);
        return await context.SupplierPurchaseOrderLines.Where(x => x.SupplierPurchaseOrderId == purchaseOrderId)
            .Select(x => x.Id).SingleAsync();
    }

    private static PostGoodsReceiptCommand Receipt(long tenant, long offset, long poId, long lineId,
        decimal quantity, long version, string key, string number) => new(
        tenant, poId, ProcurementTestData.Warehouse + offset, number, DateTime.UtcNow, version,
        [new PostGoodsReceiptLine(lineId, quantity)], key, "qa", $"corr-{key}");

    private async Task<(GoodsReceiptResult? Result, Exception? Error, PostGoodsReceiptCommand Command)> CaptureReceiptAsync(
        PostGoodsReceiptCommand command)
    {
        try
        {
            return (await PostReceiptAsync(command), null, command);
        }
        catch (Exception exception)
        {
            return (null, exception, command);
        }
    }

    private Task<GoodsReceiptResult> PostReceiptAsync(PostGoodsReceiptCommand command) =>
        Execute(command.BusinessUnitId, service => service.PostGoodsReceiptAsync(command));

    private async Task AssertReconciliationAsync(long tenant, long offset, int expectedReceipts,
        decimal expectedReceived, decimal expectedOnHand)
    {
        await using var context = _database.ContextFor(tenant);
        Assert.Equal(expectedReceipts, await context.GoodsReceipts.CountAsync());
        var movements = await context.InventoryMovements.Where(x => x.BusinessUnitId == tenant).ToListAsync();
        Assert.Equal(expectedReceipts, movements.Count);
        Assert.Equal(expectedReceived, movements.Sum(x => x.Quantity));
        Assert.Equal(expectedOnHand, await context.Set<ERP_RFQ_Automation.Models.Inventory>()
            .Where(x => x.Id == ProcurementTestData.Inventory + offset).Select(x => x.QtyOnHand).SingleAsync());
        Assert.Equal(expectedReceived, await context.SupplierPurchaseOrderLines.Select(x => x.ReceivedQuantity).SingleAsync());
        Assert.Equal(expectedReceived, await context.IncomingInventory.Select(x => x.ReceivedQuantity).SingleAsync());
    }
}

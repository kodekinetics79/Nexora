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

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Procurement_data_and_history_survive_latest_restore_and_reupgrade()
    {
        var databaseName = $"procurement_restore_{Guid.NewGuid():N}";
        var backupName = $"procurement_backup_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
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
            const long tenant = 97_901;
            const long offset = 190_000;
            const string firstProcurementMigration = "20260726052445_GovernSupplierSourcingAndProcurement";

            await using (var migrate = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null))
            {
                await migrate.GetService<IMigrator>().MigrateAsync();
                ProcurementTestData.SeedGraph(migrate, tenant, offset);
                await migrate.SaveChangesAsync();
            }

            async Task<T> ExecuteIsolated<T>(Func<ProcurementApplicationService, Task<T>> operation)
            {
                await using var scoped = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, tenant);
                return await operation(new ProcurementApplicationService(scoped));
            }

            var solicitation = await ExecuteIsolated(service => service.CreateSolicitationAsync(new(
                tenant, ProcurementTestData.Rfq + offset, ProcurementTestData.Supplier + offset,
                [ProcurementTestData.RfqItem + offset], DateTime.UtcNow.AddDays(2),
                "restore-sol", "qa", "restore-sol")));
            await using (var delivered = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, tenant))
            {
                var solicitationRow = await delivered.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>()
                    .SingleAsync(x => x.Id == solicitation.Id);
                var outbox = await delivered.ProcurementOutboxMessages
                    .SingleAsync(x => x.SupplierSolicitationId == solicitation.Id);
                solicitationRow.Status = ERP_RFQ_Automation.Agent.Models.SolicitationStatus.Sent;
                solicitationRow.SentOn = DateTime.UtcNow;
                outbox.Status = ProcurementOutboxStatuses.Sent;
                outbox.ProviderReference = "restore-provider";
                outbox.SentOn = DateTime.UtcNow;
                await delivered.SaveChangesAsync();
            }
            var quote = await ExecuteIsolated(service => service.CaptureSupplierQuoteAsync(new(
                tenant, solicitation.Id, "RESTORE-SQ", 1, DateTime.UtcNow.AddDays(30),
                "restore-quote", "qa", "restore-quote",
                [new(ProcurementTestData.RfqItem + offset, ProcurementTestData.Product + offset, 5m, 12m,
                    ProcurementTestData.Currency + offset, 5, 5m, 10m, 2m, 1m, 0m, 0m, 1m, 95m)])));
            var award = await ExecuteIsolated(service => service.ApproveAwardAsync(new(
                tenant, Assert.Single(quote.LineIds), 5m, 1, "restore-award", "qa", "restore-award", 42, "restore")));
            var draft = await ExecuteIsolated(service => service.CreatePurchaseOrderAsync(new(
                tenant, ProcurementTestData.Rfq + offset, ProcurementTestData.Supplier + offset,
                ProcurementTestData.Currency + offset, ProcurementTestData.Warehouse + offset,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), [award.Id],
                "restore-po", "qa", "restore-po")));
            var issued = await ExecuteIsolated(service => service.IssuePurchaseOrderAsync(new(
                tenant, draft.Id, 1, "provider-receipt:restore", "restore-issue", "qa", "restore-issue",
                new string('a', 64), DateTime.UtcNow)));
            long purchaseOrderLineId;
            await using (var scoped = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, tenant))
            {
                purchaseOrderLineId = await scoped.SupplierPurchaseOrderLines
                    .Where(x => x.SupplierPurchaseOrderId == issued.Id).Select(x => x.Id).SingleAsync();
            }
            await ExecuteIsolated(service => service.PostGoodsReceiptAsync(new(
                tenant, issued.Id, ProcurementTestData.Warehouse + offset, "RESTORE-GR", DateTime.UtcNow,
                2, [new PostGoodsReceiptLine(purchaseOrderLineId, 5m)], "restore-gr", "qa", "restore-gr")));

            await using (var mark = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null))
            {
                await mark.Database.ExecuteSqlRawAsync("""
                    UPDATE "RFQ" SET "RFQNo" = 'NXR-RFQ-97901-2026-00000420' WHERE "ID" = 286060;
                    """);
            }

            NpgsqlConnection.ClearAllPools();
            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync();
                foreach (var sql in new[]
                         {
                             $"CREATE DATABASE \"{backupName}\" WITH TEMPLATE \"{databaseName}\"",
                             $"DROP DATABASE \"{databaseName}\" WITH (FORCE)",
                             $"CREATE DATABASE \"{databaseName}\" WITH TEMPLATE \"{backupName}\""
                         })
                {
                    await using var command = admin.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
            }

            await using var restored = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = restored.GetService<IMigrator>();
            Assert.Equal(1, await restored.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM goods_receipts WHERE \"IdempotencyKey\" = 'restore-gr'").SingleAsync());
            Assert.True(await restored.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM procurement_events WHERE \"CorrelationId\" LIKE 'restore-%'").SingleAsync() >= 5);

            await migrator.MigrateAsync(firstProcurementMigration);
            Assert.Equal(1, await restored.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM goods_receipts WHERE \"IdempotencyKey\" = 'restore-gr'").SingleAsync());
            Assert.Equal(3, await restored.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM pg_policies
                WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                  AND tablename = ANY(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'])
                """).SingleAsync());
            Assert.True(await restored.Database.SqlQueryRaw<bool>("""
                SELECT bool_and(c.relrowsecurity) AS "Value"
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relname = ANY(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'])
                """).SingleAsync());
            Assert.True(await restored.Database.SqlQueryRaw<bool>("""
                SELECT bool_and(has_table_privilege('nexora_tenant_app', format('public.%I', table_name), 'DELETE')) AS "Value"
                FROM unnest(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems']) table_name
                """).SingleAsync());
            Assert.Equal(1, await restored.Database.SqlQueryRaw<int>($"""
                SELECT count(*)::int AS "Value" FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '{firstProcurementMigration}'
                """).SingleAsync());
            Assert.Equal(0, await restored.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = ANY(ARRAY[
                    '20260726060029_HardenProcurementTenantLineage',
                    '20260726061608_EnforceProcurementTenantBoundaries',
                    '20260726063743_EnforceProcurementProductCurrencyLineage',
                    '20260726064437_ServerAuthoritativeRfqNumbers'])
                """).SingleAsync());

            await restored.Database.ExecuteSqlRawAsync("""
                UPDATE "RFQ" SET "RFQNo" = 'NXR-RFQ-97901-2026-00000900' WHERE "ID" = 286060;
                """);
            await migrator.MigrateAsync();
            Assert.Equal(5, await restored.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = ANY(ARRAY[
                    '20260726052445_GovernSupplierSourcingAndProcurement',
                    '20260726060029_HardenProcurementTenantLineage',
                    '20260726061608_EnforceProcurementTenantBoundaries',
                    '20260726063743_EnforceProcurementProductCurrencyLineage',
                    '20260726064437_ServerAuthoritativeRfqNumbers'])
                """).SingleAsync());
            Assert.True(await restored.Database.SqlQueryRaw<long>(
                "SELECT nextval('public.nexora_rfq_number_seq') AS \"Value\"").SingleAsync() > 900);
            Assert.False(await restored.Database.SqlQueryRaw<bool>("""
                SELECT bool_or(has_table_privilege('nexora_tenant_app', format('public.%I', table_name), 'DELETE')) AS "Value"
                FROM unnest(ARRAY['SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems']) table_name
                """).SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            foreach (var name in new[] { databaseName, backupName })
            {
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("missing_tenant")]
    [InlineData("unlinked_quote")]
    [InlineData("unlinked_award")]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_supplier_quote_upgrade_reconciles_unique_lineage_or_fails_with_row_evidence(string scenario)
    {
        var databaseName = $"procurement_upgrade_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
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
            await using var context = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260725232839_AppendCommercialResolutionSnapshotsAndOwnershipIdempotency");
            var legacyTenant = scenario == "missing_tenant" ? (long?)null : 97801;
            var quoteReference = scenario == "unlinked_quote" ? null : "rfq=97805;item=97806;lead=5";
            var awardUnitPrice = scenario == "unlinked_award" ? 99m : 12.5m;
            var supplierEmail = $"upgrade-{scenario}@example.test";
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (97801, 'PROC-UP', 'Procurement upgrade', 'qa', now());
                INSERT INTO "Currency"
                    ("ID", "Code", "CurrencyName", "ExchangeRate", "IsBaseCurrency", "BusinessUnitID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES (97804, 'QUP', 'Upgrade currency', 1, true, 97801, true, 'qa', now());
                INSERT INTO "Suppliers" ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES (97802, 'Upgrade supplier', {supplierEmail}, 'n/a', 97801, true, 'qa', now());
                INSERT INTO "Products"
                    ("ID", "ProductName", "PartNo", "QtyOnHand", "ReorderPoint", "CreatedBy", "CreatedOn", "BUID", "IsActive")
                VALUES (97807, 'Upgrade product', 'PROC-UP-PART', 0, 0, 'qa', now(), 97801, true);
                INSERT INTO "RFQ" ("ID", "RFQNo", "RecDate", "CreatedBy", "CreatedDate", "BusinessUnitID")
                VALUES (97805, 'PROC-UP-RFQ', now(), 'qa', now(), 97801);
                INSERT INTO "RFQItems"
                    ("ID", "RFQID", "ProductID", "CurrencyID", "Quantity", "CreatedBy", "CreatedDate")
                VALUES (97806, 97805, 97807, 97804, 4, 'qa', now());
                INSERT INTO "SupplierSolicitations"
                    ("Id", "BusinessUnitId", "RfqId", "SupplierId", "Status", "SentOn", "Channel", "CreatedOn", "UpdatedOn")
                VALUES (97808, 97801, 97805, 97802, 'SENT', now(), 'EMAIL', now(), now());
                INSERT INTO "SupplierQuotedItems"
                    ("Id", "SupplierId", "Quantity", "UnitPrice", "CurrencyId", "QuoteReference",
                     "CreatedBy", "CreatedDate", "IsActive", "BusinessUnitId")
                VALUES (97803, 97802, 4, 12.5, 97804, {quoteReference}, 'qa', now(), true, {legacyTenant});
                INSERT INTO "SourcingAwards"
                    ("Id", "BusinessUnitId", "RfqId", "RfqItemId", "SupplierId", "UnitPrice",
                     "Quantity", "TotalValue", "AwardedByAgent", "CreatedOn")
                VALUES (97809, 97801, 97805, 97806, 97802, {awardUnitPrice}, 4, {awardUnitPrice * 4}, false, now());
                """);

            if (scenario != "linked")
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
                var expectedMessage = scenario == "unlinked_award"
                    ? "Unresolved legacy sourcing award lineage blocks procurement upgrade"
                    : "Unresolved legacy supplier quote lineage blocks procurement upgrade";
                Assert.Equal(expectedMessage, error.MessageText);
                Assert.Contains(scenario == "unlinked_award" ? "97809" : "97803", error.Detail);
            }
            else
            {
                await migrator.MigrateAsync();
                await using var lineage = context.Database.GetDbConnection().CreateCommand();
                await context.Database.OpenConnectionAsync();
                lineage.CommandText = """
                    SELECT quoted."BusinessUnitId", quoted."RfqId", quoted."RfqItemId", quoted."ProductId",
                           quoted."SupplierSolicitationId", quoted."ResponseIdempotencyKey",
                           award."SupplierQuotedItemId", award."CurrencyId", award."LandedUnitCost", award."IdempotencyKey"
                    FROM "SupplierQuotedItems" quoted
                    JOIN "SourcingAwards" award ON award."Id" = 97809
                    WHERE quoted."Id" = 97803
                    """;
                await using var reader = await lineage.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(97801L, reader.GetInt64(0));
                Assert.Equal(97805L, reader.GetInt64(1));
                Assert.Equal(97806L, reader.GetInt64(2));
                Assert.Equal(97807L, reader.GetInt64(3));
                Assert.Equal(97808L, reader.GetInt64(4));
                Assert.Equal("legacy-quote:97803", reader.GetString(5));
                Assert.Equal(97803L, reader.GetInt64(6));
                Assert.Equal(97804L, reader.GetInt64(7));
                Assert.Equal(12.5m, reader.GetDecimal(8));
                Assert.Equal("legacy-award:97809", reader.GetString(9));
            }
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

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Migration_installs_procurement_tables_rls_grants_and_append_only_events()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using var migration = connection.CreateCommand();
        migration.CommandText = """
            SELECT count(*) FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = '20260726052445_GovernSupplierSourcingAndProcurement'
            """;
        Assert.Equal(1L, (long)(await migration.ExecuteScalarAsync())!);

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
        await PostReceiptAsync(Receipt(tenantA, offsetA, poA.Id, lineA, 1m, 2, "pg-rls-gr-a", "GR-RLS-A"));
        await PostReceiptAsync(Receipt(tenantB, offsetB, poB.Id, lineB, 1m, 2, "pg-rls-gr-b", "GR-RLS-B"));

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
        var first = Receipt(raceTenant, raceOffset, racePo.Id, raceLineId, 8m, 2, "pg-race-a", "GR-RACE-A");
        var second = Receipt(raceTenant, raceOffset, racePo.Id, raceLineId, 8m, 2, "pg-race-b", "GR-RACE-B");

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
        var over = Receipt(rollbackTenant, rollbackOffset, rollbackPo.Id, rollbackLineId, 9m, 2,
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
        return await Execute(tenant, service => service.IssuePurchaseOrderAsync(new(
            tenant, draft.Id, 1, $"provider-receipt:{key}", $"{key}-issue", "qa", $"corr-{key}-issue",
            new string('a', 64), DateTime.UtcNow)));
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

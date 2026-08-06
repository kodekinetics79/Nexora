using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Module04ProductInventoryMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260730234426_Module03TenantSafeSalesRouting";
    private const string CurrentMigration = "20260731014905_Module04ProductInventoryAuthority";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_backfills_shortage_and_lineage_then_downgrades_and_reupgrades()
    {
        var databaseName = $"nexora_module04_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName };
        await ExecuteAdminAsync(database.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            await using var context = database.ContextForConnectionString(connection.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            // Pinned to the rehearsal era (see Seed.HistoricalLead).
            var lead = Seed.HistoricalLead(context, 98_401, 98_400, "Module 04 migration");
            context.Products.Add(new Product
            {
                Id = 98_410, Buid = 98_400, PartNo = "M04-PART", ProductName = "Migration part",
                IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
            var batch = new LeadIngestionBatch
            {
                Id = Guid.NewGuid(), BusinessUnitId = 98_400, SourceChannel = "Test",
                CreatedBy = "tests", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            var occurrence = new LeadIngestionOccurrence
            {
                BusinessUnitId = 98_400, Batch = batch, SourceChannel = "Test",
                IdempotencyKey = "module04-occurrence", LogicalInquiryFingerprint = new string('a', 64),
                Classification = LeadOccurrenceClassification.New, ProcessingPath = LeadProcessingPath.Deterministic,
                IngestedAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow,
                ActorId = "tests", CorrelationId = "module04-migration",
            };
            var revision = new LeadRevision
            {
                BusinessUnitId = 98_400, Lead = lead, RevisionNumber = 1,
                EstablishedByOccurrence = occurrence, LogicalInquiryFingerprint = new string('b', 64),
                SnapshotJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "tests",
                ProcessingPath = LeadProcessingPath.Deterministic,
            };
            var line = new LeadItemRevision
            {
                BusinessUnitId = 98_400, LineNumber = 1, LineFingerprint = new string('c', 64),
                SnapshotJson = "{\"part\":\"M04-PART\",\"quantity\":20}",
            };
            revision.Items.Add(line);
            var rfq = new Rfq
            {
                Id = 98_420, BusinessUnitId = 98_400, Lead = lead, Rfqno = "M04-RFQ",
                RecDate = DateTime.UtcNow, CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            };
            rfq.InheritCommercialIdentity(lead);
            context.AddRange(revision, rfq);
            await context.SaveChangesAsync();

            // The RFQ LINE is inserted with raw SQL naming only the columns that exist at
            // PreviousMigration, exactly as lead_line_commercial_resolutions is below.
            //
            // It used to be written through the EF model, which silently assumed that every
            // column the CURRENT model knows about already exists at the pinned historical
            // migration. That assumption breaks the moment anyone adds a column to RFQItems —
            // the insert emits the new column and Postgres answers 42703. Naming the columns
            // explicitly pins the test to the schema era it is actually rehearsing, so a future
            // column addition can no longer fail a migration test that has nothing to do with it.
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO public."RFQItems"
                    ("ID", "RFQID", "LineItemNo", "ProductID", "ManufacturerPartNumber",
                     "Quantity", "CreatedBy", "CreatedDate")
                VALUES (98421, 98420, '1', 98410, 'M04-PART', 20, 'tests', now())
                """);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO public.lead_line_commercial_resolutions
                    ("BusinessUnitId", "LeadId", "LeadRevisionId", "LeadLineId", "RfqId", "ProductId",
                     "ResolutionBatchId", "ResourceLimit", "RequestedPartNumber", "RequestedQuantity",
                     "Classification", "AvailableToPromise", "IncomingAvailable", "FulfilmentJson",
                     "RelatedResourcesJson", "ProductResolutionJson", "ResolutionMethod",
                     "EvidenceReference", "InventoryAsOfUtc", "ResolvedOn")
                VALUES
                    (98400, 98401, 1, 1, 98420, 98410,
                     '04040404-0000-0000-0000-000000000001', 10, 'M04-PART', 20,
                     'KnownShortage', 10, 3, '{{}}', '[]', '{{}}', 'MigrationRehearsal',
                     'module04:migration', now(), now())
                """);

            await migrator.MigrateAsync(CurrentMigration);

            var upgraded = await context.Database.SqlQueryRaw<UpgradeRow>("""
                SELECT "ProjectedShortage", "RfqItemId"
                FROM public.lead_line_commercial_resolutions
                WHERE "BusinessUnitId" = 98400
                """).SingleAsync();
            Assert.Equal(7m, upgraded.ProjectedShortage);
            Assert.Equal(98_421, upgraded.RfqItemId);

            Assert.Equal("O", await context.Database.SqlQueryRaw<string>("""
                SELECT tgenabled::text AS "Value"
                FROM pg_trigger
                WHERE tgrelid = 'public.lead_line_commercial_resolutions'::regclass
                  AND tgname = 'commercial_line_resolution_update_guard'
                """).SingleAsync());

            var immutableUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE public.lead_line_commercial_resolutions
                    SET "ProjectedShortage" = 6 WHERE "BusinessUnitId" = 98400
                    """));
            Assert.Equal("P0001", immutableUpdate.SqlState);

            Seed.EnsureBusinessUnit(context, 98_499);
            context.Products.Add(new Product
            {
                Id = 98_419, Buid = 98_499, PartNo = "OTHER-TENANT", ProductName = "Other tenant part",
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
            var crossTenantProduct = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO public.lead_line_commercial_resolutions
                        ("BusinessUnitId", "LeadId", "LeadRevisionId", "LeadLineId", "RfqId", "ProductId",
                         "ResolutionBatchId", "ResourceLimit", "RequestedPartNumber", "RequestedQuantity",
                         "Classification", "AvailableToPromise", "IncomingAvailable", "ProjectedShortage",
                         "FulfilmentJson", "RelatedResourcesJson", "ProductResolutionJson", "ResolutionMethod",
                         "EvidenceReference", "InventoryAsOfUtc", "ResolvedOn")
                    VALUES
                        (98400, 98401, 1, 2, 98420, 98419,
                         '04040404-0000-0000-0000-000000000002', 10, 'OTHER-TENANT', 1,
                         'KnownShortage', 0, 0, 1,
                         '{{}}', '[]', '{{}}', 'MigrationRehearsal',
                         'module04:cross-tenant', now(), now())
                    """));
            Assert.Equal("P0001", crossTenantProduct.SqlState);
            Assert.Contains("product must belong to the same tenant", crossTenantProduct.MessageText);
            Assert.True(await context.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'public.lead_line_commercial_resolutions'::regclass
                      AND contype = 'f'
                      AND conname = 'FK_lead_line_commercial_resolutions_Products_BusinessUnitId_Pr~'
                ) AS "Value"
                """).SingleAsync());

            await migrator.MigrateAsync(PreviousMigration);
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM public.lead_line_commercial_resolutions
                WHERE "BusinessUnitId" = 98400
                """).SingleAsync());
            await migrator.MigrateAsync(CurrentMigration);
            Assert.Equal(7m, await context.Database.SqlQueryRaw<decimal>("""
                SELECT "ProjectedShortage" AS "Value" FROM public.lead_line_commercial_resolutions
                WHERE "BusinessUnitId" = 98400
                """).SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(database.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private sealed record UpgradeRow(decimal ProjectedShortage, long? RfqItemId);

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

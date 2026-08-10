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
            // Raw SQL, same reason as the rows below: saving a Product through EF fires the
            // master-data audit interceptor, whose MasterDataChangeEvents table does not exist at
            // PreviousMigration. Relaxing the interceptor instead would let an audit write be
            // skipped silently, which is worse than a red test.
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO public."Products" ("ID", "BUID", "PartNo", "ProductName", "IsActive", "QtyOnHand", "ReorderPoint", "CreatedBy", "CreatedOn")
                VALUES (98410, 98400, 'M04-PART', 'Migration part', true, 0, 0, 'tests', now())
                """);
            // Batch and occurrence are inserted with raw SQL naming only the columns that exist
            // at PreviousMigration — same reason as the RFQ line below. Writing them through the
            // EF model emits every column the CURRENT model knows about, so adding any column to
            // LeadIngestionOccurrences (e.g. RecordKind) breaks a migration test that has nothing
            // to do with it, with a bare 42703.
            var batchId = Guid.NewGuid();
            var fingerprintA = new string('a', 64);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LeadIngestionBatches"
                    ("Id","BusinessUnitId","SourceChannel","CreatedBy","CreatedAtUtc","UpdatedAtUtc","Version")
                VALUES ({batchId},98400,'Test','tests',now(),now(),1)
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LeadIngestionOccurrences"
                    ("BusinessUnitId","BatchId","SourceChannel","IdempotencyKey","LogicalInquiryFingerprint",
                     "Classification","Confidence","DecisionReasonsJson","PolicyVersion","ProcessingPath",
                     "ExternalAiUsed","IngestedAtUtc","CreatedAtUtc","ActorType","ActorId","CorrelationId","Version")
                VALUES (98400,{batchId},'Test','module04-occurrence',{fingerprintA},
                        'New',1,'[]','release-01a/v1','Deterministic',
                        false,now(),now(),'Service','tests','module04-migration',1)
                """);
            var occurrenceId = (await context.Database.SqlQueryRaw<long>(
                """SELECT "Id" AS "Value" FROM "LeadIngestionOccurrences" WHERE "IdempotencyKey" = 'module04-occurrence'""").ToListAsync()).Single();

            var revision = new LeadRevision
            {
                BusinessUnitId = 98_400, Lead = lead, RevisionNumber = 1,
                EstablishedByOccurrenceId = occurrenceId, LogicalInquiryFingerprint = new string('b', 64),
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

            // Raw-SQL seed: pinned-era database, see Seed.HistoricalBusinessUnit.
            Seed.HistoricalBusinessUnit(context, 98_499);
            // Raw SQL rather than context.Products.Add: saving master data through EF fires the
            // master-data audit interceptor, which writes to MasterDataChangeEvents — a table that
            // does not exist at this pinned migration, so the save dies with 42P01. Making the
            // interceptor tolerate a missing table would be the wrong fix: silently skipping an
            // audit write is precisely the false assurance this build has spent two days removing.
            // Note "ID" and "BUID" upper-case here; Products maps them that way and Inventory does
            // not, and PostgreSQL quoted identifiers are case-sensitive.
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO public."Products" ("ID", "BUID", "PartNo", "ProductName", "QtyOnHand", "ReorderPoint", "CreatedBy", "CreatedOn")
                VALUES (98419, 98499, 'OTHER-TENANT', 'Other tenant part', 0, 0, 'tests', now())
                """);
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

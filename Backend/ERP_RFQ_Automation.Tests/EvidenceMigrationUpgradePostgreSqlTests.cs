using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

public sealed class EvidenceMigrationUpgradePostgreSqlTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AuthoritativeEvidenceMigration_BackfillsLegacyRunsAndGuardsTerminalState()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_evidence_upgrade_host")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();

        var databaseName = $"evidence_upgrade_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = databaseName };
        var legacyRunId = Guid.NewGuid();

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseNpgsql(isolatedBuilder.ConnectionString)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .EnableDetailedErrors()
                .Options;
            await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260724003000_GovernTreasuryRulesAdjustmentsAndCashBridge");

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94901, 'EVMIG', 'Evidence migration', 'tests', now());

                INSERT INTO "ExtractionJobs"
                    ("Id", "BatchId", "BusinessUnitId", "SourceType", "ContentHash", "StoragePath",
                     "FileName", "FileType", "Status", "Priority", "SchedulerTag", "Attempts",
                     "MaxAttempts", "NextAttemptAt", "CreatedOn", "UpdatedOn")
                VALUES
                    (94902, {Guid.NewGuid()}, 94901, 'ManualUpload', {new string('a', 64)},
                     'evidence://legacy/source', 'legacy.csv', 'csv', 'Succeeded', 0, 0, 1, 5,
                     now(), now(), now());

                INSERT INTO document_corpora
                    (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
                VALUES (94903, 94901, {Guid.NewGuid()}, 'ManualUpload', 'Completed', now(), now());

                INSERT INTO source_documents
                    (id, business_unit_id, corpus_id, extraction_job_id, content_hash,
                     original_file_name, detected_mime_type, object_bucket, object_key,
                     object_version, byte_size, page_count, security_status, processing_status,
                     created_on, updated_on)
                VALUES
                    (94904, 94901, 94903, 94902, {new string('b', 64)}, 'legacy.csv', 'text/csv',
                     'legacy-evidence', 'tenant/94901/legacy.csv', 'v1', 128, 1, 'Cleared',
                     'Completed', now(), now());

                INSERT INTO document_pages
                    (id, business_unit_id, document_id, page_number, width, height, rotation,
                     text_hash, ocr_status, ocr_confidence, created_on, updated_on)
                VALUES
                    (94905, 94901, 94904, 1, 100, 100, 0, NULL, 'NotRequired', NULL, now(), now());

                INSERT INTO document_regions
                    (id, business_unit_id, page_id, region_type, x, y, width, height, text,
                     confidence, created_on)
                VALUES (94906, 94901, 94905, 'TableCell', 0, 0, 10, 10, 'RFQ-LEGACY', 1, now());

                INSERT INTO canonical_inquiries
                    (id, business_unit_id, corpus_id, inquiry_number, customer_rfq_number,
                     status, created_on, updated_on)
                VALUES (94907, 94901, 94903, 1, 'RFQ-LEGACY', 'Validated', now(), now());

                INSERT INTO field_evidence
                    (id, business_unit_id, region_id, inquiry_id, line_item_id, field_name,
                     raw_value, normalized_value, confidence, extractor, run_id, created_on)
                VALUES
                    (94908, 94901, 94906, 94907, NULL, 'CustomerRfqNumber', ' RFQ-LEGACY ',
                     'RFQ-LEGACY', 1, 'legacy-parser', {legacyRunId}, now());
                """);

            await migrator.MigrateAsync("20260724004000_AuthoritativeEvidenceIngestion");

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (95901, 'EVMIG2', 'Other evidence tenant', 'tests', now());
                INSERT INTO "ExtractionJobs"
                    ("Id", "BatchId", "BusinessUnitId", "SourceType", "ContentHash", "StoragePath",
                     "FileName", "FileType", "Status", "Priority", "SchedulerTag", "Attempts",
                     "MaxAttempts", "NextAttemptAt", "CreatedOn", "UpdatedOn")
                VALUES
                    (95902, {Guid.NewGuid()}, 95901, 'ManualUpload', {new string('d', 64)},
                     'evidence://other/source', 'other.csv', 'csv', 'Succeeded', 0, 0, 1, 5,
                     now(), now(), now());
                """);

            var crossTenantRun = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO extraction_runs
                        (business_unit_id, source_document_id, run_id, extraction_job_id,
                         attempt_number, parser_version, schema_version, status, page_count,
                         region_count, inquiry_count, line_item_count, evidence_count, finding_count,
                         created_on, updated_on)
                    VALUES (94901, 94904, {Guid.NewGuid()}, 95902, 2, 'test', 'test', 'Pending',
                            0, 0, 0, 0, 0, 0, now(), now())
                    """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenantRun.SqlState);

            var crossTenantOccurrence = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO source_document_occurrences
                        (business_unit_id, source_document_id, corpus_id, extraction_job_id,
                         idempotency_key, source_metadata, received_on)
                    VALUES (94901, 94904, 94903, 95902, 'cross-tenant-job', jsonb_build_object(), now())
                    """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenantOccurrence.SqlState);

            var preserved = await context.Database.SqlQueryRaw<long>("""
                SELECT count(*) AS "Value"
                FROM field_evidence evidence
                JOIN extraction_runs run
                  ON run.business_unit_id = evidence.business_unit_id
                 AND run.run_id = evidence.run_id
                WHERE evidence.id = 94908
                  AND evidence.raw_value = ' RFQ-LEGACY '
                  AND evidence.normalized_value = 'RFQ-LEGACY'
                  AND run.source_document_id = 94904
                  AND run.extraction_job_id = 94902
                  AND run.status = 'Completed'
                  AND run.evidence_count = 1
                  AND run.page_count = 1
                  AND run.region_count = 1
                  AND run.inquiry_count = 1
                """).SingleAsync();
            Assert.Equal(1, preserved);

            var unknownRun = Guid.NewGuid();
            var fkError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO field_evidence
                        (business_unit_id, region_id, inquiry_id, line_item_id, field_name,
                         raw_value, normalized_value, confidence, extractor, run_id, created_on,
                         evidence_key, transformations, validation_status, value_kind)
                    VALUES
                        (94901, 94906, 94907, NULL, 'BuyerName', 'Buyer', 'Buyer', 1,
                         'legacy-parser', {unknownRun}, now(), {new string('c', 64)}, '[]'::jsonb,
                         'Valid', 'Text')
                    """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fkError.SqlState);

            var terminalError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE extraction_runs SET updated_on = now()
                    WHERE business_unit_id = 94901 AND run_id = (
                        SELECT run_id FROM field_evidence WHERE id = 94908)
                    """));
            Assert.Equal("55000", terminalError.SqlState);

            var lifecycleRunId = Guid.NewGuid();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO extraction_runs
                    (business_unit_id, source_document_id, run_id, extraction_job_id,
                     attempt_number, parser_version, schema_version, status, page_count,
                     region_count, inquiry_count, line_item_count, evidence_count, finding_count,
                     created_on, updated_on)
                VALUES
                    (94901, 94904, {lifecycleRunId}, 94902, 2, 'native-test', 'evidence-v2',
                     'Pending', 0, 0, 0, 0, 0, 0, now(), now());

                UPDATE extraction_runs
                SET status = 'Processing', started_on = now(), updated_on = now()
                WHERE business_unit_id = 94901 AND run_id = {lifecycleRunId};

                UPDATE extraction_runs
                SET status = 'Failed', completed_on = now(), failure_reason = 'test failure',
                    updated_on = now()
                WHERE business_unit_id = 94901 AND run_id = {lifecycleRunId};
                """);

            var failedTerminalError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE extraction_runs SET failure_reason = 'rewritten'
                    WHERE business_unit_id = 94901 AND run_id = {lifecycleRunId}
                    """));
            Assert.Equal("55000", failedTerminalError.SqlState);
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
}

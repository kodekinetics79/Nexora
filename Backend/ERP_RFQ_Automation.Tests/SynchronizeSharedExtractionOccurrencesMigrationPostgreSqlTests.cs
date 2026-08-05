using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SynchronizeSharedExtractionOccurrencesMigrationPostgreSqlTests(
    PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260730104456_PilotReadinessDeadLetterOperations";
    private const string CurrentMigration = "20260730193414_SynchronizeSharedExtractionOccurrences";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PopulatedUpgrade_DowngradeAndReupgradePreserveAndCorrectOccurrenceEvidence()
    {
        var databaseName = $"nexora_shared_occurrence_{Guid.NewGuid():N}";
        var rehearsal = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = databaseName
        };

        await ExecuteAdminAsync(database.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            await using var context = database.ContextForConnectionString(rehearsal.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            var historicalMigrationCount = await MigrationCountAsync(context);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (99301, 'SHARED-OCC', 'Shared occurrence migration', 'tests', now());

                INSERT INTO document_corpora
                    (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
                VALUES (99302, 99301, '99301000-0000-0000-0000-000000000001',
                        'ManualUpload', 'Processing', now() - interval '2 minutes', now());

                INSERT INTO source_documents
                    (id, business_unit_id, corpus_id, content_hash, original_file_name,
                     detected_mime_type, object_bucket, object_key, object_version, byte_size,
                     page_count, security_status, processing_status, created_on, updated_on)
                VALUES (99303, 99301, 99302, repeat('a', 64), 'shared-rfq.csv', 'text/csv',
                        'evidence', '99301/shared-rfq.csv', 'v1', 321, 1, 'Cleared', 'Completed',
                        now() - interval '2 minutes', now());

                INSERT INTO source_document_occurrences
                    (id, business_unit_id, source_document_id, corpus_id, idempotency_key,
                     source_metadata, intake_status, original_occurrence_id, outcome_state,
                     processing_reused, parser_reused, ocr_reused, local_model_reused,
                     external_model_reused, received_on, updated_on)
                VALUES
                    (99304, 99301, 99303, 99302, 'original', '{{"source":"test"}}'::jsonb,
                     'Queued', NULL, 'NONE', false, false, false, false, false,
                     now() - interval '2 minutes', now()),
                    (99305, 99301, 99303, 99302, 'duplicate', '{{"source":"test"}}'::jsonb,
                     'Resolved', 99304, 'EXACT_DUPLICATE_CONFIRMED', true, true, true, true, true,
                     now() - interval '1 minute', now());

                INSERT INTO "ExtractionJobs"
                    ("Id", "SourceDocumentOccurrenceId", "BatchId", "BusinessUnitId", "SourceType",
                     "ContentHash", "StoragePath", "FileName", "FileType", "Status", "Priority",
                     "SchedulerTag", "Attempts", "MaxAttempts", "NextAttemptAt", "CreatedOn", "UpdatedOn")
                VALUES (99306, 99304, '99301000-0000-0000-0000-000000000001', 99301,
                        'ManualUpload', repeat('a', 64), 'evidence/99301/shared-rfq.csv',
                        'shared-rfq.csv', 'csv', 'Pending', 0, 0, 0, 5, now(), now(), now());

                UPDATE source_documents SET extraction_job_id = 99306 WHERE id = 99303;
                UPDATE source_document_occurrences SET extraction_job_id = 99306
                WHERE id IN (99304, 99305);
                """);

            await migrator.MigrateAsync(CurrentMigration);

            var corrected = await context.Database.SqlQueryRaw<OccurrenceState>("""
                SELECT intake_status AS "IntakeStatus", processing_reused AS "ProcessingReused",
                       parser_reused AS "ParserReused", ocr_reused AS "OcrReused"
                FROM source_document_occurrences WHERE id = 99305
                """).SingleAsync();
            Assert.Equal("Queued", corrected.IntakeStatus);
            Assert.False(corrected.ProcessingReused);
            Assert.False(corrected.ParserReused);
            Assert.False(corrected.OcrReused);
            Assert.Equal(historicalMigrationCount + 1, await MigrationCountAsync(context));

            var metadataError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE source_document_occurrences SET source_metadata = '{{"source":"changed"}}'::jsonb
                    WHERE id = 99305;
                    """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, metadataError.SqlState);

            var identityError = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE source_documents SET object_key = '99301/replaced.csv' WHERE id = 99303;
                    """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, identityError.SqlState);

            await context.Database.ExecuteSqlRawAsync("""
                UPDATE source_document_occurrences
                SET outcome_state = 'SOURCE_OBJECT_UNAVAILABLE' WHERE id = 99305;
                """);
            await migrator.MigrateAsync(PreviousMigration);
            Assert.Equal("UNSUPPORTED_FORMAT", await OutcomeAsync(context));
            Assert.Equal(historicalMigrationCount, await MigrationCountAsync(context));

            await migrator.MigrateAsync(CurrentMigration);
            Assert.Equal("UNSUPPORTED_FORMAT", await OutcomeAsync(context));
            Assert.Equal(historicalMigrationCount + 1, await MigrationCountAsync(context));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(database.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static Task<int> MigrationCountAsync(DbContext context) =>
        context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM "__EFMigrationsHistory"
            """).SingleAsync();

    private static Task<string> OutcomeAsync(DbContext context) =>
        context.Database.SqlQueryRaw<string>("""
            SELECT outcome_state AS "Value" FROM source_document_occurrences WHERE id = 99305
            """).SingleAsync();

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class OccurrenceState
    {
        public string IntakeStatus { get; init; } = null!;
        public bool ProcessingReused { get; init; }
        public bool ParserReused { get; init; }
        public bool OcrReused { get; init; }
    }
}

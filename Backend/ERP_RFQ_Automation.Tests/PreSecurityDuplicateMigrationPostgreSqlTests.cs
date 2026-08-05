using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PreSecurityDuplicateMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task DataBearingUpgrade_BackfillsDuplicateLineageWithoutFabricatingScannerEvidence()
    {
        var databaseName = $"nexora_p0_dup_upgrade_{Guid.NewGuid():N}";
        await using (var admin = await database.OpenConnectionAsync())
        {
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        var rehearsal = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = databaseName
        };

        try
        {
            await using var context = database.ContextForConnectionString(rehearsal.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260729135045_V2Gate05SalesCoachingGrowthIntelligence");
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (97901, 'P0DUP', 'P0 duplicate upgrade', 'tests', now());

                INSERT INTO document_corpora
                    (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
                VALUES
                    (97902, 97901, '97901000-0000-0000-0000-000000000001',
                     'ManualUpload', 'Completed', now() - interval '2 minutes', now());

                INSERT INTO source_documents
                    (id, business_unit_id, corpus_id, content_hash, original_file_name,
                     detected_mime_type, object_bucket, object_key, object_version, byte_size,
                     page_count, security_status, processing_status, created_on, updated_on)
                VALUES
                    (97903, 97901, 97902, repeat('a', 64), 'legacy-rfq.csv', 'text/csv',
                     'evidence', '97901/legacy-rfq.csv', 'v1', 321, 1, 'Cleared', 'Completed',
                     now() - interval '2 minutes', now());

                INSERT INTO source_document_occurrences
                    (id, business_unit_id, source_document_id, corpus_id, idempotency_key,
                     source_metadata, intake_status, received_on, updated_on)
                VALUES
                    (97904, 97901, 97903, 97902, 'original', '{{}}'::jsonb,
                     'Resolved', now() - interval '2 minutes', now() - interval '2 minutes'),
                    (97905, 97901, 97903, 97902, 'duplicate', '{{}}'::jsonb,
                     'Resolved', now() - interval '1 minute', now() - interval '1 minute');
                """);

            await migrator.MigrateAsync();

            await using var verify = new NpgsqlConnection(rehearsal.ConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT original_occurrence_id, outcome_state, bytes_uploaded,
                       storage_logical_bytes, storage_physical_bytes, cost_status
                FROM source_document_occurrences
                WHERE business_unit_id = 97901
                ORDER BY id;
                """;
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.Equal("NONE", reader.GetString(1));
            Assert.Equal(321, reader.GetInt64(2));
            Assert.Equal(321, reader.GetInt64(3));
            Assert.Equal(0, reader.GetInt64(4));
            Assert.Equal("LOCAL_COMPUTE_UNPRICED", reader.GetString(5));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(97904, reader.GetInt64(0));
            Assert.Equal("EXACT_DUPLICATE_CONFIRMED", reader.GetString(1));
            Assert.Equal(321, reader.GetInt64(2));
            Assert.Equal(321, reader.GetInt64(3));
            Assert.Equal(0, reader.GetInt64(4));
            Assert.Equal("LOCAL_COMPUTE_UNPRICED", reader.GetString(5));
            Assert.False(await reader.ReadAsync());

            await reader.DisposeAsync();
            await command.DisposeAsync();
            await using var verdict = verify.CreateCommand();
            verdict.CommandText = """
                SELECT malware_verdict_status IS NULL AND malware_scanned_on IS NULL
                FROM source_documents WHERE id = 97903;
                """;
            Assert.True((bool)(await verdict.ExecuteScalarAsync())!);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await database.OpenConnectionAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

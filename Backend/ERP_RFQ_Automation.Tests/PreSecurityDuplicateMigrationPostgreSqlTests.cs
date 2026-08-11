using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — this file used to be
/// DataBearingUpgrade_BackfillsDuplicateLineageWithoutFabricatingScannerEvidence.
///
/// It stood a database up at 20260729135045_V2Gate05SalesCoachingGrowthIntelligence, wrote a
/// document with two occurrences that predated the duplicate-lineage and resource-accounting
/// columns, migrated to head and asserted the backfill inferred the duplicate's original, charged
/// both occurrences honestly (uploaded once, stored once, nothing external), and — the assertion
/// the test was named for — left malware_verdict_status and malware_scanned_on NULL rather than
/// inventing a clean verdict for a file no scanner ever saw.
///
/// 20260811033109_SquashedSchemaBaseline erased that id, so there is no earlier point to stand a
/// database up at. The BACKFILL is retired — a row missing those columns cannot exist, they are
/// NOT NULL with store defaults — but the two rules it was obeying are still enforced by the
/// database and are asserted here directly, and more strongly than reading one backfilled row:
///
///   * "do not fabricate scanner evidence" is now a CHECK constraint, not a convention. A verdict
///     without a scan time and an engine is REFUSED, so no code path — migration, service or
///     console — can record that a file was found clean without recording who found it and when.
///   * Un-costed evidence lands on LOCAL_COMPUTE_UNPRICED with zero external spend and outcome
///     NONE by DEFAULT, so an occurrence nobody has priced never reads as free external work.
///   * An occurrence cannot be its own original, which is the degenerate lineage the backfill's
///     inference had to avoid.
///
/// NOT RE-ASSERTED, and named rather than left to be discovered:
///   * bytes_uploaded = storage_logical_bytes = byte_size (321) on both occurrences, and
///     storage_physical_bytes = 0 on the duplicate — the "uploaded once, stored once" arithmetic.
///     Those figures were COPIED FROM byte_size BY THE BACKFILL. The columns default to 0, so a row
///     written today carries 0 until the ingestion pipeline accounts for it; asserting 321 here
///     would require this test to write the number it then checks, which proves nothing. The
///     surviving half — that an unaccounted occurrence reads as zero external spend rather than as
///     unknown — is asserted below.
///   * original_occurrence_id = 97904 and outcome_state = 'EXACT_DUPLICATE_CONFIRMED' on the second
///     occurrence: the duplicate lineage the backfill INFERRED from content hashes. Inference over
///     rows that predate the columns cannot happen again; what replaces it is the constraint that
///     the inference had to respect, asserted below.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PreSecurityDuplicateMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 97_901;
    private const long CorpusId = 97_902;
    private const long DocumentId = 97_903;
    private const long OccurrenceId = 97_904;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Unscanned_evidence_stays_unscanned_and_unpriced_and_cannot_originate_itself()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES ({Tenant}, 'P0DUP', 'P0 duplicate lineage', 'tests', now());

                INSERT INTO document_corpora
                    (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
                VALUES ({CorpusId}, {Tenant}, '97901000-0000-0000-0000-000000000001',
                        'ManualUpload', 'Completed', now() - interval '2 minutes', now());

                INSERT INTO source_documents
                    (id, business_unit_id, corpus_id, content_hash, original_file_name,
                     detected_mime_type, object_bucket, object_key, object_version, byte_size,
                     page_count, security_status, processing_status, created_on, updated_on)
                VALUES ({DocumentId}, {Tenant}, {CorpusId}, repeat('a', 64), 'legacy-rfq.csv', 'text/csv',
                        'evidence', '{Tenant}/legacy-rfq.csv', 'v1', 321, 1, 'Cleared', 'Completed',
                        now() - interval '2 minutes', now());

                INSERT INTO source_document_occurrences
                    (id, business_unit_id, source_document_id, corpus_id, idempotency_key,
                     source_metadata, intake_status, received_on, updated_on)
                VALUES ({OccurrenceId}, {Tenant}, {DocumentId}, {CorpusId}, 'original',
                        jsonb_build_object(), 'Resolved',
                        now() - interval '2 minutes', now() - interval '2 minutes');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // Nothing scanned it, so nothing claims to have.
        await using (var verdict = connection.CreateCommand())
        {
            verdict.Transaction = transaction;
            verdict.CommandText = $"""
                SELECT malware_verdict_status IS NULL AND malware_scanned_on IS NULL
                       AND malware_scanner_engine IS NULL
                FROM source_documents WHERE id = {DocumentId};
                """;
            Assert.True((bool)(await verdict.ExecuteScalarAsync())!);
        }

        // Nothing priced it, so it reads as unpriced local compute rather than free external work.
        await using (var accounting = connection.CreateCommand())
        {
            accounting.Transaction = transaction;
            accounting.CommandText = $"""
                SELECT cost_status = 'LOCAL_COMPUTE_UNPRICED'
                       AND outcome_state = 'NONE'
                       AND original_occurrence_id IS NULL
                       AND external_processing_cost = 0
                       AND storage_physical_bytes = 0
                FROM source_document_occurrences WHERE id = {OccurrenceId};
                """;
            Assert.True((bool)(await accounting.ExecuteScalarAsync())!);
        }

        // A verdict with no scan behind it is refused by the database, not merely avoided by the
        // code that happens to write it today.
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE source_documents SET malware_verdict_status = 'Clean' WHERE id = {DocumentId};
            """);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE source_documents
            SET malware_verdict_status = 'Clean', malware_scanned_on = now()
            WHERE id = {DocumentId};
            """);

        // And an occurrence cannot be recorded as its own original.
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE source_document_occurrences
            SET original_occurrence_id = {OccurrenceId} WHERE id = {OccurrenceId};
            """);

        await transaction.RollbackAsync();
    }

    private static async Task AssertRejectedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await transaction.SaveAsync("guard");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await transaction.RollbackAsync("guard");
    }
}

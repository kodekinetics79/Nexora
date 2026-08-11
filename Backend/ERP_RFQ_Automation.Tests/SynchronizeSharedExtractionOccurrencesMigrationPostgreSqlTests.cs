using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — this file used to be
/// PopulatedUpgrade_DowngradeAndReupgradePreserveAndCorrectOccurrenceEvidence.
///
/// It stood a database up at 20260730104456_PilotReadinessDeadLetterOperations, wrote a source
/// document with two occurrences — an original and a duplicate carrying reuse flags it had no right
/// to — and upgraded to 20260730193414_SynchronizeSharedExtractionOccurrences to prove the
/// correction ran, then walked down and up again to prove neither direction rewrote outcome_state.
///
/// 20260811033109_SquashedSchemaBaseline erased both ids. The CORRECTION is retired: it could only
/// act on occurrences written while the reuse flags were being set wrongly, and no database can
/// reach that state again. What was never about migration identity is the set of guards the
/// migration left behind, and those are asserted here directly, against the live catalogue and
/// against real writes:
///
///   * Shared-evidence metadata is immutable once written — an occurrence cannot be re-described
///     after the fact to justify a reuse decision already taken.
///   * A source document's storage identity is immutable — the object a piece of evidence points at
///     cannot be swapped underneath it.
///   * outcome_state is bounded by a CHECK constraint over the governed vocabulary, so no writer
///     can invent an outcome, and the terminal states the migration introduced are in it.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SynchronizeSharedExtractionOccurrencesMigrationPostgreSqlTests(
    PostgreSqlTestDatabase database)
{
    private const long Tenant = 99_301;
    private const long CorpusId = 99_302;
    private const long DocumentId = 99_303;
    private const long OriginalOccurrenceId = 99_304;
    private const long DuplicateOccurrenceId = 99_305;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Shared_occurrence_evidence_is_immutable_and_outcome_state_is_bounded()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SeedAsync(connection, transaction);

        // 1. The duplicate's metadata cannot be rewritten after the fact.
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE source_document_occurrences
            SET source_metadata = jsonb_build_object('source', 'changed')
            WHERE id = {DuplicateOccurrenceId};
            """);

        // 2. Nor can the storage identity of the document they both point at.
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE source_documents SET object_key = '{Tenant}/replaced.csv' WHERE id = {DocumentId};
            """);

        // 3. outcome_state accepts a governed terminal state…
        await using (var governed = connection.CreateCommand())
        {
            governed.Transaction = transaction;
            governed.CommandText = $"""
                UPDATE source_document_occurrences
                SET outcome_state = 'SOURCE_OBJECT_UNAVAILABLE' WHERE id = {DuplicateOccurrenceId};
                """;
            Assert.Equal(1, await governed.ExecuteNonQueryAsync());
        }

        // …and refuses one nobody governed. Asserted by writing, not by reading the constraint
        // text, because a constraint that exists but is NOT VALID would pass the text check.
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE source_document_occurrences
            SET outcome_state = 'INVENTED_OUTCOME' WHERE id = {DuplicateOccurrenceId};
            """);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Each rejection runs in its own savepoint: a failed statement aborts the enclosing
    /// transaction, so without one the second guard could never be reached and would pass
    /// vacuously.
    /// </summary>
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

    private static async Task SeedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO "BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES ({Tenant}, 'SHARED-OCC', 'Shared occurrence evidence', 'tests', now());

            INSERT INTO document_corpora
                (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
            VALUES ({CorpusId}, {Tenant}, '99301000-0000-0000-0000-000000000001',
                    'ManualUpload', 'Processing', now() - interval '2 minutes', now());

            INSERT INTO source_documents
                (id, business_unit_id, corpus_id, content_hash, original_file_name,
                 detected_mime_type, object_bucket, object_key, object_version, byte_size,
                 page_count, security_status, processing_status, created_on, updated_on)
            VALUES ({DocumentId}, {Tenant}, {CorpusId}, repeat('a', 64), 'shared-rfq.csv', 'text/csv',
                    'evidence', '{Tenant}/shared-rfq.csv', 'v1', 321, 1, 'Cleared', 'Completed',
                    now() - interval '2 minutes', now());

            INSERT INTO source_document_occurrences
                (id, business_unit_id, source_document_id, corpus_id, idempotency_key,
                 source_metadata, intake_status, original_occurrence_id, outcome_state,
                 processing_reused, parser_reused, ocr_reused, local_model_reused,
                 external_model_reused, received_on, updated_on)
            VALUES
                ({OriginalOccurrenceId}, {Tenant}, {DocumentId}, {CorpusId}, 'original',
                 jsonb_build_object('source', 'test'), 'Queued', NULL, 'NONE',
                 false, false, false, false, false, now() - interval '2 minutes', now()),
                ({DuplicateOccurrenceId}, {Tenant}, {DocumentId}, {CorpusId}, 'duplicate',
                 jsonb_build_object('source', 'test'), 'Queued', {OriginalOccurrenceId}, 'NONE',
                 false, false, false, false, false, now() - interval '1 minute', now());
            """;
        await command.ExecuteNonQueryAsync();
    }
}

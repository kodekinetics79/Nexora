using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — this file used to be
/// AuthoritativeEvidenceMigration_BackfillsLegacyRunsAndGuardsTerminalState.
///
/// It started its own container, built a database at
/// 20260724003000_GovernTreasuryRulesAdjustmentsAndCashBridge, wrote a corpus, a document, a page,
/// a region, an inquiry and one piece of field evidence carrying a run_id that no extraction_runs
/// row existed for, then migrated to 20260724004000_AuthoritativeEvidenceIngestion and asserted the
/// migration MANUFACTURED the missing run from the evidence it could see — right counts, right
/// document, right job — instead of orphaning or discarding the evidence.
///
/// 20260811033109_SquashedSchemaBaseline erased both ids. The RECONSTRUCTION is retired: evidence
/// without a run cannot be written any more, because the foreign key that reconstruction existed to
/// make satisfiable is now in place from the first row. That foreign key, and the other three
/// guards the same migration installed, are what this file asserts — at head, on the shared
/// fixture, which also removes a whole container start from the suite:
///
///   * evidence cannot name a run that does not exist;
///   * a run cannot be attached to a job belonging to another tenant, nor an occurrence to a
///     document belonging to one;
///   * a run that has reached a terminal state — Completed or Failed — cannot be edited, so the
///     record of what an extraction found, or why it failed, is not rewritable after the fact.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EvidenceMigrationUpgradePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 94_901;
    private const long OtherTenant = 95_901;
    private const long JobId = 94_902;
    private const long OtherTenantJobId = 95_902;
    private const long CorpusId = 94_903;
    private const long DocumentId = 94_904;
    private const long PageId = 94_905;
    private const long RegionId = 94_906;
    private const long InquiryId = 94_907;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Evidence_lineage_is_tenant_bound_and_terminal_runs_are_immutable()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var completedRun = await SeedAsync(connection, transaction);

        // A run cannot borrow another tenant's extraction job…
        await AssertForeignKeyViolationAsync(connection, transaction, $"""
            INSERT INTO extraction_runs
                (business_unit_id, source_document_id, run_id, extraction_job_id,
                 attempt_number, parser_version, schema_version, status, page_count,
                 region_count, inquiry_count, line_item_count, evidence_count, finding_count,
                 processing_cost_status, ocr_cost_status, created_on, updated_on)
            VALUES ({Tenant}, {DocumentId}, gen_random_uuid(), {OtherTenantJobId}, 2, 'test', 'test',
                    'Pending', 0, 0, 0, 0, 0, 0, 'HistoricalUnpriced', 'HistoricalUnknown', now(), now());
            """);

        // …nor an occurrence.
        await AssertForeignKeyViolationAsync(connection, transaction, $"""
            INSERT INTO source_document_occurrences
                (business_unit_id, source_document_id, corpus_id, extraction_job_id,
                 idempotency_key, source_metadata, received_on)
            VALUES ({Tenant}, {DocumentId}, {CorpusId}, {OtherTenantJobId}, 'cross-tenant-job',
                    jsonb_build_object(), now());
            """);

        // Evidence cannot name a run that was never recorded — the orphan shape the migration had
        // to reconstruct is now unwritable.
        await AssertForeignKeyViolationAsync(connection, transaction, $"""
            INSERT INTO field_evidence
                (business_unit_id, region_id, inquiry_id, line_item_id, field_name,
                 raw_value, normalized_value, confidence, extractor, run_id, created_on,
                 evidence_key, transformations, validation_status, value_kind)
            VALUES ({Tenant}, {RegionId}, {InquiryId}, NULL, 'BuyerName', 'Buyer', 'Buyer', 1,
                    'legacy-parser', gen_random_uuid(), now(), repeat('c', 64), '[]'::jsonb,
                    'Valid', 'Text');
            """);

        // A Completed run is closed.
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE extraction_runs SET updated_on = now()
            WHERE business_unit_id = {Tenant} AND run_id = '{completedRun}';
            """);

        // So is a Failed one — the failure reason is evidence too.
        var failedRun = Guid.NewGuid();
        await using (var lifecycle = connection.CreateCommand())
        {
            lifecycle.Transaction = transaction;
            lifecycle.CommandText = $"""
                INSERT INTO extraction_runs
                    (business_unit_id, source_document_id, run_id, extraction_job_id,
                     attempt_number, parser_version, schema_version, status, page_count,
                     region_count, inquiry_count, line_item_count, evidence_count, finding_count,
                     processing_cost_status, ocr_cost_status, created_on, updated_on)
                VALUES ({Tenant}, {DocumentId}, '{failedRun}', {JobId}, 2, 'native-test', 'evidence-v2',
                        'Pending', 0, 0, 0, 0, 0, 0, 'HistoricalUnpriced', 'HistoricalUnknown', now(), now());

                UPDATE extraction_runs
                SET status = 'Processing', started_on = now(), updated_on = now()
                WHERE business_unit_id = {Tenant} AND run_id = '{failedRun}';

                UPDATE extraction_runs
                SET status = 'Failed', completed_on = now(), failure_reason = 'test failure',
                    updated_on = now()
                WHERE business_unit_id = {Tenant} AND run_id = '{failedRun}';
                """;
            await lifecycle.ExecuteNonQueryAsync();
        }

        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE extraction_runs SET failure_reason = 'rewritten'
            WHERE business_unit_id = {Tenant} AND run_id = '{failedRun}';
            """);

        await transaction.RollbackAsync();
    }

    private static async Task<Guid> SeedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var completedRun = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO "BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES ({Tenant}, 'EVMIG', 'Evidence lineage', 'tests', now()),
                   ({OtherTenant}, 'EVMIG2', 'Other evidence tenant', 'tests', now());

            INSERT INTO "ExtractionJobs"
                ("Id", "BatchId", "BusinessUnitId", "SourceType", "ContentHash", "StoragePath",
                 "FileName", "FileType", "Status", "Priority", "SchedulerTag", "Attempts",
                 "MaxAttempts", "NextAttemptAt", "CreatedOn", "UpdatedOn")
            VALUES ({JobId}, gen_random_uuid(), {Tenant}, 'ManualUpload', repeat('a', 64),
                    'evidence://legacy/source', 'legacy.csv', 'csv', 'Succeeded', 0, 0, 1, 5,
                    now(), now(), now()),
                   ({OtherTenantJobId}, gen_random_uuid(), {OtherTenant}, 'ManualUpload', repeat('d', 64),
                    'evidence://other/source', 'other.csv', 'csv', 'Succeeded', 0, 0, 1, 5,
                    now(), now(), now());

            INSERT INTO document_corpora
                (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
            VALUES ({CorpusId}, {Tenant}, gen_random_uuid(), 'ManualUpload', 'Completed', now(), now());

            INSERT INTO source_documents
                (id, business_unit_id, corpus_id, extraction_job_id, content_hash,
                 original_file_name, detected_mime_type, object_bucket, object_key,
                 object_version, byte_size, page_count, security_status, processing_status,
                 created_on, updated_on)
            VALUES ({DocumentId}, {Tenant}, {CorpusId}, {JobId}, repeat('b', 64), 'legacy.csv', 'text/csv',
                    'legacy-evidence', 'tenant/{Tenant}/legacy.csv', 'v1', 128, 1, 'Cleared',
                    'Completed', now(), now());

            -- page_kind must be named: its store default is '' and ck_document_pages_sheet_name
            -- pairs it with sheet_name, so an unnamed kind is rejected outright.
            INSERT INTO document_pages
                (id, business_unit_id, document_id, page_number, width, height, rotation,
                 text_hash, ocr_status, ocr_confidence, page_kind, created_on, updated_on)
            VALUES ({PageId}, {Tenant}, {DocumentId}, 1, 100, 100, 0, NULL, 'NotRequired', NULL,
                    'PhysicalPage', now(), now());

            INSERT INTO document_regions
                (id, business_unit_id, page_id, region_type, x, y, width, height, text,
                 confidence, created_on)
            VALUES ({RegionId}, {Tenant}, {PageId}, 'TableCell', 0, 0, 10, 10, 'RFQ-LEGACY', 1, now());

            INSERT INTO canonical_inquiries
                (id, business_unit_id, corpus_id, inquiry_number, customer_rfq_number,
                 status, created_on, updated_on)
            VALUES ({InquiryId}, {Tenant}, {CorpusId}, 1, 'RFQ-LEGACY', 'Validated', now(), now());

            INSERT INTO extraction_runs
                (business_unit_id, source_document_id, run_id, extraction_job_id,
                 attempt_number, parser_version, schema_version, status,
                 started_on, completed_on, page_count, region_count, inquiry_count,
                 line_item_count, evidence_count, finding_count,
                 processing_cost_status, ocr_cost_status, created_on, updated_on)
            VALUES ({Tenant}, {DocumentId}, '{completedRun}', {JobId}, 1, 'historical/v1',
                    'historical/v1', 'Completed', now(), now(), 1, 1, 1, 0, 1, 0,
                    'HistoricalUnpriced', 'HistoricalUnknown', now(), now());

            INSERT INTO field_evidence
                (business_unit_id, region_id, inquiry_id, line_item_id, field_name,
                 raw_value, normalized_value, confidence, extractor, run_id, created_on,
                 evidence_key, transformations, validation_status, value_kind)
            VALUES ({Tenant}, {RegionId}, {InquiryId}, NULL, 'CustomerRfqNumber', ' RFQ-LEGACY ',
                    'RFQ-LEGACY', 1, 'legacy-parser', '{completedRun}', now(), repeat('e', 64),
                    '[]'::jsonb, 'Valid', 'Text');
            """;
        await command.ExecuteNonQueryAsync();
        return completedRun;
    }

    private static Task AssertForeignKeyViolationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        AssertSqlStateAsync(connection, transaction, sql, PostgresErrorCodes.ForeignKeyViolation);

    /// <summary>55000 — object_not_in_prerequisite_state, the terminal-state guard's own code.</summary>
    private static Task AssertRejectedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        AssertSqlStateAsync(connection, transaction, sql, PostgresErrorCodes.ObjectNotInPrerequisiteState);

    private static async Task AssertSqlStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, string expectedSqlState)
    {
        await transaction.SaveAsync("guard");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, error.SqlState);
        await transaction.RollbackAsync("guard");
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class SynchronizeSharedExtractionOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences",
                sql: "outcome_state IN ('NONE','EXACT_DUPLICATE_PENDING_SECURITY','EXACT_DUPLICATE_CONFIRMED','BUSINESS_DUPLICATE_CONFIRMED','DUPLICATE_RESCAN_REQUIRED','REVISION','POSSIBLE_MATCH','SECURITY_SCAN_BLOCKED','MALWARE_DETECTED','UNSUPPORTED_FORMAT','SOURCE_OBJECT_UNAVAILABLE','EVIDENCE_INTEGRITY_FAILURE')");

            migrationBuilder.Sql("""
                UPDATE source_document_occurrences AS intake
                SET intake_status = CASE job."Status"
                        WHEN 'Succeeded' THEN 'Resolved'
                        WHEN 'DeadLetter' THEN 'DeadLetter'
                        WHEN 'Leased' THEN 'Processing'
                        WHEN 'Extracting' THEN 'Processing'
                        WHEN 'Persisting' THEN 'Processing'
                        ELSE 'Queued'
                    END,
                    processing_reused = job."Status" = 'Succeeded',
                    parser_reused = job."Status" = 'Succeeded',
                    ocr_reused = job."Status" = 'Succeeded',
                    local_model_reused = job."Status" = 'Succeeded',
                    external_model_reused = job."Status" = 'Succeeded',
                    last_error_category = CASE WHEN job."Status" = 'DeadLetter' THEN 'Extraction' ELSE NULL END,
                    last_error_code = CASE WHEN job."Status" = 'DeadLetter' THEN 'extraction_dead_letter' ELSE NULL END,
                    updated_on = now()
                FROM "ExtractionJobs" AS job
                WHERE intake.business_unit_id = job."BusinessUnitId"
                  AND intake.extraction_job_id = job."Id"
                  AND intake.original_occurrence_id IS NOT NULL
                  AND intake.outcome_state = 'EXACT_DUPLICATE_CONFIRMED';
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_release01c_sync_intake_from_job()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE
                    next_status text;
                    error_category text;
                    error_code text;
                    error_details jsonb;
                BEGIN
                    IF NEW."SourceDocumentOccurrenceId" IS NULL OR NEW."Status" IS NOT DISTINCT FROM OLD."Status" THEN
                        RETURN NEW;
                    END IF;

                    IF NEW."Status" IN ('Leased', 'Extracting') THEN
                        next_status := 'Processing';
                    ELSIF NEW."Status" = 'Pending' AND OLD."Status" IN ('Leased', 'Extracting', 'Persisting') THEN
                        next_status := 'Retryable';
                        error_category := 'Extraction';
                        error_code := 'extraction_retryable';
                    ELSIF NEW."Status" = 'Pending' AND OLD."Status" = 'DeadLetter' THEN
                        next_status := 'Queued';
                    ELSIF NEW."Status" = 'DeadLetter' THEN
                        next_status := 'DeadLetter';
                        error_category := 'Extraction';
                        error_code := 'extraction_dead_letter';
                    ELSIF NEW."Status" = 'Succeeded' THEN
                        next_status := 'Resolved';
                    ELSE
                        RETURN NEW;
                    END IF;

                    IF error_code IS NOT NULL THEN
                        error_details := jsonb_build_object(
                            'attempt', NEW."Attempts",
                            'maxAttempts', NEW."MaxAttempts",
                            'message', left(COALESCE(NEW."LastError", ''), 1000));
                    END IF;

                    UPDATE source_document_occurrences AS intake
                    SET intake_status = CASE
                            WHEN NEW."Status" = 'Succeeded' AND EXISTS (
                                SELECT 1 FROM "LeadIngestionOccurrences" reconciliation
                                WHERE reconciliation."BusinessUnitId" = NEW."BusinessUnitId"
                                  AND reconciliation."SourceDocumentOccurrenceId" = intake.id
                                  AND reconciliation."Classification" = 'PossibleMatchReviewRequired')
                                THEN 'ReviewRequired'
                            ELSE next_status
                        END,
                        processing_reused = CASE
                            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                                THEN true ELSE intake.processing_reused END,
                        parser_reused = CASE
                            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                                THEN true ELSE intake.parser_reused END,
                        ocr_reused = CASE
                            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                                THEN true ELSE intake.ocr_reused END,
                        local_model_reused = CASE
                            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                                THEN true ELSE intake.local_model_reused END,
                        external_model_reused = CASE
                            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                                THEN true ELSE intake.external_model_reused END,
                        last_error_category = error_category,
                        last_error_code = error_code,
                        last_error_details = error_details,
                        updated_on = now()
                    WHERE intake.business_unit_id = NEW."BusinessUnitId"
                      AND intake.extraction_job_id = NEW."Id";
                    RETURN NEW;
                END; $function$;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_protect_source_document_identity()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW.business_unit_id IS DISTINCT FROM OLD.business_unit_id
                       OR NEW.corpus_id IS DISTINCT FROM OLD.corpus_id
                       OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
                       OR NEW.original_file_name IS DISTINCT FROM OLD.original_file_name
                       OR NEW.byte_size IS DISTINCT FROM OLD.byte_size
                       OR NEW.created_on IS DISTINCT FROM OLD.created_on THEN
                        RAISE EXCEPTION 'Source document provenance is immutable' USING ERRCODE = '23514';
                    END IF;

                    IF OLD.security_status = 'Cleared'
                       AND (NEW.object_bucket IS DISTINCT FROM OLD.object_bucket
                            OR NEW.object_key IS DISTINCT FROM OLD.object_key
                            OR NEW.object_version IS DISTINCT FROM OLD.object_version) THEN
                        RAISE EXCEPTION 'Cleared source object identity is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $function$;

                DROP TRIGGER IF EXISTS trg_protect_source_document_identity ON source_documents;
                CREATE TRIGGER trg_protect_source_document_identity
                    BEFORE UPDATE ON source_documents
                    FOR EACH ROW EXECUTE FUNCTION nexora_protect_source_document_identity();

                CREATE OR REPLACE FUNCTION nexora_protect_source_occurrence_metadata()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW.source_metadata IS DISTINCT FROM OLD.source_metadata THEN
                        RAISE EXCEPTION 'Source occurrence metadata is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $function$;

                DROP TRIGGER IF EXISTS trg_protect_source_occurrence_metadata ON source_document_occurrences;
                CREATE TRIGGER trg_protect_source_occurrence_metadata
                    BEFORE UPDATE ON source_document_occurrences
                    FOR EACH ROW EXECUTE FUNCTION nexora_protect_source_occurrence_metadata();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_protect_source_occurrence_metadata ON source_document_occurrences;
                DROP FUNCTION IF EXISTS nexora_protect_source_occurrence_metadata();
                DROP TRIGGER IF EXISTS trg_protect_source_document_identity ON source_documents;
                DROP FUNCTION IF EXISTS nexora_protect_source_document_identity();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences");

            migrationBuilder.Sql("""
                UPDATE source_document_occurrences
                SET outcome_state = 'UNSUPPORTED_FORMAT'
                WHERE outcome_state IN ('SOURCE_OBJECT_UNAVAILABLE', 'EVIDENCE_INTEGRITY_FAILURE');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences",
                sql: "outcome_state IN ('NONE','EXACT_DUPLICATE_PENDING_SECURITY','EXACT_DUPLICATE_CONFIRMED','BUSINESS_DUPLICATE_CONFIRMED','DUPLICATE_RESCAN_REQUIRED','REVISION','POSSIBLE_MATCH','SECURITY_SCAN_BLOCKED','MALWARE_DETECTED','UNSUPPORTED_FORMAT')");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_release01c_sync_intake_from_job()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE
                    next_status text;
                    error_category text;
                    error_code text;
                    error_details jsonb;
                BEGIN
                    IF NEW."SourceDocumentOccurrenceId" IS NULL OR NEW."Status" IS NOT DISTINCT FROM OLD."Status" THEN
                        RETURN NEW;
                    END IF;

                    IF NEW."Status" IN ('Leased', 'Extracting') THEN
                        next_status := 'Processing';
                    ELSIF NEW."Status" = 'Pending' AND OLD."Status" IN ('Leased', 'Extracting', 'Persisting') THEN
                        next_status := 'Retryable';
                        error_category := 'Extraction';
                        error_code := 'extraction_retryable';
                    ELSIF NEW."Status" = 'DeadLetter' THEN
                        next_status := 'DeadLetter';
                        error_category := 'Extraction';
                        error_code := 'extraction_dead_letter';
                    ELSIF NEW."Status" = 'Succeeded' THEN
                        next_status := CASE WHEN EXISTS (
                            SELECT 1 FROM "LeadIngestionOccurrences" occurrence
                            WHERE occurrence."BusinessUnitId" = NEW."BusinessUnitId"
                              AND occurrence."SourceDocumentOccurrenceId" = NEW."SourceDocumentOccurrenceId"
                              AND occurrence."Classification" = 'PossibleMatchReviewRequired')
                            THEN 'ReviewRequired' ELSE 'Resolved' END;
                    ELSE
                        RETURN NEW;
                    END IF;

                    IF error_code IS NOT NULL THEN
                        error_details := jsonb_build_object(
                            'attempt', NEW."Attempts",
                            'maxAttempts', NEW."MaxAttempts",
                            'message', left(COALESCE(NEW."LastError", ''), 1000));
                    END IF;

                    UPDATE source_document_occurrences
                    SET intake_status = next_status,
                        last_error_category = error_category,
                        last_error_code = error_code,
                        last_error_details = error_details,
                        updated_on = now()
                    WHERE business_unit_id = NEW."BusinessUnitId"
                      AND id = NEW."SourceDocumentOccurrenceId";
                    RETURN NEW;
                END; $function$;
                """);
        }
    }
}

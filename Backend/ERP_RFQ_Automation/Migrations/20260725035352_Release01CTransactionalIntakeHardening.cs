using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01CTransactionalIntakeHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "Contacts"
                        WHERE ("CustomerID" IS NULL) = ("SupplierID" IS NULL)) THEN
                        RAISE EXCEPTION 'Release 01C requires every Contact to have exactly one tenant-owned parent'
                            USING ERRCODE = '23514';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "Contacts" contact
                        LEFT JOIN "Suppliers" supplier ON supplier."ID" = contact."SupplierID"
                        WHERE contact."SupplierID" IS NOT NULL
                          AND (supplier."BUID" IS NULL OR supplier."BUID" <> contact."BusinessUnitID")) THEN
                        RAISE EXCEPTION 'Release 01C found a Supplier Contact without tenant-qualified ownership'
                            USING ERRCODE = '23503';
                    END IF;
                END $block$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "last_error_category",
                table: "source_document_occurrences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error_details",
                table: "source_document_occurrences",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_cost_status",
                table: "extraction_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "processing_cost_amount",
                table: "extraction_runs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_cost_currency",
                table: "extraction_runs",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_cost_status",
                table: "extraction_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE extraction_runs DISABLE TRIGGER trg_extraction_runs_guard;
                UPDATE extraction_runs
                SET processing_cost_status = 'HistoricalUnpriced',
                    ocr_cost_status = 'HistoricalUnknown';
                ALTER TABLE extraction_runs ENABLE TRIGGER trg_extraction_runs_guard;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "processing_cost_status",
                table: "extraction_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ocr_cost_status",
                table: "extraction_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_extraction_runs_cost",
                table: "extraction_runs",
                sql: "(processing_cost_amount IS NULL AND processing_cost_currency IS NULL) OR (processing_cost_amount >= 0 AND processing_cost_currency ~ '^[A-Z]{3}$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contacts_ExactlyOneParent",
                table: "Contacts",
                sql: "(\"CustomerID\" IS NULL) <> (\"SupplierID\" IS NULL)");

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

                DROP TRIGGER IF EXISTS trg_release01c_sync_intake_from_job ON "ExtractionJobs";
                CREATE TRIGGER trg_release01c_sync_intake_from_job
                    AFTER UPDATE OF "Status" ON "ExtractionJobs"
                    FOR EACH ROW EXECUTE FUNCTION nexora_release01c_sync_intake_from_job();

                CREATE OR REPLACE FUNCTION nexora_release01b_intake_before_claim_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."Status" = 'Leased' AND OLD."Status" IS DISTINCT FROM NEW."Status" AND NOT EXISTS (
                        SELECT 1 FROM source_document_occurrences occurrence
                        WHERE occurrence.business_unit_id = NEW."BusinessUnitId"
                          AND occurrence.id = NEW."SourceDocumentOccurrenceId"
                          AND occurrence.extraction_job_id = NEW."Id"
                          AND (occurrence.intake_status IN ('Queued', 'Retryable')
                            OR (occurrence.intake_status = 'Processing'
                              AND OLD."Status" IN ('Leased', 'Extracting', 'Persisting')
                              AND (OLD."LeaseExpiresAt" IS NULL OR OLD."LeaseExpiresAt" <= now())))) THEN
                        RAISE EXCEPTION 'Extraction cannot start before its durable intake occurrence is queued' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_release01c_sync_intake_from_job ON "ExtractionJobs";
                DROP FUNCTION IF EXISTS nexora_release01c_sync_intake_from_job();
                CREATE OR REPLACE FUNCTION nexora_release01b_intake_before_claim_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."Status" = 'Leased' AND OLD."Status" IS DISTINCT FROM NEW."Status" AND NOT EXISTS (
                        SELECT 1 FROM source_document_occurrences occurrence
                        WHERE occurrence.business_unit_id = NEW."BusinessUnitId"
                          AND occurrence.id = NEW."SourceDocumentOccurrenceId"
                          AND occurrence.extraction_job_id = NEW."Id"
                          AND occurrence.intake_status IN ('Queued', 'Retryable')) THEN
                        RAISE EXCEPTION 'Extraction cannot start before its durable intake occurrence is queued' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $function$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_extraction_runs_cost",
                table: "extraction_runs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Contacts_ExactlyOneParent",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "last_error_category",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "last_error_details",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "ocr_cost_status",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "processing_cost_amount",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "processing_cost_currency",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "processing_cost_status",
                table: "extraction_runs");
        }
    }
}

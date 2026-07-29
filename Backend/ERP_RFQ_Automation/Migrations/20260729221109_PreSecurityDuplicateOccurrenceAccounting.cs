using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class PreSecurityDuplicateOccurrenceAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "malware_scanned_on",
                table: "source_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "malware_scanner_engine",
                table: "source_documents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "malware_signature_version",
                table: "source_documents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "malware_verdict_status",
                table: "source_documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "bytes_uploaded",
                table: "source_document_occurrences",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "cost_status",
                table: "source_document_occurrences",
                type: "character varying(48)",
                maxLength: 48,
                nullable: false,
                defaultValue: "LOCAL_COMPUTE_UNPRICED");

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_processing_avoided",
                table: "source_document_occurrences",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "external_model_reused",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "external_processing_cost",
                table: "source_document_occurrences",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "hashing_duration_ms",
                table: "source_document_occurrences",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "local_compute_cost",
                table: "source_document_occurrences",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "local_model_reused",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "malware_scan_rerun",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "malware_scan_reused",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ocr_reused",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "original_occurrence_id",
                table: "source_document_occurrences",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome_state",
                table: "source_document_occurrences",
                type: "character varying(48)",
                maxLength: 48,
                nullable: false,
                defaultValue: "NONE");

            migrationBuilder.AddColumn<bool>(
                name: "parser_reused",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "processing_reused",
                table: "source_document_occurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "storage_logical_bytes",
                table: "source_document_occurrences",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "storage_physical_bytes",
                table: "source_document_occurrences",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "total_actual_cost",
                table: "source_document_occurrences",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("""
                UPDATE source_document_occurrences AS occurrence
                SET bytes_uploaded = document.byte_size,
                    storage_logical_bytes = document.byte_size,
                    cost_status = 'LOCAL_COMPUTE_UNPRICED',
                    outcome_state = CASE
                        WHEN occurrence.intake_status = 'AwaitingSecurityScan' THEN 'SECURITY_SCAN_BLOCKED'
                        WHEN occurrence.last_error_code = 'malware_detected' THEN 'MALWARE_DETECTED'
                        WHEN occurrence.last_error_code IN ('unsupported_format', 'document_rejected') THEN 'UNSUPPORTED_FORMAT'
                        ELSE 'NONE'
                    END
                FROM source_documents AS document
                WHERE document.business_unit_id = occurrence.business_unit_id
                  AND document.id = occurrence.source_document_id;

                WITH ordered AS (
                    SELECT id,
                           business_unit_id,
                           source_document_id,
                           FIRST_VALUE(id) OVER (
                               PARTITION BY business_unit_id, source_document_id
                               ORDER BY received_on, id) AS original_id
                    FROM source_document_occurrences
                )
                UPDATE source_document_occurrences AS occurrence
                SET original_occurrence_id = ordered.original_id,
                    outcome_state = CASE
                        WHEN occurrence.outcome_state <> 'NONE' THEN occurrence.outcome_state
                        WHEN document.security_status = 'Cleared' THEN 'EXACT_DUPLICATE_CONFIRMED'
                        ELSE 'DUPLICATE_RESCAN_REQUIRED'
                    END
                FROM ordered
                JOIN source_documents AS document
                  ON document.business_unit_id = ordered.business_unit_id
                 AND document.id = ordered.source_document_id
                WHERE occurrence.id = ordered.id
                  AND occurrence.business_unit_id = ordered.business_unit_id
                  AND occurrence.id <> ordered.original_id;

                UPDATE source_document_occurrences AS occurrence
                SET outcome_state = CASE reconciliation."Classification"
                    WHEN 'ExactDuplicate' THEN 'BUSINESS_DUPLICATE_CONFIRMED'
                    WHEN 'Revision' THEN 'REVISION'
                    WHEN 'PossibleMatchReviewRequired' THEN 'POSSIBLE_MATCH'
                    ELSE occurrence.outcome_state
                END
                FROM "LeadIngestionOccurrences" AS reconciliation
                WHERE reconciliation."BusinessUnitId" = occurrence.business_unit_id
                  AND reconciliation."SourceDocumentOccurrenceId" = occurrence.id
                  AND reconciliation."Classification" IN (
                      'ExactDuplicate','Revision','PossibleMatchReviewRequired');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_documents_malware_verdict",
                table: "source_documents",
                sql: "(malware_verdict_status IS NULL AND malware_scanned_on IS NULL) OR (malware_verdict_status IN ('Clean','Infected','Unavailable','Error') AND malware_scanned_on IS NOT NULL AND malware_scanner_engine IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_source_document_occurrences_tenant_original",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "original_occurrence_id" });

            migrationBuilder.CreateIndex(
                name: "ix_source_document_occurrences_tenant_outcome",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "outcome_state", "received_on" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_original",
                table: "source_document_occurrences",
                sql: "original_occurrence_id IS NULL OR original_occurrence_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences",
                sql: "outcome_state IN ('NONE','EXACT_DUPLICATE_PENDING_SECURITY','EXACT_DUPLICATE_CONFIRMED','BUSINESS_DUPLICATE_CONFIRMED','DUPLICATE_RESCAN_REQUIRED','REVISION','POSSIBLE_MATCH','SECURITY_SCAN_BLOCKED','MALWARE_DETECTED','UNSUPPORTED_FORMAT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_resource_costs",
                table: "source_document_occurrences",
                sql: "local_compute_cost >= 0 AND external_processing_cost >= 0 AND total_actual_cost >= 0 AND estimated_processing_avoided >= 0 AND length(cost_status) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_resource_counts",
                table: "source_document_occurrences",
                sql: "bytes_uploaded >= 0 AND hashing_duration_ms >= 0 AND storage_physical_bytes >= 0 AND storage_logical_bytes >= 0");

            migrationBuilder.AddForeignKey(
                name: "FK_source_document_occurrences_source_document_occurrences_bus~",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "original_occurrence_id" },
                principalTable: "source_document_occurrences",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_source_document_occurrences_source_document_occurrences_bus~",
                table: "source_document_occurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_documents_malware_verdict",
                table: "source_documents");

            migrationBuilder.DropIndex(
                name: "ix_source_document_occurrences_tenant_original",
                table: "source_document_occurrences");

            migrationBuilder.DropIndex(
                name: "ix_source_document_occurrences_tenant_outcome",
                table: "source_document_occurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_original",
                table: "source_document_occurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_resource_costs",
                table: "source_document_occurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_resource_counts",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "malware_scanned_on",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "malware_scanner_engine",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "malware_signature_version",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "malware_verdict_status",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "bytes_uploaded",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "cost_status",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "estimated_processing_avoided",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "external_model_reused",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "external_processing_cost",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "hashing_duration_ms",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "local_compute_cost",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "local_model_reused",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "malware_scan_rerun",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "malware_scan_reused",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "ocr_reused",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "original_occurrence_id",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "outcome_state",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "parser_reused",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "processing_reused",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "storage_logical_bytes",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "storage_physical_bytes",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "total_actual_cost",
                table: "source_document_occurrences");
        }
    }
}

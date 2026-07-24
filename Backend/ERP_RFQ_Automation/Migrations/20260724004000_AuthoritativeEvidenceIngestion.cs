using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritativeEvidenceIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_canonical_inquiries_document_corpora_corpus_id",
                table: "canonical_inquiries");

            migrationBuilder.DropForeignKey(
                name: "FK_canonical_line_items_canonical_inquiries_inquiry_id",
                table: "canonical_line_items");

            migrationBuilder.DropForeignKey(
                name: "FK_document_pages_source_documents_document_id",
                table: "document_pages");

            migrationBuilder.DropForeignKey(
                name: "FK_document_regions_document_pages_page_id",
                table: "document_regions");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_canonical_inquiries_inquiry_id",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_canonical_line_items_line_item_id",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_document_regions_region_id",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_source_documents_document_corpora_corpus_id",
                table: "source_documents");

            migrationBuilder.DropIndex(
                name: "IX_source_documents_corpus_id",
                table: "source_documents");

            migrationBuilder.DropIndex(
                name: "IX_field_evidence_inquiry_id",
                table: "field_evidence");

            migrationBuilder.DropIndex(
                name: "IX_field_evidence_line_item_id",
                table: "field_evidence");

            migrationBuilder.DropIndex(
                name: "IX_document_regions_page_id",
                table: "document_regions");

            migrationBuilder.AddColumn<string>(
                name: "evidence_key",
                table: "field_evidence",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "transformations",
                table: "field_evidence",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "validation_status",
                table: "field_evidence",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "value_kind",
                table: "field_evidence",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "column_number",
                table: "document_regions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_number",
                table: "document_regions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_address",
                table: "document_regions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "page_kind",
                table: "document_pages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sheet_name",
                table: "document_pages",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "lead_item_id",
                table: "canonical_line_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "lead_time_days",
                table: "canonical_line_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_price",
                table: "canonical_line_items",
                type: "numeric(20,6)",
                precision: 20,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_status",
                table: "canonical_line_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bid_closing_date",
                table: "canonical_inquiries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "received_date",
                table: "canonical_inquiries",
                type: "timestamp with time zone",
                nullable: true);

            // Existing evidence rows predate authoritative lifecycle fields. Backfill
            // valid, deterministic values before adding the new checks and unique key.
            migrationBuilder.Sql("""
                UPDATE field_evidence
                SET evidence_key = md5(business_unit_id::text || '|' || id::text || '|' || run_id::text)
                                 || md5('v2|' || business_unit_id::text || '|' || id::text || '|' || run_id::text),
                    transformations = '[]'::jsonb,
                    validation_status = 'Unvalidated',
                    value_kind = 'Text';
                UPDATE document_pages SET page_kind = 'PhysicalPage';
                UPDATE canonical_line_items SET validation_status = 'Unvalidated';
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_source_documents_tenant_id",
                table: "source_documents",
                columns: new[] { "business_unit_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_document_regions_tenant_id",
                table: "document_regions",
                columns: new[] { "business_unit_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_document_pages_tenant_id",
                table: "document_pages",
                columns: new[] { "business_unit_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_document_corpora_tenant_id",
                table: "document_corpora",
                columns: new[] { "business_unit_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_canonical_line_items_tenant_id",
                table: "canonical_line_items",
                columns: new[] { "business_unit_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_canonical_inquiries_tenant_id",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ExtractionJobs_BusinessUnitId_Id",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_source_documents_ExtractionJobs_business_unit_id_extraction_job_id",
                table: "source_documents",
                columns: new[] { "business_unit_id", "extraction_job_id" },
                principalTable: "ExtractionJobs",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateTable(
                name: "extraction_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    source_document_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_job_id = table.Column<long>(type: "bigint", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    parser_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    schema_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    page_count = table.Column<int>(type: "integer", nullable: false),
                    region_count = table.Column<int>(type: "integer", nullable: false),
                    inquiry_count = table.Column<int>(type: "integer", nullable: false),
                    line_item_count = table.Column<int>(type: "integer", nullable: false),
                    evidence_count = table.Column<int>(type: "integer", nullable: false),
                    finding_count = table.Column<int>(type: "integer", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extraction_runs", x => x.id);
                    table.UniqueConstraint("ak_extraction_runs_run_id", x => x.run_id);
                    table.UniqueConstraint("ak_extraction_runs_tenant_id", x => new { x.business_unit_id, x.id });
                    table.UniqueConstraint("ak_extraction_runs_tenant_run_id", x => new { x.business_unit_id, x.run_id });
                    table.CheckConstraint("ck_extraction_runs_attempt", "attempt_number > 0");
                    table.CheckConstraint("ck_extraction_runs_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_extraction_runs_completion", "(status IN ('Completed', 'Failed') AND completed_on IS NOT NULL) OR (status NOT IN ('Completed', 'Failed') AND completed_on IS NULL)");
                    table.CheckConstraint("ck_extraction_runs_counts", "page_count >= 0 AND region_count >= 0 AND inquiry_count >= 0 AND line_item_count >= 0 AND evidence_count >= 0 AND finding_count >= 0");
                    table.CheckConstraint("ck_extraction_runs_failure", "(status = 'Failed' AND failure_reason IS NOT NULL) OR (status <> 'Failed' AND failure_reason IS NULL)");
                    table.CheckConstraint("ck_extraction_runs_job", "extraction_job_id > 0");
                    table.ForeignKey(
                        name: "FK_extraction_runs_ExtractionJobs_business_unit_id_extraction_job_id",
                        columns: x => new { x.business_unit_id, x.extraction_job_id },
                        principalTable: "ExtractionJobs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extraction_runs_source_documents_business_unit_id_source_do~",
                        columns: x => new { x.business_unit_id, x.source_document_id },
                        principalTable: "source_documents",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Legacy field evidence already has a stable tenant/run identity. Build one
            // representative terminal run from its authoritative region -> page -> source
            // chain before enforcing the new FK. Refuse ambiguous or incomplete lineage
            // instead of inventing a tenant, source document, or extraction job.
            migrationBuilder.Sql("""
                DO $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM field_evidence evidence
                        LEFT JOIN document_regions region
                          ON region.id = evidence.region_id
                         AND region.business_unit_id = evidence.business_unit_id
                        LEFT JOIN document_pages page
                          ON page.id = region.page_id
                         AND page.business_unit_id = evidence.business_unit_id
                        LEFT JOIN source_documents source
                          ON source.id = page.document_id
                         AND source.business_unit_id = evidence.business_unit_id
                        LEFT JOIN "ExtractionJobs" job
                          ON job."Id" = source.extraction_job_id
                         AND job."BusinessUnitId" = evidence.business_unit_id
                        WHERE region.id IS NULL
                           OR page.id IS NULL
                           OR source.id IS NULL
                           OR source.extraction_job_id IS NULL
                           OR job."Id" IS NULL
                    ) THEN
                        RAISE EXCEPTION
                            'legacy field evidence has incomplete tenant-qualified source or extraction-job lineage'
                            USING ERRCODE = '23503';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM field_evidence evidence
                        JOIN document_regions region
                          ON region.id = evidence.region_id
                         AND region.business_unit_id = evidence.business_unit_id
                        JOIN document_pages page
                          ON page.id = region.page_id
                         AND page.business_unit_id = evidence.business_unit_id
                        JOIN source_documents source
                          ON source.id = page.document_id
                         AND source.business_unit_id = evidence.business_unit_id
                        GROUP BY evidence.business_unit_id, evidence.run_id
                        HAVING count(DISTINCT source.id) <> 1
                            OR count(DISTINCT source.extraction_job_id) <> 1
                    ) THEN
                        RAISE EXCEPTION
                            'legacy field evidence run maps to more than one source document or extraction job'
                            USING ERRCODE = '23514';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM field_evidence
                        GROUP BY run_id
                        HAVING count(DISTINCT business_unit_id) <> 1
                    ) THEN
                        RAISE EXCEPTION
                            'legacy field evidence run id is reused across tenants'
                            USING ERRCODE = '23505';
                    END IF;
                END; $function$;

                INSERT INTO extraction_runs
                    (business_unit_id, source_document_id, run_id, extraction_job_id,
                     attempt_number, parser_version, schema_version, status, started_on,
                     completed_on, page_count, region_count, inquiry_count, line_item_count,
                     evidence_count, finding_count, failure_reason, created_on, updated_on)
                SELECT
                    evidence.business_unit_id,
                    min(source.id),
                    evidence.run_id,
                    min(source.extraction_job_id),
                    greatest(1, min(job."Attempts")),
                    'legacy-pre-authoritative',
                    'legacy-evidence-v1',
                    'Completed',
                    min(evidence.created_on),
                    max(evidence.created_on),
                    count(DISTINCT page.id)::integer,
                    count(DISTINCT region.id)::integer,
                    count(DISTINCT evidence.inquiry_id)::integer,
                    count(DISTINCT evidence.line_item_id)::integer,
                    count(*)::integer,
                    0,
                    NULL,
                    min(evidence.created_on),
                    max(evidence.created_on)
                FROM field_evidence evidence
                JOIN document_regions region
                  ON region.id = evidence.region_id
                 AND region.business_unit_id = evidence.business_unit_id
                JOIN document_pages page
                  ON page.id = region.page_id
                 AND page.business_unit_id = evidence.business_unit_id
                JOIN source_documents source
                  ON source.id = page.document_id
                 AND source.business_unit_id = evidence.business_unit_id
                JOIN "ExtractionJobs" job
                  ON job."Id" = source.extraction_job_id
                 AND job."BusinessUnitId" = evidence.business_unit_id
                GROUP BY evidence.business_unit_id, evidence.run_id;
                """);

            migrationBuilder.CreateTable(
                name: "source_document_occurrences",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    source_document_id = table.Column<long>(type: "bigint", nullable: false),
                    corpus_id = table.Column<long>(type: "bigint", nullable: false),
                    extraction_job_id = table.Column<long>(type: "bigint", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_metadata = table.Column<string>(type: "jsonb", nullable: false),
                    received_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_document_occurrences", x => x.id);
                    table.UniqueConstraint("ak_source_document_occurrences_tenant_id", x => new { x.business_unit_id, x.id });
                    table.CheckConstraint("ck_source_document_occurrences_business_unit", "business_unit_id > 0");
                    table.ForeignKey(
                        name: "FK_source_document_occurrences_ExtractionJobs_business_unit_id_extraction_job_id",
                        columns: x => new { x.business_unit_id, x.extraction_job_id },
                        principalTable: "ExtractionJobs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_document_occurrences_document_corpora_business_unit_~",
                        columns: x => new { x.business_unit_id, x.corpus_id },
                        principalTable: "document_corpora",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_document_occurrences_source_documents_business_unit_~",
                        columns: x => new { x.business_unit_id, x.source_document_id },
                        principalTable: "source_documents",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "validation_findings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    extraction_run_id = table.Column<long>(type: "bigint", nullable: false),
                    inquiry_id = table.Column<long>(type: "bigint", nullable: true),
                    line_item_id = table.Column<long>(type: "bigint", nullable: true),
                    region_id = table.Column<long>(type: "bigint", nullable: true),
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_findings", x => x.id);
                    table.CheckConstraint("ck_validation_findings_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_validation_findings_target", "NOT (inquiry_id IS NOT NULL AND line_item_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_validation_findings_canonical_inquiries_business_unit_id_in~",
                        columns: x => new { x.business_unit_id, x.inquiry_id },
                        principalTable: "canonical_inquiries",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_validation_findings_canonical_line_items_business_unit_id_l~",
                        columns: x => new { x.business_unit_id, x.line_item_id },
                        principalTable: "canonical_line_items",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_validation_findings_document_regions_business_unit_id_regio~",
                        columns: x => new { x.business_unit_id, x.region_id },
                        principalTable: "document_regions",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_validation_findings_extraction_runs_business_unit_id_extrac~",
                        columns: x => new { x.business_unit_id, x.extraction_run_id },
                        principalTable: "extraction_runs",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_evidence_business_unit_id_region_id",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "region_id" });

            migrationBuilder.CreateIndex(
                name: "ux_field_evidence_tenant_key",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "evidence_key" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_evidence_key",
                table: "field_evidence",
                sql: "evidence_key ~ '^[0-9a-f]{64}$'");

            migrationBuilder.CreateIndex(
                name: "ix_document_regions_page_address",
                table: "document_regions",
                columns: new[] { "page_id", "source_address" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_regions_coordinates",
                table: "document_regions",
                sql: "(row_number IS NULL OR row_number > 0) AND (column_number IS NULL OR column_number > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_regions_source_address",
                table: "document_regions",
                sql: "(row_number IS NULL AND column_number IS NULL) OR source_address IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_pages_sheet_name",
                table: "document_pages",
                sql: "(page_kind = 'PhysicalPage' AND sheet_name IS NULL) OR (page_kind IN ('Worksheet', 'CsvSheet') AND sheet_name IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_canonical_line_items_lead_item",
                table: "canonical_line_items",
                column: "lead_item_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_canonical_line_items_currency",
                table: "canonical_line_items",
                sql: "currency_code IS NULL OR currency_code ~ '^[A-Z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_canonical_line_items_lead_time",
                table: "canonical_line_items",
                sql: "lead_time_days IS NULL OR lead_time_days >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_canonical_line_items_unit_price",
                table: "canonical_line_items",
                sql: "unit_price IS NULL OR unit_price >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_canonical_inquiries_business_unit_id_corpus_id",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "corpus_id" });

            migrationBuilder.CreateIndex(
                name: "IX_extraction_runs_business_unit_id_source_document_id",
                table: "extraction_runs",
                columns: new[] { "business_unit_id", "source_document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_runs_extraction_job",
                table: "extraction_runs",
                column: "extraction_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_extraction_runs_tenant_status_created",
                table: "extraction_runs",
                columns: new[] { "business_unit_id", "status", "created_on" });

            migrationBuilder.CreateIndex(
                name: "ux_extraction_runs_tenant_job_attempt",
                table: "extraction_runs",
                columns: new[] { "business_unit_id", "extraction_job_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_document_occurrences_business_unit_id_corpus_id",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "corpus_id" });

            migrationBuilder.CreateIndex(
                name: "ix_source_document_occurrences_extraction_job",
                table: "source_document_occurrences",
                column: "extraction_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_document_occurrences_tenant_document",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "source_document_id", "received_on" });

            migrationBuilder.CreateIndex(
                name: "ux_source_document_occurrences_tenant_idempotency",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_findings_business_unit_id_inquiry_id",
                table: "validation_findings",
                columns: new[] { "business_unit_id", "inquiry_id" });

            migrationBuilder.CreateIndex(
                name: "IX_validation_findings_business_unit_id_line_item_id",
                table: "validation_findings",
                columns: new[] { "business_unit_id", "line_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_validation_findings_business_unit_id_region_id",
                table: "validation_findings",
                columns: new[] { "business_unit_id", "region_id" });

            migrationBuilder.CreateIndex(
                name: "ix_validation_findings_tenant_code",
                table: "validation_findings",
                columns: new[] { "business_unit_id", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_validation_findings_tenant_run_severity",
                table: "validation_findings",
                columns: new[] { "business_unit_id", "extraction_run_id", "severity" });

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_inquiries_document_corpora_business_unit_id_corpu~",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "corpus_id" },
                principalTable: "document_corpora",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_line_items_canonical_inquiries_business_unit_id_i~",
                table: "canonical_line_items",
                columns: new[] { "business_unit_id", "inquiry_id" },
                principalTable: "canonical_inquiries",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_document_pages_source_documents_business_unit_id_document_id",
                table: "document_pages",
                columns: new[] { "business_unit_id", "document_id" },
                principalTable: "source_documents",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_document_regions_document_pages_business_unit_id_page_id",
                table: "document_regions",
                columns: new[] { "business_unit_id", "page_id" },
                principalTable: "document_pages",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_canonical_inquiries_business_unit_id_inquiry~",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "inquiry_id" },
                principalTable: "canonical_inquiries",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_canonical_line_items_business_unit_id_line_i~",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "line_item_id" },
                principalTable: "canonical_line_items",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_document_regions_business_unit_id_region_id",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "region_id" },
                principalTable: "document_regions",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_extraction_runs_business_unit_id_run_id",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "run_id" },
                principalTable: "extraction_runs",
                principalColumns: new[] { "business_unit_id", "run_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_source_documents_document_corpora_business_unit_id_corpus_id",
                table: "source_documents",
                columns: new[] { "business_unit_id", "corpus_id" },
                principalTable: "document_corpora",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE source_document_occurrences ENABLE ROW LEVEL SECURITY;
                ALTER TABLE extraction_runs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE validation_findings ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON source_document_occurrences TO nexora_tenant_app
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON extraction_runs TO nexora_tenant_app
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON validation_findings TO nexora_tenant_app
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                GRANT SELECT, INSERT, UPDATE ON source_document_occurrences, extraction_runs TO nexora_tenant_app;
                GRANT SELECT, INSERT ON validation_findings TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE source_document_occurrences_id_seq,
                    extraction_runs_id_seq, validation_findings_id_seq TO nexora_tenant_app;
                REVOKE SELECT, UPDATE ON SEQUENCE source_document_occurrences_id_seq,
                    extraction_runs_id_seq, validation_findings_id_seq FROM nexora_tenant_app;
                REVOKE DELETE, TRUNCATE ON source_document_occurrences, extraction_runs, validation_findings
                    FROM nexora_tenant_app;
                REVOKE DELETE, TRUNCATE ON document_corpora, source_documents, document_pages,
                    document_regions, canonical_inquiries, canonical_line_items, field_evidence
                    FROM nexora_tenant_app;

                CREATE OR REPLACE FUNCTION nexora_evidence_append_only()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION '% is immutable evidence', TG_TABLE_NAME USING ERRCODE = '55000';
                END; $function$;
                CREATE TRIGGER trg_validation_findings_append_only
                    BEFORE UPDATE OR DELETE ON validation_findings
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_document_regions_append_only
                    BEFORE UPDATE OR DELETE ON document_regions
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_field_evidence_append_only
                    BEFORE UPDATE OR DELETE ON field_evidence
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_document_pages_append_only
                    BEFORE UPDATE OR DELETE ON document_pages
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_canonical_inquiries_append_only
                    BEFORE UPDATE OR DELETE ON canonical_inquiries
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_canonical_line_items_append_only
                    BEFORE UPDATE OR DELETE ON canonical_line_items
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_document_corpora_no_delete
                    BEFORE DELETE ON document_corpora
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();
                CREATE TRIGGER trg_source_documents_no_delete
                    BEFORE DELETE ON source_documents
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_append_only();

                CREATE OR REPLACE FUNCTION nexora_extraction_run_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'extraction runs cannot be deleted' USING ERRCODE = '55000';
                    END IF;

                    IF (NEW.id, NEW.business_unit_id, NEW.source_document_id, NEW.run_id,
                        NEW.extraction_job_id, NEW.attempt_number, NEW.parser_version,
                        NEW.schema_version, NEW.created_on)
                       IS DISTINCT FROM
                       (OLD.id, OLD.business_unit_id, OLD.source_document_id, OLD.run_id,
                        OLD.extraction_job_id, OLD.attempt_number, OLD.parser_version,
                        OLD.schema_version, OLD.created_on) THEN
                        RAISE EXCEPTION 'extraction run identity and versions are immutable'
                            USING ERRCODE = '55000';
                    END IF;

                    IF NOT (
                        (OLD.status = 'Pending' AND NEW.status IN ('Processing', 'Failed'))
                        OR (OLD.status = 'Processing' AND NEW.status IN ('Completed', 'Failed'))
                    ) THEN
                        RAISE EXCEPTION 'illegal or repeated extraction run transition % -> %',
                            OLD.status, NEW.status USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END; $function$;
                CREATE TRIGGER trg_extraction_runs_guard
                    BEFORE UPDATE OR DELETE ON extraction_runs
                    FOR EACH ROW EXECUTE FUNCTION nexora_extraction_run_guard();

                CREATE OR REPLACE FUNCTION nexora_evidence_occurrence_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'source occurrence is immutable evidence' USING ERRCODE = '55000';
                    END IF;
                    IF OLD.extraction_job_id IS NOT NULL
                       OR NEW.extraction_job_id IS NULL
                       OR (NEW.business_unit_id, NEW.id, NEW.source_document_id, NEW.corpus_id,
                           NEW.idempotency_key, NEW.source_metadata, NEW.received_on)
                          IS DISTINCT FROM
                          (OLD.business_unit_id, OLD.id, OLD.source_document_id, OLD.corpus_id,
                           OLD.idempotency_key, OLD.source_metadata, OLD.received_on) THEN
                        RAISE EXCEPTION 'source occurrence permits only initial job binding' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;
                CREATE TRIGGER trg_source_document_occurrences_guard
                    BEFORE UPDATE OR DELETE ON source_document_occurrences
                    FOR EACH ROW EXECUTE FUNCTION nexora_evidence_occurrence_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_source_document_occurrences_guard ON source_document_occurrences;
                DROP TRIGGER IF EXISTS trg_extraction_runs_guard ON extraction_runs;
                DROP TRIGGER IF EXISTS trg_validation_findings_append_only ON validation_findings;
                DROP TRIGGER IF EXISTS trg_document_regions_append_only ON document_regions;
                DROP TRIGGER IF EXISTS trg_field_evidence_append_only ON field_evidence;
                DROP TRIGGER IF EXISTS trg_document_pages_append_only ON document_pages;
                DROP TRIGGER IF EXISTS trg_canonical_inquiries_append_only ON canonical_inquiries;
                DROP TRIGGER IF EXISTS trg_canonical_line_items_append_only ON canonical_line_items;
                DROP TRIGGER IF EXISTS trg_document_corpora_no_delete ON document_corpora;
                DROP TRIGGER IF EXISTS trg_source_documents_no_delete ON source_documents;
                DROP FUNCTION IF EXISTS nexora_evidence_occurrence_guard();
                DROP FUNCTION IF EXISTS nexora_extraction_run_guard();
                DROP FUNCTION IF EXISTS nexora_evidence_append_only();
                DROP POLICY IF EXISTS nexora_tenant_isolation ON source_document_occurrences;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON extraction_runs;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON validation_findings;
                GRANT DELETE ON document_corpora, source_documents, document_pages,
                    document_regions, canonical_inquiries, canonical_line_items, field_evidence
                    TO nexora_tenant_app;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_canonical_inquiries_document_corpora_business_unit_id_corpu~",
                table: "canonical_inquiries");

            migrationBuilder.DropForeignKey(
                name: "FK_canonical_line_items_canonical_inquiries_business_unit_id_i~",
                table: "canonical_line_items");

            migrationBuilder.DropForeignKey(
                name: "FK_document_pages_source_documents_business_unit_id_document_id",
                table: "document_pages");

            migrationBuilder.DropForeignKey(
                name: "FK_document_regions_document_pages_business_unit_id_page_id",
                table: "document_regions");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_canonical_inquiries_business_unit_id_inquiry~",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_canonical_line_items_business_unit_id_line_i~",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_document_regions_business_unit_id_region_id",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_field_evidence_extraction_runs_business_unit_id_run_id",
                table: "field_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_source_documents_ExtractionJobs_business_unit_id_extraction_job_id",
                table: "source_documents");

            migrationBuilder.DropForeignKey(
                name: "FK_source_documents_document_corpora_business_unit_id_corpus_id",
                table: "source_documents");

            migrationBuilder.DropTable(
                name: "source_document_occurrences");

            migrationBuilder.DropTable(
                name: "validation_findings");

            migrationBuilder.DropTable(
                name: "extraction_runs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ExtractionJobs_BusinessUnitId_Id",
                table: "ExtractionJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_source_documents_tenant_id",
                table: "source_documents");

            migrationBuilder.DropIndex(
                name: "IX_field_evidence_business_unit_id_region_id",
                table: "field_evidence");

            migrationBuilder.DropIndex(
                name: "ux_field_evidence_tenant_key",
                table: "field_evidence");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_evidence_key",
                table: "field_evidence");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_document_regions_tenant_id",
                table: "document_regions");

            migrationBuilder.DropIndex(
                name: "ix_document_regions_page_address",
                table: "document_regions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_regions_coordinates",
                table: "document_regions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_regions_source_address",
                table: "document_regions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_document_pages_tenant_id",
                table: "document_pages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_pages_sheet_name",
                table: "document_pages");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_document_corpora_tenant_id",
                table: "document_corpora");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_canonical_line_items_tenant_id",
                table: "canonical_line_items");

            migrationBuilder.DropIndex(
                name: "ix_canonical_line_items_lead_item",
                table: "canonical_line_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_canonical_line_items_currency",
                table: "canonical_line_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_canonical_line_items_lead_time",
                table: "canonical_line_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_canonical_line_items_unit_price",
                table: "canonical_line_items");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_canonical_inquiries_tenant_id",
                table: "canonical_inquiries");

            migrationBuilder.DropIndex(
                name: "IX_canonical_inquiries_business_unit_id_corpus_id",
                table: "canonical_inquiries");

            migrationBuilder.DropColumn(
                name: "evidence_key",
                table: "field_evidence");

            migrationBuilder.DropColumn(
                name: "transformations",
                table: "field_evidence");

            migrationBuilder.DropColumn(
                name: "validation_status",
                table: "field_evidence");

            migrationBuilder.DropColumn(
                name: "value_kind",
                table: "field_evidence");

            migrationBuilder.DropColumn(
                name: "column_number",
                table: "document_regions");

            migrationBuilder.DropColumn(
                name: "row_number",
                table: "document_regions");

            migrationBuilder.DropColumn(
                name: "source_address",
                table: "document_regions");

            migrationBuilder.DropColumn(
                name: "page_kind",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "sheet_name",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "lead_item_id",
                table: "canonical_line_items");

            migrationBuilder.DropColumn(
                name: "lead_time_days",
                table: "canonical_line_items");

            migrationBuilder.DropColumn(
                name: "unit_price",
                table: "canonical_line_items");

            migrationBuilder.DropColumn(
                name: "validation_status",
                table: "canonical_line_items");

            migrationBuilder.DropColumn(
                name: "bid_closing_date",
                table: "canonical_inquiries");

            migrationBuilder.DropColumn(
                name: "received_date",
                table: "canonical_inquiries");

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_corpus_id",
                table: "source_documents",
                column: "corpus_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_evidence_inquiry_id",
                table: "field_evidence",
                column: "inquiry_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_evidence_line_item_id",
                table: "field_evidence",
                column: "line_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_regions_page_id",
                table: "document_regions",
                column: "page_id");

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_inquiries_document_corpora_corpus_id",
                table: "canonical_inquiries",
                column: "corpus_id",
                principalTable: "document_corpora",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_line_items_canonical_inquiries_inquiry_id",
                table: "canonical_line_items",
                column: "inquiry_id",
                principalTable: "canonical_inquiries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_document_pages_source_documents_document_id",
                table: "document_pages",
                column: "document_id",
                principalTable: "source_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_document_regions_document_pages_page_id",
                table: "document_regions",
                column: "page_id",
                principalTable: "document_pages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_canonical_inquiries_inquiry_id",
                table: "field_evidence",
                column: "inquiry_id",
                principalTable: "canonical_inquiries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_canonical_line_items_line_item_id",
                table: "field_evidence",
                column: "line_item_id",
                principalTable: "canonical_line_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_evidence_document_regions_region_id",
                table: "field_evidence",
                column: "region_id",
                principalTable: "document_regions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_source_documents_document_corpora_corpus_id",
                table: "source_documents",
                column: "corpus_id",
                principalTable: "document_corpora",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

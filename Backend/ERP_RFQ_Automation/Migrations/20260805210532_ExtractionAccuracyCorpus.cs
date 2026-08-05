using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// EXTRACTION ACCURACY CORPUS.
    ///
    /// Production evidence this migration exists to fix: 27 leads, 2,966 line items, and no
    /// measured accuracy of any kind. The confidences the product persists are not
    /// measurements — the structured path writes a literal 1.0 when a cell parsed and 0.2
    /// when it did not, and the model path stores the model's own self-report against a
    /// rubric written into its prompt. Zero LeadReviewAudits had ever been read.
    ///
    /// The labels already existed and were being thrown away: every human review writes a
    /// whole-lead before/after image, and an APPROVED review is a human assertion that the
    /// after image is correct. This table is that assertion reduced to countable cells —
    /// one row per (approved document, field) with how many machine values the reviewer
    /// judged and how many were wrong.
    ///
    /// Deliberately NOT here: a backfill. There are no approved reviews to harvest, because
    /// the approval path was closed for upload-door leads until this change set; the corpus
    /// begins accumulating at the first pilot approval.
    /// </summary>
    public partial class ExtractionAccuracyCorpus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtractionCorpusEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    LeadReviewAuditId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentOccurrenceId = table.Column<long>(type: "bigint", nullable: true),
                    ExtractionPath = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FieldName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservedCount = table.Column<int>(type: "integer", nullable: false),
                    CorrectedCount = table.Column<int>(type: "integer", nullable: false),
                    FieldCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    CapturedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionCorpusEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtractionCorpusEntries_LeadReviewAudits_LeadReviewAuditId",
                        column: x => x.LeadReviewAuditId,
                        principalTable: "LeadReviewAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionCorpusEntries_BU_Path_Field",
                table: "ExtractionCorpusEntries",
                columns: new[] { "BusinessUnitId", "ExtractionPath", "Scope", "FieldName" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionCorpusEntries_LeadReviewAuditId",
                table: "ExtractionCorpusEntries",
                column: "LeadReviewAuditId");

            migrationBuilder.CreateIndex(
                name: "UX_ExtractionCorpusEntries_BU_Audit_Field",
                table: "ExtractionCorpusEntries",
                columns: new[] { "BusinessUnitId", "LeadReviewAuditId", "Scope", "FieldName" },
                unique: true);

            // Tenant isolation in the DATABASE, not only in EF — mirrors
            // 20260805202414_ClientOrganisationIdentity. Corpus rows are append-only
            // evidence: the application role may INSERT and SELECT, and nothing more.
            // A row that could be UPDATEd or DELETEd by the application is a label that
            // could be quietly edited into agreement with the machine, which would make
            // every number computed from it worthless.
            migrationBuilder.Sql("""
                ALTER TABLE public."ExtractionCorpusEntries" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ExtractionCorpusEntries" FORCE ROW LEVEL SECURITY;

                DO $security$
                DECLARE corpus_sequence text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        CREATE POLICY nexora_tenant_isolation ON public."ExtractionCorpusEntries"
                            TO nexora_tenant_app
                            USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                            WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                        REVOKE ALL ON TABLE public."ExtractionCorpusEntries" FROM nexora_tenant_app;
                        GRANT SELECT, INSERT ON TABLE public."ExtractionCorpusEntries" TO nexora_tenant_app;
                        corpus_sequence := pg_get_serial_sequence('public."ExtractionCorpusEntries"', 'Id');
                        IF corpus_sequence IS NOT NULL THEN
                            EXECUTE format('REVOKE ALL ON SEQUENCE %s FROM nexora_tenant_app', corpus_sequence);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', corpus_sequence);
                        END IF;
                    END IF;
                END
                $security$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtractionCorpusEntries");
        }
    }
}

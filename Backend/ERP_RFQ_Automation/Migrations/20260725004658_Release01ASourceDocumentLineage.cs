using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01ASourceDocumentLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeadOccurrenceDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OccurrenceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    LinkedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadOccurrenceDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadOccurrenceDocuments_LeadIngestionOccurrences_BusinessUn~",
                        columns: x => new { x.BusinessUnitId, x.OccurrenceId },
                        principalTable: "LeadIngestionOccurrences",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadOccurrenceDocuments_source_documents_BusinessUnitId_Sou~",
                        columns: x => new { x.BusinessUnitId, x.SourceDocumentId },
                        principalTable: "source_documents",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeadOccurrenceDocuments_BusinessUnitId_OccurrenceId_SourceD~",
                table: "LeadOccurrenceDocuments",
                columns: new[] { "BusinessUnitId", "OccurrenceId", "SourceDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadOccurrenceDocuments_BusinessUnitId_SourceDocumentId",
                table: "LeadOccurrenceDocuments",
                columns: new[] { "BusinessUnitId", "SourceDocumentId" });

            migrationBuilder.Sql("""
                ALTER TABLE "LeadOccurrenceDocuments" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "LeadOccurrenceDocuments" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "LeadOccurrenceDocuments" TO nexora_tenant_app
                  USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                  WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                GRANT SELECT, INSERT ON TABLE "LeadOccurrenceDocuments" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE "LeadOccurrenceDocuments_Id_seq" TO nexora_tenant_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "LeadOccurrenceDocuments" FROM nexora_tenant_app;
                CREATE TRIGGER trg_lead_occurrence_documents_append_only
                  BEFORE UPDATE OR DELETE ON "LeadOccurrenceDocuments"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_forbid_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadOccurrenceDocuments");
        }
    }
}

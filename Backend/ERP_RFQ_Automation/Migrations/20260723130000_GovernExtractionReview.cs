using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernExtractionReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CommercialFactsVerified",
                table: "Leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresCommercialReview",
                table: "Leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReviewApprovedBy",
                table: "Leads",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewApprovedOn",
                table: "Leads",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReviewVersion",
                table: "Leads",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Leads_BusinessUnitID_ID",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.Sql("""
                UPDATE public."Leads" lead
                SET "RequiresCommercialReview" = TRUE,
                    "CommercialFactsVerified" = FALSE,
                    "ReviewVersion" = 1
                WHERE lead."AIConfidence" IS NOT NULL
                  AND COALESCE(lead."LeadSource", '') <> 'Excel Upload'
                  AND NOT EXISTS (
                      SELECT 1 FROM public."RFQ" rfq WHERE rfq."LeadID" = lead."ID");

                UPDATE public."EmailIngests" ingest
                SET "ParseStatus" = 'NeedsReview'
                FROM public."Leads" lead
                WHERE lead."EmailIngestsID" = ingest."ID"
                  AND lead."RequiresCommercialReview" = TRUE
                  AND lead."CommercialFactsVerified" = FALSE;
                """);

            migrationBuilder.CreateTable(
                name: "LeadReviewAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    FromVersion = table.Column<long>(type: "bigint", nullable: false),
                    ToVersion = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: false),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReviewedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadReviewAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadReviewAudits_Leads_BusinessUnitId_LeadId",
                        columns: x => new { x.BusinessUnitId, x.LeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_LeadReviewAudits_BU_Lead_Version",
                table: "LeadReviewAudits",
                columns: new[] { "BusinessUnitId", "LeadId", "ToVersion" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."LeadReviewAudits" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LeadReviewAudits" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."LeadReviewAudits";
                CREATE POLICY nexora_tenant_isolation ON public."LeadReviewAudits"
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                REVOKE ALL PRIVILEGES ON TABLE public."LeadReviewAudits" FROM nexora_tenant_app;
                GRANT SELECT, INSERT ON TABLE public."LeadReviewAudits" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."LeadReviewAudits_Id_seq" TO nexora_tenant_app;

                CREATE OR REPLACE FUNCTION public.nexora_reject_lead_review_audit_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'Lead review audit records are immutable' USING ERRCODE = '55000';
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_reject_lead_review_audit_mutation() FROM PUBLIC;
                DROP TRIGGER IF EXISTS lead_review_audits_immutable ON public."LeadReviewAudits";
                CREATE TRIGGER lead_review_audits_immutable
                    BEFORE UPDATE OR DELETE ON public."LeadReviewAudits"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_lead_review_audit_mutation();
                DROP TRIGGER IF EXISTS lead_review_audits_reject_truncate ON public."LeadReviewAudits";
                CREATE TRIGGER lead_review_audits_reject_truncate
                    BEFORE TRUNCATE ON public."LeadReviewAudits"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_lead_review_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS lead_review_audits_reject_truncate ON public."LeadReviewAudits";
                DROP TRIGGER IF EXISTS lead_review_audits_immutable ON public."LeadReviewAudits";
                DROP FUNCTION IF EXISTS public.nexora_reject_lead_review_audit_mutation();
                """);

            migrationBuilder.DropTable(
                name: "LeadReviewAudits");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Leads_BusinessUnitID_ID",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CommercialFactsVerified",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "RequiresCommercialReview",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ReviewApprovedBy",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ReviewApprovedOn",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ReviewVersion",
                table: "Leads");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CoreLeadRfqQuoteIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_line_commercial_resolutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    LeadRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    LeadLineId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: true),
                    ProductId = table.Column<long>(type: "bigint", nullable: true),
                    RequestedPartNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Classification = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AvailableToPromise = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IncomingAvailable = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FulfilmentJson = table.Column<string>(type: "jsonb", nullable: false),
                    RelatedResourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProductResolutionJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResolutionMethod = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    InventoryAsOfUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ResolvedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_line_commercial_resolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lead_line_commercial_resolutions_LeadItemRevisions_Business~",
                        columns: x => new { x.BusinessUnitId, x.LeadLineId },
                        principalTable: "LeadItemRevisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_line_commercial_resolutions_LeadRevisions_BusinessUnit~",
                        columns: x => new { x.BusinessUnitId, x.LeadRevisionId },
                        principalTable: "LeadRevisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadLineId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadRevisionId", "LeadLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.Sql("""
                ALTER TABLE public.lead_line_commercial_resolutions ENABLE ROW LEVEL SECURITY;

                DO $govern$
                DECLARE resolution_sequence text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        DROP POLICY IF EXISTS nexora_tenant_isolation ON public.lead_line_commercial_resolutions;
                        CREATE POLICY nexora_tenant_isolation ON public.lead_line_commercial_resolutions
                            TO nexora_tenant_app
                            USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                            WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                        GRANT SELECT, INSERT, UPDATE ON public.lead_line_commercial_resolutions TO nexora_tenant_app;
                        SELECT pg_get_serial_sequence('public.lead_line_commercial_resolutions', 'Id') INTO resolution_sequence;
                        IF resolution_sequence IS NOT NULL THEN
                            EXECUTE 'GRANT USAGE ON SEQUENCE ' || resolution_sequence || ' TO nexora_tenant_app';
                        END IF;
                    END IF;
                END $govern$;

                CREATE OR REPLACE FUNCTION public.nexora_validate_commercial_line_resolution()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM public."Leads" l
                        WHERE l."ID" = NEW."LeadId" AND l."BusinessUnitID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'resolution lead must belong to the same tenant';
                    END IF;
                    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."Products" p
                        WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'resolution product must belong to the same tenant';
                    END IF;
                    IF NEW."RfqId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."RFQ" r
                        WHERE r."ID" = NEW."RfqId" AND r."BusinessUnitID" = NEW."BusinessUnitId"
                          AND r."LeadID" = NEW."LeadId") THEN
                        RAISE EXCEPTION 'resolution RFQ must belong to the same tenant and lead';
                    END IF;
                    RETURN NEW;
                END $fn$;

                CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_line_resolution_update()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW."RfqId" IS NOT NULL AND OLD."RfqId" IS NULL
                       AND NEW."BusinessUnitId" = OLD."BusinessUnitId"
                       AND NEW."LeadId" = OLD."LeadId"
                       AND NEW."LeadRevisionId" = OLD."LeadRevisionId"
                       AND NEW."LeadLineId" = OLD."LeadLineId"
                       AND NEW."ProductId" IS NOT DISTINCT FROM OLD."ProductId"
                       AND NEW."RequestedPartNumber" = OLD."RequestedPartNumber"
                       AND NEW."RequestedQuantity" = OLD."RequestedQuantity"
                       AND NEW."Classification" = OLD."Classification"
                       AND NEW."AvailableToPromise" = OLD."AvailableToPromise"
                       AND NEW."IncomingAvailable" = OLD."IncomingAvailable"
                       AND NEW."FulfilmentJson" = OLD."FulfilmentJson"
                       AND NEW."RelatedResourcesJson" = OLD."RelatedResourcesJson"
                       AND NEW."ProductResolutionJson" = OLD."ProductResolutionJson"
                       AND NEW."ResolutionMethod" = OLD."ResolutionMethod"
                       AND NEW."EvidenceReference" IS NOT DISTINCT FROM OLD."EvidenceReference"
                       AND NEW."InventoryAsOfUtc" = OLD."InventoryAsOfUtc"
                       AND NEW."ResolvedOn" = OLD."ResolvedOn" THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'commercial line resolutions are immutable';
                END $fn$;

                CREATE TRIGGER commercial_line_resolution_tenant_integrity
                    BEFORE INSERT OR UPDATE ON public.lead_line_commercial_resolutions
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_commercial_line_resolution();
                CREATE TRIGGER commercial_line_resolution_update_guard
                    BEFORE UPDATE ON public.lead_line_commercial_resolutions
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_line_resolution_update();
                CREATE TRIGGER commercial_line_resolution_delete_guard
                    BEFORE DELETE ON public.lead_line_commercial_resolutions
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS commercial_line_resolution_delete_guard ON public.lead_line_commercial_resolutions;
                DROP TRIGGER IF EXISTS commercial_line_resolution_update_guard ON public.lead_line_commercial_resolutions;
                DROP TRIGGER IF EXISTS commercial_line_resolution_tenant_integrity ON public.lead_line_commercial_resolutions;
                DROP FUNCTION IF EXISTS public.nexora_guard_commercial_line_resolution_update();
                DROP FUNCTION IF EXISTS public.nexora_validate_commercial_line_resolution();
                """);
            migrationBuilder.DropTable(
                name: "lead_line_commercial_resolutions");
        }
    }
}

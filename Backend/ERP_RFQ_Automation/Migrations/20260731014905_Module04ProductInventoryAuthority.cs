using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Module04ProductInventoryAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CostCurrencyCode",
                table: "lead_line_commercial_resolutions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpectedAvailableOn",
                table: "lead_line_commercial_resolutions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LeadTimeDays",
                table: "lead_line_commercial_resolutions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProjectedShortage",
                table: "lead_line_commercial_resolutions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "RfqItemId",
                table: "lead_line_commercial_resolutions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "lead_line_commercial_resolutions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE public.lead_line_commercial_resolutions
                    DISABLE TRIGGER commercial_line_resolution_update_guard;

                UPDATE public.lead_line_commercial_resolutions
                SET "ProjectedShortage" = CASE
                    WHEN "Classification" = 'NonInventoryService' THEN 0
                    ELSE GREATEST(0, "RequestedQuantity" - "AvailableToPromise" - "IncomingAvailable")
                END;

                WITH product_candidates AS (
                    SELECT resolution."Id" AS resolution_id, MIN(item."ID") AS item_id
                    FROM public.lead_line_commercial_resolutions AS resolution
                    JOIN public."RFQItems" AS item
                      ON item."RFQID" = resolution."RfqId"
                     AND resolution."ProductId" IS NOT NULL
                     AND item."ProductID" = resolution."ProductId"
                    GROUP BY resolution."Id"
                    HAVING COUNT(*) = 1
                )
                UPDATE public.lead_line_commercial_resolutions AS resolution
                SET "RfqItemId" = candidate.item_id
                FROM product_candidates AS candidate
                WHERE candidate.resolution_id = resolution."Id";

                WITH line_candidates AS (
                    SELECT resolution."Id" AS resolution_id, MIN(item."ID") AS item_id
                    FROM public.lead_line_commercial_resolutions AS resolution
                    JOIN public."LeadItemRevisions" AS lead_line
                      ON lead_line."BusinessUnitId" = resolution."BusinessUnitId"
                     AND lead_line."Id" = resolution."LeadLineId"
                    JOIN public."RFQItems" AS item
                      ON item."RFQID" = resolution."RfqId"
                     AND item."LineItemNo" = lead_line."LineNumber"::text
                    WHERE resolution."RfqItemId" IS NULL
                    GROUP BY resolution."Id"
                    HAVING COUNT(*) = 1
                )
                UPDATE public.lead_line_commercial_resolutions AS resolution
                SET "RfqItemId" = candidate.item_id
                FROM line_candidates AS candidate
                WHERE candidate.resolution_id = resolution."Id";

                ALTER TABLE public.lead_line_commercial_resolutions
                    ENABLE TRIGGER commercial_line_resolution_update_guard;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_line_resolution_update()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW."RfqId" IS NOT NULL AND OLD."RfqId" IS NULL
                       AND NEW."RfqItemId" IS NOT NULL AND OLD."RfqItemId" IS NULL
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
                       AND NEW."ProjectedShortage" = OLD."ProjectedShortage"
                       AND NEW."LeadTimeDays" IS NOT DISTINCT FROM OLD."LeadTimeDays"
                       AND NEW."ExpectedAvailableOn" IS NOT DISTINCT FROM OLD."ExpectedAvailableOn"
                       AND NEW."UnitCost" IS NOT DISTINCT FROM OLD."UnitCost"
                       AND NEW."CostCurrencyCode" IS NOT DISTINCT FROM OLD."CostCurrencyCode"
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
                """);

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_ProductId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId_RfqIt~",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "RfqId", "RfqItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_RfqItemId_RfqId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_resolution_cost_evidence",
                table: "lead_line_commercial_resolutions",
                sql: "(\"UnitCost\" IS NULL AND \"CostCurrencyCode\" IS NULL) OR (\"UnitCost\" >= 0 AND \"CostCurrencyCode\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_resolution_lead_time",
                table: "lead_line_commercial_resolutions",
                sql: "\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_resolution_quantities",
                table: "lead_line_commercial_resolutions",
                sql: "\"RequestedQuantity\" > 0 AND \"AvailableToPromise\" >= 0 AND \"IncomingAvailable\" >= 0 AND \"ProjectedShortage\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_resolution_rfq_item",
                table: "lead_line_commercial_resolutions",
                sql: "\"RfqItemId\" IS NULL OR \"RfqId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_lead_line_commercial_resolutions_Leads_BusinessUnitId_LeadId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadId" },
                principalTable: "Leads",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_line_commercial_resolutions_Products_BusinessUnitId_Pr~",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_line_commercial_resolutions_RFQItems_RfqItemId_RfqId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "RfqItemId", "RfqId" },
                principalTable: "RFQItems",
                principalColumns: new[] { "ID", "RFQID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_line_commercial_resolutions_RFQ_BusinessUnitId_RfqId",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "RfqId" },
                principalTable: "RFQ",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_lead_line_commercial_resolutions_Leads_BusinessUnitId_LeadId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_line_commercial_resolutions_Products_BusinessUnitId_Pr~",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_line_commercial_resolutions_RFQItems_RfqItemId_RfqId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_line_commercial_resolutions_RFQ_BusinessUnitId_RfqId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_ProductId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId_RfqIt~",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_RfqItemId_RfqId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_resolution_cost_evidence",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_resolution_lead_time",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_resolution_quantities",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_resolution_rfq_item",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "CostCurrencyCode",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "ExpectedAvailableOn",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "LeadTimeDays",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "ProjectedShortage",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "RfqItemId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "lead_line_commercial_resolutions");
        }
    }
}

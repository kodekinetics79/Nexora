using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AppendCommercialResolutionSnapshotsAndOwnershipIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.AddColumn<Guid>(
                name: "ResolutionBatchId",
                table: "lead_line_commercial_resolutions",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<int>(
                name: "ResourceLimit",
                table: "lead_line_commercial_resolutions",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<string>(
                name: "MutationIdempotencyKey",
                table: "customer_ownerships",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisi~1",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadRevisionId", "LeadLineId", "ResolvedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadRevisionId", "LeadLineId", "ResolutionBatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_BusinessUnitId_MutationIdempotencyKey",
                table: "customer_ownerships",
                columns: new[] { "BusinessUnitId", "MutationIdempotencyKey" },
                unique: true,
                filter: "\"MutationIdempotencyKey\" IS NOT NULL");

            migrationBuilder.Sql("""
                DO $inventory_warehouse_guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM public."Inventory" i
                        LEFT JOIN public."Warehouses" w
                          ON w."ID" = i."WarehouseId" AND w."BusinessUnitID" = i."Buid"
                        WHERE i."WarehouseId" IS NOT NULL AND w."ID" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'cross-tenant inventory warehouse relationships must be resolved before upgrade';
                    END IF;
                END $inventory_warehouse_guard$;

                CREATE OR REPLACE FUNCTION public.nexora_validate_inventory_warehouse_tenant()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW."WarehouseId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM public."Warehouses" w
                        WHERE w."ID" = NEW."WarehouseId" AND w."BusinessUnitID" = NEW."Buid") THEN
                        RAISE EXCEPTION 'inventory warehouse must belong to the same tenant';
                    END IF;
                    RETURN NEW;
                END $fn$;

                DROP TRIGGER IF EXISTS inventory_warehouse_tenant_integrity ON public."Inventory";
                CREATE TRIGGER inventory_warehouse_tenant_integrity
                    BEFORE INSERT OR UPDATE OF "Buid", "WarehouseId" ON public."Inventory"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_warehouse_tenant();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS inventory_warehouse_tenant_integrity ON public."Inventory";
                DROP FUNCTION IF EXISTS public.nexora_validate_inventory_warehouse_tenant();
                """);

            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisi~1",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropIndex(
                name: "IX_customer_ownerships_BusinessUnitId_MutationIdempotencyKey",
                table: "customer_ownerships");

            migrationBuilder.DropColumn(
                name: "ResolutionBatchId",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "ResourceLimit",
                table: "lead_line_commercial_resolutions");

            migrationBuilder.DropColumn(
                name: "MutationIdempotencyKey",
                table: "customer_ownerships");

            migrationBuilder.CreateIndex(
                name: "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~",
                table: "lead_line_commercial_resolutions",
                columns: new[] { "BusinessUnitId", "LeadRevisionId", "LeadLineId" },
                unique: true);
        }
    }
}

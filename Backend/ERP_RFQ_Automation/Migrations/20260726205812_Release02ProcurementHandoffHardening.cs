using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release02ProcurementHandoffHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_RfqId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_RfqItemId_RfqId",
                table: "procurement_handoffs",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.AddForeignKey(
                name: "FK_procurement_handoffs_RFQItems_RfqItemId_RfqId",
                table: "procurement_handoffs",
                columns: new[] { "RfqItemId", "RfqId" },
                principalTable: "RFQItems",
                principalColumns: new[] { "ID", "RFQID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_procurement_handoffs_RFQ_BusinessUnitId_RfqId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "RfqId" },
                principalTable: "RFQ",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public.procurement_handoffs;
                CREATE POLICY nexora_tenant_isolation ON public.procurement_handoffs
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                REVOKE SELECT, UPDATE ON SEQUENCE public."procurement_handoffs_Id_seq" FROM nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."procurement_handoffs_Id_seq" TO nexora_tenant_app;

                CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_handoff_lineage()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $body$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Procurement handoff records are append-preserving.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                       OR NEW."CustomerOrderId" IS DISTINCT FROM OLD."CustomerOrderId"
                       OR NEW."CustomerOrderLineId" IS DISTINCT FROM OLD."CustomerOrderLineId"
                       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
                       OR NEW."SourcingAwardId" IS DISTINCT FROM OLD."SourcingAwardId"
                       OR NEW."SupplierQuotedItemId" IS DISTINCT FROM OLD."SupplierQuotedItemId"
                       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
                       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
                       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
                       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId"
                       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
                       OR NEW."RequiredQuantity" IS DISTINCT FROM OLD."RequiredQuantity"
                       OR NEW."SelectedUnitCost" IS DISTINCT FROM OLD."SelectedUnitCost"
                       OR NEW."RequiredOn" IS DISTINCT FROM OLD."RequiredOn"
                       OR NEW."DestinationType" IS DISTINCT FROM OLD."DestinationType"
                       OR NEW."WarehouseId" IS DISTINCT FROM OLD."WarehouseId"
                       OR NEW."DeliveryLocation" IS DISTINCT FROM OLD."DeliveryLocation"
                       OR NEW."ExternalSystemTarget" IS DISTINCT FROM OLD."ExternalSystemTarget"
                       OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                       OR NEW."RequestHash" IS DISTINCT FROM OLD."RequestHash" THEN
                        RAISE EXCEPTION 'Procurement handoff commercial lineage is immutable.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND NOT (
                        (OLD."Status" = 'CREATED' AND NEW."Status" IN ('EXTERNAL_PO_CREATED','CANCELLED')) OR
                        (OLD."Status" = 'EXTERNAL_PO_CREATED' AND NEW."Status" IN ('SUPPLIER_CONFIRMED','CANCELLED')) OR
                        (OLD."Status" = 'SUPPLIER_CONFIRMED' AND NEW."Status" IN ('PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
                        (OLD."Status" = 'PARTIALLY_RECEIVED' AND NEW."Status" IN ('RECEIVED','CANCELLED'))
                    ) THEN
                        RAISE EXCEPTION 'Invalid procurement handoff status transition.' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_procurement_handoffs_RFQItems_RfqItemId_RfqId",
                table: "procurement_handoffs");

            migrationBuilder.DropForeignKey(
                name: "FK_procurement_handoffs_RFQ_BusinessUnitId_RfqId",
                table: "procurement_handoffs");

            migrationBuilder.DropIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_RfqId",
                table: "procurement_handoffs");

            migrationBuilder.DropIndex(
                name: "IX_procurement_handoffs_RfqItemId_RfqId",
                table: "procurement_handoffs");

            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public.procurement_handoffs;
                CREATE POLICY nexora_tenant_isolation ON public.procurement_handoffs
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                REVOKE SELECT, UPDATE ON SEQUENCE public."procurement_handoffs_Id_seq" FROM nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."procurement_handoffs_Id_seq" TO nexora_tenant_app;

                CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_handoff_lineage()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $body$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Procurement handoff records are append-preserving.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                       OR NEW."CustomerOrderId" IS DISTINCT FROM OLD."CustomerOrderId"
                       OR NEW."CustomerOrderLineId" IS DISTINCT FROM OLD."CustomerOrderLineId"
                       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
                       OR NEW."SourcingAwardId" IS DISTINCT FROM OLD."SourcingAwardId"
                       OR NEW."SupplierQuotedItemId" IS DISTINCT FROM OLD."SupplierQuotedItemId"
                       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
                       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
                       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
                       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId"
                       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
                       OR NEW."RequiredQuantity" IS DISTINCT FROM OLD."RequiredQuantity"
                       OR NEW."SelectedUnitCost" IS DISTINCT FROM OLD."SelectedUnitCost"
                       OR NEW."RequiredOn" IS DISTINCT FROM OLD."RequiredOn"
                       OR NEW."DestinationType" IS DISTINCT FROM OLD."DestinationType"
                       OR NEW."WarehouseId" IS DISTINCT FROM OLD."WarehouseId"
                       OR NEW."DeliveryLocation" IS DISTINCT FROM OLD."DeliveryLocation"
                       OR NEW."ExternalSystemTarget" IS DISTINCT FROM OLD."ExternalSystemTarget"
                       OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                       OR NEW."RequestHash" IS DISTINCT FROM OLD."RequestHash" THEN
                        RAISE EXCEPTION 'Procurement handoff commercial lineage is immutable.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND NOT (
                        (OLD."Status" = 'CREATED' AND NEW."Status" IN ('EXTERNAL_PO_CREATED','CANCELLED')) OR
                        (OLD."Status" = 'EXTERNAL_PO_CREATED' AND NEW."Status" IN ('SUPPLIER_CONFIRMED','CANCELLED')) OR
                        (OLD."Status" = 'SUPPLIER_CONFIRMED' AND NEW."Status" IN ('PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
                        (OLD."Status" = 'PARTIALLY_RECEIVED' AND NEW."Status" IN ('RECEIVED','CANCELLED'))
                    ) THEN
                        RAISE EXCEPTION 'Invalid procurement handoff status transition.' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                """);
        }
    }
}

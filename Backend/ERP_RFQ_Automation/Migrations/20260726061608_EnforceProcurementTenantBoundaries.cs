using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class EnforceProcurementTenantBoundaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM procurement_events e
                        LEFT JOIN "BusinessUnits" b ON b."ID" = e."BusinessUnitId"
                        WHERE b."ID" IS NULL
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'procurement_events contains an orphaned tenant reference';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Suppliers",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_SupplierId",
                table: "SupplierQuotedItems");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_SupplierId_BusinessUnitId",
                table: "SupplierQuotedItems",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.AddForeignKey(
                name: "FK_procurement_events_BusinessUnits_BusinessUnitId",
                table: "procurement_events",
                column: "BusinessUnitId",
                principalTable: "BusinessUnits",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Suppliers",
                table: "SupplierQuotedItems",
                columns: new[] { "SupplierId", "BusinessUnitId" },
                principalTable: "Suppliers",
                principalColumns: new[] { "ID", "BUID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM supplier_purchase_order_lines line
                        LEFT JOIN "Inventory" inventory ON inventory."Id" = line."InventoryId"
                        WHERE line."InventoryId" IS NOT NULL
                          AND (inventory."Id" IS NULL OR inventory."Buid" IS DISTINCT FROM line."BusinessUnitId")
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'supplier purchase-order lines contain cross-tenant inventory references';
                    END IF;
                END
                $block$;

                CREATE OR REPLACE FUNCTION nexora_validate_procurement_inventory_tenant()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."InventoryId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Inventory" i
                        WHERE i."Id" = NEW."InventoryId"
                          AND i."Buid" = NEW."BusinessUnitId"
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'purchase-order inventory must belong to the same tenant';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER supplier_po_line_inventory_tenant
                    BEFORE INSERT OR UPDATE OF "InventoryId", "BusinessUnitId"
                    ON supplier_purchase_order_lines
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_procurement_inventory_tenant();

                CREATE OR REPLACE FUNCTION nexora_reject_referenced_inventory_tenant_change()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."Buid" IS DISTINCT FROM OLD."Buid" AND EXISTS (
                        SELECT 1 FROM supplier_purchase_order_lines line
                        WHERE line."InventoryId" = OLD."Id"
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'inventory tenant ownership is immutable while referenced by procurement';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER inventory_procurement_tenant_immutable
                    BEFORE UPDATE OF "Buid" ON "Inventory"
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_referenced_inventory_tenant_change();

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'
                    ]
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', table_name);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
                            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            table_name);
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON TABLE public.%I TO nexora_tenant_app', table_name);
                        EXECUTE format('REVOKE DELETE ON TABLE public.%I FROM nexora_tenant_app', table_name);
                    END LOOP;
                END
                $block$;

                DO $block$
                DECLARE table_name text; sequence_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'
                    ]
                    LOOP
                        sequence_name := pg_get_serial_sequence(format('public.%I', table_name), 'Id');
                        IF sequence_name IS NOT NULL THEN
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                        END IF;
                    END LOOP;
                END
                $block$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_po_line_inventory_tenant ON supplier_purchase_order_lines;
                DROP FUNCTION IF EXISTS nexora_validate_procurement_inventory_tenant();
                DROP TRIGGER IF EXISTS inventory_procurement_tenant_immutable ON "Inventory";
                DROP FUNCTION IF EXISTS nexora_reject_referenced_inventory_tenant_change();

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'SupplierSolicitations', 'SourcingAwards', 'SupplierQuotedItems'
                    ]
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', table_name);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
                            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            table_name);
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO nexora_tenant_app', table_name);
                    END LOOP;
                END
                $block$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_procurement_events_BusinessUnits_BusinessUnitId",
                table: "procurement_events");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Suppliers",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_SupplierId_BusinessUnitId",
                table: "SupplierQuotedItems");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_SupplierId",
                table: "SupplierQuotedItems",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Suppliers",
                table: "SupplierQuotedItems",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "ID");
        }
    }
}

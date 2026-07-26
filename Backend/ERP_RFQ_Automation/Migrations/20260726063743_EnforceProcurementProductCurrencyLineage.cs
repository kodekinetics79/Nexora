using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class EnforceProcurementProductCurrencyLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "SupplierQuotedItems" quote
                        LEFT JOIN "Products" product ON product."ID" = quote."ProductId"
                        WHERE quote."ProductId" IS NOT NULL
                          AND (product."ID" IS NULL OR product."BUID" IS DISTINCT FROM quote."BusinessUnitId")
                    ) OR EXISTS (
                        SELECT 1 FROM supplier_purchase_order_lines line
                        LEFT JOIN "Products" product ON product."ID" = line."ProductId"
                        WHERE product."ID" IS NULL OR product."BUID" IS DISTINCT FROM line."BusinessUnitId"
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'procurement rows contain cross-tenant product references';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Currency",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_CurrencyId",
                table: "SupplierQuotedItems");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_CurrencyId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BusinessUnitId_CurrencyId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_Currency_BusinessUnitId_CurrencyId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "CurrencyId" },
                principalTable: "Currency",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Currency",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "CurrencyId" },
                principalTable: "Currency",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_validate_procurement_product_tenant()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Products" product
                        WHERE product."ID" = NEW."ProductId"
                          AND product."BUID" = NEW."BusinessUnitId"
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'procurement product must belong to the same tenant';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER supplier_quote_product_tenant
                    BEFORE INSERT OR UPDATE OF "ProductId", "BusinessUnitId"
                    ON "SupplierQuotedItems"
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_procurement_product_tenant();
                CREATE TRIGGER supplier_po_line_product_tenant
                    BEFORE INSERT OR UPDATE OF "ProductId", "BusinessUnitId"
                    ON supplier_purchase_order_lines
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_procurement_product_tenant();

                CREATE OR REPLACE FUNCTION nexora_reject_referenced_product_tenant_change()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."BUID" IS DISTINCT FROM OLD."BUID" AND (
                        EXISTS (SELECT 1 FROM "SupplierQuotedItems" quote WHERE quote."ProductId" = OLD."ID")
                        OR EXISTS (SELECT 1 FROM supplier_purchase_order_lines line WHERE line."ProductId" = OLD."ID")
                    ) THEN
                        RAISE EXCEPTION USING ERRCODE = '23503',
                            MESSAGE = 'product tenant ownership is immutable while referenced by procurement';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER product_procurement_tenant_immutable
                    BEFORE UPDATE OF "BUID" ON "Products"
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_referenced_product_tenant_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_quote_product_tenant ON "SupplierQuotedItems";
                DROP TRIGGER IF EXISTS supplier_po_line_product_tenant ON supplier_purchase_order_lines;
                DROP FUNCTION IF EXISTS nexora_validate_procurement_product_tenant();
                DROP TRIGGER IF EXISTS product_procurement_tenant_immutable ON "Products";
                DROP FUNCTION IF EXISTS nexora_reject_referenced_product_tenant_change();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_Currency_BusinessUnitId_CurrencyId",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Currency",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_CurrencyId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_BusinessUnitId_CurrencyId",
                table: "SourcingAwards");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_CurrencyId",
                table: "SupplierQuotedItems",
                column: "CurrencyId");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Currency",
                table: "SupplierQuotedItems",
                column: "CurrencyId",
                principalTable: "Currency",
                principalColumn: "ID");
        }
    }
}

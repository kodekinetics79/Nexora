using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V1Gate02CommercialIntelligenceIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."Products" WHERE "BUID" IS NULL) THEN
                        RAISE EXCEPTION 'V1 Gate 02: Products contains rows without tenant ownership';
                    END IF;
                    IF EXISTS (SELECT 1 FROM public."Inventory" WHERE "Buid" IS NULL) THEN
                        RAISE EXCEPTION 'V1 Gate 02: Inventory contains rows without tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public."Inventory" i
                        JOIN public."Products" p ON p."ID" = i."ProductId"
                        WHERE i."ProductId" IS NOT NULL AND i."Buid" IS DISTINCT FROM p."BUID") THEN
                        RAISE EXCEPTION 'V1 Gate 02: Inventory crosses Product tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public.product_aliases a
                        JOIN public."Products" p ON p."ID" = a."ProductId"
                        WHERE a."BusinessUnitId" IS DISTINCT FROM p."BUID") THEN
                        RAISE EXCEPTION 'V1 Gate 02: Product alias crosses Product tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public.product_supersessions s
                        JOIN public."Products" p ON p."ID" IN (s."SupersededProductId", s."ReplacementProductId")
                        WHERE s."BusinessUnitId" IS DISTINCT FROM p."BUID") THEN
                        RAISE EXCEPTION 'V1 Gate 02: Product supersession crosses Product tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public.inventory_movements m
                        JOIN public."Products" p ON p."ID" = m."ProductId"
                        JOIN public."Inventory" i ON i."Id" = m."InventoryId"
                        JOIN public."Warehouses" w ON w."ID" = m."WarehouseId"
                        WHERE m."BusinessUnitId" IS DISTINCT FROM p."BUID"
                           OR m."BusinessUnitId" IS DISTINCT FROM i."Buid"
                           OR m."BusinessUnitId" IS DISTINCT FROM w."BusinessUnitID") THEN
                        RAISE EXCEPTION 'V1 Gate 02: Inventory movement crosses tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public.incoming_inventory n
                        JOIN public."Products" p ON p."ID" = n."ProductId"
                        JOIN public."Warehouses" w ON w."ID" = n."WarehouseId"
                        LEFT JOIN public."Inventory" i ON i."Id" = n."InventoryId"
                        WHERE n."BusinessUnitId" IS DISTINCT FROM p."BUID"
                           OR n."BusinessUnitId" IS DISTINCT FROM w."BusinessUnitID"
                           OR (n."InventoryId" IS NOT NULL AND n."BusinessUnitId" IS DISTINCT FROM i."Buid")) THEN
                        RAISE EXCEPTION 'V1 Gate 02: Incoming inventory crosses tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public.stock_reservations r
                        JOIN public."Inventory" i ON i."Id" = r."InventoryId"
                        WHERE r."BusinessUnitId" IS DISTINCT FROM i."Buid") THEN
                        RAISE EXCEPTION 'V1 Gate 02: Stock reservation crosses Inventory tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public."SupplierQuotedItems" q
                        JOIN public."Products" p ON p."ID" = q."ProductId"
                        WHERE q."ProductId" IS NOT NULL AND q."BusinessUnitId" IS DISTINCT FROM p."BUID") THEN
                        RAISE EXCEPTION 'V1 Gate 02: Supplier offer crosses Product tenant ownership';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public.supplier_purchase_order_lines l
                        JOIN public."Products" p ON p."ID" = l."ProductId"
                        LEFT JOIN public."Inventory" i ON i."Id" = l."InventoryId"
                        WHERE l."BusinessUnitId" IS DISTINCT FROM p."BUID"
                           OR (l."InventoryId" IS NOT NULL AND l."BusinessUnitId" IS DISTINCT FROM i."Buid")) THEN
                        RAISE EXCEPTION 'V1 Gate 02: Supplier PO line crosses tenant ownership';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Inventory_InventoryId",
                table: "incoming_inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Products_ProductId",
                table: "incoming_inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Warehouses_WarehouseId",
                table: "incoming_inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_Inventory_Products_ProductId",
                table: "Inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_Inventory_InventoryId",
                table: "inventory_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_Products_ProductId",
                table: "inventory_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_Warehouses_WarehouseId",
                table: "inventory_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_product_aliases_Products_ProductId",
                table: "product_aliases");

            migrationBuilder.DropForeignKey(
                name: "FK_product_supersessions_Products_ReplacementProductId",
                table: "product_supersessions");

            migrationBuilder.DropForeignKey(
                name: "FK_product_supersessions_Products_SupersededProductId",
                table: "product_supersessions");

            migrationBuilder.DropForeignKey(
                name: "FK__Products__BUID",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_stock_reservations_Inventory_InventoryId",
                table: "stock_reservations");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_Inventory_InventoryId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_Products_ProductId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Products_ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_InventoryId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_ProductId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_stock_reservations_InventoryId",
                table: "stock_reservations");

            migrationBuilder.DropIndex(
                name: "IX_Products_BUID",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_product_supersessions_ReplacementProductId",
                table: "product_supersessions");

            migrationBuilder.DropIndex(
                name: "IX_product_supersessions_SupersededProductId",
                table: "product_supersessions");

            migrationBuilder.DropIndex(
                name: "IX_product_aliases_ProductId",
                table: "product_aliases");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_InventoryId",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_ProductId",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_WarehouseId",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_Inventory_ProductId",
                table: "Inventory");

            migrationBuilder.DropIndex(
                name: "IX_incoming_inventory_InventoryId",
                table: "incoming_inventory");

            migrationBuilder.DropIndex(
                name: "IX_incoming_inventory_ProductId",
                table: "incoming_inventory");

            migrationBuilder.DropIndex(
                name: "IX_incoming_inventory_WarehouseId",
                table: "incoming_inventory");

            migrationBuilder.AlterColumn<long>(
                name: "BUID",
                table: "Products",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Buid",
                table: "Inventory",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Products_BUID_ID",
                table: "Products",
                columns: new[] { "BUID", "ID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Inventory_Buid_Id",
                table: "Inventory",
                columns: new[] { "Buid", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_ProductId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_InventoryId",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "InventoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_ProductId",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_supersessions_BusinessUnitId_ReplacementProductId",
                table: "product_supersessions",
                columns: new[] { "BusinessUnitId", "ReplacementProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_BusinessUnitId_InventoryId",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "InventoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_BusinessUnitId_WarehouseId",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_BusinessUnitId_InventoryId",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "InventoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_BusinessUnitId_WarehouseId",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "WarehouseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Inventory_BusinessUnitId_InventoryId",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "InventoryId" },
                principalTable: "Inventory",
                principalColumns: new[] { "Buid", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Products_BusinessUnitId_ProductId",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Warehouses_BusinessUnitId_WarehouseId",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "WarehouseId" },
                principalTable: "Warehouses",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Inventory_Products_Buid_ProductId",
                table: "Inventory",
                columns: new[] { "Buid", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_Inventory_BusinessUnitId_InventoryId",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "InventoryId" },
                principalTable: "Inventory",
                principalColumns: new[] { "Buid", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_Products_BusinessUnitId_ProductId",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_Warehouses_BusinessUnitId_WarehouseId",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "WarehouseId" },
                principalTable: "Warehouses",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_aliases_Products_BusinessUnitId_ProductId",
                table: "product_aliases",
                columns: new[] { "BusinessUnitId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_supersessions_Products_BusinessUnitId_ReplacementPr~",
                table: "product_supersessions",
                columns: new[] { "BusinessUnitId", "ReplacementProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_supersessions_Products_BusinessUnitId_SupersededPro~",
                table: "product_supersessions",
                columns: new[] { "BusinessUnitId", "SupersededProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK__Products__BUID",
                table: "Products",
                column: "BUID",
                principalTable: "BusinessUnits",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_stock_reservations_Inventory_BusinessUnitId_InventoryId",
                table: "stock_reservations",
                columns: new[] { "BusinessUnitId", "InventoryId" },
                principalTable: "Inventory",
                principalColumns: new[] { "Buid", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_Inventory_BusinessUnitId_Inve~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "InventoryId" },
                principalTable: "Inventory",
                principalColumns: new[] { "Buid", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_Products_BusinessUnitId_Produ~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Products_BusinessUnitId_ProductId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Inventory_BusinessUnitId_InventoryId",
                table: "incoming_inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Products_BusinessUnitId_ProductId",
                table: "incoming_inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Warehouses_BusinessUnitId_WarehouseId",
                table: "incoming_inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_Inventory_Products_Buid_ProductId",
                table: "Inventory");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_Inventory_BusinessUnitId_InventoryId",
                table: "inventory_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_Products_BusinessUnitId_ProductId",
                table: "inventory_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_Warehouses_BusinessUnitId_WarehouseId",
                table: "inventory_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_product_aliases_Products_BusinessUnitId_ProductId",
                table: "product_aliases");

            migrationBuilder.DropForeignKey(
                name: "FK_product_supersessions_Products_BusinessUnitId_ReplacementPr~",
                table: "product_supersessions");

            migrationBuilder.DropForeignKey(
                name: "FK_product_supersessions_Products_BusinessUnitId_SupersededPro~",
                table: "product_supersessions");

            migrationBuilder.DropForeignKey(
                name: "FK__Products__BUID",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_stock_reservations_Inventory_BusinessUnitId_InventoryId",
                table: "stock_reservations");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_Inventory_BusinessUnitId_Inve~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_Products_BusinessUnitId_Produ~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Products_BusinessUnitId_ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_InventoryId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_ProductId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Products_BUID_ID",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_product_supersessions_BusinessUnitId_ReplacementProductId",
                table: "product_supersessions");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_BusinessUnitId_InventoryId",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_BusinessUnitId_WarehouseId",
                table: "inventory_movements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Inventory_Buid_Id",
                table: "Inventory");

            migrationBuilder.DropIndex(
                name: "IX_incoming_inventory_BusinessUnitId_InventoryId",
                table: "incoming_inventory");

            migrationBuilder.DropIndex(
                name: "IX_incoming_inventory_BusinessUnitId_WarehouseId",
                table: "incoming_inventory");

            migrationBuilder.AlterColumn<long>(
                name: "BUID",
                table: "Products",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "Buid",
                table: "Inventory",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_ProductId",
                table: "SupplierQuotedItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_InventoryId",
                table: "supplier_purchase_order_lines",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_ProductId",
                table: "supplier_purchase_order_lines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_InventoryId",
                table: "stock_reservations",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_BUID",
                table: "Products",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_product_supersessions_ReplacementProductId",
                table: "product_supersessions",
                column: "ReplacementProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_supersessions_SupersededProductId",
                table: "product_supersessions",
                column: "SupersededProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_aliases_ProductId",
                table: "product_aliases",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_InventoryId",
                table: "inventory_movements",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ProductId",
                table: "inventory_movements",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_WarehouseId",
                table: "inventory_movements",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_ProductId",
                table: "Inventory",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_InventoryId",
                table: "incoming_inventory",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_ProductId",
                table: "incoming_inventory",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_WarehouseId",
                table: "incoming_inventory",
                column: "WarehouseId");

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Inventory_InventoryId",
                table: "incoming_inventory",
                column: "InventoryId",
                principalTable: "Inventory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Products_ProductId",
                table: "incoming_inventory",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Warehouses_WarehouseId",
                table: "incoming_inventory",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Inventory_Products_ProductId",
                table: "Inventory",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_Inventory_InventoryId",
                table: "inventory_movements",
                column: "InventoryId",
                principalTable: "Inventory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_Products_ProductId",
                table: "inventory_movements",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_Warehouses_WarehouseId",
                table: "inventory_movements",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_aliases_Products_ProductId",
                table: "product_aliases",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_supersessions_Products_ReplacementProductId",
                table: "product_supersessions",
                column: "ReplacementProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_supersessions_Products_SupersededProductId",
                table: "product_supersessions",
                column: "SupersededProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK__Products__BUID",
                table: "Products",
                column: "BUID",
                principalTable: "BusinessUnits",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_stock_reservations_Inventory_InventoryId",
                table: "stock_reservations",
                column: "InventoryId",
                principalTable: "Inventory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_Inventory_InventoryId",
                table: "supplier_purchase_order_lines",
                column: "InventoryId",
                principalTable: "Inventory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_Products_ProductId",
                table: "supplier_purchase_order_lines",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Products_ProductId",
                table: "SupplierQuotedItems",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

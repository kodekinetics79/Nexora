using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class HardenProcurementTenantLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "SupplierQuotedItems" WHERE "BusinessUnitId" IS NULL) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23502',
                            MESSAGE = 'Supplier quote tenant identity is missing; reconcile affected rows before upgrade';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_goods_receipt_lines_inventory_movements_InventoryMovementId",
                table: "goods_receipt_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~",
                table: "goods_receipt_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_goods_receipts_Warehouses_WarehouseId",
                table: "goods_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_SupplierQuotedItems_SupplierQuotedItemId",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_SupplierQuotedItems_SupplierQ~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_Warehouses_WarehouseId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_incoming_inventory_IncomingIn~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_BusinessUnits",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_SupplierSolicitations_SupplierSolicitat~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_Warehouses_BusinessUnitID",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_SupplierSolicitationId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_IncomingInventoryId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_SupplierQuotedItemId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_WarehouseId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_BusinessUnitId_RfqItemId",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_SupplierQuotedItemId",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_goods_receipts_WarehouseId",
                table: "goods_receipts");

            migrationBuilder.DropIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~",
                table: "goods_receipt_lines");

            migrationBuilder.AlterColumn<long>(
                name: "BusinessUnitId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Warehouses_BusinessUnitID_ID",
                table: "Warehouses",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SupplierQuotedItems_BusinessUnitId_Id",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "Id", "ProductId", "WarehouseId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "Id", "ProductId", "InventoryId", "WarehouseId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_incoming_inventory_BusinessUnitId_Id",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SupplierSolicitationId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SupplierSolicitationId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_IncomingInvent~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "IncomingInventoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_SupplierQuoted~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "SupplierQuotedItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_WarehouseId",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BusinessUnitId_RfqItemId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "RfqItemId" },
                filter: "\"RfqItemId\" IS NOT NULL AND \"Status\" IN ('PROPOSED','APPROVED')");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BusinessUnitId_SupplierQuotedItemId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "SupplierQuotedItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipts_BusinessUnitId_WarehouseId",
                table: "goods_receipts",
                columns: new[] { "BusinessUnitId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_InventoryMovementId_Prod~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "InventoryMovementId", "ProductId", "InventoryId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId", "ProductId", "WarehouseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_goods_receipt_lines_inventory_movements_BusinessUnitId_Inve~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "InventoryMovementId", "ProductId", "InventoryId", "WarehouseId" },
                principalTable: "inventory_movements",
                principalColumns: new[] { "BusinessUnitId", "Id", "ProductId", "InventoryId", "WarehouseId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId", "ProductId", "WarehouseId" },
                principalTable: "supplier_purchase_order_lines",
                principalColumns: new[] { "BusinessUnitId", "Id", "ProductId", "WarehouseId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_goods_receipts_Warehouses_BusinessUnitId_WarehouseId",
                table: "goods_receipts",
                columns: new[] { "BusinessUnitId", "WarehouseId" },
                principalTable: "Warehouses",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_SupplierQuotedItems_BusinessUnitId_SupplierQ~",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "SupplierQuotedItemId" },
                principalTable: "SupplierQuotedItems",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_SupplierQuotedItems_BusinessU~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "SupplierQuotedItemId" },
                principalTable: "SupplierQuotedItems",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_Warehouses_BusinessUnitId_War~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "WarehouseId" },
                principalTable: "Warehouses",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_incoming_inventory_BusinessUn~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "IncomingInventoryId" },
                principalTable: "incoming_inventory",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_BusinessUnits",
                table: "SupplierQuotedItems",
                column: "BusinessUnitId",
                principalTable: "BusinessUnits",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_SupplierSolicitations_BusinessUnitId_Su~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SupplierSolicitationId" },
                principalTable: "SupplierSolicitations",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_goods_receipt_lines_inventory_movements_BusinessUnitId_Inve~",
                table: "goods_receipt_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~",
                table: "goods_receipt_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_goods_receipts_Warehouses_BusinessUnitId_WarehouseId",
                table: "goods_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_SupplierQuotedItems_BusinessUnitId_SupplierQ~",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_SupplierQuotedItems_BusinessU~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_Warehouses_BusinessUnitId_War~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_order_lines_incoming_inventory_BusinessUn~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_BusinessUnits",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_SupplierSolicitations_BusinessUnitId_Su~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Warehouses_BusinessUnitID_ID",
                table: "Warehouses");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SupplierQuotedItems_BusinessUnitId_Id",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SupplierSolicitationId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_IncomingInvent~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_SupplierQuoted~",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_WarehouseId",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_BusinessUnitId_RfqItemId",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_BusinessUnitId_SupplierQuotedItemId",
                table: "SourcingAwards");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~",
                table: "inventory_movements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_incoming_inventory_BusinessUnitId_Id",
                table: "incoming_inventory");

            migrationBuilder.DropIndex(
                name: "IX_goods_receipts_BusinessUnitId_WarehouseId",
                table: "goods_receipts");

            migrationBuilder.DropIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_InventoryMovementId_Prod~",
                table: "goods_receipt_lines");

            migrationBuilder.DropIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~",
                table: "goods_receipt_lines");

            migrationBuilder.AlterColumn<long>(
                name: "BusinessUnitId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_BusinessUnitID",
                table: "Warehouses",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_SupplierSolicitationId",
                table: "SupplierQuotedItems",
                column: "SupplierSolicitationId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_IncomingInventoryId",
                table: "supplier_purchase_order_lines",
                column: "IncomingInventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_SupplierQuotedItemId",
                table: "supplier_purchase_order_lines",
                column: "SupplierQuotedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_WarehouseId",
                table: "supplier_purchase_order_lines",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BusinessUnitId_RfqItemId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "RfqItemId" },
                unique: true,
                filter: "\"RfqItemId\" IS NOT NULL AND \"Status\" IN ('PROPOSED','APPROVED')");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_SupplierQuotedItemId",
                table: "SourcingAwards",
                column: "SupplierQuotedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipts_WarehouseId",
                table: "goods_receipts",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_goods_receipt_lines_inventory_movements_InventoryMovementId",
                table: "goods_receipt_lines",
                column: "InventoryMovementId",
                principalTable: "inventory_movements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId" },
                principalTable: "supplier_purchase_order_lines",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_goods_receipts_Warehouses_WarehouseId",
                table: "goods_receipts",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_SupplierQuotedItems_SupplierQuotedItemId",
                table: "SourcingAwards",
                column: "SupplierQuotedItemId",
                principalTable: "SupplierQuotedItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_SupplierQuotedItems_SupplierQ~",
                table: "supplier_purchase_order_lines",
                column: "SupplierQuotedItemId",
                principalTable: "SupplierQuotedItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_Warehouses_WarehouseId",
                table: "supplier_purchase_order_lines",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_order_lines_incoming_inventory_IncomingIn~",
                table: "supplier_purchase_order_lines",
                column: "IncomingInventoryId",
                principalTable: "incoming_inventory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_BusinessUnits",
                table: "SupplierQuotedItems",
                column: "BusinessUnitId",
                principalTable: "BusinessUnits",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_SupplierSolicitations_SupplierSolicitat~",
                table: "SupplierQuotedItems",
                column: "SupplierSolicitationId",
                principalTable: "SupplierSolicitations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

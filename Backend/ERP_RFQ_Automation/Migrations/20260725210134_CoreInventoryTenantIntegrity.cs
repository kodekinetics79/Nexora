using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CoreInventoryTenantIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Inventory_BU_Product_Warehouse",
                table: "Inventory");

            migrationBuilder.CreateIndex(
                name: "UX_Inventory_BU_Product_Warehouse",
                table: "Inventory",
                columns: new[] { "Buid", "ProductId", "WarehouseId" },
                unique: true,
                filter: "\"ProductId\" IS NOT NULL AND \"WarehouseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_InventoryId",
                table: "incoming_inventory",
                column: "InventoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_incoming_inventory_Inventory_InventoryId",
                table: "incoming_inventory",
                column: "InventoryId",
                principalTable: "Inventory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_incoming_inventory_Inventory_InventoryId",
                table: "incoming_inventory");

            migrationBuilder.DropIndex(
                name: "UX_Inventory_BU_Product_Warehouse",
                table: "Inventory");

            migrationBuilder.DropIndex(
                name: "IX_incoming_inventory_InventoryId",
                table: "incoming_inventory");

            migrationBuilder.CreateIndex(
                name: "UX_Inventory_BU_Product_Warehouse",
                table: "Inventory",
                columns: new[] { "Buid", "ProductId", "WarehouseId" },
                unique: true,
                filter: "\"ProductId\" IS NOT NULL AND \"WarehouseID\" IS NOT NULL");
        }
    }
}

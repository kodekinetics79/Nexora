using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryStockReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Inventory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocId = table.Column<string>(type: "text", nullable: true),
                    ProductName = table.Column<string>(type: "text", nullable: true),
                    PartNo = table.Column<string>(type: "text", nullable: false),
                    ModelNo = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CategoryId = table.Column<long>(type: "bigint", nullable: true),
                    QtyOnHand = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReorderPoint = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    SellingPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: true),
                    PreferredSupplierId = table.Column<long>(type: "bigint", nullable: true),
                    ItemStatusId = table.Column<long>(type: "bigint", nullable: true),
                    BatchTracking = table.Column<bool>(type: "boolean", nullable: true),
                    SerialTracking = table.Column<bool>(type: "boolean", nullable: true),
                    ExpirationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TaxId = table.Column<long>(type: "bigint", nullable: true),
                    Weight = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Dimensions = table.Column<string>(type: "text", nullable: true),
                    Barcode = table.Column<string>(type: "text", nullable: true),
                    Qrcode = table.Column<string>(type: "text", nullable: true),
                    LeadTime = table.Column<int>(type: "integer", nullable: true),
                    Hscode = table.Column<string>(type: "text", nullable: true),
                    CountryOfOrigin = table.Column<string>(type: "text", nullable: true),
                    PrimaryImageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Buid = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: true),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReleasedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ConsumedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_reservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_stock_reservations_Inventory_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_BU_PartNo",
                table: "Inventory",
                columns: new[] { "Buid", "PartNo" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_availability",
                table: "stock_reservations",
                columns: new[] { "BusinessUnitId", "InventoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_InventoryId",
                table: "stock_reservations",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_order",
                table: "stock_reservations",
                columns: new[] { "BusinessUnitId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "UX_stock_reservations_idempotency",
                table: "stock_reservations",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_reservations");

            migrationBuilder.DropTable(
                name: "Inventory");
        }
    }
}

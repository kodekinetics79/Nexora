using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate3CustomerPoIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuoteId",
                table: "CustomerPurchaseOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RfqId",
                table: "CustomerPurchaseOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceAttachmentId",
                table: "CustomerPurchaseOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerItemCode",
                table: "CustomerPurchaseOrderLines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManufacturerName",
                table: "CustomerPurchaseOrderLines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManufacturerPartNumber",
                table: "CustomerPurchaseOrderLines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuoteId",
                table: "CustomerPurchaseOrders");

            migrationBuilder.DropColumn(
                name: "RfqId",
                table: "CustomerPurchaseOrders");

            migrationBuilder.DropColumn(
                name: "SourceAttachmentId",
                table: "CustomerPurchaseOrders");

            migrationBuilder.DropColumn(
                name: "CustomerItemCode",
                table: "CustomerPurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "ManufacturerName",
                table: "CustomerPurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "ManufacturerPartNumber",
                table: "CustomerPurchaseOrderLines");
        }
    }
}

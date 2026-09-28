using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class ProductPriceCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PriceCurrencyId",
                table: "Products",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_PriceCurrencyId",
                table: "Products",
                column: "PriceCurrencyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Currency_PriceCurrencyId",
                table: "Products",
                column: "PriceCurrencyId",
                principalTable: "Currency",
                principalColumn: "ID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_Currency_PriceCurrencyId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_PriceCurrencyId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PriceCurrencyId",
                table: "Products");
        }
    }
}

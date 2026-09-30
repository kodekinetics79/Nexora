using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260917025434_StockMarginPercent")]
    public partial class StockMarginPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StockMarginPercent",
                table: "QuoteConfiguration",
                type: "numeric(7,2)",
                precision: 7,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StockMarginPercent",
                table: "QuoteConfiguration");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate2QuoteHeaderDiscountAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HeaderDiscountAllocated",
                table: "QuoteItems",
                type: "numeric(18,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeaderDiscountAllocated",
                table: "QuoteItems");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The rep's own message to the suppliers, written in the "Send supplier RFQs" window and
    /// shown in the email after the line table. One nullable column on
    /// <c>SupplierSolicitations</c>; null means the email carried its standard sentence
    /// ("Please submit your best pricing and lead times."), which is the truthful state of every
    /// Supplier RFQ sent before this column existed. No data migration.
    /// </summary>
    public partial class SupplierRfqBuyerMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuyerMessage",
                table: "SupplierSolicitations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyerMessage",
                table: "SupplierSolicitations");
        }
    }
}

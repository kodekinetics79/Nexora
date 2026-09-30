using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260916230119_SupplierEmailCopies")]
    public partial class SupplierEmailCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultBcc",
                table: "supplier_email_settings",
                type: "character varying(2600)",
                maxLength: 2600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultCc",
                table: "supplier_email_settings",
                type: "character varying(2600)",
                maxLength: 2600,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultBcc",
                table: "supplier_email_settings");

            migrationBuilder.DropColumn(
                name: "DefaultCc",
                table: "supplier_email_settings");
        }
    }
}

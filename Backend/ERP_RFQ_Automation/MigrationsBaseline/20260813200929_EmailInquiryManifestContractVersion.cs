using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class EmailInquiryManifestContractVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Version",
                table: "EmailInquiryComponents",
                newName: "ConcurrencyVersion");

            migrationBuilder.RenameColumn(
                name: "Version",
                table: "EmailInquiryAssemblies",
                newName: "ConcurrencyVersion");

            migrationBuilder.AddColumn<int>(
                name: "ManifestContractVersion",
                table: "EmailInquiryAssemblies",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManifestContractVersion",
                table: "EmailInquiryAssemblies");

            migrationBuilder.RenameColumn(
                name: "ConcurrencyVersion",
                table: "EmailInquiryComponents",
                newName: "Version");

            migrationBuilder.RenameColumn(
                name: "ConcurrencyVersion",
                table: "EmailInquiryAssemblies",
                newName: "Version");
        }
    }
}

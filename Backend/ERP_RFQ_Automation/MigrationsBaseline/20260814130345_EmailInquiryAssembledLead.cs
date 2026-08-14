using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.MigrationsBaseline
{
    /// <inheritdoc />
    public partial class EmailInquiryAssembledLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AssembledLeadId",
                table: "EmailInquiryAssemblies",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryAssemblies_BusinessUnitId_AssembledLeadId",
                table: "EmailInquiryAssemblies",
                columns: new[] { "BusinessUnitId", "AssembledLeadId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailInquiryAssemblies_BusinessUnitId_AssembledLeadId",
                table: "EmailInquiryAssemblies");

            migrationBuilder.DropColumn(
                name: "AssembledLeadId",
                table: "EmailInquiryAssemblies");
        }
    }
}

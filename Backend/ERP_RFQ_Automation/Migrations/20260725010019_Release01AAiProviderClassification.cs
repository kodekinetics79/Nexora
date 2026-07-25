using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01AAiProviderClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderClass",
                table: "AiRequests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_BusinessUnitId_ProviderClass_CreatedOn",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "ProviderClass", "CreatedOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiRequests_BusinessUnitId_ProviderClass_CreatedOn",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "ProviderClass",
                table: "AiRequests");
        }
    }
}

using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260815041719_PersistEmailThreadIdentityHeaders")]
    public partial class PersistEmailThreadIdentityHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InReplyToMessageId",
                table: "EmailIngests",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferencesJson",
                table: "EmailIngests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InReplyToMessageId",
                table: "EmailIngests");

            migrationBuilder.DropColumn(
                name: "ReferencesJson",
                table: "EmailIngests");
        }
    }
}

using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260822120246_DispatchRoundOnProcurementOutbox")]
    public partial class DispatchRoundOnProcurementOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DispatchRound",
                table: "procurement_outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DispatchRound",
                table: "procurement_outbox");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernPromiseIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "PromisesToPay",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "PromisesToPay",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UX_PromisesToPay_BU_Idempotency",
                table: "PromisesToPay",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DunningPolicies_BU_Active",
                table: "DunningPolicies",
                columns: new[] { "BusinessUnitId", "Status" },
                unique: true,
                filter: "\"Status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PromisesToPay_BU_Idempotency",
                table: "PromisesToPay");

            migrationBuilder.DropIndex(
                name: "UX_DunningPolicies_BU_Active",
                table: "DunningPolicies");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "PromisesToPay");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "PromisesToPay");
        }
    }
}

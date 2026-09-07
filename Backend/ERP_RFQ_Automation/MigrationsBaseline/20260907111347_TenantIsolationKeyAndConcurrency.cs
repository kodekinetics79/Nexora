using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class TenantIsolationKeyAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "platform",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_PrimaryBusinessUnitId",
                schema: "platform",
                table: "Tenants",
                column: "PrimaryBusinessUnitId",
                unique: true,
                filter: "\"PrimaryBusinessUnitId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Tenants_BusinessUnits_PrimaryBusinessUnitId",
                schema: "platform",
                table: "Tenants",
                column: "PrimaryBusinessUnitId",
                principalTable: "BusinessUnits",
                principalColumn: "ID",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tenants_BusinessUnits_PrimaryBusinessUnitId",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_PrimaryBusinessUnitId",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "platform",
                table: "Tenants");
        }
    }
}

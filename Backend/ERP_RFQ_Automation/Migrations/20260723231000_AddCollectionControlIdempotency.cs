using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionControlIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "CollectionControls",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "CollectionControls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionControls_BU_Idempotency",
                table: "CollectionControls",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CollectionControls_BU_Idempotency",
                table: "CollectionControls");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "CollectionControls");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "CollectionControls");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01CTenantContactMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Suppliers" WHERE "BUID" IS NULL) THEN
                        RAISE EXCEPTION 'Release 01C requires every Supplier to have tenant ownership before contact-key enforcement'
                            USING ERRCODE = '23514';
                    END IF;
                END $block$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK__Contacts__Suppli__18EBB532",
                table: "Contacts");

            migrationBuilder.DropForeignKey(
                name: "FK__Suppliers__BUID__1332DBDC",
                table: "Suppliers");

            migrationBuilder.AlterColumn<long>(
                name: "BUID",
                table: "Suppliers",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Suppliers_ID_BUID",
                table: "Suppliers",
                columns: new[] { "ID", "BUID" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_SupplierID_BusinessUnitID",
                table: "Contacts",
                columns: new[] { "SupplierID", "BusinessUnitID" });

            migrationBuilder.AddForeignKey(
                name: "FK_Contacts_Suppliers_SupplierID_BusinessUnitID",
                table: "Contacts",
                columns: new[] { "SupplierID", "BusinessUnitID" },
                principalTable: "Suppliers",
                principalColumns: new[] { "ID", "BUID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK__Suppliers__BUID__1332DBDC",
                table: "Suppliers",
                column: "BUID",
                principalTable: "BusinessUnits",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contacts_Suppliers_SupplierID_BusinessUnitID",
                table: "Contacts");

            migrationBuilder.DropForeignKey(
                name: "FK__Suppliers__BUID__1332DBDC",
                table: "Suppliers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Suppliers_ID_BUID",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_SupplierID_BusinessUnitID",
                table: "Contacts");

            migrationBuilder.AlterColumn<long>(
                name: "BUID",
                table: "Suppliers",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddForeignKey(
                name: "FK__Contacts__Suppli__18EBB532",
                table: "Contacts",
                column: "SupplierID",
                principalTable: "Suppliers",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK__Suppliers__BUID__1332DBDC",
                table: "Suppliers",
                column: "BUID",
                principalTable: "BusinessUnits",
                principalColumn: "ID");
        }
    }
}

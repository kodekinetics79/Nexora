using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Module02CustomerContinuity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                DO $preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "Customers"
                        WHERE "DocId" IS NOT NULL
                        GROUP BY "BUID", "DocId"
                        HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Module 02 found duplicate tenant customer numbers; resolve identity conflicts before upgrade'
                            USING ERRCODE = '23505';
                    END IF;
                END $preflight$;

                UPDATE "Customers"
                SET "ConcurrencyToken" = gen_random_uuid()
                WHERE "ConcurrencyToken" IS NULL;

                UPDATE "Contacts"
                SET "ConcurrencyToken" = gen_random_uuid()
                WHERE "ConcurrencyToken" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Customers",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "UX_Contacts_BU_Customer_Primary",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "UX_Contacts_BU_Supplier_Primary",
                table: "Contacts");

            migrationBuilder.Sql("""
                UPDATE "Contacts"
                SET "IsPrimary" = FALSE
                WHERE "IsPrimary" = TRUE AND "IsActive" IS FALSE;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Contacts_BU_Customer_Primary",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "CustomerID" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"IsActive\" IS DISTINCT FROM FALSE AND \"CustomerID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Contacts_BU_Supplier_Primary",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "SupplierID" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"IsActive\" IS DISTINCT FROM FALSE AND \"SupplierID\" IS NOT NULL");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Contacts",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Customers_BU_DocId",
                table: "Customers",
                columns: new[] { "BUID", "DocId" },
                unique: true,
                filter: "\"DocId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Customers_BU_DocId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "UX_Contacts_BU_Customer_Primary",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "UX_Contacts_BU_Supplier_Primary",
                table: "Contacts");

            migrationBuilder.Sql("""
                UPDATE "Contacts"
                SET "IsPrimary" = FALSE
                WHERE "IsPrimary" = TRUE AND "IsActive" IS FALSE;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Contacts_BU_Customer_Primary",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "CustomerID" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"CustomerID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Contacts_BU_Supplier_Primary",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "SupplierID" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"SupplierID\" IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "Contacts");
        }
    }
}

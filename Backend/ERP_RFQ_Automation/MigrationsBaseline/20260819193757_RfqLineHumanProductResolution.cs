using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.MigrationsBaseline
{
    /// <inheritdoc />
    public partial class RfqLineHumanProductResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProductResolutionReason",
                table: "RFQItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductResolvedBy",
                table: "RFQItems",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProductResolvedOn",
                table: "RFQItems",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rfqitems_ProductResolution",
                table: "RFQItems",
                sql: "(\"ProductResolvedBy\" IS NULL AND \"ProductResolvedOn\" IS NULL) OR (\"ProductResolvedBy\" IS NOT NULL AND trim(\"ProductResolvedBy\") <> '' AND \"ProductResolvedOn\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Rfqitems_ProductResolution",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "ProductResolutionReason",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "ProductResolvedBy",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "ProductResolvedOn",
                table: "RFQItems");
        }
    }
}

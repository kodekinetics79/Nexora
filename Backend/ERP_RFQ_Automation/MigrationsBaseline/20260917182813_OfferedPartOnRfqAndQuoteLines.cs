using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260917182813_OfferedPartOnRfqAndQuoteLines")]
    public partial class OfferedPartOnRfqAndQuoteLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfferedKind",
                table: "RFQItems",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferedMakerName",
                table: "RFQItems",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferedNote",
                table: "RFQItems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferedPartNumber",
                table: "RFQItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferedSpecs",
                table: "RFQItems",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferedNote",
                table: "QuoteItems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferedSpecs",
                table: "QuoteItems",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfferedKind",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "OfferedMakerName",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "OfferedNote",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "OfferedPartNumber",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "OfferedSpecs",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "OfferedNote",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "OfferedSpecs",
                table: "QuoteItems");
        }
    }
}

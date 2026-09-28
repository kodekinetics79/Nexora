using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// The buyer's material number, maker and maker part number on the quote line (pilot audit
/// UX-03 / CB-08).
///
/// <para>SEC and Aramco evaluators match a quote line to their request by THEIR material number
/// and confirm the part by maker and part number. RFQItems carried all three, QuoteItems had no
/// column for any of them, so every quote and every printed PDF dropped them. The columns mirror
/// the RFQItems lengths (200 / 400 / 200) so a copy can never be truncated.</para>
///
/// <para>Existing quote lines that point at an RFQ line are filled from it once, here, so drafts
/// already on file print the same thing a new draft does. Lines typed by hand have no RFQ line
/// and stay null. Row-level security on QuoteItems applies to the tenant roles only; the
/// migrating owner sees every row, which is what a one-time backfill needs.</para>
///
/// <para>Hand-written (no Designer), like the other repair migrations in this folder, so the
/// Designer line budget in the Dockerfile is not spent on three nullable columns. The model
/// snapshot carries the same three properties.</para>
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260929020000_QuoteLineBuyerIdentity")]
public sealed class QuoteLineBuyerIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CustomerMaterialCode",
            table: "QuoteItems",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ManufacturerName",
            table: "QuoteItems",
            type: "character varying(400)",
            maxLength: 400,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ManufacturerPartNumber",
            table: "QuoteItems",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE public."QuoteItems" AS qi
               SET "CustomerMaterialCode" = NULLIF(btrim(ri."ItemMaterialCode"), ''),
                   "ManufacturerName" = NULLIF(btrim(ri."ManufacturerName"), ''),
                   "ManufacturerPartNumber" = NULLIF(btrim(ri."ManufacturerPartNumber"), '')
              FROM public."RFQItems" AS ri
             WHERE qi."RFQItemID" = ri."ID"
               AND qi."CustomerMaterialCode" IS NULL
               AND qi."ManufacturerName" IS NULL
               AND qi."ManufacturerPartNumber" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CustomerMaterialCode", table: "QuoteItems");
        migrationBuilder.DropColumn(name: "ManufacturerName", table: "QuoteItems");
        migrationBuilder.DropColumn(name: "ManufacturerPartNumber", table: "QuoteItems");
    }
}

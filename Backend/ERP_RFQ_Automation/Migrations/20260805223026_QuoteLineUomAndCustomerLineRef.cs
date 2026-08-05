using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Adds QuoteItems.UnitOfMeasure and QuoteItems.CustomerLineRef (nullable — existing
    /// rows keep NULL; readers fall back to synthetic numbering / a blank UOM cell).
    ///
    /// COORDINATION NOTE: this migration was generated while the parallel
    /// GuardQuoteableQuantities migration had been removed for regeneration, so the model
    /// diff here initially ALSO captured that work's pending operations (the three
    /// Quantity &gt; 0 check constraints and the Products BUID+PartNo unique-index fix).
    /// Those operations have since been MOVED — exactly once, remove-and-add in the same
    /// change — into 20260805223247_GuardQuoteableQuantities, which sorts directly after
    /// this migration and whose name and doc comment explain them. This migration now
    /// carries only the two QuoteItems columns it is named for.
    /// </summary>
    public partial class QuoteLineUomAndCustomerLineRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerLineRef",
                table: "QuoteItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitOfMeasure",
                table: "QuoteItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerLineRef",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "UnitOfMeasure",
                table: "QuoteItems");
        }
    }
}

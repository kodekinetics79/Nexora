using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.MigrationsBaseline
{
    /// <inheritdoc />
    public partial class QuoteBackfillOriginAndExternalReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalQuoteReference",
                table: "Quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Quotes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "PIPELINE");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BU_Origin_QuoteDate",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "Origin", "QuoteDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_BU_ExternalQuoteReference",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "ExternalQuoteReference" },
                unique: true,
                filter: "\"ExternalQuoteReference\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Quotes_Origin",
                table: "Quotes",
                sql: "\"Origin\" IN ('PIPELINE', 'BACKFILL')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Quotes_BU_Origin_QuoteDate",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "UX_Quotes_BU_ExternalQuoteReference",
                table: "Quotes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Quotes_Origin",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "ExternalQuoteReference",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Quotes");
        }
    }
}

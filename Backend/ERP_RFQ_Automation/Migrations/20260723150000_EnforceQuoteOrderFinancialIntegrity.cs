using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class EnforceQuoteOrderFinancialIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FinancialCalculationVersion",
                table: "Quotes",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT qi."QuoteID"
                        FROM public."QuoteItems" qi
                        WHERE round(coalesce(qi."TaxAmount", 0), 2) <> 0
                        GROUP BY qi."QuoteID"
                        HAVING bool_or(
                                   round(qi."TotalAmount", 2) = round(qi."Quantity" * qi."UnitPrice" - coalesce(qi."Discount", 0), 2))
                           AND bool_or(
                                   round(qi."TotalAmount", 2) = round(qi."Quantity" * qi."UnitPrice" - coalesce(qi."Discount", 0) + coalesce(qi."TaxAmount", 0), 2))
                            OR bool_or(
                                   round(qi."TotalAmount", 2) <> round(qi."Quantity" * qi."UnitPrice" - coalesce(qi."Discount", 0), 2)
                               AND round(qi."TotalAmount", 2) <> round(qi."Quantity" * qi."UnitPrice" - coalesce(qi."Discount", 0) + coalesce(qi."TaxAmount", 0), 2))) THEN
                        RAISE EXCEPTION 'Cannot classify quote calculation version: mixed or unrecognized taxed line totals require reconciliation';
                    END IF;
                END
                $block$;

                UPDATE public."Quotes" q
                SET "FinancialCalculationVersion" = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM public."QuoteItems" qi
                        WHERE qi."QuoteID" = q."ID"
                          AND round(coalesce(qi."TaxAmount", 0), 2) <> 0
                          AND round(qi."TotalAmount", 2) = round(qi."Quantity" * qi."UnitPrice" - coalesce(qi."Discount", 0), 2))
                    THEN 1
                    ELSE 2
                END;

                ALTER TABLE public."Quotes"
                    ALTER COLUMN "FinancialCalculationVersion" SET DEFAULT 2,
                    ALTER COLUMN "FinancialCalculationVersion" SET NOT NULL;
                """);

            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public."Orders"
                        WHERE "QuoteID" IS NOT NULL
                        GROUP BY "BusinessUnitID", "QuoteID"
                        HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Cannot enforce one order per quote: duplicate tenant/quote orders require reconciliation';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Orders_BU_QuoteID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "QuoteID" },
                unique: true,
                filter: "\"QuoteID\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Orders_BU_QuoteID",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FinancialCalculationVersion",
                table: "Quotes");

        }
    }
}

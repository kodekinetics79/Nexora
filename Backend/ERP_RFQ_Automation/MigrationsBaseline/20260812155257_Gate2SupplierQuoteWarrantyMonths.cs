using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// FR-QTM-03's fourth scoring criterion gets a number behind it.
    ///
    /// <para>The supplier quote line carried only free-text <c>Warranty</c> — "24 months on parts,
    /// 12 on labour" — while the governed scorer consumed a numeric warranty that nothing supplied.
    /// Three of the four criteria the requirement names worked; the fourth was inert, and the
    /// warranty weight could never be raised above zero without every offer correctly reporting that
    /// it could not be scored.</para>
    ///
    /// <para><b>Captured, never parsed.</b> No regex, no classifier and no model reads the wording.
    /// An operator types the months or there is no number. The free text is left exactly as it is:
    /// a warranty clause carries conditions no integer can hold, and rewriting it to fit a score
    /// would trade a true statement for a partial one. The two live side by side — the wording
    /// carries the terms, the number is the only part a comparison can rank.</para>
    ///
    /// <para><b>NULL, and never a backfilled zero.</b> Existing rows keep NULL, which means "nobody
    /// captured this". Zero is a different and much stronger claim — that the supplier offered no
    /// warranty at all — and defaulting to it would assert that about every historical offer in the
    /// estate. Under the scorer's rule that a weighted criterion with no value is never scored as
    /// zero, a NULL row reports "Cannot score — warranty missing" and stays awardable.</para>
    ///
    /// <para>The upper bound of 600 months is fifty years: generous enough for any real industrial
    /// warranty and tight enough that a mistyped year ("2026") is refused by the database rather
    /// than silently dominating the ranking as the longest warranty in the set.</para>
    /// </summary>
    public partial class Gate2SupplierQuoteWarrantyMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_quote_lines_Values",
                table: "supplier_quote_lines");

            migrationBuilder.AddColumn<int>(
                name: "WarrantyMonths",
                table: "supplier_quote_lines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_quote_lines_Values",
                table: "supplier_quote_lines",
                sql: "\"LineNumber\" > 0 AND \"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND (\"AvailableQuantity\" IS NULL OR \"AvailableQuantity\" >= 0) AND (\"MinimumOrderQuantity\" IS NULL OR \"MinimumOrderQuantity\" > 0) AND (\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0) AND (\"WarrantyMonths\" IS NULL OR (\"WarrantyMonths\" >= 0 AND \"WarrantyMonths\" <= 600))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_quote_lines_Values",
                table: "supplier_quote_lines");

            migrationBuilder.DropColumn(
                name: "WarrantyMonths",
                table: "supplier_quote_lines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_quote_lines_Values",
                table: "supplier_quote_lines",
                sql: "\"LineNumber\" > 0 AND \"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND (\"AvailableQuantity\" IS NULL OR \"AvailableQuantity\" >= 0) AND (\"MinimumOrderQuantity\" IS NULL OR \"MinimumOrderQuantity\" > 0) AND (\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0)");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Database backstop for the wrong-quantity defect, re-expressed MODEL-FIRST.
    ///
    /// An earlier hand-written revision added these CHECK constraints via raw SQL in a
    /// DO $$ block. The DB-architect panel ruled that wrong in form: raw SQL is invisible
    /// to the EF model, so databases built from the model (the SQLite test databases,
    /// EnsureCreated) never received the constraints, the model snapshot did not record
    /// them, and the PostgreSqlProductionDialectTests drift guard could not see them. The
    /// constraints now live in the model configuration
    /// (Models/ErpRfqAutomationContext.cs — Rfqitem, QuoteItem, OrderItem) and this
    /// migration is generated from that model, matching the 13 supplier/finance tables
    /// that already express their quantity checks the same way.
    ///
    /// WHY THESE THREE TABLES AND NOT LeadItems: RFQItems / QuoteItems / OrderItems are
    /// downstream of extraction review — by the time a row exists there a quantity has
    /// been established and must be real. RfqController.ApproveAsync creates the Quote
    /// AND emails it in the same request, so no screen between approval and the
    /// customer's inbox ever displays a quantity; a constraint is the only guard a future
    /// code path cannot bypass. LeadItems is deliberately UNCONSTRAINED: it is the raw
    /// extraction landing zone where 0 = "the document did not state a quantity" plus a
    /// RequiresCommercialReview flag. Constraining it would force the ingestion doors
    /// back into fabricating a value — precisely the behaviour being removed.
    ///
    /// Also fixes Products uniqueness: part numbers were GLOBALLY unique across tenants
    /// ("UQ__Inventor__7C3FF6B67DFB4EBD" on PartNo alone), so one tenant's part number
    /// collided with another tenant's catalogue import. Replaced by per-tenant
    /// UQ_Products_BUID_PartNo (BUID, PartNo), the same remedy the Suppliers table
    /// received (UX_Suppliers_BU_ContactEmail). Shared master-data rows (NULL BUID) are
    /// exempt, as elsewhere.
    ///
    /// Verified against production before writing: 0 rows violate any of the three
    /// constraints, and Products holds a single row, so both changes are free now and
    /// expensive after the pilot client loads data.
    ///
    /// COORDINATION NOTE: the parallel 20260805223026_QuoteLineUomAndCustomerLineRef was
    /// generated while this migration was removed for regeneration and briefly carried
    /// these operations. They were MOVED here — remove-and-add in the same change, so
    /// each operation exists exactly once — and the two migrations apply cleanly in
    /// sequence (columns first, then constraints and the index swap).
    /// </summary>
    public partial class GuardQuoteableQuantities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ__Inventor__7C3FF6B67DFB4EBD",
                table: "Products");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RFQItems_Quantity_Positive",
                table: "RFQItems",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QuoteItems_Quantity_Positive",
                table: "QuoteItems",
                sql: "\"Quantity\" > 0");

            migrationBuilder.CreateIndex(
                name: "UQ_Products_BUID_PartNo",
                table: "Products",
                columns: new[] { "BUID", "PartNo" },
                unique: true,
                filter: "\"BUID\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_Quantity_Positive",
                table: "OrderItems",
                sql: "\"Quantity\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RFQItems_Quantity_Positive",
                table: "RFQItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QuoteItems_Quantity_Positive",
                table: "QuoteItems");

            migrationBuilder.DropIndex(
                name: "UQ_Products_BUID_PartNo",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_Quantity_Positive",
                table: "OrderItems");

            migrationBuilder.CreateIndex(
                name: "UQ__Inventor__7C3FF6B67DFB4EBD",
                table: "Products",
                column: "PartNo",
                unique: true);
        }
    }
}

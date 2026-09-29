using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// The client's own quote statuses (owner request 2026-09-28, decisions OD1–OD5).
///
/// <para>The client's steps, endings and reasons are ordinary <c>Setup_Master</c> rows of the
/// tenant (SetupType <c>QuoteStep</c>, <c>QuoteEnding</c>, <c>QuoteOutcomeReason</c>), each pointing
/// at the fixed status it belongs to through <c>ParentSetupID</c>. Two things were missing:</para>
/// <list type="bullet">
/// <item><c>Setup_Master.SortOrder</c> — the table had no order, and a customer's steps have one
/// (evaluation, clarification, BAFO). NOT NULL DEFAULT 0, so every existing row keeps its place.</item>
/// <item><c>Quotes.SubStatusId</c> + <c>SubStatusOn</c> — which step or ending the quote carries, and
/// since when. Nullable: every existing quote is untouched. The foreign key sets NULL on delete, so
/// removing a setup row can never take a quote with it.</item>
/// </list>
///
/// <para>Hand-written (no Designer), like the other additive migrations in this folder. The model
/// snapshot carries the same columns, index and key. No rows are seeded here: the Gulf defaults
/// are seeded per tenant on first use, and never over a tenant's own rows.</para>
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260929060000_QuoteClientStatuses")]
public sealed class QuoteClientStatuses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<short>(
            name: "SortOrder", table: "Setup_Master",
            type: "smallint", nullable: false, defaultValue: (short)0);

        migrationBuilder.AddColumn<long>(
            name: "SubStatusId", table: "Quotes",
            type: "bigint", nullable: true);
        migrationBuilder.AddColumn<System.DateTime>(
            name: "SubStatusOn", table: "Quotes",
            type: "timestamp without time zone", nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Quotes_SubStatusId", table: "Quotes", column: "SubStatusId");

        migrationBuilder.AddForeignKey(
            name: "FK_Quotes_SubStatus", table: "Quotes", column: "SubStatusId",
            principalTable: "Setup_Master", principalColumn: "SetupID",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_Quotes_SubStatus", table: "Quotes");
        migrationBuilder.DropIndex(name: "IX_Quotes_SubStatusId", table: "Quotes");
        migrationBuilder.DropColumn(name: "SubStatusId", table: "Quotes");
        migrationBuilder.DropColumn(name: "SubStatusOn", table: "Quotes");
        migrationBuilder.DropColumn(name: "SortOrder", table: "Setup_Master");
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Statement-line provenance outgrew <c>varchar(400)</c>: page and storage meters carry a
    /// signal-coverage caveat, and the combined text reached ~900 characters, so computing a
    /// statement against a rate card that priced those meters failed on PostgreSQL with 22001.
    /// The portable lane could not catch it because SQLite ignores varchar length.
    ///
    /// <c>SourceNote</c> becomes unbounded text and the caveat moves to its own
    /// <c>CoverageNote</c> column, so a priced line carries its warning as structured data
    /// rather than as a suffix that can be truncated away.
    ///
    /// Forward is a widening and is safe on populated tables. <b>Down truncates:</b> any note
    /// longer than 400 characters would be rejected on the way back, so a rollback past this
    /// point must be taken only on a database whose notes are known to be short.
    /// </summary>
    public partial class UnboundBillingStatementLineNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SourceNote",
                schema: "platform",
                table: "BillingStatementLines",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverageNote",
                schema: "platform",
                table: "BillingStatementLines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverageNote",
                schema: "platform",
                table: "BillingStatementLines");

            migrationBuilder.AlterColumn<string>(
                name: "SourceNote",
                schema: "platform",
                table: "BillingStatementLines",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}

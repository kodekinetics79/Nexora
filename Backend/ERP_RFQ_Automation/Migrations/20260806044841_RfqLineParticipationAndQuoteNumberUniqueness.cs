using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Two repairs to the Lead → RFQ → Quote Draft path.
    ///
    /// <para><b>1. RFQ line participation.</b> A partial bid — quote 12 of 84 lines — had no
    /// representation. The only bid field was <c>RFQ.BiddingDecision</c>, header-level and an
    /// untyped nullable string. Every existing row defaults to <c>Pending</c>, which is the
    /// truthful backfill: nobody has decided those lines. It is deliberately NOT <c>Quote</c>,
    /// because that would retroactively assert a commercial decision no human ever made.</para>
    ///
    /// <para><b>2. Quote number uniqueness.</b> Three independent generators write
    /// <c>Quotes.QuoteNo</c> and none was backstopped — <c>IX_Quotes_QuoteNo</c> is not unique.
    /// Two customer-facing documents could carry the same number. The pre-flight below audits
    /// the data BEFORE the constraint is applied and fails with the offending values named,
    /// rather than letting Postgres raise an opaque violation.</para>
    ///
    /// <para><b>Index consolidation.</b> <c>IX_RFQItems_RFQID</c> is dropped because the new
    /// <c>IX_RFQItems_Rfqid_Participation</c> leads with the same column and therefore serves
    /// every lookup the old index served. This is a replacement, not a loss of an access path.</para>
    ///
    /// <para>Reversible: <c>Down()</c> restores the dropped index, removes the columns,
    /// constraints and the unique index.</para>
    /// </summary>
    public partial class RfqLineParticipationAndQuoteNumberUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- PRE-FLIGHT DATA AUDIT --------------------------------------------------
            // Refuse to proceed if the data cannot satisfy the constraint we are about to add.
            // Failing here names the duplicates; failing on CREATE UNIQUE INDEX does not.
            migrationBuilder.Sql(@"
DO $$
DECLARE offending text;
BEGIN
    SELECT string_agg(format('BU %s / %s (x%s)', ""BusinessUnitID"", ""QuoteNo"", n), '; ')
      INTO offending
      FROM (
        SELECT ""BusinessUnitID"", ""QuoteNo"", count(*) AS n
          FROM ""Quotes""
         GROUP BY ""BusinessUnitID"", ""QuoteNo""
        HAVING count(*) > 1
      ) d;

    IF offending IS NOT NULL THEN
        RAISE EXCEPTION
          'Cannot apply UX_Quotes_BusinessUnitID_QuoteNo: duplicate quote numbers already exist -> %. Resolve these rows (re-number the later duplicates, preserving the number already sent to a customer) and re-run.', offending;
    END IF;
END $$;");

            migrationBuilder.DropIndex(
                name: "IX_RFQItems_RFQID",
                table: "RFQItems");

            migrationBuilder.AddColumn<string>(
                name: "NoQuoteReason",
                table: "RFQItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParticipationDecidedBy",
                table: "RFQItems",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParticipationDecidedOn",
                table: "RFQItems",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParticipationDecision",
                table: "RFQItems",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_Rfqid_Participation",
                table: "RFQItems",
                columns: new[] { "RFQID", "ParticipationDecision" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RFQItems_NoQuote_Requires_Reason",
                table: "RFQItems",
                sql: "\"ParticipationDecision\" <> 'NoQuote' OR (\"NoQuoteReason\" IS NOT NULL AND trim(\"NoQuoteReason\") <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RFQItems_Participation_Decision",
                table: "RFQItems",
                sql: "\"ParticipationDecision\" IN ('Pending','Quote','NoQuote')");

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_BusinessUnitID_QuoteNo",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "QuoteNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RFQItems_Rfqid_Participation",
                table: "RFQItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RFQItems_NoQuote_Requires_Reason",
                table: "RFQItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RFQItems_Participation_Decision",
                table: "RFQItems");

            migrationBuilder.DropIndex(
                name: "UX_Quotes_BusinessUnitID_QuoteNo",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "NoQuoteReason",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "ParticipationDecidedBy",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "ParticipationDecidedOn",
                table: "RFQItems");

            migrationBuilder.DropColumn(
                name: "ParticipationDecision",
                table: "RFQItems");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_RFQID",
                table: "RFQItems",
                column: "RFQID");
        }
    }
}

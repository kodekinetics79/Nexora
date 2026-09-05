using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// One quote per RFQ per tenant — for ORIGINAL quotes only.
    ///
    /// <para><c>UX_Quotes_BusinessUnitID_RFQID</c> (20260722051308_OperationalizeCommercialLifecycle,
    /// carried into the squashed baseline's 05_indexes.sql) is <c>UNIQUE ("BusinessUnitID", "RFQID")
    /// WHERE "RFQID" IS NOT NULL</c>. A revision is a second quote row on the same RFQ
    /// (<c>QuoteService.ReviseQuoteAsync</c> copies <c>Rfqid</c> and sets <c>RevisionOfQuoteId</c>),
    /// so on every PostgreSQL database the INSERT was refused and no quote could ever be revised:
    /// the "issue this quote as a new revision" exit every readiness blocker names was shut. SQLite
    /// never carried the index (it is raw SQL, not model metadata), which is why the revision tests
    /// stayed green while production could not revise.</para>
    ///
    /// <para>The rule itself stands: an RFQ still carries one quote, and each quote at most one direct
    /// successor (<c>UX_Quotes_BU_RevisionOfQuoteId</c>). Only the filter changes, so the index
    /// ignores revision rows. Data-safe in both directions on any database the old index accepted.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260905093000_ScopeOneQuotePerRfqToOriginalQuotes")]
    public partial class ScopeOneQuotePerRfqToOriginalQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "UX_Quotes_BusinessUnitID_RFQID";
                CREATE UNIQUE INDEX "UX_Quotes_BusinessUnitID_RFQID"
                    ON "Quotes" ("BusinessUnitID", "RFQID")
                    WHERE "RFQID" IS NOT NULL AND "RevisionOfQuoteId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restoring the unfiltered rule is only possible while no revision rows exist; refuse
            // loudly rather than fail on an opaque duplicate-key error halfway through.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Quotes" WHERE "RFQID" IS NOT NULL AND "RevisionOfQuoteId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Quote revisions exist; the unfiltered one-quote-per-RFQ index cannot be restored.';
                    END IF;
                END $$;
                DROP INDEX IF EXISTS "UX_Quotes_BusinessUnitID_RFQID";
                CREATE UNIQUE INDEX "UX_Quotes_BusinessUnitID_RFQID"
                    ON "Quotes" ("BusinessUnitID", "RFQID")
                    WHERE "RFQID" IS NOT NULL;
                """);
        }
    }
}

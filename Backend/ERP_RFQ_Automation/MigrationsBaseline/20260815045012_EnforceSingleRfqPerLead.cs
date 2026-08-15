using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// One lead, one RFQ — replaces the plain IX_RFQ_LeadID with a partial UNIQUE index so a
    /// retried or racing conversion physically cannot create a second RFQ for a lead. The
    /// filter keeps the leadless spreadsheet-import RFQs (NULL LeadID) unconstrained.
    ///
    /// <para><b>Existing duplicates.</b> This is a pre-launch system with no real customer
    /// data, but the very defect this index closes (the ungated POST /api/Rfq door) may have
    /// left duplicate lead→RFQ rows in a dev/demo database. The decision, deliberately, is to
    /// FAIL the migration with a message naming the offending leads rather than silently
    /// deleting or unlinking either RFQ: even simulation data deserves an operator choosing
    /// which RFQ survives (one may carry quotes). Resolve by deleting the spurious RFQ or
    /// NULLing its "LeadID", then rerun.</para>
    /// </summary>
    public partial class EnforceSingleRfqPerLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The dedicated lead→RFQ promotion event ("PromotedToRfq") joins the governed
            // event vocabulary. The CHECK constraint is the enforcement point for that
            // vocabulary on PostgreSQL (the relational-SQLite test lane builds its schema
            // from the EF model, which carries no such check), so it is widened here — and
            // ONLY widened: StatusTransitioned/Reopened stay constrained exactly as before,
            // and CK_lifecycle_events_StatusChanged is untouched because a promotion event
            // states no previous status.
            migrationBuilder.Sql("""
                ALTER TABLE public.commercial_lifecycle_events
                    DROP CONSTRAINT IF EXISTS "CK_lifecycle_events_EventType";
                ALTER TABLE public.commercial_lifecycle_events
                    ADD CONSTRAINT "CK_lifecycle_events_EventType"
                    CHECK ("EventType" IN ('StatusTransitioned', 'Reopened', 'PromotedToRfq'));
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE duplicated text;
                BEGIN
                    SELECT string_agg(DISTINCT "LeadID"::text, ', ') INTO duplicated
                    FROM (
                        SELECT "LeadID"
                        FROM public."RFQ"
                        WHERE "LeadID" IS NOT NULL
                        GROUP BY "LeadID"
                        HAVING COUNT(*) > 1
                    ) AS duplicates;
                    IF duplicated IS NOT NULL THEN
                        RAISE EXCEPTION USING
                            MESSAGE = 'EnforceSingleRfqPerLead: lead(s) ' || duplicated ||
                                      ' have more than one RFQ. Delete the spurious RFQ rows '
                                      '(or NULL their "LeadID") before this unique index can '
                                      'be created; no data is deleted automatically.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_RFQ_LeadID",
                table: "RFQ");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_LeadID",
                table: "RFQ",
                column: "LeadID",
                unique: true,
                filter: "\"LeadID\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RFQ_LeadID",
                table: "RFQ");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_LeadID",
                table: "RFQ",
                column: "LeadID");

            // Any PromotedToRfq events written while this migration was applied would violate
            // the narrowed constraint; NOT VALID skips revalidating existing rows (the
            // downgrade restores the OLD vocabulary for NEW writes without demanding history
            // be rewritten — the same posture every downgrade here takes toward data).
            migrationBuilder.Sql("""
                ALTER TABLE public.commercial_lifecycle_events
                    DROP CONSTRAINT IF EXISTS "CK_lifecycle_events_EventType";
                ALTER TABLE public.commercial_lifecycle_events
                    ADD CONSTRAINT "CK_lifecycle_events_EventType"
                    CHECK ("EventType" IN ('StatusTransitioned', 'Reopened')) NOT VALID;
                """);
        }
    }
}

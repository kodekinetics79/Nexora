using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Enforces the cross-row participation aggregate at transaction commit. Constraint triggers are
/// deferred because EF inserts the decision header before its lines in the same transaction.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260827170000_EnforceParticipationOutcomeConsistency")]
public partial class EnforceParticipationOutcomeConsistency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nexora_validate_lead_participation_snapshot(
                p_business_unit_id bigint,
                p_decision_id bigint)
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            DECLARE
                decision_row record;
                line_count integer;
                bid_count integer;
                no_bid_count integer;
                unresolved_count integer;
                expected_outcome text;
            BEGIN
                SELECT "IsCommitted", "Outcome"
                  INTO decision_row
                  FROM public."LeadParticipationDecisions"
                 WHERE "BusinessUnitId" = p_business_unit_id
                   AND "Id" = p_decision_id;

                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*)::integer,
                       count(*) FILTER (WHERE "Choice" = 'Bid')::integer,
                       count(*) FILTER (WHERE "Choice" = 'NoBid')::integer,
                       count(*) FILTER (WHERE "Choice" IN ('Pending', 'Clarify'))::integer
                  INTO line_count, bid_count, no_bid_count, unresolved_count
                  FROM public."LeadLineParticipationDecisions"
                 WHERE "BusinessUnitId" = p_business_unit_id
                   AND "ParticipationDecisionId" = p_decision_id;

                IF line_count = 0 THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'A participation decision must contain at least one line';
                END IF;

                IF NOT decision_row."IsCommitted" THEN
                    IF decision_row."Outcome" <> 'Pending' THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            MESSAGE = 'A draft participation decision must have outcome Pending';
                    END IF;
                    RETURN;
                END IF;

                IF unresolved_count > 0 THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'A committed participation decision cannot contain Pending or Clarify lines';
                END IF;

                expected_outcome := CASE
                    WHEN bid_count = 0 AND no_bid_count = line_count THEN 'NoBid'
                    WHEN bid_count = line_count THEN 'FullBid'
                    WHEN bid_count > 0 AND bid_count < line_count
                         AND bid_count + no_bid_count = line_count THEN 'PartialBid'
                    ELSE NULL
                END;

                IF expected_outcome IS NULL OR decision_row."Outcome" <> expected_outcome THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = format(
                            'Committed participation outcome %s is inconsistent with %s Bid and %s NoBid line(s)',
                            decision_row."Outcome", bid_count, no_bid_count);
                END IF;
            END;
            $$;

            CREATE OR REPLACE FUNCTION nexora_check_lead_participation_header()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM nexora_validate_lead_participation_snapshot(NEW."BusinessUnitId", NEW."Id");
                RETURN NEW;
            END;
            $$;

            CREATE OR REPLACE FUNCTION nexora_check_lead_participation_line()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM nexora_validate_lead_participation_snapshot(
                    NEW."BusinessUnitId", NEW."ParticipationDecisionId");
                RETURN NEW;
            END;
            $$;

            DROP TRIGGER IF EXISTS "TR_LeadParticipationDecisions_OutcomeConsistency"
                ON public."LeadParticipationDecisions";
            CREATE CONSTRAINT TRIGGER "TR_LeadParticipationDecisions_OutcomeConsistency"
                AFTER INSERT OR UPDATE ON public."LeadParticipationDecisions"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION nexora_check_lead_participation_header();

            DROP TRIGGER IF EXISTS "TR_LeadLineParticipationDecisions_OutcomeConsistency"
                ON public."LeadLineParticipationDecisions";
            CREATE CONSTRAINT TRIGGER "TR_LeadLineParticipationDecisions_OutcomeConsistency"
                AFTER INSERT OR UPDATE ON public."LeadLineParticipationDecisions"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION nexora_check_lead_participation_line();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS "TR_LeadLineParticipationDecisions_OutcomeConsistency"
                ON public."LeadLineParticipationDecisions";
            DROP TRIGGER IF EXISTS "TR_LeadParticipationDecisions_OutcomeConsistency"
                ON public."LeadParticipationDecisions";
            DROP FUNCTION IF EXISTS nexora_check_lead_participation_line();
            DROP FUNCTION IF EXISTS nexora_check_lead_participation_header();
            DROP FUNCTION IF EXISTS nexora_validate_lead_participation_snapshot(bigint, bigint);
            """);
    }
}

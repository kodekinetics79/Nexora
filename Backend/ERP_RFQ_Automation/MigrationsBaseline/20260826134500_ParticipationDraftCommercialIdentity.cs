using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Aligns the durable commercial-identity invariant with the domain lifecycle. Draft Bid choices
/// may retain incomplete UOM/currency identity for later human completion; committed Bid choices
/// must remain complete. A database-bound parent-state mirror prevents a child from claiming draft
/// semantics beneath a committed participation decision.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260826134500_ParticipationDraftCommercialIdentity")]
public partial class ParticipationDraftCommercialIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."LeadParticipationDecisions" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."LeadLineParticipationDecisions" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."LeadLineParticipationDecisions"
                DISABLE TRIGGER "TR_LeadLineParticipationDecisions_AppendOnly";

            ALTER TABLE public."LeadLineParticipationDecisions"
                ADD COLUMN "DecisionIsCommitted" boolean NULL;

            UPDATE public."LeadLineParticipationDecisions" line
               SET "DecisionIsCommitted" = decision."IsCommitted"
              FROM public."LeadParticipationDecisions" decision
             WHERE decision."BusinessUnitId" = line."BusinessUnitId"
               AND decision."Id" = line."ParticipationDecisionId"
               AND decision."LeadId" = line."LeadId"
               AND decision."LeadRevisionId" = line."LeadRevisionId";

            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                      FROM public."LeadLineParticipationDecisions"
                     WHERE "DecisionIsCommitted" IS NULL
                ) THEN
                    RAISE EXCEPTION 'Participation line parent-state backfill is incomplete.';
                END IF;
            END $$;

            ALTER TABLE public."LeadLineParticipationDecisions"
                ALTER COLUMN "DecisionIsCommitted" SET NOT NULL;

            ALTER TABLE public."LeadLineParticipationDecisions"
                DROP CONSTRAINT "FK_LeadLineParticipationDecisions_DecisionConsistency";
            ALTER TABLE public."LeadParticipationDecisions"
                ADD CONSTRAINT "AK_LeadParticipationDecisions_CommittedConsistency"
                UNIQUE ("BusinessUnitId", "Id", "LeadId", "LeadRevisionId", "IsCommitted");
            ALTER TABLE public."LeadLineParticipationDecisions"
                ADD CONSTRAINT "FK_LeadLineParticipationDecisions_DecisionCommitConsistency"
                FOREIGN KEY ("BusinessUnitId", "ParticipationDecisionId", "LeadId", "LeadRevisionId", "DecisionIsCommitted")
                REFERENCES public."LeadParticipationDecisions"
                    ("BusinessUnitId", "Id", "LeadId", "LeadRevisionId", "IsCommitted")
                ON DELETE RESTRICT;

            ALTER TABLE public."LeadLineParticipationDecisions"
                DROP CONSTRAINT "CK_LeadLineParticipationDecisions_BidCommercialIdentity";
            ALTER TABLE public."LeadLineParticipationDecisions"
                ADD CONSTRAINT "CK_LeadLineParticipationDecisions_BidCommercialIdentity" CHECK (
                    NOT ("DecisionIsCommitted" AND "Choice" = 'Bid') OR
                    ("Quantity" > 0 AND "UomId" IS NOT NULL AND "CurrencyId" IS NOT NULL AND
                     "UnitOfMeasure" IS NOT NULL AND btrim("UnitOfMeasure") <> '' AND
                     "Currency" IS NOT NULL AND btrim("Currency") <> '')
                );

            ALTER TABLE public."LeadLineParticipationDecisions"
                ENABLE TRIGGER "TR_LeadLineParticipationDecisions_AppendOnly";
            ALTER TABLE public."LeadParticipationDecisions" FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."LeadLineParticipationDecisions" FORCE ROW LEVEL SECURITY;

            DO $$
            BEGIN
                IF NOT (SELECT relforcerowsecurity FROM pg_class
                        WHERE oid = 'public."LeadParticipationDecisions"'::regclass)
                   OR NOT (SELECT relforcerowsecurity FROM pg_class
                           WHERE oid = 'public."LeadLineParticipationDecisions"'::regclass) THEN
                    RAISE EXCEPTION 'Participation tables lost FORCE ROW LEVEL SECURITY during migration 20260826134500.';
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."LeadLineParticipationDecisions"
                DROP CONSTRAINT "CK_LeadLineParticipationDecisions_BidCommercialIdentity";
            ALTER TABLE public."LeadLineParticipationDecisions"
                ADD CONSTRAINT "CK_LeadLineParticipationDecisions_BidCommercialIdentity" CHECK (
                    "Choice" <> 'Bid' OR
                    ("Quantity" > 0 AND "UomId" IS NOT NULL AND "CurrencyId" IS NOT NULL AND
                     "UnitOfMeasure" IS NOT NULL AND btrim("UnitOfMeasure") <> '' AND
                     "Currency" IS NOT NULL AND btrim("Currency") <> '')
                );

            ALTER TABLE public."LeadLineParticipationDecisions"
                DROP CONSTRAINT "FK_LeadLineParticipationDecisions_DecisionCommitConsistency";
            ALTER TABLE public."LeadParticipationDecisions"
                DROP CONSTRAINT "AK_LeadParticipationDecisions_CommittedConsistency";
            ALTER TABLE public."LeadLineParticipationDecisions"
                ADD CONSTRAINT "FK_LeadLineParticipationDecisions_DecisionConsistency"
                FOREIGN KEY ("BusinessUnitId", "ParticipationDecisionId", "LeadId", "LeadRevisionId")
                REFERENCES public."LeadParticipationDecisions"
                    ("BusinessUnitId", "Id", "LeadId", "LeadRevisionId")
                ON DELETE RESTRICT;
            ALTER TABLE public."LeadLineParticipationDecisions"
                DROP COLUMN "DecisionIsCommitted";
            """);
    }
}

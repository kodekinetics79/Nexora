using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Makes the database enforce the same boundary as RfqPromotionService: a Lead cannot enter
/// CONVERTED_TO_RFQ until the formal RFQ and its exact promotion lineage already exist in the
/// same transaction. Existing legacy rows are left untouched; the guard applies to future
/// status changes only.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260826010000_ReserveConvertedLeadStatusForRfqPromotion")]
public partial class ReserveConvertedLeadStatusForRfqPromotion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nexora_require_rfq_promotion_for_converted_lead()
            RETURNS trigger AS $$
            DECLARE
                new_status_code text;
            BEGIN
                IF NEW."LeadStatusId" IS NOT DISTINCT FROM OLD."LeadStatusId" THEN
                    RETURN NEW;
                END IF;

                SELECT upper(regexp_replace(coalesce(status."SetupCode", status."SetupValue", ''), '[^A-Za-z0-9]+', '_', 'g'))
                  INTO new_status_code
                  FROM "Setup_Master" status
                 WHERE status."BusinessUnitID" = NEW."BusinessUnitID"
                   AND status."SetupID" = NEW."LeadStatusId";

                IF new_status_code = 'CONVERTED_TO_RFQ' AND NOT EXISTS (
                    SELECT 1
                      FROM "RFQ" rfq
                      JOIN "RfqPromotions" promotion
                        ON promotion."BusinessUnitId" = rfq."BusinessUnitID"
                       AND promotion."Id" = rfq."PromotionId"
                       AND promotion."LeadId" = rfq."LeadID"
                       AND promotion."LeadRevisionId" = rfq."SourceLeadRevisionId"
                       AND promotion."ParticipationDecisionId" = rfq."ParticipationDecisionId"
                     WHERE rfq."BusinessUnitID" = NEW."BusinessUnitID"
                       AND rfq."LeadID" = NEW."ID"
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'CONVERTED_TO_RFQ requires an exactly lineaged RFQ Promotion receipt';
                END IF;

                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_leads_require_rfq_promotion_for_converted_status ON "Leads";
            CREATE TRIGGER trg_leads_require_rfq_promotion_for_converted_status
                BEFORE UPDATE OF "LeadStatusId" ON "Leads"
                FOR EACH ROW EXECUTE FUNCTION nexora_require_rfq_promotion_for_converted_lead();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_leads_require_rfq_promotion_for_converted_status ON "Leads";
            DROP FUNCTION IF EXISTS nexora_require_rfq_promotion_for_converted_lead();
            """);
    }
}

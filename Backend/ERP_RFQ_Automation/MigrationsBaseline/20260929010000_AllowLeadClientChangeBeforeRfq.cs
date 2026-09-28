using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Lets a person correct a lead's client until an RFQ is made from the lead.
///
/// <para><b>The defect.</b> <c>nexora_validate_lead_commercial_identity()</c> refused ANY change
/// of a resolved lead's CustomerID or ContactID ("Lead customer identity is immutable once
/// resolved"). The application already allowed the correction up to the RFQ
/// (<c>LeadRepository.LinkClientCoreAsync</c>'s re-pointing guard), but the trigger defeated it,
/// so one wrong click — an Aramco RFQ linked to SEC because no Aramco customer existed yet — could
/// never be undone: the lead, its quote and every document after it would name SEC, and the
/// screen's advice ("reject and raise it again") was impossible because a re-upload is a
/// duplicate.</para>
///
/// <para><b>The boundary kept.</b> Once an RFQ references the lead the rule is exactly as before:
/// the RFQ inherits the client and every downstream document is addressed from it, and
/// <c>TR_RFQ_CommercialIdentity</c> still protects the RFQ's own copy. The tenant checks on the
/// customer and contact are unchanged.</para>
///
/// <para><b>Audit.</b> The trigger only permits; the change is recorded by the application in the
/// same transaction — an immutable <c>LeadReviewAudits</c> row (action <c>change-client</c>, with
/// before/after images) and a human canonical revision in the lead identity trail.</para>
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260929010000_AllowLeadClientChangeBeforeRfq")]
public partial class AllowLeadClientChangeBeforeRfq : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.nexora_validate_lead_commercial_identity() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
            BEGIN
                IF TG_OP = 'UPDATE'
                   AND ((OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID")
                     OR (OLD."ContactID" IS NOT NULL AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID"))
                   AND EXISTS (
                       SELECT 1 FROM "RFQ" rfq
                        WHERE rfq."LeadID" = OLD."ID"
                          AND rfq."BusinessUnitID" = OLD."BusinessUnitID") THEN
                    IF OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
                        RAISE EXCEPTION 'Lead customer identity is immutable once the lead has an RFQ' USING ERRCODE = '55000';
                    END IF;
                    RAISE EXCEPTION 'Lead contact identity is immutable once the lead has an RFQ' USING ERRCODE = '55000';
                END IF;
                IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM "Customers" customer
                    WHERE customer."ID" = NEW."CustomerID"
                      AND customer."BUID" = NEW."BusinessUnitID") THEN
                    RAISE EXCEPTION 'Lead customer must belong to the same tenant' USING ERRCODE = '23503';
                END IF;
                IF NEW."ContactID" IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM "Contacts" contact
                    WHERE contact."ID" = NEW."ContactID"
                      AND contact."CustomerID" = NEW."CustomerID") THEN
                    RAISE EXCEPTION 'Lead contact must belong to the resolved customer' USING ERRCODE = '23503';
                END IF;
                RETURN NEW;
            END; $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.nexora_validate_lead_commercial_identity() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
            BEGIN
                IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
                    RAISE EXCEPTION 'Lead customer identity is immutable once resolved' USING ERRCODE = '55000';
                END IF;
                IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
                    RAISE EXCEPTION 'Lead contact identity is immutable once resolved' USING ERRCODE = '55000';
                END IF;
                IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM "Customers" customer
                    WHERE customer."ID" = NEW."CustomerID"
                      AND customer."BUID" = NEW."BusinessUnitID") THEN
                    RAISE EXCEPTION 'Lead customer must belong to the same tenant' USING ERRCODE = '23503';
                END IF;
                IF NEW."ContactID" IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM "Contacts" contact
                    WHERE contact."ID" = NEW."ContactID"
                      AND contact."CustomerID" = NEW."CustomerID") THEN
                    RAISE EXCEPTION 'Lead contact must belong to the resolved customer' USING ERRCODE = '23503';
                END IF;
                RETURN NEW;
            END; $$;
            """);
    }
}

using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20260823180000_SimplifyLeadOwnershipAndQualification")]
public sealed class SimplifyLeadOwnershipAndQualification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AssignmentMethod", table: "Leads", type: "character varying(16)",
            maxLength: 16, nullable: false, defaultValue: "AUTOMATIC");
        migrationBuilder.AddColumn<long>(
            name: "AssignedByUserId", table: "Leads", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<bool>(
            name: "ManualAssignmentOverride", table: "Leads", type: "boolean",
            nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<long>(
            name: "AssignmentVersion", table: "Leads", type: "bigint",
            nullable: false, defaultValue: 1L);

        migrationBuilder.AddColumn<string>(
            name: "AssignmentMethod", table: "lead_assignments", type: "character varying(16)",
            maxLength: 16, nullable: false, defaultValue: "AUTOMATIC");
        migrationBuilder.AddColumn<bool>(
            name: "IsManualOverride", table: "lead_assignments", type: "boolean",
            nullable: false, defaultValue: false);

        // These tenant tables use FORCE RLS, which intentionally subjects even their owner to
        // tenant policies. Migration backfills run as that owner without a tenant setting, so
        // PostgreSQL correctly refuses the UPDATE. Lift FORCE only inside this migration's
        // transaction (ALTER takes ACCESS EXCLUSIVE), then restore and verify it below.
        migrationBuilder.Sql("""
            ALTER TABLE public."Leads" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."lead_assignments" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."RFQ" NO FORCE ROW LEVEL SECURITY;
            """);

        migrationBuilder.Sql("""
            UPDATE "lead_assignments"
               SET "AssignmentMethod" = 'MANUAL', "IsManualOverride" = TRUE
             WHERE "ReasonCode" = 'MANUAL_ASSIGNMENT';

            UPDATE "Leads" l
               SET "AssignmentMethod" = 'MANUAL',
                   "ManualAssignmentOverride" = TRUE,
                   "AssignedByUserId" = a."AssignedByUserId"
              FROM "lead_assignments" a
             WHERE a."BusinessUnitId" = l."BusinessUnitID"
               AND a."LeadId" = l."ID"
               AND a."EffectiveTo" IS NULL
               AND a."AssignmentMethod" = 'MANUAL';
            """);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Leads_AssignmentMethod", table: "Leads",
            sql: "\"AssignmentMethod\" IN ('AUTOMATIC','MANUAL')");
        migrationBuilder.CreateIndex(
            name: "IX_Leads_BusinessUnitID_AssignTo_AssignmentMethod",
            table: "Leads", columns: new[] { "BusinessUnitID", "AssignTo", "AssignmentMethod" });

        // Preserve legacy duplicate RFQs while making the oldest link canonical. The detached
        // rows remain fully queryable by RFQ id/number/commercial case and carry an explanation;
        // only their ambiguous LeadID edge is cleared before the uniqueness backstop is added.
        migrationBuilder.Sql("""
            WITH ranked AS (
                SELECT "ID", "LeadID",
                       first_value("ID") OVER (
                           PARTITION BY "BusinessUnitID", "LeadID"
                           ORDER BY "CreatedDate", "ID") AS canonical_id,
                       row_number() OVER (
                           PARTITION BY "BusinessUnitID", "LeadID"
                           ORDER BY "CreatedDate", "ID") AS ordinal
                  FROM "RFQ"
                 WHERE "LeadID" IS NOT NULL
            )
            UPDATE "RFQ" rfq
               SET "HeaderRemarks" = concat_ws(E'\n', nullif(rfq."HeaderRemarks", ''),
                       '[Migration] Duplicate LeadID link detached; canonical RFQ ID ' || ranked.canonical_id || '.'),
                   "LeadID" = NULL
              FROM ranked
             WHERE ranked."ID" = rfq."ID" AND ranked.ordinal > 1;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE public."Leads" FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."lead_assignments" FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."RFQ" FORCE ROW LEVEL SECURITY;

            DO $$
            DECLARE target text;
            BEGIN
                FOREACH target IN ARRAY ARRAY['Leads', 'lead_assignments', 'RFQ'] LOOP
                    IF NOT (SELECT relforcerowsecurity FROM pg_class
                            WHERE oid = format('public.%I', target)::regclass) THEN
                        RAISE EXCEPTION '% lost FORCE ROW LEVEL SECURITY during migration 20260823180000.', target;
                    END IF;
                END LOOP;
            END $$;
            """);

        migrationBuilder.DropCheckConstraint(name: "CK_RFQItems_Quantity_Positive", table: "RFQItems");
        migrationBuilder.AlterColumn<int>(
            name: "Quantity", table: "RFQItems", type: "integer", nullable: true,
            oldClrType: typeof(int), oldType: "integer");
        migrationBuilder.AddCheckConstraint(
            name: "CK_RFQItems_Quantity_Positive", table: "RFQItems",
            sql: "\"Quantity\" IS NULL OR \"Quantity\" > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Leads_BusinessUnitID_AssignTo_AssignmentMethod", table: "Leads");
        migrationBuilder.DropCheckConstraint(name: "CK_Leads_AssignmentMethod", table: "Leads");
        migrationBuilder.DropColumn(name: "AssignmentMethod", table: "lead_assignments");
        migrationBuilder.DropColumn(name: "IsManualOverride", table: "lead_assignments");
        migrationBuilder.DropColumn(name: "AssignmentMethod", table: "Leads");
        migrationBuilder.DropColumn(name: "AssignedByUserId", table: "Leads");
        migrationBuilder.DropColumn(name: "ManualAssignmentOverride", table: "Leads");
        migrationBuilder.DropColumn(name: "AssignmentVersion", table: "Leads");

        migrationBuilder.DropCheckConstraint(name: "CK_RFQItems_Quantity_Positive", table: "RFQItems");
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "RFQItems" WHERE "Quantity" IS NULL) THEN
                    RAISE EXCEPTION 'Cannot make RFQItems.Quantity non-null while clarification rows exist.';
                END IF;
            END $$;
            """);
        migrationBuilder.AlterColumn<int>(
            name: "Quantity", table: "RFQItems", type: "integer", nullable: false,
            oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.AddCheckConstraint(
            name: "CK_RFQItems_Quantity_Positive", table: "RFQItems", sql: "\"Quantity\" > 0");
    }
}

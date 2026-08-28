using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Projects the issuer identity needed by tenant-owned documents into the RLS-protected business
/// unit. This removes the need for a quote worker to read the platform control plane.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260828170000_BusinessUnitIssuerIdentity")]
public partial class BusinessUnitIssuerIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LegalName",
            table: "BusinessUnits",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CommercialRegistrationNumber",
            table: "BusinessUnits",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.Sql("""
            DO $issuer_identity$
            BEGIN
                IF EXISTS (
                    SELECT "PrimaryBusinessUnitId"
                      FROM platform."Tenants"
                     WHERE "PrimaryBusinessUnitId" IS NOT NULL
                     GROUP BY "PrimaryBusinessUnitId"
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23505',
                        MESSAGE = 'Cannot backfill business-unit issuer identity: multiple platform tenants claim the same primary business unit';
                END IF;
            END
            $issuer_identity$;

            UPDATE public."BusinessUnits" AS bu
               SET "LegalName" = NULLIF(btrim(t."LegalName"), ''),
                   "CommercialRegistrationNumber" = NULLIF(btrim(t."RegistrationNumber"), '')
              FROM platform."Tenants" AS t
             WHERE t."PrimaryBusinessUnitId" = bu."ID";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CommercialRegistrationNumber", table: "BusinessUnits");
        migrationBuilder.DropColumn(name: "LegalName", table: "BusinessUnits");
    }
}

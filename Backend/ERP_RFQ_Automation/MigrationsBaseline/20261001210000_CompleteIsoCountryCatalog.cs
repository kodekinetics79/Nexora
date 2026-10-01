using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Makes every existing tenant's supplier/customer country picker complete. Provisioning used to
/// create only the tenant's country of incorporation, which meant an international supplier could
/// not be assigned an address until an administrator manually recreated ISO reference data.
///
/// Existing codes are never overwritten: tenant spelling, activation and audit history remain
/// authoritative. This migration only inserts missing ISO-3166-1 alpha-2 codes.
/// </summary>
[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20261001210000_CompleteIsoCountryCatalog")]
public sealed class CompleteIsoCountryCatalog : Migration
{
    private const string SeedActor = "iso-country-catalog-v1";
    private const string SeedDescription = "ISO 3166 country catalogue";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        var values = string.Join(",\n", IsoCountryCatalogV1.Countries.Select(country =>
            $"('{SqlLiteral(country.Code)}', '{SqlLiteral(country.Name)}')"));

        migrationBuilder.Sql($$"""
            INSERT INTO "SetCountry"
                ("CountryCode", "CountryName", "Description", "BUID", "IsActive", "CreatedBy", "CreatedDate")
            SELECT catalog."Code", catalog."Name", '{{SeedDescription}}', business_unit."ID", TRUE,
                   '{{SeedActor}}', now()
            FROM "BusinessUnits" AS business_unit
            CROSS JOIN (VALUES
            {{values}}
            ) AS catalog("Code", "Name")
            WHERE NOT EXISTS (
                SELECT 1
                FROM "SetCountry" AS existing
                WHERE existing."BUID" = business_unit."ID"
                  AND upper(existing."CountryCode") = catalog."Code"
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        // Never remove a country that acquired address descendants after this migration. A safe
        // rollback may leave used reference rows behind; breaking a supplier address is worse than
        // an imperfectly symmetrical data migration.
        migrationBuilder.Sql($$"""
            DELETE FROM "SetCountry" AS country
            WHERE country."CreatedBy" = '{{SeedActor}}'
              AND country."Description" = '{{SeedDescription}}'
              AND NOT EXISTS (SELECT 1 FROM "SetState" state WHERE state."CountryID" = country."CountryID")
              AND NOT EXISTS (SELECT 1 FROM "SetCity" city WHERE city."CountryID" = country."CountryID")
              AND NOT EXISTS (SELECT 1 FROM "Suppliers" supplier WHERE supplier."CountryID" = country."CountryID");
            """);
    }

    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

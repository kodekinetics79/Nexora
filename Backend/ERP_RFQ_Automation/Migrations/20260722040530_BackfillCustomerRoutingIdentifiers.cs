using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCustomerRoutingIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO customer_identifiers
                    ("BusinessUnitId", "CustomerId", "IdentifierType", "NormalizedValue", "DisplayValue",
                     "IsVerified", "Confidence", "Source", "EffectiveFrom", "EffectiveTo")
                SELECT c."BUID", c."ID", 'ErpAccount',
                       regexp_replace(upper(trim(c."DocId")), '[^A-Z0-9]', '', 'g'), trim(c."DocId"),
                       true, 1.0, 'MigrationBackfill', COALESCE(c."CreatedOn", now()), NULL
                FROM "Customers" c
                WHERE c."BUID" IS NOT NULL AND NULLIF(trim(c."DocId"), '') IS NOT NULL
                ON CONFLICT DO NOTHING;

                INSERT INTO customer_identifiers
                    ("BusinessUnitId", "CustomerId", "IdentifierType", "NormalizedValue", "DisplayValue",
                     "IsVerified", "Confidence", "Source", "EffectiveFrom", "EffectiveTo")
                SELECT c."BUID", c."ID", 'Email', lower(trim(c."ContactEmail")), trim(c."ContactEmail"),
                       true, 1.0, 'MigrationBackfill', COALESCE(c."CreatedOn", now()), NULL
                FROM "Customers" c
                WHERE c."BUID" IS NOT NULL AND NULLIF(trim(c."ContactEmail"), '') IS NOT NULL
                ON CONFLICT DO NOTHING;

                INSERT INTO customer_identifiers
                    ("BusinessUnitId", "CustomerId", "IdentifierType", "NormalizedValue", "DisplayValue",
                     "IsVerified", "Confidence", "Source", "EffectiveFrom", "EffectiveTo")
                SELECT c."BUID", c."ID", 'CustomerName',
                       regexp_replace(upper(trim(c."Name")), '\s+', ' ', 'g'), trim(c."Name"),
                       true, 0.9, 'MigrationBackfill', COALESCE(c."CreatedOn", now()), NULL
                FROM "Customers" c
                WHERE c."BUID" IS NOT NULL AND NULLIF(trim(c."Name"), '') IS NOT NULL
                ON CONFLICT DO NOTHING;

                INSERT INTO customer_identifiers
                    ("BusinessUnitId", "CustomerId", "IdentifierType", "NormalizedValue", "DisplayValue",
                     "IsVerified", "Confidence", "Source", "EffectiveFrom", "EffectiveTo")
                SELECT c."BUID", contact."CustomerID", 'Email', lower(trim(contact."Email")), trim(contact."Email"),
                       true, 1.0, 'MigrationBackfill', COALESCE(contact."CreatedOn", now()), NULL
                FROM "Contacts" contact
                JOIN "Customers" c ON c."ID" = contact."CustomerID"
                WHERE c."BUID" IS NOT NULL AND NULLIF(trim(contact."Email"), '') IS NOT NULL
                  AND (contact."IsActive" = true OR contact."IsActive" IS NULL)
                ON CONFLICT DO NOTHING;

                INSERT INTO customer_identifiers
                    ("BusinessUnitId", "CustomerId", "IdentifierType", "NormalizedValue", "DisplayValue",
                     "IsVerified", "Confidence", "Source", "EffectiveFrom", "EffectiveTo")
                SELECT c."BUID", contact."CustomerID", 'Phone',
                       regexp_replace(COALESCE(NULLIF(trim(contact."MobileNo"), ''), trim(contact."PhoneNo")), '[^0-9]', '', 'g'),
                       COALESCE(NULLIF(trim(contact."MobileNo"), ''), trim(contact."PhoneNo")),
                       true, 0.95, 'MigrationBackfill', COALESCE(contact."CreatedOn", now()), NULL
                FROM "Contacts" contact
                JOIN "Customers" c ON c."ID" = contact."CustomerID"
                WHERE c."BUID" IS NOT NULL
                  AND COALESCE(NULLIF(trim(contact."MobileNo"), ''), NULLIF(trim(contact."PhoneNo"), '')) IS NOT NULL
                  AND (contact."IsActive" = true OR contact."IsActive" IS NULL)
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM customer_identifiers WHERE "Source" = 'MigrationBackfill';
                """);
        }
    }
}

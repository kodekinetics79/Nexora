using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class FixCommercialLineResolutionRfqTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $sync$
                DECLARE
                    contact_sequence text;
                    contact_max bigint;
                BEGIN
                    SELECT pg_get_serial_sequence('public."Contacts"', 'ID') INTO contact_sequence;
                    IF contact_sequence IS NOT NULL THEN
                        SELECT MAX("ID") INTO contact_max FROM public."Contacts";
                        IF contact_max IS NULL THEN
                            PERFORM setval(contact_sequence, 1, false);
                        ELSE
                            PERFORM setval(contact_sequence, contact_max, true);
                        END IF;
                    END IF;
                END $sync$;

                CREATE OR REPLACE FUNCTION public.nexora_validate_commercial_line_resolution()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM public."Leads" l
                        WHERE l."ID" = NEW."LeadId" AND l."BusinessUnitID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'resolution lead must belong to the same tenant';
                    END IF;
                    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."Products" p
                        WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'resolution product must belong to the same tenant';
                    END IF;
                    IF NEW."RfqId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."RFQ" r
                        WHERE r."ID" = NEW."RfqId" AND r."BusinessUnitID" = NEW."BusinessUnitId"
                          AND r."LeadID" = NEW."LeadId") THEN
                        RAISE EXCEPTION 'resolution RFQ must belong to the same tenant and lead';
                    END IF;
                    RETURN NEW;
                END $fn$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The migration repairs installed database drift without changing the EF model.
            // Keep the valid function and sequence position during a metadata rollback.
        }
    }
}

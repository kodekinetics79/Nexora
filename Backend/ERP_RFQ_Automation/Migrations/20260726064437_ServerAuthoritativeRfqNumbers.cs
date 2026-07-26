using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class ServerAuthoritativeRfqNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE SEQUENCE IF NOT EXISTS public.nexora_rfq_number_seq
                    AS bigint
                    START WITH 1
                    INCREMENT BY 1;

                DO $reconcile$
                DECLARE
                    persisted_high_water bigint;
                    sequence_high_water bigint;
                BEGIN
                    SELECT max((regexp_match("RFQNo", '([0-9]+)$'))[1]::bigint)
                    INTO persisted_high_water
                    FROM public."RFQ"
                    WHERE "RFQNo" ~ '^NXR-RFQ-[0-9]+-[0-9]{4}-[0-9]+$';

                    SELECT last_value INTO sequence_high_water
                    FROM public.nexora_rfq_number_seq;

                    IF persisted_high_water IS NOT NULL AND persisted_high_water >= sequence_high_water THEN
                        PERFORM setval('public.nexora_rfq_number_seq', persisted_high_water, true);
                    END IF;
                END
                $reconcile$;

                DO $grant$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT USAGE ON SEQUENCE public.nexora_rfq_number_seq TO nexora_tenant_app;
                    END IF;
                END
                $grant$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $revoke$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        REVOKE USAGE ON SEQUENCE public.nexora_rfq_number_seq FROM nexora_tenant_app;
                    END IF;
                END
                $revoke$;
                -- Preserve the high-water mark across rollback so issued RFQ numbers cannot be reused.
                """);
        }
    }
}

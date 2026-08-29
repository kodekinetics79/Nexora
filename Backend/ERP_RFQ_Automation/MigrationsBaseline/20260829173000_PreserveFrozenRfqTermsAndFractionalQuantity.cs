using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Preserves the buyer's immutable inquiry terms on the formal RFQ and removes the integer-only
/// quantity boundary from Lead intake through participation and RFQ promotion.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260829173000_PreserveFrozenRfqTermsAndFractionalQuantity")]
public partial class PreserveFrozenRfqTermsAndFractionalQuantity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- Fail the deployment instead of waiting indefinitely behind a long-running
            -- commercial write. Program.cs applies migrations before accepting traffic, so
            -- this is a bounded startup/predeploy guard rather than background schema drift.
            SET LOCAL lock_timeout = '30s';

            -- All three source columns are PostgreSQL integer columns at this migration boundary.
            -- Every integer is exactly representable by numeric(20,6), so ALTER TYPE itself is
            -- the atomic overflow/preflight guard. Do not SELECT the rows here: participation
            -- history is protected by FORCE ROW LEVEL SECURITY and migrations intentionally run
            -- without a tenant scope.
            ALTER TABLE public."LeadItems"
                ALTER COLUMN "Quantity" TYPE numeric(20,6)
                USING "Quantity"::numeric(20,6);
            ALTER TABLE public."LeadLineParticipationDecisions"
                ALTER COLUMN "Quantity" TYPE numeric(20,6)
                USING "Quantity"::numeric(20,6);
            ALTER TABLE public."RFQItems"
                ALTER COLUMN "Quantity" TYPE numeric(20,6)
                USING "Quantity"::numeric(20,6);

            -- Existing installations should already have this check from the baseline, but
            -- restore it idempotently for drifted databases and validate historical rows before
            -- the application can accept traffic. NULL remains legal for an unquoted draft line;
            -- zero and negative formal RFQ quantities never are.
            DO $rfq_quantity_constraint$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                      FROM pg_constraint
                     WHERE conrelid = 'public."RFQItems"'::regclass
                       AND conname = 'CK_RFQItems_Quantity_Positive'
                ) THEN
                    ALTER TABLE public."RFQItems"
                        ADD CONSTRAINT "CK_RFQItems_Quantity_Positive"
                        CHECK ("Quantity" IS NULL OR "Quantity" > 0) NOT VALID;
                END IF;
                ALTER TABLE public."RFQItems"
                    VALIDATE CONSTRAINT "CK_RFQItems_Quantity_Positive";
            END
            $rfq_quantity_constraint$;

            ALTER TABLE public."RFQ"
                ADD COLUMN IF NOT EXISTS "CustomerRfqReference" varchar(256) NULL,
                ADD COLUMN IF NOT EXISTS "RequiredDeliveryDate" timestamp without time zone NULL,
                ADD COLUMN IF NOT EXISTS "DeliveryLocation" varchar(1000) NULL,
                ADD COLUMN IF NOT EXISTS "AgreementReference" varchar(256) NULL,
                ADD COLUMN IF NOT EXISTS "BidClosingDateHijri" varchar(32) NULL,
                ADD COLUMN IF NOT EXISTS "InquiryType" varchar(16) NULL;
            ALTER TABLE public."RFQItems"
                ADD COLUMN IF NOT EXISTS "ExtraFields" jsonb NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."RFQItems" DROP COLUMN IF EXISTS "ExtraFields";
            ALTER TABLE public."RFQ"
                DROP COLUMN IF EXISTS "InquiryType",
                DROP COLUMN IF EXISTS "BidClosingDateHijri",
                DROP COLUMN IF EXISTS "AgreementReference",
                DROP COLUMN IF EXISTS "DeliveryLocation",
                DROP COLUMN IF EXISTS "RequiredDeliveryDate",
                DROP COLUMN IF EXISTS "CustomerRfqReference";

            ALTER TABLE public."RFQItems"
                ALTER COLUMN "Quantity" TYPE integer USING
                    CASE
                        WHEN "Quantity" IS NULL THEN NULL
                        WHEN "Quantity" = trunc("Quantity")
                         AND abs("Quantity") <= 2147483647
                        THEN "Quantity"::integer
                        ELSE "Quantity"::text::integer
                    END;
            ALTER TABLE public."LeadLineParticipationDecisions"
                ALTER COLUMN "Quantity" TYPE integer USING
                    CASE
                        WHEN "Quantity" IS NULL THEN NULL
                        WHEN "Quantity" = trunc("Quantity")
                         AND abs("Quantity") <= 2147483647
                        THEN "Quantity"::integer
                        ELSE "Quantity"::text::integer
                    END;
            ALTER TABLE public."LeadItems"
                ALTER COLUMN "Quantity" TYPE integer USING
                    CASE
                        WHEN "Quantity" IS NULL THEN NULL
                        WHEN "Quantity" = trunc("Quantity")
                         AND abs("Quantity") <= 2147483647
                        THEN "Quantity"::integer
                        ELSE "Quantity"::text::integer
                    END;
            """);
    }
}

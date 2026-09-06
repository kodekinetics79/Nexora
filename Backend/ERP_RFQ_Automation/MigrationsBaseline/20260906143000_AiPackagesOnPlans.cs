using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The database half of "a plan sells an AI package, and a tenant's deviation from it says why".
    ///
    /// <para><c>platform."Plans"</c> gains the package a plan sells and the monthly AI allowance a
    /// tenant on it starts with. <c>"AiProcessingPolicies"</c> gains the three fields that record a
    /// deliberate deviation from that package — reason, approver, timestamp — set together and
    /// cleared together, exactly as <c>Tenants."DeploymentProfileReason"</c> already is.</para>
    ///
    /// <para><b>Existing rows keep today's behaviour, deliberately.</b> Every plan that grants
    /// <c>capability.ai</c> is backfilled to <c>Private</c>, which is precisely what
    /// <c>AiProcessingPolicy.CreateSecureDefault</c> and the <c>nexora_create_default_ai_policy</c>
    /// trigger already produce: processing on, external processing off, whole-document egress shut.
    /// Every other plan becomes <c>Off</c>. The allowance is left NULL rather than invented —
    /// nobody here knows what a customer's monthly ceiling should be, and a number picked by a
    /// migration is a commercial term nobody agreed to. New plan writes must decide it, and
    /// existing tenants keep reporting the standing "no ceiling set" warning until somebody does.</para>
    ///
    /// <para>No tenant's own policy row is touched. A plan is copied at provisioning and never
    /// re-read — editing one does not reach back into tenants already created from it, and a
    /// migration must not do what the product itself refuses to do.</para>
    ///
    /// <para>The same DDL is in <c>MigrationsBaseline/Sql/03_tables_and_sequences.sql</c>, which is
    /// what builds a schema from scratch. Both are needed and they have to agree: the SQL covers a
    /// fresh database, this covers every database that already exists.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260906143000_AiPackagesOnPlans")]
    public partial class AiPackagesOnPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql("""
ALTER TABLE platform."Plans"
    ADD COLUMN IF NOT EXISTS "AiPackage" character varying(32) DEFAULT 'Off'::character varying NOT NULL,
    ADD COLUMN IF NOT EXISTS "AiMonthlyTokenAllowance" bigint NULL,
    ADD COLUMN IF NOT EXISTS "AiAllowanceUnlimited" boolean DEFAULT false NOT NULL;
""");

            // Preserves what these plans already provision. capability.ai present and true is the
            // only signal in the data about whether a plan was sold with AI at all.
            migrationBuilder.Sql("""
UPDATE platform."Plans"
   SET "AiPackage" = 'Private'
 WHERE "AiPackage" = 'Off'
   AND COALESCE(("Features"::jsonb ->> 'capability.ai')::boolean, FALSE) = TRUE;
""");

            migrationBuilder.Sql("""
ALTER TABLE public."AiProcessingPolicies"
    ADD COLUMN IF NOT EXISTS "PlanDeviationReason" character varying(1000) NULL,
    ADD COLUMN IF NOT EXISTS "PlanDeviationApprovedBy" character varying(255) NULL,
    ADD COLUMN IF NOT EXISTS "PlanDeviationApprovedOn" timestamp without time zone NULL;
""");

            // All three or none of them. A reason with no approver, or an approver with no reason,
            // is a half-written exception, and half-written is the shape things take when they are
            // typed under pressure.
            migrationBuilder.Sql("""
ALTER TABLE public."AiProcessingPolicies"
    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_PlanDeviation";
ALTER TABLE public."AiProcessingPolicies"
    ADD CONSTRAINT "CK_AiProcessingPolicies_PlanDeviation" CHECK (
        ("PlanDeviationReason" IS NULL AND "PlanDeviationApprovedBy" IS NULL
            AND "PlanDeviationApprovedOn" IS NULL)
        OR ("PlanDeviationReason" IS NOT NULL AND "PlanDeviationApprovedBy" IS NOT NULL
            AND "PlanDeviationApprovedOn" IS NOT NULL));
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql("""
ALTER TABLE public."AiProcessingPolicies"
    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_PlanDeviation",
    DROP COLUMN IF EXISTS "PlanDeviationReason",
    DROP COLUMN IF EXISTS "PlanDeviationApprovedBy",
    DROP COLUMN IF EXISTS "PlanDeviationApprovedOn";
""");
            migrationBuilder.Sql("""
ALTER TABLE platform."Plans"
    DROP COLUMN IF EXISTS "AiPackage",
    DROP COLUMN IF EXISTS "AiMonthlyTokenAllowance",
    DROP COLUMN IF EXISTS "AiAllowanceUnlimited";
""");
        }
    }
}

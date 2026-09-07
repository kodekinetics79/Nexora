using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Reading documents with the configured model IS the product, so a tenant is provisioned able
    /// to do it.
    ///
    /// <para>This deployment resolves exactly one inference endpoint, off-host. A tenant shipped
    /// with <c>ExternalProcessingAllowed = false</c> therefore gets no extraction at all, and the
    /// operator console tells whoever is standing in front of the customer that the contracted
    /// feature is refused. Two settings, on every tenant, with exactly one correct answer between
    /// them: that is not a control, it is a step. <c>EgressPolicy</c> is worse than a step — you
    /// cannot redact fields in a scanned raster before extraction, because locating the fields IS
    /// the extraction, so "RedactedFieldsOnly" on a scanned PDF returns nothing at all.</para>
    ///
    /// <para><b>What still bites.</b> The destination allow-list refuses any endpoint that is not
    /// this deployment's configured one; the purpose list still applies; the token ceiling still
    /// rations spend; and the switch can still be turned OFF per tenant — which is the answer that
    /// can genuinely differ: a customer whose contract forbids external processing, or a tenant
    /// being suspended during an incident.</para>
    ///
    /// <para><b>The constraint.</b> External processing required AllowedProvider and AllowedModel
    /// to be non-null. Unset means "any provider, any model", which the enforcement chain already
    /// reports as satisfied and which the allow-list narrows regardless — so the requirement bought
    /// nothing, and it made the switch impossible to set from a trigger or a migration, neither of
    /// which can know which endpoint the deployment runs. Redaction and privacy review still ride
    /// with it.</para>
    ///
    /// <para><b>What this migration deliberately does NOT touch.</b> There was once an RLS policy,
    /// <c>nexora_ai_default_provisioning</c>, pinning the exact shape of the provisioning row —
    /// including this switch. It is gone, and it must stay gone:
    /// <c>20260811110019_DropAiDefaultProvisioningPolicy</c> removed it because it pinned nine
    /// columns and not <c>BusinessUnitId</c>, so any RLS-bound role could write an AI governance
    /// row for ANOTHER tenant by reproducing those constants. Re-creating it with the switch
    /// flipped would reintroduce a proven cross-tenant write. That policy is FOR INSERT, and it
    /// pins <c>ExternalProcessingAllowed = false</c> on the row the provisioning trigger writes —
    /// so the trigger is left exactly as it is, and the value is set by the provisioning code
    /// immediately after the row exists, which the policy does not govern.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260906200000_ExtractionWorksOutOfTheBox")]
    public partial class ExtractionWorksOutOfTheBox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql("""
ALTER TABLE public."AiProcessingPolicies"
    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_TrustControls";
ALTER TABLE public."AiProcessingPolicies"
    ADD CONSTRAINT "CK_AiProcessingPolicies_TrustControls" CHECK (
        "ExternalDependencyCeilingPercent" BETWEEN 0 AND 10
        AND "RetentionDays" BETWEEN 1 AND 3650
        AND length(trim("AllowedDataClassifications")) > 0
        AND length(trim("EgressPolicy")) > 0
        AND length(trim("DataResidency")) > 0
        AND (NOT "ExternalProcessingAllowed"
             OR ("RedactionRequired" AND "PrivacyReviewRequired")));
""");

            migrationBuilder.Sql("""
ALTER TABLE public."AiProcessingPolicies"
    ALTER COLUMN "ExternalProcessingAllowed" SET DEFAULT TRUE,
    ALTER COLUMN "EgressPolicy" SET DEFAULT 'FullDocument'::character varying;
""");

            // Existing tenants, not only new ones. The tenants already provisioned are precisely
            // the ones sitting in a console that says their documents will not extract. Scoped to
            // IsEnabled, so a tenant deliberately switched off stays off.
            //
            // FORCE ROW LEVEL SECURITY applies to the table owner too, so a cross-tenant data
            // migration is refused with 42501 unless the force is lifted for the statement — the
            // same lift the Leads and EmailInquiryComponents migrations take, restored immediately
            // after.
            // The force is RESTORED to whatever it was, not asserted. Re-enabling it unconditionally
            // turns it ON in any database where it was off, and this table's only remaining policy
            // is scoped TO nexora_tenant_app — so under force, the SECURITY DEFINER provisioning
            // trigger has no policy admitting its insert and tenant creation fails with 42501.
            // Read the flag, lift it, write, put back exactly what was there.
            migrationBuilder.Sql("""
DO $nexora_extraction_default$
DECLARE
    was_forced boolean;
BEGIN
    SELECT relforcerowsecurity INTO was_forced
      FROM pg_class
     WHERE oid = 'public."AiProcessingPolicies"'::regclass;

    IF was_forced THEN
        ALTER TABLE public."AiProcessingPolicies" NO FORCE ROW LEVEL SECURITY;
    END IF;

    UPDATE public."AiProcessingPolicies"
       SET "ExternalProcessingAllowed" = TRUE,
           "EgressPolicy" = 'FullDocument',
           "RedactionRequired" = TRUE,
           "PrivacyReviewRequired" = TRUE,
           "Version" = "Version" + 1,
           "UpdatedOn" = now(),
           "UpdatedBy" = 'migration:extraction-works-out-of-the-box'
     WHERE "IsEnabled"
       AND (NOT "ExternalProcessingAllowed" OR "EgressPolicy" <> 'FullDocument');

    IF was_forced THEN
        ALTER TABLE public."AiProcessingPolicies" FORCE ROW LEVEL SECURITY;
    END IF;
END
$nexora_extraction_default$;
""");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql("""
ALTER TABLE public."AiProcessingPolicies"
    ALTER COLUMN "ExternalProcessingAllowed" SET DEFAULT FALSE,
    ALTER COLUMN "EgressPolicy" SET DEFAULT 'RedactedFieldsOnly'::character varying;
""");
        }
    }
}

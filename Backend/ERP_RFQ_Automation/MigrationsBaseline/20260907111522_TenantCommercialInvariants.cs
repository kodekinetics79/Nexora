using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Moves four tenant commercial invariants out of C# and into the database.
    ///
    /// WHY. Billing, usage, provisioning and offboarding are already enforced in Postgres —
    /// unique indexes, insert guards, append-only triggers that fire even under
    /// <c>session_replication_role = 'replica'</c>. Tenant configuration was the exception: its
    /// rules lived in per-endpoint C# with no backstop, so a rule was only as good as the last
    /// controller that remembered it, and a hand-written UPDATE or a restored backup was bound
    /// by nothing at all. Two of the four rules below were already provably skippable by
    /// ordering two legitimate API calls (set Internal, clear the invoice recipient, set
    /// Billable again — a Billable tenant that invoicing then refuses to bill, and that cannot
    /// be offboarded either, because offboarding requires a finalized invoice).
    ///
    /// ALL FOUR SHIP AS <c>NOT VALID</c>. That is deliberate and is the whole deployment story:
    /// Postgres applies a NOT VALID check to every INSERT and UPDATE from this moment on, but
    /// does not scan the existing table. New writes are held to the rule immediately; historical
    /// rows that predate it do not block the deploy. Run
    /// <c>scripts/deploy/tenant-integrity-preflight.sql</c> against production to count those
    /// rows, remediate them, and only then VALIDATE the constraints in a later release. A
    /// migration that takes an ACCESS EXCLUSIVE lock to scan platform."Tenants" during a deploy
    /// window, and fails the deploy on a row somebody created in March, is how integrity work
    /// gets reverted and not attempted again.
    ///
    /// PAYMENT TERMS ARE DELIBERATELY ABSENT from the invoiceability constraint even though they
    /// belong in it. <c>ValidateAccountContact</c> accepts null and accepts 0 today, and
    /// provisioning passes the request value straight through, so a database-level rule would
    /// turn an accepted API call into a 500 rather than a 400. The application layer is tightened
    /// first (a clean refusal, and a sane provisioning default); the column joins this constraint
    /// once production data is clean. Preflight check 3 already counts the affected rows.
    /// </summary>
    public partial class TenantCommercialInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A charged tenant must have somewhere to send the invoice.
            //
            // SubscriptionInvoiceService throws BillingConflictException without a recipient, so a
            // Billable tenant with a null BillingContactEmail is billable on paper and unbillable
            // in fact — and cannot be offboarded either, because offboarding readiness requires a
            // finalized invoice. ValidateAccountContact refuses to CLEAR the field, but that check
            // is one-directional: setting billingMode to Internal, clearing the address, and
            // setting Billable again walked straight past it, because ValidateCommercialTerms
            // never re-examines the recipient. This closes that sequence at the table.
            //
            // PLANID IS DELIBERATELY NOT IN THIS CONSTRAINT, though the reflex is to add it.
            // A Billable tenant with no plan is a REAL AND SUPPORTED STATE: UnplannedTenantAllowance
            // (Platform/Entitlements/EntitlementService.cs:35) gives a newly provisioned tenant full
            // capacity for fourteen days and a reduced floor of 3 seats and 50 documents a month
            // afterwards, expressly "so setup is never interrupted". Requiring a plan here would
            // forbid the state the product is designed around and would break provisioning for
            // every customer whose plan is chosen after the workspace is created. Four tests in
            // the existing suite assert that state; they were right and the first draft of this
            // constraint was wrong.
            //
            // SCOPED TO THE LIVE STATUSES, and that scoping is load-bearing rather than timid.
            // ErasePersonalDataAsync deliberately NULLS BillingContactEmail — the customer's AP
            // address is the customer's personal data — and TenantLifecycleGraph.ErasureAllowedFrom
            // permits erasure only from Suspended or Archived. An unscoped constraint would
            // therefore make a compliance operation fail on any tenant that had ever been
            // Billable, which is a worse defect than the one being closed. The rule that actually
            // matters is narrower and truer: a tenant WE ARE CHARGING must have somewhere to send
            // the invoice.
            migrationBuilder.Sql("""
                ALTER TABLE platform."Tenants"
                    ADD CONSTRAINT "CK_Tenants_BillableIsInvoiceable"
                    CHECK (
                        "Status" NOT IN ('Provisioning', 'Active', 'PastDue')
                        OR "BillingMode" <> 'Billable'
                        OR "BillingContactEmail" IS NOT NULL
                    ) NOT VALID;
                """);

            // A trial without an end date is an unbounded giveaway. The activation policy already
            // says so; nothing stopped a direct write from disagreeing.
            migrationBuilder.Sql("""
                ALTER TABLE platform."Tenants"
                    ADD CONSTRAINT "CK_Tenants_TrialHasEnd"
                    CHECK ("BillingMode" <> 'Trial' OR "TrialEndsOn" IS NOT NULL) NOT VALID;
                """);

            // Free service is a decision somebody has to own in writing. Fifteen characters is the
            // floor the API already imposes; putting it here means the row cannot exist without it,
            // whichever code path wrote it.
            migrationBuilder.Sql("""
                ALTER TABLE platform."Tenants"
                    ADD CONSTRAINT "CK_Tenants_NonBillableHasReason"
                    CHECK (
                        "BillingMode" = 'Billable'
                        OR length(btrim(coalesce("BillingModeReason", ''))) >= 15
                    ) NOT VALID;
                """);

            // A non-Production deployment profile decides which production prerequisites a tenant
            // may DEFER at activation. It is therefore an approval, and an approval with no
            // approver recorded is not one. DeploymentProfilePolicy.IsApproved already fails
            // closed on a missing approver; this makes the unapproved row unrepresentable.
            migrationBuilder.Sql("""
                ALTER TABLE platform."Tenants"
                    ADD CONSTRAINT "CK_Tenants_NonProdApproved"
                    CHECK (
                        "DeploymentProfile" = 'Production'
                        OR (
                               "DeploymentProfileReason"     IS NOT NULL
                           AND "DeploymentProfileApprovedBy" IS NOT NULL
                           AND "DeploymentProfileApprovedOn" IS NOT NULL
                        )
                    ) NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE platform."Tenants"
                    DROP CONSTRAINT IF EXISTS "CK_Tenants_NonProdApproved",
                    DROP CONSTRAINT IF EXISTS "CK_Tenants_NonBillableHasReason",
                    DROP CONSTRAINT IF EXISTS "CK_Tenants_TrialHasEnd",
                    DROP CONSTRAINT IF EXISTS "CK_Tenants_BillableIsInvoiceable";
                """);
        }
    }
}

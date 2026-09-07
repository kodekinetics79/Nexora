-- Tenant integrity preflight.
--
-- RUN THIS AGAINST PRODUCTION BEFORE DEPLOYING THE TENANT INTEGRITY MIGRATIONS.
-- It is read-only. Every query is written to return ZERO ROWS on a healthy database;
-- any row returned is a constraint that would fail to apply, or would apply and then
-- refuse a write the application currently makes.
--
--   psql "$PROD_CONNECTION" -f scripts/deploy/tenant-integrity-preflight.sql
--
-- A non-empty result from checks 1 or 2 is an INCIDENT, not a migration blocker: two
-- tenants sharing a primary business unit means the row-level-security predicate that
-- isolates customer data has been resolving two customers to one scope. Stop and
-- investigate before touching the schema.
--
-- Checks 3-6 gate the commercial CHECK constraints, and they are NOT merely a VALIDATE-time
-- concern. READ THIS BEFORE DEPLOYING.
--
-- NOT VALID means PostgreSQL does not SCAN the table when the constraint is added, so the
-- migration itself cannot fail on legacy rows. It does NOT mean legacy rows are exempt
-- afterwards: the constraint is re-evaluated on EVERY UPDATE of a row, including updates that
-- touch none of the constrained columns. A pre-existing Active + Billable tenant with a null
-- invoice recipient therefore becomes UNWRITABLE the moment these constraints land -- it cannot
-- be renamed, cannot be suspended, cannot have its status changed during an incident. Verified
-- empirically against this schema, not assumed.
--
-- So any row returned by checks 3-6 must be REMEDIATED BEFORE THE DEPLOY, not before a later
-- VALIDATE. Treat them as blockers, not as a backlog.
--
-- LOCKS. This release is not instantaneous: CREATE UNIQUE INDEX (not CONCURRENTLY) holds SHARE
-- on platform."Tenants", and ADD FOREIGN KEY holds ACCESS EXCLUSIVE on it plus SHARE ROW
-- EXCLUSIVE on public."BusinessUnits" while it scans. Tenant writes block for the duration.
--
-- MIGRATIONS APPLY AT STARTUP by default (Database:ApplyMigrationsOnStartup). A duplicate found
-- by check 1 is therefore not a failed script -- it is a crash-looping deploy, the same shape as
-- the inotify outage. Run this BEFORE the release, and consider disabling startup migration for
-- this one.

\echo '=============================================================='
\echo ' 1. Tenants sharing a primary business unit  (MUST be empty)'
\echo '=============================================================='
SELECT "PrimaryBusinessUnitId",
       count(*)                       AS tenant_count,
       string_agg("Slug", ', ' ORDER BY "Id") AS slugs
FROM platform."Tenants"
WHERE "PrimaryBusinessUnitId" IS NOT NULL
GROUP BY "PrimaryBusinessUnitId"
HAVING count(*) > 1;

\echo ''
\echo '=============================================================='
\echo ' 2. Tenants pointing at a business unit that does not exist'
\echo '    (MUST be empty)'
\echo '=============================================================='
SELECT t."Id", t."Slug", t."Status", t."PrimaryBusinessUnitId"
FROM platform."Tenants" t
LEFT JOIN public."BusinessUnits" b ON b."ID" = t."PrimaryBusinessUnitId"
WHERE t."PrimaryBusinessUnitId" IS NOT NULL
  AND b."ID" IS NULL;

\echo ''
\echo '=============================================================='
\echo ' 3. LIVE Billable tenants with nowhere to send the invoice'
\echo '    (blocks CK_Tenants_BillableIsInvoiceable at VALIDATE time.'
\echo '     Scoped to live statuses because personal-data erasure'
\echo '     legitimately nulls this column on a Suspended/Archived'
\echo '     tenant -- the customer AP address is personal data.)'
\echo '=============================================================='
SELECT "Id", "Slug", "Status", "BillingMode"
FROM platform."Tenants"
WHERE "Status" IN ('Provisioning', 'Active', 'PastDue')
  AND "BillingMode" = 'Billable'
  AND "BillingContactEmail" IS NULL
ORDER BY "Status", "Id";

-- Informational, and NOT constrained: a plan-less Billable tenant is a supported state
-- (UnplannedTenantAllowance), and payment terms are not in the constraint because zero is a
-- real term ("due on receipt") and null is refused by the API rather than the table.
SELECT "Id", "Slug", "Status",
       ("PlanId" IS NULL)                     AS no_plan_unplanned_allowance,
       (coalesce("PaymentTermsDays", -1) < 0) AS payment_terms_unstated
FROM platform."Tenants"
WHERE "Status" IN ('Provisioning', 'Active', 'PastDue')
  AND "BillingMode" = 'Billable'
  AND ("PlanId" IS NULL OR "PaymentTermsDays" IS NULL)
ORDER BY "Id";

\echo ''
\echo '=============================================================='
\echo ' 4. Trials with no end date  (an unbounded giveaway)'
\echo '=============================================================='
SELECT "Id", "Slug", "Status", "CreatedOn"
FROM platform."Tenants"
WHERE "BillingMode" = 'Trial'
  AND "TrialEndsOn" IS NULL
ORDER BY "CreatedOn";

\echo ''
\echo '=============================================================='
\echo ' 5. Non-billable tenants with no written justification'
\echo '=============================================================='
SELECT "Id", "Slug", "BillingMode", "Status"
FROM platform."Tenants"
WHERE "BillingMode" <> 'Billable'
  AND length(btrim(coalesce("BillingModeReason", ''))) < 15
ORDER BY "BillingMode", "Id";

\echo ''
\echo '=============================================================='
\echo ' 6. Non-production deployment profiles with no recorded approval'
\echo '=============================================================='
SELECT "Id", "Slug", "DeploymentProfile", "Status",
       ("DeploymentProfileReason" IS NULL)     AS missing_reason,
       ("DeploymentProfileApprovedBy" IS NULL) AS missing_approver,
       ("DeploymentProfileApprovedOn" IS NULL) AS missing_approval_date
FROM platform."Tenants"
WHERE "DeploymentProfile" <> 'Production'
  AND NOT (
        "DeploymentProfileReason"     IS NOT NULL
    AND "DeploymentProfileApprovedBy" IS NOT NULL
    AND "DeploymentProfileApprovedOn" IS NOT NULL
  )
ORDER BY "Id";

\echo ''
\echo '=============================================================='
\echo ' 7. Live tenants referencing a plan that no longer exists'
\echo '    (today the FK is SET NULL, so this should be empty --'
\echo '     a row here means a plan deletion already blanked a tenant)'
\echo '=============================================================='
SELECT t."Id", t."Slug", t."Status", t."PlanId"
FROM platform."Tenants" t
LEFT JOIN platform."Plans" p ON p."Id" = t."PlanId"
WHERE t."PlanId" IS NOT NULL
  AND p."Id" IS NULL;

\echo ''
\echo '=============================================================='
\echo ' 8. Active tenants that would fail their own activation bar'
\echo '    (informational -- the continuous-invariant change refuses'
\echo '     FUTURE writes that leave a tenant here; it does not'
\echo '     retroactively suspend anyone)'
\echo '=============================================================='
SELECT "Id", "Slug",
       ("LegalName" IS NULL OR btrim("LegalName") = '')                 AS missing_legal_name,
       ("RegistrationNumber" IS NULL OR btrim("RegistrationNumber") = '') AS missing_registration,
       ("ContactEmail" IS NULL OR btrim("ContactEmail") = '')           AS missing_contact,
       ("CountryCode" IS NULL)                                          AS missing_country,
       ("DataRegion" IS NULL OR btrim("DataRegion") = '')               AS missing_data_region
FROM platform."Tenants"
WHERE "Status" = 'Active'
  AND (
       "LegalName" IS NULL OR btrim("LegalName") = ''
    OR "RegistrationNumber" IS NULL OR btrim("RegistrationNumber") = ''
    OR "ContactEmail" IS NULL OR btrim("ContactEmail") = ''
    OR "CountryCode" IS NULL
    OR "DataRegion" IS NULL OR btrim("DataRegion") = ''
  )
ORDER BY "Id";

\echo ''
\echo '=============================================================='
\echo ' Preflight complete. Zero rows above = safe to migrate.'
\echo '=============================================================='

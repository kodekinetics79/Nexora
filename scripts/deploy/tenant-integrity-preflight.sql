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
-- Checks 3-6 gate the commercial CHECK constraints. Those constraints ship as NOT VALID
-- precisely so legacy rows do not block a deploy — but you still need to know how many
-- there are, because VALIDATE CONSTRAINT in the following release will fail until they
-- are remediated.

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
\echo ' 3. Billable tenants that cannot actually be invoiced'
\echo '    (blocks CK_Tenants_BillableIsInvoiceable at VALIDATE time)'
\echo '=============================================================='
SELECT "Id", "Slug", "Status",
       ("PlanId" IS NULL)                                   AS missing_plan,
       ("BillingContactEmail" IS NULL)                      AS missing_invoice_recipient,
       (coalesce("PaymentTermsDays", 0) <= 0)               AS missing_payment_terms
FROM platform."Tenants"
WHERE "BillingMode" = 'Billable'
  AND NOT (
        "PlanId" IS NOT NULL
    AND "BillingContactEmail" IS NOT NULL
    AND coalesce("PaymentTermsDays", 0) > 0
  )
ORDER BY "Status", "Id";

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

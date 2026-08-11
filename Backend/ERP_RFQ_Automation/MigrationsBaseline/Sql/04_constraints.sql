-- ==========================================================================
-- Primary keys, unique keys, CHECK and EXCLUDE constraints
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
--
-- Every statement is IDEMPOTENT. Production is still at the pre-squash head with
-- the whole schema already materialised, and Program.cs applies migrations
-- uncaught at boot, so a bare CREATE here is a failed deploy. Objects with no
-- IF NOT EXISTS form are wrapped in a DO block that checks pg_catalog for that
-- exact object - never a broader condition that could skip a policy or a
-- constraint the database is genuinely missing.
-- ==========================================================================

--
-- Name: SubscriptionInvoices AK_SubscriptionInvoices_TenantId_Id; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SubscriptionInvoices_TenantId_Id'
      AND conrelid = to_regclass('platform."SubscriptionInvoices"')
) THEN
ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "AK_SubscriptionInvoices_TenantId_Id" UNIQUE ("TenantId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionRevenueActions AK_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_Id; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_Id'
      AND conrelid = to_regclass('platform."SubscriptionRevenueActions"')
) THEN
ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "AK_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_Id" UNIQUE ("TenantId", "SubscriptionInvoiceId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionTaxRules AK_SubscriptionTaxRules_Id_Version; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SubscriptionTaxRules_Id_Version'
      AND conrelid = to_regclass('platform."SubscriptionTaxRules"')
) THEN
ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "AK_SubscriptionTaxRules_Id_Version" UNIQUE ("Id", "Version");
END IF;
END
$nexora_idem$;



--
-- Name: TenantDataAssets AK_TenantDataAssets_TenantId_Id; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_TenantDataAssets_TenantId_Id'
      AND conrelid = to_regclass('platform."TenantDataAssets"')
) THEN
ALTER TABLE ONLY platform."TenantDataAssets"
    ADD CONSTRAINT "AK_TenantDataAssets_TenantId_Id" UNIQUE ("TenantId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: UsageEvents AK_UsageEvents_TenantId_UsageEventId; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_UsageEvents_TenantId_UsageEventId'
      AND conrelid = to_regclass('platform."UsageEvents"')
) THEN
ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "AK_UsageEvents_TenantId_UsageEventId" UNIQUE ("TenantId", "UsageEventId");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionTaxRules EX_SubscriptionTaxRules_ApprovedInterval; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'EX_SubscriptionTaxRules_ApprovedInterval'
      AND conrelid = to_regclass('platform."SubscriptionTaxRules"')
) THEN
ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "EX_SubscriptionTaxRules_ApprovedInterval" EXCLUDE USING gist ("JurisdictionCode" WITH =, "BuyerCountryCode" WITH =, "Currency" WITH =, tstzrange("EffectiveFromUtc", COALESCE("EffectiveToUtc", 'infinity'::timestamp with time zone), '[)'::text) WITH &&) WHERE ((("Status")::text = 'Approved'::text));
END IF;
END
$nexora_idem$;



--
-- Name: UsageCoverageSegments EX_UsageCoverageSegments_NoAuthoritativeOverlap; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'EX_UsageCoverageSegments_NoAuthoritativeOverlap'
      AND conrelid = to_regclass('platform."UsageCoverageSegments"')
) THEN
ALTER TABLE ONLY platform."UsageCoverageSegments"
    ADD CONSTRAINT "EX_UsageCoverageSegments_NoAuthoritativeOverlap" EXCLUDE USING gist ("TenantId" WITH =, "MeterKey" WITH =, tstzrange("StartUtc", "EndUtc", '[)'::text) WITH &&) WHERE ((("Completeness")::text = 'Complete'::text));
END IF;
END
$nexora_idem$;



--
-- Name: AccountingOutbox PK_AccountingOutbox; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AccountingOutbox'
      AND conrelid = to_regclass('platform."AccountingOutbox"')
) THEN
ALTER TABLE ONLY platform."AccountingOutbox"
    ADD CONSTRAINT "PK_AccountingOutbox" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BillingStatementLines PK_BillingStatementLines; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BillingStatementLines'
      AND conrelid = to_regclass('platform."BillingStatementLines"')
) THEN
ALTER TABLE ONLY platform."BillingStatementLines"
    ADD CONSTRAINT "PK_BillingStatementLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BillingStatements PK_BillingStatements; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BillingStatements'
      AND conrelid = to_regclass('platform."BillingStatements"')
) THEN
ALTER TABLE ONLY platform."BillingStatements"
    ADD CONSTRAINT "PK_BillingStatements" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ImpersonationSessions PK_ImpersonationSessions; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ImpersonationSessions'
      AND conrelid = to_regclass('platform."ImpersonationSessions"')
) THEN
ALTER TABLE ONLY platform."ImpersonationSessions"
    ADD CONSTRAINT "PK_ImpersonationSessions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: Plans PK_Plans; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_Plans'
      AND conrelid = to_regclass('platform."Plans"')
) THEN
ALTER TABLE ONLY platform."Plans"
    ADD CONSTRAINT "PK_Plans" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformAuditLogs PK_PlatformAuditLogs; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformAuditLogs'
      AND conrelid = to_regclass('platform."PlatformAuditLogs"')
) THEN
ALTER TABLE ONLY platform."PlatformAuditLogs"
    ADD CONSTRAINT "PK_PlatformAuditLogs" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformBrowserTrusts PK_PlatformBrowserTrusts; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformBrowserTrusts'
      AND conrelid = to_regclass('platform."PlatformBrowserTrusts"')
) THEN
ALTER TABLE ONLY platform."PlatformBrowserTrusts"
    ADD CONSTRAINT "PK_PlatformBrowserTrusts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformEmailSettings PK_PlatformEmailSettings; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformEmailSettings'
      AND conrelid = to_regclass('platform."PlatformEmailSettings"')
) THEN
ALTER TABLE ONLY platform."PlatformEmailSettings"
    ADD CONSTRAINT "PK_PlatformEmailSettings" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformMfaChallenges PK_PlatformMfaChallenges; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformMfaChallenges'
      AND conrelid = to_regclass('platform."PlatformMfaChallenges"')
) THEN
ALTER TABLE ONLY platform."PlatformMfaChallenges"
    ADD CONSTRAINT "PK_PlatformMfaChallenges" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformMfaCredentials PK_PlatformMfaCredentials; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformMfaCredentials'
      AND conrelid = to_regclass('platform."PlatformMfaCredentials"')
) THEN
ALTER TABLE ONLY platform."PlatformMfaCredentials"
    ADD CONSTRAINT "PK_PlatformMfaCredentials" PRIMARY KEY ("PlatformUserId");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformMfaPolicies PK_PlatformMfaPolicies; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformMfaPolicies'
      AND conrelid = to_regclass('platform."PlatformMfaPolicies"')
) THEN
ALTER TABLE ONLY platform."PlatformMfaPolicies"
    ADD CONSTRAINT "PK_PlatformMfaPolicies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformMfaRecoveryCodes PK_PlatformMfaRecoveryCodes; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformMfaRecoveryCodes'
      AND conrelid = to_regclass('platform."PlatformMfaRecoveryCodes"')
) THEN
ALTER TABLE ONLY platform."PlatformMfaRecoveryCodes"
    ADD CONSTRAINT "PK_PlatformMfaRecoveryCodes" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformSessions PK_PlatformSessions; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformSessions'
      AND conrelid = to_regclass('platform."PlatformSessions"')
) THEN
ALTER TABLE ONLY platform."PlatformSessions"
    ADD CONSTRAINT "PK_PlatformSessions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PlatformUsers PK_PlatformUsers; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PlatformUsers'
      AND conrelid = to_regclass('platform."PlatformUsers"')
) THEN
ALTER TABLE ONLY platform."PlatformUsers"
    ADD CONSTRAINT "PK_PlatformUsers" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ProvisioningDrafts PK_ProvisioningDrafts; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ProvisioningDrafts'
      AND conrelid = to_regclass('platform."ProvisioningDrafts"')
) THEN
ALTER TABLE ONLY platform."ProvisioningDrafts"
    ADD CONSTRAINT "PK_ProvisioningDrafts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ProvisioningExecutions PK_ProvisioningExecutions; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ProvisioningExecutions'
      AND conrelid = to_regclass('platform."ProvisioningExecutions"')
) THEN
ALTER TABLE ONLY platform."ProvisioningExecutions"
    ADD CONSTRAINT "PK_ProvisioningExecutions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ProvisioningSteps PK_ProvisioningSteps; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ProvisioningSteps'
      AND conrelid = to_regclass('platform."ProvisioningSteps"')
) THEN
ALTER TABLE ONLY platform."ProvisioningSteps"
    ADD CONSTRAINT "PK_ProvisioningSteps" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: RateCardLines PK_RateCardLines; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_RateCardLines'
      AND conrelid = to_regclass('platform."RateCardLines"')
) THEN
ALTER TABLE ONLY platform."RateCardLines"
    ADD CONSTRAINT "PK_RateCardLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: RateCards PK_RateCards; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_RateCards'
      AND conrelid = to_regclass('platform."RateCards"')
) THEN
ALTER TABLE ONLY platform."RateCards"
    ADD CONSTRAINT "PK_RateCards" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionCreditNotes PK_SubscriptionCreditNotes; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SubscriptionCreditNotes'
      AND conrelid = to_regclass('platform."SubscriptionCreditNotes"')
) THEN
ALTER TABLE ONLY platform."SubscriptionCreditNotes"
    ADD CONSTRAINT "PK_SubscriptionCreditNotes" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionInvoices PK_SubscriptionInvoices; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SubscriptionInvoices'
      AND conrelid = to_regclass('platform."SubscriptionInvoices"')
) THEN
ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "PK_SubscriptionInvoices" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionPayments PK_SubscriptionPayments; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SubscriptionPayments'
      AND conrelid = to_regclass('platform."SubscriptionPayments"')
) THEN
ALTER TABLE ONLY platform."SubscriptionPayments"
    ADD CONSTRAINT "PK_SubscriptionPayments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionRevenueActions PK_SubscriptionRevenueActions; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SubscriptionRevenueActions'
      AND conrelid = to_regclass('platform."SubscriptionRevenueActions"')
) THEN
ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "PK_SubscriptionRevenueActions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionTaxRules PK_SubscriptionTaxRules; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SubscriptionTaxRules'
      AND conrelid = to_regclass('platform."SubscriptionTaxRules"')
) THEN
ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "PK_SubscriptionTaxRules" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupportTicketLinks PK_SupportTicketLinks; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SupportTicketLinks'
      AND conrelid = to_regclass('platform."SupportTicketLinks"')
) THEN
ALTER TABLE ONLY platform."SupportTicketLinks"
    ADD CONSTRAINT "PK_SupportTicketLinks" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupportTicketNotes PK_SupportTicketNotes; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SupportTicketNotes'
      AND conrelid = to_regclass('platform."SupportTicketNotes"')
) THEN
ALTER TABLE ONLY platform."SupportTicketNotes"
    ADD CONSTRAINT "PK_SupportTicketNotes" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupportTickets PK_SupportTickets; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SupportTickets'
      AND conrelid = to_regclass('platform."SupportTickets"')
) THEN
ALTER TABLE ONLY platform."SupportTickets"
    ADD CONSTRAINT "PK_SupportTickets" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantAdminInvitations PK_TenantAdminInvitations; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantAdminInvitations'
      AND conrelid = to_regclass('platform."TenantAdminInvitations"')
) THEN
ALTER TABLE ONLY platform."TenantAdminInvitations"
    ADD CONSTRAINT "PK_TenantAdminInvitations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantDataAssets PK_TenantDataAssets; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantDataAssets'
      AND conrelid = to_regclass('platform."TenantDataAssets"')
) THEN
ALTER TABLE ONLY platform."TenantDataAssets"
    ADD CONSTRAINT "PK_TenantDataAssets" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantDataRecoveryEvidence PK_TenantDataRecoveryEvidence; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantDataRecoveryEvidence'
      AND conrelid = to_regclass('platform."TenantDataRecoveryEvidence"')
) THEN
ALTER TABLE ONLY platform."TenantDataRecoveryEvidence"
    ADD CONSTRAINT "PK_TenantDataRecoveryEvidence" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantDeletionCertificates PK_TenantDeletionCertificates; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantDeletionCertificates'
      AND conrelid = to_regclass('platform."TenantDeletionCertificates"')
) THEN
ALTER TABLE ONLY platform."TenantDeletionCertificates"
    ADD CONSTRAINT "PK_TenantDeletionCertificates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantExportReceipts PK_TenantExportReceipts; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantExportReceipts'
      AND conrelid = to_regclass('platform."TenantExportReceipts"')
) THEN
ALTER TABLE ONLY platform."TenantExportReceipts"
    ADD CONSTRAINT "PK_TenantExportReceipts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantLegalHolds PK_TenantLegalHolds; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantLegalHolds'
      AND conrelid = to_regclass('platform."TenantLegalHolds"')
) THEN
ALTER TABLE ONLY platform."TenantLegalHolds"
    ADD CONSTRAINT "PK_TenantLegalHolds" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantLifecycleEvents PK_TenantLifecycleEvents; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantLifecycleEvents'
      AND conrelid = to_regclass('platform."TenantLifecycleEvents"')
) THEN
ALTER TABLE ONLY platform."TenantLifecycleEvents"
    ADD CONSTRAINT "PK_TenantLifecycleEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: TenantMeterSourcePolicies PK_TenantMeterSourcePolicies; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantMeterSourcePolicies'
      AND conrelid = to_regclass('platform."TenantMeterSourcePolicies"')
) THEN
ALTER TABLE ONLY platform."TenantMeterSourcePolicies"
    ADD CONSTRAINT "PK_TenantMeterSourcePolicies" PRIMARY KEY ("TenantId", "MeterKey");
END IF;
END
$nexora_idem$;



--
-- Name: TenantOffboardings PK_TenantOffboardings; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantOffboardings'
      AND conrelid = to_regclass('platform."TenantOffboardings"')
) THEN
ALTER TABLE ONLY platform."TenantOffboardings"
    ADD CONSTRAINT "PK_TenantOffboardings" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: Tenants PK_Tenants; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_Tenants'
      AND conrelid = to_regclass('platform."Tenants"')
) THEN
ALTER TABLE ONLY platform."Tenants"
    ADD CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: UsageCoverageSegments PK_UsageCoverageSegments; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_UsageCoverageSegments'
      AND conrelid = to_regclass('platform."UsageCoverageSegments"')
) THEN
ALTER TABLE ONLY platform."UsageCoverageSegments"
    ADD CONSTRAINT "PK_UsageCoverageSegments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: UsageEventRatings PK_UsageEventRatings; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_UsageEventRatings'
      AND conrelid = to_regclass('platform."UsageEventRatings"')
) THEN
ALTER TABLE ONLY platform."UsageEventRatings"
    ADD CONSTRAINT "PK_UsageEventRatings" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: UsageEvents PK_UsageEvents; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_UsageEvents'
      AND conrelid = to_regclass('platform."UsageEvents"')
) THEN
ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "PK_UsageEvents" PRIMARY KEY ("UsageEventId");
END IF;
END
$nexora_idem$;



--
-- Name: UsageMinuteAggregates PK_UsageMinuteAggregates; Type: CONSTRAINT; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_UsageMinuteAggregates'
      AND conrelid = to_regclass('platform."UsageMinuteAggregates"')
) THEN
ALTER TABLE ONLY platform."UsageMinuteAggregates"
    ADD CONSTRAINT "PK_UsageMinuteAggregates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods AK_AccountingPeriods_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_AccountingPeriods_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."AccountingPeriods"')
) THEN
ALTER TABLE ONLY public."AccountingPeriods"
    ADD CONSTRAINT "AK_AccountingPeriods_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: AiRequests AK_AiRequests_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_AiRequests_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."AiRequests"')
) THEN
ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "AK_AiRequests_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts AK_BankAccounts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankAccounts_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."BankAccounts"')
) THEN
ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "AK_BankAccounts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts AK_BankAccounts_BusinessUnitId_Id_CurrencyId; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankAccounts_BusinessUnitId_Id_CurrencyId'
      AND conrelid = to_regclass('public."BankAccounts"')
) THEN
ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "AK_BankAccounts_BusinessUnitId_Id_CurrencyId" UNIQUE ("BusinessUnitId", "Id", "CurrencyId");
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments AK_BankAdjustments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankAdjustments_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."BankAdjustments"')
) THEN
ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "AK_BankAdjustments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankMatchingRules AK_BankMatchingRules_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankMatchingRules_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."BankMatchingRules"')
) THEN
ALTER TABLE ONLY public."BankMatchingRules"
    ADD CONSTRAINT "AK_BankMatchingRules_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports AK_BankStatementImports_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankStatementImports_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."BankStatementImports"')
) THEN
ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "AK_BankStatementImports_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports AK_BankStatementImports_BusinessUnitId_Id_BankAccountId; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankStatementImports_BusinessUnitId_Id_BankAccountId'
      AND conrelid = to_regclass('public."BankStatementImports"')
) THEN
ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "AK_BankStatementImports_BusinessUnitId_Id_BankAccountId" UNIQUE ("BusinessUnitId", "Id", "BankAccountId");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementLines AK_BankStatementLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankStatementLines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."BankStatementLines"')
) THEN
ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "AK_BankStatementLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementLines AK_BankStatementLines_BusinessUnitId_Id_BankAccountId; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankStatementLines_BusinessUnitId_Id_BankAccountId'
      AND conrelid = to_regclass('public."BankStatementLines"')
) THEN
ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "AK_BankStatementLines_BusinessUnitId_Id_BankAccountId" UNIQUE ("BusinessUnitId", "Id", "BankAccountId");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements AK_BankStatements_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankStatements_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."BankStatements"')
) THEN
ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "AK_BankStatements_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements AK_BankStatements_BusinessUnitId_Id_BankAccountId; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_BankStatements_BusinessUnitId_Id_BankAccountId'
      AND conrelid = to_regclass('public."BankStatements"')
) THEN
ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "AK_BankStatements_BusinessUnitId_Id_BankAccountId" UNIQUE ("BusinessUnitId", "Id", "BankAccountId");
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls AK_CollectionControls_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CollectionControls_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CollectionControls"')
) THEN
ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "AK_CollectionControls_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CommercialCases AK_CommercialCases_BusinessUnitID_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CommercialCases_BusinessUnitID_Id'
      AND conrelid = to_regclass('public."CommercialCases"')
) THEN
ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "AK_CommercialCases_BusinessUnitID_Id" UNIQUE ("BusinessUnitID", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CommercialCases AK_CommercialCases_BusinessUnitID_Id_MasterReference; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CommercialCases_BusinessUnitID_Id_MasterReference'
      AND conrelid = to_regclass('public."CommercialCases"')
) THEN
ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "AK_CommercialCases_BusinessUnitID_Id_MasterReference" UNIQUE ("BusinessUnitID", "Id", "MasterReference");
END IF;
END
$nexora_idem$;



--
-- Name: CommercialMatchingPolicies AK_CommercialMatchingPolicies_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CommercialMatchingPolicies_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CommercialMatchingPolicies"')
) THEN
ALTER TABLE ONLY public."CommercialMatchingPolicies"
    ADD CONSTRAINT "AK_CommercialMatchingPolicies_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: Contacts AK_Contacts_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Contacts_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Contacts"')
) THEN
ALTER TABLE ONLY public."Contacts"
    ADD CONSTRAINT "AK_Contacts_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: Currency AK_Currency_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Currency_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Currency"')
) THEN
ALTER TABLE ONLY public."Currency"
    ADD CONSTRAINT "AK_Currency_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwards AK_CustomerAwards_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerAwards_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerAwards"')
) THEN
ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "AK_CustomerAwards_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles AK_CustomerCollectionProfiles_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerCollectionProfiles_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerCollectionProfiles"')
) THEN
ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "AK_CustomerCollectionProfiles_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments AK_CustomerPayments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerPayments_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerPayments"')
) THEN
ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "AK_CustomerPayments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrderLines AK_CustomerPurchaseOrderLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerPurchaseOrderLines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerPurchaseOrderLines"')
) THEN
ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "AK_CustomerPurchaseOrderLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrders AK_CustomerPurchaseOrders_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerPurchaseOrders_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerPurchaseOrders"')
) THEN
ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "AK_CustomerPurchaseOrders_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds AK_CustomerRefunds_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerRefunds_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerRefunds"')
) THEN
ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "AK_CustomerRefunds_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements AK_CustomerStatements_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_CustomerStatements_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."CustomerStatements"')
) THEN
ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "AK_CustomerStatements_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: Customers AK_Customers_BUID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Customers_BUID_ID'
      AND conrelid = to_regclass('public."Customers"')
) THEN
ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "AK_Customers_BUID_ID" UNIQUE ("BUID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases AK_DunningCases_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_DunningCases_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."DunningCases"')
) THEN
ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "AK_DunningCases_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices AK_DunningNotices_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_DunningNotices_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."DunningNotices"')
) THEN
ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "AK_DunningNotices_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies AK_DunningPolicies_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_DunningPolicies_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."DunningPolicies"')
) THEN
ALTER TABLE ONLY public."DunningPolicies"
    ADD CONSTRAINT "AK_DunningPolicies_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns AK_DunningRuns_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_DunningRuns_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."DunningRuns"')
) THEN
ALTER TABLE ONLY public."DunningRuns"
    ADD CONSTRAINT "AK_DunningRuns_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionJobs AK_ExtractionJobs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ExtractionJobs_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."ExtractionJobs"')
) THEN
ALTER TABLE ONLY public."ExtractionJobs"
    ADD CONSTRAINT "AK_ExtractionJobs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts AK_FinanceCommunicationContacts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_FinanceCommunicationContacts_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."FinanceCommunicationContacts"')
) THEN
ALTER TABLE ONLY public."FinanceCommunicationContacts"
    ADD CONSTRAINT "AK_FinanceCommunicationContacts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: Inventory AK_Inventory_Buid_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Inventory_Buid_Id'
      AND conrelid = to_regclass('public."Inventory"')
) THEN
ALTER TABLE ONLY public."Inventory"
    ADD CONSTRAINT "AK_Inventory_Buid_Id" UNIQUE ("Buid", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries AK_JournalEntries_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_JournalEntries_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."JournalEntries"')
) THEN
ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "AK_JournalEntries_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntryLines AK_JournalEntryLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_JournalEntryLines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."JournalEntryLines"')
) THEN
ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "AK_JournalEntryLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionBatches AK_LeadIngestionBatches_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_LeadIngestionBatches_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."LeadIngestionBatches"')
) THEN
ALTER TABLE ONLY public."LeadIngestionBatches"
    ADD CONSTRAINT "AK_LeadIngestionBatches_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionOccurrences AK_LeadIngestionOccurrences_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_LeadIngestionOccurrences_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."LeadIngestionOccurrences"')
) THEN
ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "AK_LeadIngestionOccurrences_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadItemRevisions AK_LeadItemRevisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_LeadItemRevisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."LeadItemRevisions"')
) THEN
ALTER TABLE ONLY public."LeadItemRevisions"
    ADD CONSTRAINT "AK_LeadItemRevisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisions AK_LeadRevisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_LeadRevisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."LeadRevisions"')
) THEN
ALTER TABLE ONLY public."LeadRevisions"
    ADD CONSTRAINT "AK_LeadRevisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: Leads AK_Leads_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Leads_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Leads"')
) THEN
ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "AK_Leads_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: LedgerAccounts AK_LedgerAccounts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_LedgerAccounts_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."LedgerAccounts"')
) THEN
ALTER TABLE ONLY public."LedgerAccounts"
    ADD CONSTRAINT "AK_LedgerAccounts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks AK_LedgerBooks_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_LedgerBooks_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."LedgerBooks"')
) THEN
ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "AK_LedgerBooks_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataChangeEvents AK_MasterDataChangeEvents_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_MasterDataChangeEvents_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."MasterDataChangeEvents"')
) THEN
ALTER TABLE ONLY public."MasterDataChangeEvents"
    ADD CONSTRAINT "AK_MasterDataChangeEvents_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: OrderItems AK_OrderItems_ID_OrderID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_OrderItems_ID_OrderID'
      AND conrelid = to_regclass('public."OrderItems"')
) THEN
ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "AK_OrderItems_ID_OrderID" UNIQUE ("ID", "OrderID");
END IF;
END
$nexora_idem$;



--
-- Name: Orders AK_Orders_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Orders_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Orders"')
) THEN
ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "AK_Orders_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: Products AK_Products_BUID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Products_BUID_ID'
      AND conrelid = to_regclass('public."Products"')
) THEN
ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "AK_Products_BUID_ID" UNIQUE ("BUID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: QuoteItems AK_QuoteItems_ID_QuoteID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_QuoteItems_ID_QuoteID'
      AND conrelid = to_regclass('public."QuoteItems"')
) THEN
ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "AK_QuoteItems_ID_QuoteID" UNIQUE ("ID", "QuoteID");
END IF;
END
$nexora_idem$;



--
-- Name: QuotePriceAttestations AK_QuotePriceAttestations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_QuotePriceAttestations_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."QuotePriceAttestations"')
) THEN
ALTER TABLE ONLY public."QuotePriceAttestations"
    ADD CONSTRAINT "AK_QuotePriceAttestations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: QuoteValidityExtensions AK_QuoteValidityExtensions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_QuoteValidityExtensions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."QuoteValidityExtensions"')
) THEN
ALTER TABLE ONLY public."QuoteValidityExtensions"
    ADD CONSTRAINT "AK_QuoteValidityExtensions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: Quotes AK_Quotes_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Quotes_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Quotes"')
) THEN
ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "AK_Quotes_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: RFQItems AK_RFQItems_ID_RFQID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_RFQItems_ID_RFQID'
      AND conrelid = to_regclass('public."RFQItems"')
) THEN
ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "AK_RFQItems_ID_RFQID" UNIQUE ("ID", "RFQID");
END IF;
END
$nexora_idem$;



--
-- Name: RFQ AK_RFQ_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_RFQ_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."RFQ"')
) THEN
ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "AK_RFQ_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocumentLines AK_ReceivableDocumentLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ReceivableDocumentLines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."ReceivableDocumentLines"')
) THEN
ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "AK_ReceivableDocumentLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocuments AK_ReceivableDocuments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ReceivableDocuments_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."ReceivableDocuments"')
) THEN
ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "AK_ReceivableDocuments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableWriteOffs AK_ReceivableWriteOffs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ReceivableWriteOffs_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."ReceivableWriteOffs"')
) THEN
ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "AK_ReceivableWriteOffs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches AK_ReconciliationMatches_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ReconciliationMatches_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."ReconciliationMatches"')
) THEN
ALTER TABLE ONLY public."ReconciliationMatches"
    ADD CONSTRAINT "AK_ReconciliationMatches_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns AK_ReconciliationRuns_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ReconciliationRuns_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."ReconciliationRuns"')
) THEN
ALTER TABLE ONLY public."ReconciliationRuns"
    ADD CONSTRAINT "AK_ReconciliationRuns_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: SetCity AK_SetCity_BUID_CityID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SetCity_BUID_CityID'
      AND conrelid = to_regclass('public."SetCity"')
) THEN
ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "AK_SetCity_BUID_CityID" UNIQUE ("BUID", "CityID");
END IF;
END
$nexora_idem$;



--
-- Name: Setup_Master AK_Setup_Master_BusinessUnitID_SetupID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Setup_Master_BusinessUnitID_SetupID'
      AND conrelid = to_regclass('public."Setup_Master"')
) THEN
ALTER TABLE ONLY public."Setup_Master"
    ADD CONSTRAINT "AK_Setup_Master_BusinessUnitID_SetupID" UNIQUE ("BusinessUnitID", "SetupID");
END IF;
END
$nexora_idem$;



--
-- Name: ShipmentItems AK_ShipmentItems_ID_ShipmentID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ShipmentItems_ID_ShipmentID'
      AND conrelid = to_regclass('public."ShipmentItems"')
) THEN
ALTER TABLE ONLY public."ShipmentItems"
    ADD CONSTRAINT "AK_ShipmentItems_ID_ShipmentID" UNIQUE ("ID", "ShipmentID");
END IF;
END
$nexora_idem$;



--
-- Name: Shipments AK_Shipments_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Shipments_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Shipments"')
) THEN
ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "AK_Shipments_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: SourcingAwards AK_SourcingAwards_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SourcingAwards_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."SourcingAwards"')
) THEN
ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "AK_SourcingAwards_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupplierQuotedItems AK_SupplierQuotedItems_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SupplierQuotedItems_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."SupplierQuotedItems"')
) THEN
ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "AK_SupplierQuotedItems_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupplierSolicitations AK_SupplierSolicitations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_SupplierSolicitations_BusinessUnitId_Id'
      AND conrelid = to_regclass('public."SupplierSolicitations"')
) THEN
ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "AK_SupplierSolicitations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: Suppliers AK_Suppliers_ID_BUID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Suppliers_ID_BUID'
      AND conrelid = to_regclass('public."Suppliers"')
) THEN
ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "AK_Suppliers_ID_BUID" UNIQUE ("ID", "BUID");
END IF;
END
$nexora_idem$;



--
-- Name: Warehouses AK_Warehouses_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_Warehouses_BusinessUnitID_ID'
      AND conrelid = to_regclass('public."Warehouses"')
) THEN
ALTER TABLE ONLY public."Warehouses"
    ADD CONSTRAINT "AK_Warehouses_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_demand_lines AK_commercial_demand_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_demand_lines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_demand_lines')
) THEN
ALTER TABLE ONLY public.commercial_demand_lines
    ADD CONSTRAINT "AK_commercial_demand_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_cases AK_commercial_exception_cases_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_exception_cases_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_exception_cases')
) THEN
ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "AK_commercial_exception_cases_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_events AK_commercial_exception_events_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_exception_events_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_exception_events')
) THEN
ALTER TABLE ONLY public.commercial_exception_events
    ADD CONSTRAINT "AK_commercial_exception_events_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_operations AK_commercial_exception_operations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_exception_operations_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_exception_operations')
) THEN
ALTER TABLE ONLY public.commercial_exception_operations
    ADD CONSTRAINT "AK_commercial_exception_operations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_lifecycle_events AK_commercial_lifecycle_events_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_lifecycle_events_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_lifecycle_events')
) THEN
ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "AK_commercial_lifecycle_events_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_events AK_commercial_opportunity_events_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_opportunity_events_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_opportunity_events')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_events
    ADD CONSTRAINT "AK_commercial_opportunity_events_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_feedback AK_commercial_opportunity_feedback_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_opportunity_feedback_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_opportunity_feedback')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_feedback
    ADD CONSTRAINT "AK_commercial_opportunity_feedback_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_operations AK_commercial_opportunity_operations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_opportunity_operations_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_opportunity_operations')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_operations
    ADD CONSTRAINT "AK_commercial_opportunity_operations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outbox AK_commercial_opportunity_outbox_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_opportunity_outbox_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_opportunity_outbox')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_outbox
    ADD CONSTRAINT "AK_commercial_opportunity_outbox_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outcomes AK_commercial_opportunity_outcomes_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_opportunity_outcomes_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_opportunity_outcomes')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_outcomes
    ADD CONSTRAINT "AK_commercial_opportunity_outcomes_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_recommendations AK_commercial_opportunity_recommendations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_commercial_opportunity_recommendations_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.commercial_opportunity_recommendations')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "AK_commercial_opportunity_recommendations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_definitions AK_custom_field_definitions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_custom_field_definitions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.custom_field_definitions')
) THEN
ALTER TABLE ONLY public.custom_field_definitions
    ADD CONSTRAINT "AK_custom_field_definitions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_records AK_custom_field_records_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_custom_field_records_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.custom_field_records')
) THEN
ALTER TABLE ONLY public.custom_field_records
    ADD CONSTRAINT "AK_custom_field_records_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_values AK_custom_field_values_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_custom_field_values_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.custom_field_values')
) THEN
ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "AK_custom_field_values_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_versions AK_custom_field_versions_DefinitionId_VersionNumber; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_custom_field_versions_DefinitionId_VersionNumber'
      AND conrelid = to_regclass('public.custom_field_versions')
) THEN
ALTER TABLE ONLY public.custom_field_versions
    ADD CONSTRAINT "AK_custom_field_versions_DefinitionId_VersionNumber" UNIQUE ("DefinitionId", "VersionNumber");
END IF;
END
$nexora_idem$;



--
-- Name: customer_identifiers AK_customer_identifiers_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_customer_identifiers_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.customer_identifiers')
) THEN
ALTER TABLE ONLY public.customer_identifiers
    ADD CONSTRAINT "AK_customer_identifiers_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: customer_ownerships AK_customer_ownerships_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_customer_ownerships_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.customer_ownerships')
) THEN
ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "AK_customer_ownerships_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: customer_quote_sourcing_decisions AK_customer_quote_sourcing_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_customer_quote_sourcing_decisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.customer_quote_sourcing_decisions')
) THEN
ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "AK_customer_quote_sourcing_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: delivery_proof_lines AK_delivery_proof_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_delivery_proof_lines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.delivery_proof_lines')
) THEN
ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "AK_delivery_proof_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: delivery_proofs AK_delivery_proofs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_delivery_proofs_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.delivery_proofs')
) THEN
ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "AK_delivery_proofs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: delivery_shortfall_decisions AK_delivery_shortfall_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_delivery_shortfall_decisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.delivery_shortfall_decisions')
) THEN
ALTER TABLE ONLY public.delivery_shortfall_decisions
    ADD CONSTRAINT "AK_delivery_shortfall_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: goods_receipts AK_goods_receipts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_goods_receipts_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.goods_receipts')
) THEN
ALTER TABLE ONLY public.goods_receipts
    ADD CONSTRAINT "AK_goods_receipts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifact_versions AK_governed_artifact_versions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_governed_artifact_versions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.governed_artifact_versions')
) THEN
ALTER TABLE ONLY public.governed_artifact_versions
    ADD CONSTRAINT "AK_governed_artifact_versions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifacts AK_governed_artifacts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_governed_artifacts_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.governed_artifacts')
) THEN
ALTER TABLE ONLY public.governed_artifacts
    ADD CONSTRAINT "AK_governed_artifacts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: human_action_items AK_human_action_items_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_human_action_items_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.human_action_items')
) THEN
ALTER TABLE ONLY public.human_action_items
    ADD CONSTRAINT "AK_human_action_items_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: inbound_logistics_policies AK_inbound_logistics_policies_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_inbound_logistics_policies_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.inbound_logistics_policies')
) THEN
ALTER TABLE ONLY public.inbound_logistics_policies
    ADD CONSTRAINT "AK_inbound_logistics_policies_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: incoming_inventory AK_incoming_inventory_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_incoming_inventory_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.incoming_inventory')
) THEN
ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "AK_incoming_inventory_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: inventory_movements AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~'
      AND conrelid = to_regclass('public.inventory_movements')
) THEN
ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~" UNIQUE ("BusinessUnitId", "Id", "ProductId", "InventoryId", "WarehouseId");
END IF;
END
$nexora_idem$;



--
-- Name: lead_routing_decisions AK_lead_routing_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_lead_routing_decisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.lead_routing_decisions')
) THEN
ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "AK_lead_routing_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: material_lot_certificates AK_material_lot_certificates_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_material_lot_certificates_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.material_lot_certificates')
) THEN
ALTER TABLE ONLY public.material_lot_certificates
    ADD CONSTRAINT "AK_material_lot_certificates_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: material_lot_consumptions AK_material_lot_consumptions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_material_lot_consumptions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.material_lot_consumptions')
) THEN
ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "AK_material_lot_consumptions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: material_lots AK_material_lots_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_material_lots_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.material_lots')
) THEN
ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "AK_material_lots_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: ports_of_entry AK_ports_of_entry_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_ports_of_entry_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.ports_of_entry')
) THEN
ALTER TABLE ONLY public.ports_of_entry
    ADD CONSTRAINT "AK_ports_of_entry_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: procurement_callback_receipts AK_procurement_callback_receipts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_procurement_callback_receipts_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.procurement_callback_receipts')
) THEN
ALTER TABLE ONLY public.procurement_callback_receipts
    ADD CONSTRAINT "AK_procurement_callback_receipts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: procurement_handoffs AK_procurement_handoffs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_procurement_handoffs_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.procurement_handoffs')
) THEN
ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "AK_procurement_handoffs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: sales_coaching_acknowledgements AK_sales_coaching_acknowledgements_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_sales_coaching_acknowledgements_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.sales_coaching_acknowledgements')
) THEN
ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "AK_sales_coaching_acknowledgements_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: setUOM AK_setUOM_BusinessUnitID_UomID; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_setUOM_BusinessUnitID_UomID'
      AND conrelid = to_regclass('public."setUOM"')
) THEN
ALTER TABLE ONLY public."setUOM"
    ADD CONSTRAINT "AK_setUOM_BusinessUnitID_UomID" UNIQUE ("BusinessUnitID", "UomID");
END IF;
END
$nexora_idem$;



--
-- Name: sourcing_cases AK_sourcing_cases_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_sourcing_cases_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.sourcing_cases')
) THEN
ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "AK_sourcing_cases_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_negotiation_decisions AK_supplier_negotiation_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_negotiation_decisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_negotiation_decisions')
) THEN
ALTER TABLE ONLY public.supplier_negotiation_decisions
    ADD CONSTRAINT "AK_supplier_negotiation_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_order_lines AK_supplier_purchase_order_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_purchase_order_lines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_purchase_order_lines')
) THEN
ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "AK_supplier_purchase_order_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_order_lines AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~'
      AND conrelid = to_regclass('public.supplier_purchase_order_lines')
) THEN
ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~" UNIQUE ("BusinessUnitId", "Id", "ProductId", "WarehouseId");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_orders AK_supplier_purchase_orders_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_purchase_orders_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_purchase_orders')
) THEN
ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "AK_supplier_purchase_orders_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_field_evidence AK_supplier_quote_field_evidence_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_quote_field_evidence_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_quote_field_evidence')
) THEN
ALTER TABLE ONLY public.supplier_quote_field_evidence
    ADD CONSTRAINT "AK_supplier_quote_field_evidence_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_lines AK_supplier_quote_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_quote_lines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_quote_lines')
) THEN
ALTER TABLE ONLY public.supplier_quote_lines
    ADD CONSTRAINT "AK_supplier_quote_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_revisions AK_supplier_quote_revisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_quote_revisions_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_quote_revisions')
) THEN
ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "AK_supplier_quote_revisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_revisions AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id'
      AND conrelid = to_regclass('public.supplier_quote_revisions')
) THEN
ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id" UNIQUE ("BusinessUnitId", "SupplierQuoteId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quotes AK_supplier_quotes_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_quotes_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_quotes')
) THEN
ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "AK_supplier_quotes_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_shipment_lines AK_supplier_shipment_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_shipment_lines_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_shipment_lines')
) THEN
ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "AK_supplier_shipment_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_shipments AK_supplier_shipments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_supplier_shipments_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.supplier_shipments')
) THEN
ALTER TABLE ONLY public.supplier_shipments
    ADD CONSTRAINT "AK_supplier_shipments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: unassigned_work_items AK_unassigned_work_items_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'AK_unassigned_work_items_BusinessUnitId_Id'
      AND conrelid = to_regclass('public.unassigned_work_items')
) THEN
ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "AK_unassigned_work_items_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions CK_DunningRunDecisions_ProfileCheckpoint; Type: CHECK CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'CK_DunningRunDecisions_ProfileCheckpoint'
      AND conrelid = to_regclass('public."DunningRunDecisions"')
) THEN
ALTER TABLE public."DunningRunDecisions"
    ADD CONSTRAINT "CK_DunningRunDecisions_ProfileCheckpoint" CHECK (("CustomerCollectionProfileId" IS NOT NULL)) NOT VALID;
END IF;
END
$nexora_idem$;



--
-- Name: FinanceProviderSecrets FinanceProviderSecrets_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'FinanceProviderSecrets_pkey'
      AND conrelid = to_regclass('public."FinanceProviderSecrets"')
) THEN
ALTER TABLE ONLY public."FinanceProviderSecrets"
    ADD CONSTRAINT "FinanceProviderSecrets_pkey" PRIMARY KEY ("Name");
END IF;
END
$nexora_idem$;



--
-- Name: LedgerActorNonces LedgerActorNonces_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'LedgerActorNonces_pkey'
      AND conrelid = to_regclass('public."LedgerActorNonces"')
) THEN
ALTER TABLE ONLY public."LedgerActorNonces"
    ADD CONSTRAINT "LedgerActorNonces_pkey" PRIMARY KEY ("Nonce");
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods PK_AccountingPeriods; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AccountingPeriods'
      AND conrelid = to_regclass('public."AccountingPeriods"')
) THEN
ALTER TABLE ONLY public."AccountingPeriods"
    ADD CONSTRAINT "PK_AccountingPeriods" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AgentApprovals PK_AgentApprovals; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AgentApprovals'
      AND conrelid = to_regclass('public."AgentApprovals"')
) THEN
ALTER TABLE ONLY public."AgentApprovals"
    ADD CONSTRAINT "PK_AgentApprovals" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AgentAuditLogs PK_AgentAuditLogs; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AgentAuditLogs'
      AND conrelid = to_regclass('public."AgentAuditLogs"')
) THEN
ALTER TABLE ONLY public."AgentAuditLogs"
    ADD CONSTRAINT "PK_AgentAuditLogs" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AgentMessages PK_AgentMessages; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AgentMessages'
      AND conrelid = to_regclass('public."AgentMessages"')
) THEN
ALTER TABLE ONLY public."AgentMessages"
    ADD CONSTRAINT "PK_AgentMessages" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AgentPolicies PK_AgentPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AgentPolicies'
      AND conrelid = to_regclass('public."AgentPolicies"')
) THEN
ALTER TABLE ONLY public."AgentPolicies"
    ADD CONSTRAINT "PK_AgentPolicies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AgentSessions PK_AgentSessions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AgentSessions'
      AND conrelid = to_regclass('public."AgentSessions"')
) THEN
ALTER TABLE ONLY public."AgentSessions"
    ADD CONSTRAINT "PK_AgentSessions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AiBudgetPeriods PK_AiBudgetPeriods; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AiBudgetPeriods'
      AND conrelid = to_regclass('public."AiBudgetPeriods"')
) THEN
ALTER TABLE ONLY public."AiBudgetPeriods"
    ADD CONSTRAINT "PK_AiBudgetPeriods" PRIMARY KEY ("BusinessUnitId", "PeriodStartUtc");
END IF;
END
$nexora_idem$;



--
-- Name: AiCallAttempts PK_AiCallAttempts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AiCallAttempts'
      AND conrelid = to_regclass('public."AiCallAttempts"')
) THEN
ALTER TABLE ONLY public."AiCallAttempts"
    ADD CONSTRAINT "PK_AiCallAttempts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AiProcessingPolicies PK_AiProcessingPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AiProcessingPolicies'
      AND conrelid = to_regclass('public."AiProcessingPolicies"')
) THEN
ALTER TABLE ONLY public."AiProcessingPolicies"
    ADD CONSTRAINT "PK_AiProcessingPolicies" PRIMARY KEY ("BusinessUnitId");
END IF;
END
$nexora_idem$;



--
-- Name: AiProviderAuthorizations PK_AiProviderAuthorizations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AiProviderAuthorizations'
      AND conrelid = to_regclass('public."AiProviderAuthorizations"')
) THEN
ALTER TABLE ONLY public."AiProviderAuthorizations"
    ADD CONSTRAINT "PK_AiProviderAuthorizations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: AiRequests PK_AiRequests; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_AiRequests'
      AND conrelid = to_regclass('public."AiRequests"')
) THEN
ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "PK_AiRequests" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts PK_BankAccounts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankAccounts'
      AND conrelid = to_regclass('public."BankAccounts"')
) THEN
ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "PK_BankAccounts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustmentDistributions PK_BankAdjustmentDistributions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankAdjustmentDistributions'
      AND conrelid = to_regclass('public."BankAdjustmentDistributions"')
) THEN
ALTER TABLE ONLY public."BankAdjustmentDistributions"
    ADD CONSTRAINT "PK_BankAdjustmentDistributions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments PK_BankAdjustments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankAdjustments'
      AND conrelid = to_regclass('public."BankAdjustments"')
) THEN
ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "PK_BankAdjustments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankMatchingRules PK_BankMatchingRules; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankMatchingRules'
      AND conrelid = to_regclass('public."BankMatchingRules"')
) THEN
ALTER TABLE ONLY public."BankMatchingRules"
    ADD CONSTRAINT "PK_BankMatchingRules" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports PK_BankStatementImports; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankStatementImports'
      AND conrelid = to_regclass('public."BankStatementImports"')
) THEN
ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "PK_BankStatementImports" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementLines PK_BankStatementLines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankStatementLines'
      AND conrelid = to_regclass('public."BankStatementLines"')
) THEN
ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "PK_BankStatementLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements PK_BankStatements; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BankStatements'
      AND conrelid = to_regclass('public."BankStatements"')
) THEN
ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "PK_BankStatements" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BoqAssemblies PK_BoqAssemblies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BoqAssemblies'
      AND conrelid = to_regclass('public."BoqAssemblies"')
) THEN
ALTER TABLE ONLY public."BoqAssemblies"
    ADD CONSTRAINT "PK_BoqAssemblies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BoqAssemblyComponents PK_BoqAssemblyComponents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BoqAssemblyComponents'
      AND conrelid = to_regclass('public."BoqAssemblyComponents"')
) THEN
ALTER TABLE ONLY public."BoqAssemblyComponents"
    ADD CONSTRAINT "PK_BoqAssemblyComponents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BoqDocuments PK_BoqDocuments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BoqDocuments'
      AND conrelid = to_regclass('public."BoqDocuments"')
) THEN
ALTER TABLE ONLY public."BoqDocuments"
    ADD CONSTRAINT "PK_BoqDocuments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BoqItems PK_BoqItems; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BoqItems'
      AND conrelid = to_regclass('public."BoqItems"')
) THEN
ALTER TABLE ONLY public."BoqItems"
    ADD CONSTRAINT "PK_BoqItems" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: BoqSections PK_BoqSections; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_BoqSections'
      AND conrelid = to_regclass('public."BoqSections"')
) THEN
ALTER TABLE ONLY public."BoqSections"
    ADD CONSTRAINT "PK_BoqSections" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls PK_CollectionControls; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CollectionControls'
      AND conrelid = to_regclass('public."CollectionControls"')
) THEN
ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "PK_CollectionControls" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CommercialCases PK_CommercialCases; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CommercialCases'
      AND conrelid = to_regclass('public."CommercialCases"')
) THEN
ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "PK_CommercialCases" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CommercialFinanceAudits PK_CommercialFinanceAudits; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CommercialFinanceAudits'
      AND conrelid = to_regclass('public."CommercialFinanceAudits"')
) THEN
ALTER TABLE ONLY public."CommercialFinanceAudits"
    ADD CONSTRAINT "PK_CommercialFinanceAudits" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CommercialMatchingPolicies PK_CommercialMatchingPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CommercialMatchingPolicies'
      AND conrelid = to_regclass('public."CommercialMatchingPolicies"')
) THEN
ALTER TABLE ONLY public."CommercialMatchingPolicies"
    ADD CONSTRAINT "PK_CommercialMatchingPolicies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwardLineAllocations PK_CustomerAwardLineAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerAwardLineAllocations'
      AND conrelid = to_regclass('public."CustomerAwardLineAllocations"')
) THEN
ALTER TABLE ONLY public."CustomerAwardLineAllocations"
    ADD CONSTRAINT "PK_CustomerAwardLineAllocations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwards PK_CustomerAwards; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerAwards'
      AND conrelid = to_regclass('public."CustomerAwards"')
) THEN
ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "PK_CustomerAwards" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles PK_CustomerCollectionProfiles; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerCollectionProfiles'
      AND conrelid = to_regclass('public."CustomerCollectionProfiles"')
) THEN
ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "PK_CustomerCollectionProfiles" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments PK_CustomerPayments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerPayments'
      AND conrelid = to_regclass('public."CustomerPayments"')
) THEN
ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "PK_CustomerPayments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrderLines PK_CustomerPurchaseOrderLines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerPurchaseOrderLines'
      AND conrelid = to_regclass('public."CustomerPurchaseOrderLines"')
) THEN
ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "PK_CustomerPurchaseOrderLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrders PK_CustomerPurchaseOrders; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerPurchaseOrders'
      AND conrelid = to_regclass('public."CustomerPurchaseOrders"')
) THEN
ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "PK_CustomerPurchaseOrders" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds PK_CustomerRefunds; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerRefunds'
      AND conrelid = to_regclass('public."CustomerRefunds"')
) THEN
ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "PK_CustomerRefunds" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatementLines PK_CustomerStatementLines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerStatementLines'
      AND conrelid = to_regclass('public."CustomerStatementLines"')
) THEN
ALTER TABLE ONLY public."CustomerStatementLines"
    ADD CONSTRAINT "PK_CustomerStatementLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements PK_CustomerStatements; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_CustomerStatements'
      AND conrelid = to_regclass('public."CustomerStatements"')
) THEN
ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "PK_CustomerStatements" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases PK_DunningCases; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningCases'
      AND conrelid = to_regclass('public."DunningCases"')
) THEN
ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "PK_DunningCases" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts PK_DunningDeliveryAttempts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningDeliveryAttempts'
      AND conrelid = to_regclass('public."DunningDeliveryAttempts"')
) THEN
ALTER TABLE ONLY public."DunningDeliveryAttempts"
    ADD CONSTRAINT "PK_DunningDeliveryAttempts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices PK_DunningNotices; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningNotices'
      AND conrelid = to_regclass('public."DunningNotices"')
) THEN
ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "PK_DunningNotices" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies PK_DunningPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningPolicies'
      AND conrelid = to_regclass('public."DunningPolicies"')
) THEN
ALTER TABLE ONLY public."DunningPolicies"
    ADD CONSTRAINT "PK_DunningPolicies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicySteps PK_DunningPolicySteps; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningPolicySteps'
      AND conrelid = to_regclass('public."DunningPolicySteps"')
) THEN
ALTER TABLE ONLY public."DunningPolicySteps"
    ADD CONSTRAINT "PK_DunningPolicySteps" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions PK_DunningRunDecisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningRunDecisions'
      AND conrelid = to_regclass('public."DunningRunDecisions"')
) THEN
ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "PK_DunningRunDecisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns PK_DunningRuns; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_DunningRuns'
      AND conrelid = to_regclass('public."DunningRuns"')
) THEN
ALTER TABLE ONLY public."DunningRuns"
    ADD CONSTRAINT "PK_DunningRuns" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionCorpusEntries PK_ExtractionCorpusEntries; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ExtractionCorpusEntries'
      AND conrelid = to_regclass('public."ExtractionCorpusEntries"')
) THEN
ALTER TABLE ONLY public."ExtractionCorpusEntries"
    ADD CONSTRAINT "PK_ExtractionCorpusEntries" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionJobs PK_ExtractionJobs; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ExtractionJobs'
      AND conrelid = to_regclass('public."ExtractionJobs"')
) THEN
ALTER TABLE ONLY public."ExtractionJobs"
    ADD CONSTRAINT "PK_ExtractionJobs" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts PK_FinanceCommunicationContacts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_FinanceCommunicationContacts'
      AND conrelid = to_regclass('public."FinanceCommunicationContacts"')
) THEN
ALTER TABLE ONLY public."FinanceCommunicationContacts"
    ADD CONSTRAINT "PK_FinanceCommunicationContacts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: FinanceOutboxMessages PK_FinanceOutboxMessages; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_FinanceOutboxMessages'
      AND conrelid = to_regclass('public."FinanceOutboxMessages"')
) THEN
ALTER TABLE ONLY public."FinanceOutboxMessages"
    ADD CONSTRAINT "PK_FinanceOutboxMessages" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: FolderIngestionRetryStates PK_FolderIngestionRetryStates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_FolderIngestionRetryStates'
      AND conrelid = to_regclass('public."FolderIngestionRetryStates"')
) THEN
ALTER TABLE ONLY public."FolderIngestionRetryStates"
    ADD CONSTRAINT "PK_FolderIngestionRetryStates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: FxRateSnapshots PK_FxRateSnapshots; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_FxRateSnapshots'
      AND conrelid = to_regclass('public."FxRateSnapshots"')
) THEN
ALTER TABLE ONLY public."FxRateSnapshots"
    ADD CONSTRAINT "PK_FxRateSnapshots" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: FxRates PK_FxRates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_FxRates'
      AND conrelid = to_regclass('public."FxRates"')
) THEN
ALTER TABLE ONLY public."FxRates"
    ADD CONSTRAINT "PK_FxRates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: IamAuditEvents PK_IamAuditEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_IamAuditEvents'
      AND conrelid = to_regclass('public."IamAuditEvents"')
) THEN
ALTER TABLE ONLY public."IamAuditEvents"
    ADD CONSTRAINT "PK_IamAuditEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: Inventory PK_Inventory; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_Inventory'
      AND conrelid = to_regclass('public."Inventory"')
) THEN
ALTER TABLE ONLY public."Inventory"
    ADD CONSTRAINT "PK_Inventory" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries PK_JournalEntries; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_JournalEntries'
      AND conrelid = to_regclass('public."JournalEntries"')
) THEN
ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "PK_JournalEntries" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntryLines PK_JournalEntryLines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_JournalEntryLines'
      AND conrelid = to_regclass('public."JournalEntryLines"')
) THEN
ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "PK_JournalEntryLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadIdentityAuditEvents PK_LeadIdentityAuditEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadIdentityAuditEvents'
      AND conrelid = to_regclass('public."LeadIdentityAuditEvents"')
) THEN
ALTER TABLE ONLY public."LeadIdentityAuditEvents"
    ADD CONSTRAINT "PK_LeadIdentityAuditEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionBatches PK_LeadIngestionBatches; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadIngestionBatches'
      AND conrelid = to_regclass('public."LeadIngestionBatches"')
) THEN
ALTER TABLE ONLY public."LeadIngestionBatches"
    ADD CONSTRAINT "PK_LeadIngestionBatches" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionOccurrences PK_LeadIngestionOccurrences; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadIngestionOccurrences'
      AND conrelid = to_regclass('public."LeadIngestionOccurrences"')
) THEN
ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "PK_LeadIngestionOccurrences" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadItemRevisions PK_LeadItemRevisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadItemRevisions'
      AND conrelid = to_regclass('public."LeadItemRevisions"')
) THEN
ALTER TABLE ONLY public."LeadItemRevisions"
    ADD CONSTRAINT "PK_LeadItemRevisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadMatchCandidates PK_LeadMatchCandidates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadMatchCandidates'
      AND conrelid = to_regclass('public."LeadMatchCandidates"')
) THEN
ALTER TABLE ONLY public."LeadMatchCandidates"
    ADD CONSTRAINT "PK_LeadMatchCandidates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadOccurrenceDocuments PK_LeadOccurrenceDocuments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadOccurrenceDocuments'
      AND conrelid = to_regclass('public."LeadOccurrenceDocuments"')
) THEN
ALTER TABLE ONLY public."LeadOccurrenceDocuments"
    ADD CONSTRAINT "PK_LeadOccurrenceDocuments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadReferenceConfigurations PK_LeadReferenceConfigurations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadReferenceConfigurations'
      AND conrelid = to_regclass('public."LeadReferenceConfigurations"')
) THEN
ALTER TABLE ONLY public."LeadReferenceConfigurations"
    ADD CONSTRAINT "PK_LeadReferenceConfigurations" PRIMARY KEY ("BusinessUnitID");
END IF;
END
$nexora_idem$;



--
-- Name: LeadReviewAudits PK_LeadReviewAudits; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadReviewAudits'
      AND conrelid = to_regclass('public."LeadReviewAudits"')
) THEN
ALTER TABLE ONLY public."LeadReviewAudits"
    ADD CONSTRAINT "PK_LeadReviewAudits" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisionDifferences PK_LeadRevisionDifferences; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadRevisionDifferences'
      AND conrelid = to_regclass('public."LeadRevisionDifferences"')
) THEN
ALTER TABLE ONLY public."LeadRevisionDifferences"
    ADD CONSTRAINT "PK_LeadRevisionDifferences" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisionImpacts PK_LeadRevisionImpacts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadRevisionImpacts'
      AND conrelid = to_regclass('public."LeadRevisionImpacts"')
) THEN
ALTER TABLE ONLY public."LeadRevisionImpacts"
    ADD CONSTRAINT "PK_LeadRevisionImpacts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisions PK_LeadRevisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadRevisions'
      AND conrelid = to_regclass('public."LeadRevisions"')
) THEN
ALTER TABLE ONLY public."LeadRevisions"
    ADD CONSTRAINT "PK_LeadRevisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LeadStatusHistories PK_LeadStatusHistories; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LeadStatusHistories'
      AND conrelid = to_regclass('public."LeadStatusHistories"')
) THEN
ALTER TABLE ONLY public."LeadStatusHistories"
    ADD CONSTRAINT "PK_LeadStatusHistories" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LedgerAccounts PK_LedgerAccounts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LedgerAccounts'
      AND conrelid = to_regclass('public."LedgerAccounts"')
) THEN
ALTER TABLE ONLY public."LedgerAccounts"
    ADD CONSTRAINT "PK_LedgerAccounts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks PK_LedgerBooks; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LedgerBooks'
      AND conrelid = to_regclass('public."LedgerBooks"')
) THEN
ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "PK_LedgerBooks" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: LegalDocumentCounters PK_LegalDocumentCounters; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LegalDocumentCounters'
      AND conrelid = to_regclass('public."LegalDocumentCounters"')
) THEN
ALTER TABLE ONLY public."LegalDocumentCounters"
    ADD CONSTRAINT "PK_LegalDocumentCounters" PRIMARY KEY ("BusinessUnitId", "DocumentType", "FiscalYear");
END IF;
END
$nexora_idem$;



--
-- Name: LoginAttempts PK_LoginAttempts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_LoginAttempts'
      AND conrelid = to_regclass('public."LoginAttempts"')
) THEN
ALTER TABLE ONLY public."LoginAttempts"
    ADD CONSTRAINT "PK_LoginAttempts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataChangeEvents PK_MasterDataChangeEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_MasterDataChangeEvents'
      AND conrelid = to_regclass('public."MasterDataChangeEvents"')
) THEN
ALTER TABLE ONLY public."MasterDataChangeEvents"
    ADD CONSTRAINT "PK_MasterDataChangeEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataFieldChanges PK_MasterDataFieldChanges; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_MasterDataFieldChanges'
      AND conrelid = to_regclass('public."MasterDataFieldChanges"')
) THEN
ALTER TABLE ONLY public."MasterDataFieldChanges"
    ADD CONSTRAINT "PK_MasterDataFieldChanges" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: MetricEvents PK_MetricEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_MetricEvents'
      AND conrelid = to_regclass('public."MetricEvents"')
) THEN
ALTER TABLE ONLY public."MetricEvents"
    ADD CONSTRAINT "PK_MetricEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: OrderToCashAuditEvents PK_OrderToCashAuditEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_OrderToCashAuditEvents'
      AND conrelid = to_regclass('public."OrderToCashAuditEvents"')
) THEN
ALTER TABLE ONLY public."OrderToCashAuditEvents"
    ADD CONSTRAINT "PK_OrderToCashAuditEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: OrderToCashDocumentCounters PK_OrderToCashDocumentCounters; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_OrderToCashDocumentCounters'
      AND conrelid = to_regclass('public."OrderToCashDocumentCounters"')
) THEN
ALTER TABLE ONLY public."OrderToCashDocumentCounters"
    ADD CONSTRAINT "PK_OrderToCashDocumentCounters" PRIMARY KEY ("BusinessUnitId", "DocumentType", "CalendarYear");
END IF;
END
$nexora_idem$;



--
-- Name: PaymentAllocations PK_PaymentAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PaymentAllocations'
      AND conrelid = to_regclass('public."PaymentAllocations"')
) THEN
ALTER TABLE ONLY public."PaymentAllocations"
    ADD CONSTRAINT "PK_PaymentAllocations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: PromisesToPay PK_PromisesToPay; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_PromisesToPay'
      AND conrelid = to_regclass('public."PromisesToPay"')
) THEN
ALTER TABLE ONLY public."PromisesToPay"
    ADD CONSTRAINT "PK_PromisesToPay" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: QuoteConfiguration PK_QuoteConfiguration; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_QuoteConfiguration'
      AND conrelid = to_regclass('public."QuoteConfiguration"')
) THEN
ALTER TABLE ONLY public."QuoteConfiguration"
    ADD CONSTRAINT "PK_QuoteConfiguration" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: QuotePriceAttestationLines PK_QuotePriceAttestationLines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_QuotePriceAttestationLines'
      AND conrelid = to_regclass('public."QuotePriceAttestationLines"')
) THEN
ALTER TABLE ONLY public."QuotePriceAttestationLines"
    ADD CONSTRAINT "PK_QuotePriceAttestationLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: QuotePriceAttestations PK_QuotePriceAttestations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_QuotePriceAttestations'
      AND conrelid = to_regclass('public."QuotePriceAttestations"')
) THEN
ALTER TABLE ONLY public."QuotePriceAttestations"
    ADD CONSTRAINT "PK_QuotePriceAttestations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: QuoteRemovalRecords PK_QuoteRemovalRecords; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_QuoteRemovalRecords'
      AND conrelid = to_regclass('public."QuoteRemovalRecords"')
) THEN
ALTER TABLE ONLY public."QuoteRemovalRecords"
    ADD CONSTRAINT "PK_QuoteRemovalRecords" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: QuoteValidityExtensions PK_QuoteValidityExtensions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_QuoteValidityExtensions'
      AND conrelid = to_regclass('public."QuoteValidityExtensions"')
) THEN
ALTER TABLE ONLY public."QuoteValidityExtensions"
    ADD CONSTRAINT "PK_QuoteValidityExtensions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocumentLines PK_ReceivableDocumentLines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReceivableDocumentLines'
      AND conrelid = to_regclass('public."ReceivableDocumentLines"')
) THEN
ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "PK_ReceivableDocumentLines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocuments PK_ReceivableDocuments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReceivableDocuments'
      AND conrelid = to_regclass('public."ReceivableDocuments"')
) THEN
ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "PK_ReceivableDocuments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableWriteOffs PK_ReceivableWriteOffs; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReceivableWriteOffs'
      AND conrelid = to_regclass('public."ReceivableWriteOffs"')
) THEN
ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "PK_ReceivableWriteOffs" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationAllocations PK_ReconciliationAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReconciliationAllocations'
      AND conrelid = to_regclass('public."ReconciliationAllocations"')
) THEN
ALTER TABLE ONLY public."ReconciliationAllocations"
    ADD CONSTRAINT "PK_ReconciliationAllocations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches PK_ReconciliationMatches; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReconciliationMatches'
      AND conrelid = to_regclass('public."ReconciliationMatches"')
) THEN
ALTER TABLE ONLY public."ReconciliationMatches"
    ADD CONSTRAINT "PK_ReconciliationMatches" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRunRules PK_ReconciliationRunRules; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReconciliationRunRules'
      AND conrelid = to_regclass('public."ReconciliationRunRules"')
) THEN
ALTER TABLE ONLY public."ReconciliationRunRules"
    ADD CONSTRAINT "PK_ReconciliationRunRules" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns PK_ReconciliationRuns; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReconciliationRuns'
      AND conrelid = to_regclass('public."ReconciliationRuns"')
) THEN
ALTER TABLE ONLY public."ReconciliationRuns"
    ADD CONSTRAINT "PK_ReconciliationRuns" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ReportSubscriptions PK_ReportSubscriptions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ReportSubscriptions'
      AND conrelid = to_regclass('public."ReportSubscriptions"')
) THEN
ALTER TABLE ONLY public."ReportSubscriptions"
    ADD CONSTRAINT "PK_ReportSubscriptions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SlaEvents PK_SlaEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SlaEvents'
      AND conrelid = to_regclass('public."SlaEvents"')
) THEN
ALTER TABLE ONLY public."SlaEvents"
    ADD CONSTRAINT "PK_SlaEvents" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SlaPolicies PK_SlaPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SlaPolicies'
      AND conrelid = to_regclass('public."SlaPolicies"')
) THEN
ALTER TABLE ONLY public."SlaPolicies"
    ADD CONSTRAINT "PK_SlaPolicies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SourcingAwards PK_SourcingAwards; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SourcingAwards'
      AND conrelid = to_regclass('public."SourcingAwards"')
) THEN
ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "PK_SourcingAwards" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupplierPurchaseHistory PK_SupplierPurchaseHistory; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SupplierPurchaseHistory'
      AND conrelid = to_regclass('public."SupplierPurchaseHistory"')
) THEN
ALTER TABLE ONLY public."SupplierPurchaseHistory"
    ADD CONSTRAINT "PK_SupplierPurchaseHistory" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupplierQuotedItems PK_SupplierQuotedItems; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SupplierQuotedItems'
      AND conrelid = to_regclass('public."SupplierQuotedItems"')
) THEN
ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "PK_SupplierQuotedItems" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: SupplierSolicitations PK_SupplierSolicitations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_SupplierSolicitations'
      AND conrelid = to_regclass('public."SupplierSolicitations"')
) THEN
ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "PK_SupplierSolicitations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: Taxes PK_Taxes; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_Taxes'
      AND conrelid = to_regclass('public."Taxes"')
) THEN
ALTER TABLE ONLY public."Taxes"
    ADD CONSTRAINT "PK_Taxes" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: TenantQueueStates PK_TenantQueueStates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_TenantQueueStates'
      AND conrelid = to_regclass('public."TenantQueueStates"')
) THEN
ALTER TABLE ONLY public."TenantQueueStates"
    ADD CONSTRAINT "PK_TenantQueueStates" PRIMARY KEY ("BusinessUnitId");
END IF;
END
$nexora_idem$;



--
-- Name: UserColumnPreferences PK_UserColumnPreferences; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_UserColumnPreferences'
      AND conrelid = to_regclass('public."UserColumnPreferences"')
) THEN
ALTER TABLE ONLY public."UserColumnPreferences"
    ADD CONSTRAINT "PK_UserColumnPreferences" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: WriteOffAllocations PK_WriteOffAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_WriteOffAllocations'
      AND conrelid = to_regclass('public."WriteOffAllocations"')
) THEN
ALTER TABLE ONLY public."WriteOffAllocations"
    ADD CONSTRAINT "PK_WriteOffAllocations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: Attachments PK__Attachme__3214EC2740D763DA; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Attachme__3214EC2740D763DA'
      AND conrelid = to_regclass('public."Attachments"')
) THEN
ALTER TABLE ONLY public."Attachments"
    ADD CONSTRAINT "PK__Attachme__3214EC2740D763DA" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: BusinessUnits PK__Business__3214EC27B5E4A97A; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Business__3214EC27B5E4A97A'
      AND conrelid = to_regclass('public."BusinessUnits"')
) THEN
ALTER TABLE ONLY public."BusinessUnits"
    ADD CONSTRAINT "PK__Business__3214EC27B5E4A97A" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Contacts PK__Contacts__3214EC274B89BAF3; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Contacts__3214EC274B89BAF3'
      AND conrelid = to_regclass('public."Contacts"')
) THEN
ALTER TABLE ONLY public."Contacts"
    ADD CONSTRAINT "PK__Contacts__3214EC274B89BAF3" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Currency PK__Currency__3214EC2734927EB0; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Currency__3214EC2734927EB0'
      AND conrelid = to_regclass('public."Currency"')
) THEN
ALTER TABLE ONLY public."Currency"
    ADD CONSTRAINT "PK__Currency__3214EC2734927EB0" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Customers PK__Customer__3214EC27D6DB6FD1; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Customer__3214EC27D6DB6FD1'
      AND conrelid = to_regclass('public."Customers"')
) THEN
ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "PK__Customer__3214EC27D6DB6FD1" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: EmailIngests PK__EmailIng__3214EC2728D6F6B3; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__EmailIng__3214EC2728D6F6B3'
      AND conrelid = to_regclass('public."EmailIngests"')
) THEN
ALTER TABLE ONLY public."EmailIngests"
    ADD CONSTRAINT "PK__EmailIng__3214EC2728D6F6B3" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Email_Configurations PK__Email_Co__3214EC278A1BB987; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Email_Co__3214EC278A1BB987'
      AND conrelid = to_regclass('public."Email_Configurations"')
) THEN
ALTER TABLE ONLY public."Email_Configurations"
    ADD CONSTRAINT "PK__Email_Co__3214EC278A1BB987" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Images PK__Images__3214EC27B2D5CCF9; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Images__3214EC27B2D5CCF9'
      AND conrelid = to_regclass('public."Images"')
) THEN
ALTER TABLE ONLY public."Images"
    ADD CONSTRAINT "PK__Images__3214EC27B2D5CCF9" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Products PK__Inventor__3214EC27426EF885; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Inventor__3214EC27426EF885'
      AND conrelid = to_regclass('public."Products"')
) THEN
ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "PK__Inventor__3214EC27426EF885" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: ProductCategories PK__Inventor__3214EC27EA9C64B5; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Inventor__3214EC27EA9C64B5'
      AND conrelid = to_regclass('public."ProductCategories"')
) THEN
ALTER TABLE ONLY public."ProductCategories"
    ADD CONSTRAINT "PK__Inventor__3214EC27EA9C64B5" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: ProductAttachments PK__Inventor__442C64DEB528BA1B; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Inventor__442C64DEB528BA1B'
      AND conrelid = to_regclass('public."ProductAttachments"')
) THEN
ALTER TABLE ONLY public."ProductAttachments"
    ADD CONSTRAINT "PK__Inventor__442C64DEB528BA1B" PRIMARY KEY ("AttachmentID");
END IF;
END
$nexora_idem$;



--
-- Name: LeadItems PK__LeadItem__3214EC2776894FBF; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__LeadItem__3214EC2776894FBF'
      AND conrelid = to_regclass('public."LeadItems"')
) THEN
ALTER TABLE ONLY public."LeadItems"
    ADD CONSTRAINT "PK__LeadItem__3214EC2776894FBF" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Leads PK__Leads__3214EC2705035004; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Leads__3214EC2705035004'
      AND conrelid = to_regclass('public."Leads"')
) THEN
ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "PK__Leads__3214EC2705035004" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Module PK__Module__3214EC276837F46D; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Module__3214EC276837F46D'
      AND conrelid = to_regclass('public."Module"')
) THEN
ALTER TABLE ONLY public."Module"
    ADD CONSTRAINT "PK__Module__3214EC276837F46D" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: OrderItems PK__OrderIte__3214EC27F54B0F5F; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__OrderIte__3214EC27F54B0F5F'
      AND conrelid = to_regclass('public."OrderItems"')
) THEN
ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "PK__OrderIte__3214EC27F54B0F5F" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Orders PK__Orders__3214EC27F30500C1; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Orders__3214EC27F30500C1'
      AND conrelid = to_regclass('public."Orders"')
) THEN
ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "PK__Orders__3214EC27F30500C1" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: ProductSubCategories PK__ProductS__3214EC2758B5F2D2; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__ProductS__3214EC2758B5F2D2'
      AND conrelid = to_regclass('public."ProductSubCategories"')
) THEN
ALTER TABLE ONLY public."ProductSubCategories"
    ADD CONSTRAINT "PK__ProductS__3214EC2758B5F2D2" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: QuoteItems PK__QuoteIte__3214EC27B021232E; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__QuoteIte__3214EC27B021232E'
      AND conrelid = to_regclass('public."QuoteItems"')
) THEN
ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "PK__QuoteIte__3214EC27B021232E" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Quotes PK__Quotes__3214EC27B0FC1337; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Quotes__3214EC27B0FC1337'
      AND conrelid = to_regclass('public."Quotes"')
) THEN
ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "PK__Quotes__3214EC27B0FC1337" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: RFQItems PK__RFQItems__3214EC2712F05C03; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__RFQItems__3214EC2712F05C03'
      AND conrelid = to_regclass('public."RFQItems"')
) THEN
ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "PK__RFQItems__3214EC2712F05C03" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: RFQ PK__RFQ__3214EC27E71B0249; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__RFQ__3214EC27E71B0249'
      AND conrelid = to_regclass('public."RFQ"')
) THEN
ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "PK__RFQ__3214EC27E71B0249" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: RolePermissions PK__RolePerm__3214EC27212832A0; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__RolePerm__3214EC27212832A0'
      AND conrelid = to_regclass('public."RolePermissions"')
) THEN
ALTER TABLE ONLY public."RolePermissions"
    ADD CONSTRAINT "PK__RolePerm__3214EC27212832A0" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: SetCity PK__SetCity__F2D21A961487DC00; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__SetCity__F2D21A961487DC00'
      AND conrelid = to_regclass('public."SetCity"')
) THEN
ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "PK__SetCity__F2D21A961487DC00" PRIMARY KEY ("CityID");
END IF;
END
$nexora_idem$;



--
-- Name: SetCountry PK__SetCount__10D160BF33E5BD3A; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__SetCount__10D160BF33E5BD3A'
      AND conrelid = to_regclass('public."SetCountry"')
) THEN
ALTER TABLE ONLY public."SetCountry"
    ADD CONSTRAINT "PK__SetCount__10D160BF33E5BD3A" PRIMARY KEY ("CountryID");
END IF;
END
$nexora_idem$;



--
-- Name: SetState PK__SetState__C3BA3B5A26295488; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__SetState__C3BA3B5A26295488'
      AND conrelid = to_regclass('public."SetState"')
) THEN
ALTER TABLE ONLY public."SetState"
    ADD CONSTRAINT "PK__SetState__C3BA3B5A26295488" PRIMARY KEY ("StateID");
END IF;
END
$nexora_idem$;



--
-- Name: Setup_Master PK__Setup_Ma__C9C734B31BDDC1E2; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Setup_Ma__C9C734B31BDDC1E2'
      AND conrelid = to_regclass('public."Setup_Master"')
) THEN
ALTER TABLE ONLY public."Setup_Master"
    ADD CONSTRAINT "PK__Setup_Ma__C9C734B31BDDC1E2" PRIMARY KEY ("SetupID");
END IF;
END
$nexora_idem$;



--
-- Name: ShipmentStatusHistory PK__Shipment__3214EC0749B79ADB; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Shipment__3214EC0749B79ADB'
      AND conrelid = to_regclass('public."ShipmentStatusHistory"')
) THEN
ALTER TABLE ONLY public."ShipmentStatusHistory"
    ADD CONSTRAINT "PK__Shipment__3214EC0749B79ADB" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: Shipments PK__Shipment__3214EC2732EE97FF; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Shipment__3214EC2732EE97FF'
      AND conrelid = to_regclass('public."Shipments"')
) THEN
ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "PK__Shipment__3214EC2732EE97FF" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: ShipmentItems PK__Shipment__3214EC27B4DD8C7A; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Shipment__3214EC27B4DD8C7A'
      AND conrelid = to_regclass('public."ShipmentItems"')
) THEN
ALTER TABLE ONLY public."ShipmentItems"
    ADD CONSTRAINT "PK__Shipment__3214EC27B4DD8C7A" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Suppliers PK__Supplier__3214EC2782495266; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Supplier__3214EC2782495266'
      AND conrelid = to_regclass('public."Suppliers"')
) THEN
ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "PK__Supplier__3214EC2782495266" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Teams PK__Teams__3214EC27A735D5D4; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Teams__3214EC27A735D5D4'
      AND conrelid = to_regclass('public."Teams"')
) THEN
ALTER TABLE ONLY public."Teams"
    ADD CONSTRAINT "PK__Teams__3214EC27A735D5D4" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: UserGroups PK__UserGrou__3214EC277F8DF4F8; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__UserGrou__3214EC277F8DF4F8'
      AND conrelid = to_regclass('public."UserGroups"')
) THEN
ALTER TABLE ONLY public."UserGroups"
    ADD CONSTRAINT "PK__UserGrou__3214EC277F8DF4F8" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Users PK__Users__3214EC279AB429D5; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Users__3214EC279AB429D5'
      AND conrelid = to_regclass('public."Users"')
) THEN
ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "PK__Users__3214EC279AB429D5" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: Warehouses PK__Warehous__3214EC27E9A0A7EE; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__Warehous__3214EC27E9A0A7EE'
      AND conrelid = to_regclass('public."Warehouses"')
) THEN
ALTER TABLE ONLY public."Warehouses"
    ADD CONSTRAINT "PK__Warehous__3214EC27E9A0A7EE" PRIMARY KEY ("ID");
END IF;
END
$nexora_idem$;



--
-- Name: __EFMigrationsHistory PK___EFMigrationsHistory; Type: CONSTRAINT; Schema: public; Owner: -
--

-- ADD CONSTRAINT "PK___EFMigrationsHistory" omitted: created with the table by EF Core.


--
-- Name: setUOM PK__setUOM__F6F8D59E4737F405; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK__setUOM__F6F8D59E4737F405'
      AND conrelid = to_regclass('public."setUOM"')
) THEN
ALTER TABLE ONLY public."setUOM"
    ADD CONSTRAINT "PK__setUOM__F6F8D59E4737F405" PRIMARY KEY ("UomID");
END IF;
END
$nexora_idem$;



--
-- Name: canonical_inquiries PK_canonical_inquiries; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_canonical_inquiries'
      AND conrelid = to_regclass('public.canonical_inquiries')
) THEN
ALTER TABLE ONLY public.canonical_inquiries
    ADD CONSTRAINT "PK_canonical_inquiries" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: canonical_line_items PK_canonical_line_items; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_canonical_line_items'
      AND conrelid = to_regclass('public.canonical_line_items')
) THEN
ALTER TABLE ONLY public.canonical_line_items
    ADD CONSTRAINT "PK_canonical_line_items" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: commercial_activities PK_commercial_activities; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_activities'
      AND conrelid = to_regclass('public.commercial_activities')
) THEN
ALTER TABLE ONLY public.commercial_activities
    ADD CONSTRAINT "PK_commercial_activities" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_demand_lines PK_commercial_demand_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_demand_lines'
      AND conrelid = to_regclass('public.commercial_demand_lines')
) THEN
ALTER TABLE ONLY public.commercial_demand_lines
    ADD CONSTRAINT "PK_commercial_demand_lines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_document_classifications PK_commercial_document_classifications; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_document_classifications'
      AND conrelid = to_regclass('public.commercial_document_classifications')
) THEN
ALTER TABLE ONLY public.commercial_document_classifications
    ADD CONSTRAINT "PK_commercial_document_classifications" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_cases PK_commercial_exception_cases; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_exception_cases'
      AND conrelid = to_regclass('public.commercial_exception_cases')
) THEN
ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "PK_commercial_exception_cases" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_events PK_commercial_exception_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_exception_events'
      AND conrelid = to_regclass('public.commercial_exception_events')
) THEN
ALTER TABLE ONLY public.commercial_exception_events
    ADD CONSTRAINT "PK_commercial_exception_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_operations PK_commercial_exception_operations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_exception_operations'
      AND conrelid = to_regclass('public.commercial_exception_operations')
) THEN
ALTER TABLE ONLY public.commercial_exception_operations
    ADD CONSTRAINT "PK_commercial_exception_operations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_outbox PK_commercial_exception_outbox; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_exception_outbox'
      AND conrelid = to_regclass('public.commercial_exception_outbox')
) THEN
ALTER TABLE ONLY public.commercial_exception_outbox
    ADD CONSTRAINT "PK_commercial_exception_outbox" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_lifecycle_events PK_commercial_lifecycle_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_lifecycle_events'
      AND conrelid = to_regclass('public.commercial_lifecycle_events')
) THEN
ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "PK_commercial_lifecycle_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_events PK_commercial_opportunity_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_opportunity_events'
      AND conrelid = to_regclass('public.commercial_opportunity_events')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_events
    ADD CONSTRAINT "PK_commercial_opportunity_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_feedback PK_commercial_opportunity_feedback; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_opportunity_feedback'
      AND conrelid = to_regclass('public.commercial_opportunity_feedback')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_feedback
    ADD CONSTRAINT "PK_commercial_opportunity_feedback" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_operations PK_commercial_opportunity_operations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_opportunity_operations'
      AND conrelid = to_regclass('public.commercial_opportunity_operations')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_operations
    ADD CONSTRAINT "PK_commercial_opportunity_operations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outbox PK_commercial_opportunity_outbox; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_opportunity_outbox'
      AND conrelid = to_regclass('public.commercial_opportunity_outbox')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_outbox
    ADD CONSTRAINT "PK_commercial_opportunity_outbox" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outcomes PK_commercial_opportunity_outcomes; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_opportunity_outcomes'
      AND conrelid = to_regclass('public.commercial_opportunity_outcomes')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_outcomes
    ADD CONSTRAINT "PK_commercial_opportunity_outcomes" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_recommendations PK_commercial_opportunity_recommendations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_commercial_opportunity_recommendations'
      AND conrelid = to_regclass('public.commercial_opportunity_recommendations')
) THEN
ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "PK_commercial_opportunity_recommendations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_definitions PK_custom_field_definitions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_definitions'
      AND conrelid = to_regclass('public.custom_field_definitions')
) THEN
ALTER TABLE ONLY public.custom_field_definitions
    ADD CONSTRAINT "PK_custom_field_definitions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_dependencies PK_custom_field_dependencies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_dependencies'
      AND conrelid = to_regclass('public.custom_field_dependencies')
) THEN
ALTER TABLE ONLY public.custom_field_dependencies
    ADD CONSTRAINT "PK_custom_field_dependencies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_options PK_custom_field_options; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_options'
      AND conrelid = to_regclass('public.custom_field_options')
) THEN
ALTER TABLE ONLY public.custom_field_options
    ADD CONSTRAINT "PK_custom_field_options" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_records PK_custom_field_records; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_records'
      AND conrelid = to_regclass('public.custom_field_records')
) THEN
ALTER TABLE ONLY public.custom_field_records
    ADD CONSTRAINT "PK_custom_field_records" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_rules PK_custom_field_rules; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_rules'
      AND conrelid = to_regclass('public.custom_field_rules')
) THEN
ALTER TABLE ONLY public.custom_field_rules
    ADD CONSTRAINT "PK_custom_field_rules" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_values PK_custom_field_values; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_values'
      AND conrelid = to_regclass('public.custom_field_values')
) THEN
ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "PK_custom_field_values" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_versions PK_custom_field_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_custom_field_versions'
      AND conrelid = to_regclass('public.custom_field_versions')
) THEN
ALTER TABLE ONLY public.custom_field_versions
    ADD CONSTRAINT "PK_custom_field_versions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: customer_identifiers PK_customer_identifiers; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_customer_identifiers'
      AND conrelid = to_regclass('public.customer_identifiers')
) THEN
ALTER TABLE ONLY public.customer_identifiers
    ADD CONSTRAINT "PK_customer_identifiers" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: customer_ownerships PK_customer_ownerships; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_customer_ownerships'
      AND conrelid = to_regclass('public.customer_ownerships')
) THEN
ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "PK_customer_ownerships" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: customer_quote_sourcing_decisions PK_customer_quote_sourcing_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_customer_quote_sourcing_decisions'
      AND conrelid = to_regclass('public.customer_quote_sourcing_decisions')
) THEN
ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "PK_customer_quote_sourcing_decisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: delivery_proof_lines PK_delivery_proof_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_delivery_proof_lines'
      AND conrelid = to_regclass('public.delivery_proof_lines')
) THEN
ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "PK_delivery_proof_lines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: delivery_proofs PK_delivery_proofs; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_delivery_proofs'
      AND conrelid = to_regclass('public.delivery_proofs')
) THEN
ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "PK_delivery_proofs" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: delivery_shortfall_decisions PK_delivery_shortfall_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_delivery_shortfall_decisions'
      AND conrelid = to_regclass('public.delivery_shortfall_decisions')
) THEN
ALTER TABLE ONLY public.delivery_shortfall_decisions
    ADD CONSTRAINT "PK_delivery_shortfall_decisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: document_corpora PK_document_corpora; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_document_corpora'
      AND conrelid = to_regclass('public.document_corpora')
) THEN
ALTER TABLE ONLY public.document_corpora
    ADD CONSTRAINT "PK_document_corpora" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: document_pages PK_document_pages; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_document_pages'
      AND conrelid = to_regclass('public.document_pages')
) THEN
ALTER TABLE ONLY public.document_pages
    ADD CONSTRAINT "PK_document_pages" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: document_regions PK_document_regions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_document_regions'
      AND conrelid = to_regclass('public.document_regions')
) THEN
ALTER TABLE ONLY public.document_regions
    ADD CONSTRAINT "PK_document_regions" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: evidence_retention_policies PK_evidence_retention_policies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_evidence_retention_policies'
      AND conrelid = to_regclass('public.evidence_retention_policies')
) THEN
ALTER TABLE ONLY public.evidence_retention_policies
    ADD CONSTRAINT "PK_evidence_retention_policies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: extraction_dead_letter_events PK_extraction_dead_letter_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_extraction_dead_letter_events'
      AND conrelid = to_regclass('public.extraction_dead_letter_events')
) THEN
ALTER TABLE ONLY public.extraction_dead_letter_events
    ADD CONSTRAINT "PK_extraction_dead_letter_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: extraction_runs PK_extraction_runs; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_extraction_runs'
      AND conrelid = to_regclass('public.extraction_runs')
) THEN
ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT "PK_extraction_runs" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: field_evidence PK_field_evidence; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_field_evidence'
      AND conrelid = to_regclass('public.field_evidence')
) THEN
ALTER TABLE ONLY public.field_evidence
    ADD CONSTRAINT "PK_field_evidence" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: follow_up_tasks PK_follow_up_tasks; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_follow_up_tasks'
      AND conrelid = to_regclass('public.follow_up_tasks')
) THEN
ALTER TABLE ONLY public.follow_up_tasks
    ADD CONSTRAINT "PK_follow_up_tasks" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: follow_up_transition_events PK_follow_up_transition_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_follow_up_transition_events'
      AND conrelid = to_regclass('public.follow_up_transition_events')
) THEN
ALTER TABLE ONLY public.follow_up_transition_events
    ADD CONSTRAINT "PK_follow_up_transition_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: goods_receipt_lines PK_goods_receipt_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_goods_receipt_lines'
      AND conrelid = to_regclass('public.goods_receipt_lines')
) THEN
ALTER TABLE ONLY public.goods_receipt_lines
    ADD CONSTRAINT "PK_goods_receipt_lines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: goods_receipts PK_goods_receipts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_goods_receipts'
      AND conrelid = to_regclass('public.goods_receipts')
) THEN
ALTER TABLE ONLY public.goods_receipts
    ADD CONSTRAINT "PK_goods_receipts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifact_events PK_governed_artifact_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_governed_artifact_events'
      AND conrelid = to_regclass('public.governed_artifact_events')
) THEN
ALTER TABLE ONLY public.governed_artifact_events
    ADD CONSTRAINT "PK_governed_artifact_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifact_versions PK_governed_artifact_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_governed_artifact_versions'
      AND conrelid = to_regclass('public.governed_artifact_versions')
) THEN
ALTER TABLE ONLY public.governed_artifact_versions
    ADD CONSTRAINT "PK_governed_artifact_versions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifacts PK_governed_artifacts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_governed_artifacts'
      AND conrelid = to_regclass('public.governed_artifacts')
) THEN
ALTER TABLE ONLY public.governed_artifacts
    ADD CONSTRAINT "PK_governed_artifacts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: human_action_events PK_human_action_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_human_action_events'
      AND conrelid = to_regclass('public.human_action_events')
) THEN
ALTER TABLE ONLY public.human_action_events
    ADD CONSTRAINT "PK_human_action_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: human_action_items PK_human_action_items; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_human_action_items'
      AND conrelid = to_regclass('public.human_action_items')
) THEN
ALTER TABLE ONLY public.human_action_items
    ADD CONSTRAINT "PK_human_action_items" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: inbound_logistics_policies PK_inbound_logistics_policies; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_inbound_logistics_policies'
      AND conrelid = to_regclass('public.inbound_logistics_policies')
) THEN
ALTER TABLE ONLY public.inbound_logistics_policies
    ADD CONSTRAINT "PK_inbound_logistics_policies" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: incoming_inventory PK_incoming_inventory; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_incoming_inventory'
      AND conrelid = to_regclass('public.incoming_inventory')
) THEN
ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "PK_incoming_inventory" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: inventory_movements PK_inventory_movements; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_inventory_movements'
      AND conrelid = to_regclass('public.inventory_movements')
) THEN
ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "PK_inventory_movements" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: inventory_reorder_alerts PK_inventory_reorder_alerts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_inventory_reorder_alerts'
      AND conrelid = to_regclass('public.inventory_reorder_alerts')
) THEN
ALTER TABLE ONLY public.inventory_reorder_alerts
    ADD CONSTRAINT "PK_inventory_reorder_alerts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: lead_assignments PK_lead_assignments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_lead_assignments'
      AND conrelid = to_regclass('public.lead_assignments')
) THEN
ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "PK_lead_assignments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: lead_customer_match_candidates PK_lead_customer_match_candidates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_lead_customer_match_candidates'
      AND conrelid = to_regclass('public.lead_customer_match_candidates')
) THEN
ALTER TABLE ONLY public.lead_customer_match_candidates
    ADD CONSTRAINT "PK_lead_customer_match_candidates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: lead_line_commercial_resolutions PK_lead_line_commercial_resolutions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_lead_line_commercial_resolutions'
      AND conrelid = to_regclass('public.lead_line_commercial_resolutions')
) THEN
ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "PK_lead_line_commercial_resolutions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: lead_routing_decisions PK_lead_routing_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_lead_routing_decisions'
      AND conrelid = to_regclass('public.lead_routing_decisions')
) THEN
ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "PK_lead_routing_decisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: learning_governance_events PK_learning_governance_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_learning_governance_events'
      AND conrelid = to_regclass('public.learning_governance_events')
) THEN
ALTER TABLE ONLY public.learning_governance_events
    ADD CONSTRAINT "PK_learning_governance_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: lifecycle_outbox_messages PK_lifecycle_outbox_messages; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_lifecycle_outbox_messages'
      AND conrelid = to_regclass('public.lifecycle_outbox_messages')
) THEN
ALTER TABLE ONLY public.lifecycle_outbox_messages
    ADD CONSTRAINT "PK_lifecycle_outbox_messages" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: material_lot_certificates PK_material_lot_certificates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_material_lot_certificates'
      AND conrelid = to_regclass('public.material_lot_certificates')
) THEN
ALTER TABLE ONLY public.material_lot_certificates
    ADD CONSTRAINT "PK_material_lot_certificates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: material_lot_consumptions PK_material_lot_consumptions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_material_lot_consumptions'
      AND conrelid = to_regclass('public.material_lot_consumptions')
) THEN
ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "PK_material_lot_consumptions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: material_lots PK_material_lots; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_material_lots'
      AND conrelid = to_regclass('public.material_lots')
) THEN
ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "PK_material_lots" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: ports_of_entry PK_ports_of_entry; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_ports_of_entry'
      AND conrelid = to_regclass('public.ports_of_entry')
) THEN
ALTER TABLE ONLY public.ports_of_entry
    ADD CONSTRAINT "PK_ports_of_entry" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: procurement_callback_receipts PK_procurement_callback_receipts; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_procurement_callback_receipts'
      AND conrelid = to_regclass('public.procurement_callback_receipts')
) THEN
ALTER TABLE ONLY public.procurement_callback_receipts
    ADD CONSTRAINT "PK_procurement_callback_receipts" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: procurement_events PK_procurement_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_procurement_events'
      AND conrelid = to_regclass('public.procurement_events')
) THEN
ALTER TABLE ONLY public.procurement_events
    ADD CONSTRAINT "PK_procurement_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: procurement_handoffs PK_procurement_handoffs; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_procurement_handoffs'
      AND conrelid = to_regclass('public.procurement_handoffs')
) THEN
ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "PK_procurement_handoffs" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: procurement_outbox PK_procurement_outbox; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_procurement_outbox'
      AND conrelid = to_regclass('public.procurement_outbox')
) THEN
ALTER TABLE ONLY public.procurement_outbox
    ADD CONSTRAINT "PK_procurement_outbox" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: product_aliases PK_product_aliases; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_product_aliases'
      AND conrelid = to_regclass('public.product_aliases')
) THEN
ALTER TABLE ONLY public.product_aliases
    ADD CONSTRAINT "PK_product_aliases" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: product_supersessions PK_product_supersessions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_product_supersessions'
      AND conrelid = to_regclass('public.product_supersessions')
) THEN
ALTER TABLE ONLY public.product_supersessions
    ADD CONSTRAINT "PK_product_supersessions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: quote_delivery_requests PK_quote_delivery_requests; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_quote_delivery_requests'
      AND conrelid = to_regclass('public.quote_delivery_requests')
) THEN
ALTER TABLE ONLY public.quote_delivery_requests
    ADD CONSTRAINT "PK_quote_delivery_requests" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: sales_coaching_acknowledgements PK_sales_coaching_acknowledgements; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_sales_coaching_acknowledgements'
      AND conrelid = to_regclass('public.sales_coaching_acknowledgements')
) THEN
ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "PK_sales_coaching_acknowledgements" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: sales_contributions PK_sales_contributions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_sales_contributions'
      AND conrelid = to_regclass('public.sales_contributions')
) THEN
ALTER TABLE ONLY public.sales_contributions
    ADD CONSTRAINT "PK_sales_contributions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: sales_rep_profiles PK_sales_rep_profiles; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_sales_rep_profiles'
      AND conrelid = to_regclass('public.sales_rep_profiles')
) THEN
ALTER TABLE ONLY public.sales_rep_profiles
    ADD CONSTRAINT "PK_sales_rep_profiles" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: sales_team_memberships PK_sales_team_memberships; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_sales_team_memberships'
      AND conrelid = to_regclass('public.sales_team_memberships')
) THEN
ALTER TABLE ONLY public.sales_team_memberships
    ADD CONSTRAINT "PK_sales_team_memberships" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: source_document_occurrences PK_source_document_occurrences; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_source_document_occurrences'
      AND conrelid = to_regclass('public.source_document_occurrences')
) THEN
ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT "PK_source_document_occurrences" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: source_documents PK_source_documents; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_source_documents'
      AND conrelid = to_regclass('public.source_documents')
) THEN
ALTER TABLE ONLY public.source_documents
    ADD CONSTRAINT "PK_source_documents" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: sourcing_case_candidates PK_sourcing_case_candidates; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_sourcing_case_candidates'
      AND conrelid = to_regclass('public.sourcing_case_candidates')
) THEN
ALTER TABLE ONLY public.sourcing_case_candidates
    ADD CONSTRAINT "PK_sourcing_case_candidates" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: sourcing_cases PK_sourcing_cases; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_sourcing_cases'
      AND conrelid = to_regclass('public.sourcing_cases')
) THEN
ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "PK_sourcing_cases" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: stock_reservations PK_stock_reservations; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_stock_reservations'
      AND conrelid = to_regclass('public.stock_reservations')
) THEN
ALTER TABLE ONLY public.stock_reservations
    ADD CONSTRAINT "PK_stock_reservations" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_negotiation_decisions PK_supplier_negotiation_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_negotiation_decisions'
      AND conrelid = to_regclass('public.supplier_negotiation_decisions')
) THEN
ALTER TABLE ONLY public.supplier_negotiation_decisions
    ADD CONSTRAINT "PK_supplier_negotiation_decisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_order_lines PK_supplier_purchase_order_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_purchase_order_lines'
      AND conrelid = to_regclass('public.supplier_purchase_order_lines')
) THEN
ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "PK_supplier_purchase_order_lines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_orders PK_supplier_purchase_orders; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_purchase_orders'
      AND conrelid = to_regclass('public.supplier_purchase_orders')
) THEN
ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "PK_supplier_purchase_orders" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_field_evidence PK_supplier_quote_field_evidence; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_quote_field_evidence'
      AND conrelid = to_regclass('public.supplier_quote_field_evidence')
) THEN
ALTER TABLE ONLY public.supplier_quote_field_evidence
    ADD CONSTRAINT "PK_supplier_quote_field_evidence" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_lines PK_supplier_quote_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_quote_lines'
      AND conrelid = to_regclass('public.supplier_quote_lines')
) THEN
ALTER TABLE ONLY public.supplier_quote_lines
    ADD CONSTRAINT "PK_supplier_quote_lines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_review_decisions PK_supplier_quote_review_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_quote_review_decisions'
      AND conrelid = to_regclass('public.supplier_quote_review_decisions')
) THEN
ALTER TABLE ONLY public.supplier_quote_review_decisions
    ADD CONSTRAINT "PK_supplier_quote_review_decisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_revisions PK_supplier_quote_revisions; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_quote_revisions'
      AND conrelid = to_regclass('public.supplier_quote_revisions')
) THEN
ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "PK_supplier_quote_revisions" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quotes PK_supplier_quotes; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_quotes'
      AND conrelid = to_regclass('public.supplier_quotes')
) THEN
ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "PK_supplier_quotes" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_shipment_lines PK_supplier_shipment_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_shipment_lines'
      AND conrelid = to_regclass('public.supplier_shipment_lines')
) THEN
ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "PK_supplier_shipment_lines" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: supplier_shipments PK_supplier_shipments; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_supplier_shipments'
      AND conrelid = to_regclass('public.supplier_shipments')
) THEN
ALTER TABLE ONLY public.supplier_shipments
    ADD CONSTRAINT "PK_supplier_shipments" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: tenant_governance_audit_events PK_tenant_governance_audit_events; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_tenant_governance_audit_events'
      AND conrelid = to_regclass('public.tenant_governance_audit_events')
) THEN
ALTER TABLE ONLY public.tenant_governance_audit_events
    ADD CONSTRAINT "PK_tenant_governance_audit_events" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: unassigned_work_items PK_unassigned_work_items; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_unassigned_work_items'
      AND conrelid = to_regclass('public.unassigned_work_items')
) THEN
ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "PK_unassigned_work_items" PRIMARY KEY ("Id");
END IF;
END
$nexora_idem$;



--
-- Name: validation_findings PK_validation_findings; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'PK_validation_findings'
      AND conrelid = to_regclass('public.validation_findings')
) THEN
ALTER TABLE ONLY public.validation_findings
    ADD CONSTRAINT "PK_validation_findings" PRIMARY KEY (id);
END IF;
END
$nexora_idem$;



--
-- Name: canonical_inquiries ak_canonical_inquiries_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_canonical_inquiries_tenant_id'
      AND conrelid = to_regclass('public.canonical_inquiries')
) THEN
ALTER TABLE ONLY public.canonical_inquiries
    ADD CONSTRAINT ak_canonical_inquiries_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: canonical_line_items ak_canonical_line_items_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_canonical_line_items_tenant_id'
      AND conrelid = to_regclass('public.canonical_line_items')
) THEN
ALTER TABLE ONLY public.canonical_line_items
    ADD CONSTRAINT ak_canonical_line_items_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: commercial_document_classifications ak_commercial_document_classifications_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_commercial_document_classifications_tenant_id'
      AND conrelid = to_regclass('public.commercial_document_classifications')
) THEN
ALTER TABLE ONLY public.commercial_document_classifications
    ADD CONSTRAINT ak_commercial_document_classifications_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: document_corpora ak_document_corpora_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_document_corpora_tenant_id'
      AND conrelid = to_regclass('public.document_corpora')
) THEN
ALTER TABLE ONLY public.document_corpora
    ADD CONSTRAINT ak_document_corpora_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: document_pages ak_document_pages_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_document_pages_tenant_id'
      AND conrelid = to_regclass('public.document_pages')
) THEN
ALTER TABLE ONLY public.document_pages
    ADD CONSTRAINT ak_document_pages_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: document_regions ak_document_regions_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_document_regions_tenant_id'
      AND conrelid = to_regclass('public.document_regions')
) THEN
ALTER TABLE ONLY public.document_regions
    ADD CONSTRAINT ak_document_regions_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: extraction_runs ak_extraction_runs_run_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_extraction_runs_run_id'
      AND conrelid = to_regclass('public.extraction_runs')
) THEN
ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT ak_extraction_runs_run_id UNIQUE (run_id);
END IF;
END
$nexora_idem$;



--
-- Name: extraction_runs ak_extraction_runs_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_extraction_runs_tenant_id'
      AND conrelid = to_regclass('public.extraction_runs')
) THEN
ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT ak_extraction_runs_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: extraction_runs ak_extraction_runs_tenant_run_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_extraction_runs_tenant_run_id'
      AND conrelid = to_regclass('public.extraction_runs')
) THEN
ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT ak_extraction_runs_tenant_run_id UNIQUE (business_unit_id, run_id);
END IF;
END
$nexora_idem$;



--
-- Name: source_document_occurrences ak_source_document_occurrences_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_source_document_occurrences_tenant_id'
      AND conrelid = to_regclass('public.source_document_occurrences')
) THEN
ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT ak_source_document_occurrences_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;



--
-- Name: source_documents ak_source_documents_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.
IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_source_documents_tenant_id'
      AND conrelid = to_regclass('public.source_documents')
) THEN
ALTER TABLE ONLY public.source_documents
    ADD CONSTRAINT ak_source_documents_tenant_id UNIQUE (business_unit_id, id);
END IF;
END
$nexora_idem$;

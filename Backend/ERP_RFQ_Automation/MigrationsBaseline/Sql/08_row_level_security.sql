-- ==========================================================================
-- ENABLE ROW LEVEL SECURITY + policies
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
-- Name: AccountingOutbox; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."AccountingOutbox" ENABLE ROW LEVEL SECURITY;

--
-- Name: PlatformAuditLogs; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."PlatformAuditLogs" ENABLE ROW LEVEL SECURITY;

--
-- Name: SubscriptionRevenueActions; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ROW LEVEL SECURITY;

--
-- Name: SubscriptionTaxRules; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."SubscriptionTaxRules" ENABLE ROW LEVEL SECURITY;

--
-- Name: TenantDataRecoveryEvidence; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."TenantDataRecoveryEvidence" ENABLE ROW LEVEL SECURITY;

--
-- Name: TenantDeletionCertificates; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."TenantDeletionCertificates" ENABLE ROW LEVEL SECURITY;

--
-- Name: TenantMeterSourcePolicies; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."TenantMeterSourcePolicies" ENABLE ROW LEVEL SECURITY;

--
-- Name: UsageCoverageSegments; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."UsageCoverageSegments" ENABLE ROW LEVEL SECURITY;

--
-- Name: UsageEventRatings; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."UsageEventRatings" ENABLE ROW LEVEL SECURITY;

--
-- Name: UsageEvents; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."UsageEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: UsageMinuteAggregates; Type: ROW SECURITY; Schema: platform; Owner: -
--

ALTER TABLE platform."UsageMinuteAggregates" ENABLE ROW LEVEL SECURITY;

--
-- Name: AccountingOutbox accounting_outbox_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'accounting_outbox_platform_fleet'
      AND polrelid = to_regclass('platform."AccountingOutbox"')
) THEN
CREATE POLICY accounting_outbox_platform_fleet ON platform."AccountingOutbox" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: TenantDeletionCertificates deletion_certificates_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'deletion_certificates_platform_fleet'
      AND polrelid = to_regclass('platform."TenantDeletionCertificates"')
) THEN
CREATE POLICY deletion_certificates_platform_fleet ON platform."TenantDeletionCertificates" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: PlatformAuditLogs nexora_ai_policy_audit_insert; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_ai_policy_audit_insert'
      AND polrelid = to_regclass('platform."PlatformAuditLogs"')
) THEN
CREATE POLICY nexora_ai_policy_audit_insert ON platform."PlatformAuditLogs" FOR INSERT TO nexora_tenant_app WITH CHECK (public.nexora_ai_policy_audit_allowed("ActAsTenantId", ("Action")::text, ("TargetType")::text, ("TargetId")::text));
END IF;
END
$nexora_idem$;



--
-- Name: TenantDataRecoveryEvidence recovery_evidence_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'recovery_evidence_platform_fleet'
      AND polrelid = to_regclass('platform."TenantDataRecoveryEvidence"')
) THEN
CREATE POLICY recovery_evidence_platform_fleet ON platform."TenantDataRecoveryEvidence" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionRevenueActions subscription_revenue_actions_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'subscription_revenue_actions_platform_fleet'
      AND polrelid = to_regclass('platform."SubscriptionRevenueActions"')
) THEN
CREATE POLICY subscription_revenue_actions_platform_fleet ON platform."SubscriptionRevenueActions" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: SubscriptionTaxRules subscription_tax_rules_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'subscription_tax_rules_platform_fleet'
      AND polrelid = to_regclass('platform."SubscriptionTaxRules"')
) THEN
CREATE POLICY subscription_tax_rules_platform_fleet ON platform."SubscriptionTaxRules" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: TenantMeterSourcePolicies tenant_meter_source_policies_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'tenant_meter_source_policies_platform_fleet'
      AND polrelid = to_regclass('platform."TenantMeterSourcePolicies"')
) THEN
CREATE POLICY tenant_meter_source_policies_platform_fleet ON platform."TenantMeterSourcePolicies" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: UsageCoverageSegments usage_coverage_segments_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'usage_coverage_segments_platform_fleet'
      AND polrelid = to_regclass('platform."UsageCoverageSegments"')
) THEN
CREATE POLICY usage_coverage_segments_platform_fleet ON platform."UsageCoverageSegments" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: UsageEventRatings usage_event_ratings_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'usage_event_ratings_platform_fleet'
      AND polrelid = to_regclass('platform."UsageEventRatings"')
) THEN
CREATE POLICY usage_event_ratings_platform_fleet ON platform."UsageEventRatings" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: UsageEvents usage_events_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'usage_events_platform_fleet'
      AND polrelid = to_regclass('platform."UsageEvents"')
) THEN
CREATE POLICY usage_events_platform_fleet ON platform."UsageEvents" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: UsageMinuteAggregates usage_minutes_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'usage_minutes_platform_fleet'
      AND polrelid = to_regclass('platform."UsageMinuteAggregates"')
) THEN
CREATE POLICY usage_minutes_platform_fleet ON platform."UsageMinuteAggregates" TO nexora_pipeline_app USING (true) WITH CHECK (true);
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AccountingPeriods" ENABLE ROW LEVEL SECURITY;

--
-- Name: AgentApprovals; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AgentApprovals" ENABLE ROW LEVEL SECURITY;

--
-- Name: AgentAuditLogs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AgentAuditLogs" ENABLE ROW LEVEL SECURITY;

--
-- Name: AgentMessages; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AgentMessages" ENABLE ROW LEVEL SECURITY;

--
-- Name: AgentPolicies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AgentPolicies" ENABLE ROW LEVEL SECURITY;

--
-- Name: AgentSessions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AgentSessions" ENABLE ROW LEVEL SECURITY;

--
-- Name: AiBudgetPeriods; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AiBudgetPeriods" ENABLE ROW LEVEL SECURITY;

--
-- Name: AiCallAttempts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AiCallAttempts" ENABLE ROW LEVEL SECURITY;

--
-- Name: AiProcessingPolicies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AiProcessingPolicies" ENABLE ROW LEVEL SECURITY;

--
-- Name: AiProviderAuthorizations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AiProviderAuthorizations" ENABLE ROW LEVEL SECURITY;

--
-- Name: AiRequests; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."AiRequests" ENABLE ROW LEVEL SECURITY;

--
-- Name: Attachments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Attachments" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankAccounts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankAccounts" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankAdjustmentDistributions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankAdjustmentDistributions" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankAdjustments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankAdjustments" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankMatchingRules; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankMatchingRules" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankStatementImports; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankStatementImports" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankStatementLines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankStatementLines" ENABLE ROW LEVEL SECURITY;

--
-- Name: BankStatements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BankStatements" ENABLE ROW LEVEL SECURITY;

--
-- Name: BoqAssemblies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BoqAssemblies" ENABLE ROW LEVEL SECURITY;

--
-- Name: BoqAssemblyComponents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BoqAssemblyComponents" ENABLE ROW LEVEL SECURITY;

--
-- Name: BoqDocuments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BoqDocuments" ENABLE ROW LEVEL SECURITY;

--
-- Name: BoqItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BoqItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: BoqSections; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BoqSections" ENABLE ROW LEVEL SECURITY;

--
-- Name: BusinessUnits; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."BusinessUnits" ENABLE ROW LEVEL SECURITY;

--
-- Name: CollectionControls; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CollectionControls" ENABLE ROW LEVEL SECURITY;

--
-- Name: CommercialCases; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CommercialCases" ENABLE ROW LEVEL SECURITY;

--
-- Name: CommercialFinanceAudits; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CommercialFinanceAudits" ENABLE ROW LEVEL SECURITY;

--
-- Name: CommercialMatchingPolicies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CommercialMatchingPolicies" ENABLE ROW LEVEL SECURITY;

--
-- Name: Contacts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Contacts" ENABLE ROW LEVEL SECURITY;

--
-- Name: Currency; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Currency" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerAwardLineAllocations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerAwardLineAllocations" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerAwards; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerAwards" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerCollectionProfiles; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerCollectionProfiles" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerPayments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerPayments" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerPurchaseOrderLines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerPurchaseOrderLines" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerPurchaseOrders; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerPurchaseOrders" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerRefunds; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerRefunds" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerStatementLines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerStatementLines" ENABLE ROW LEVEL SECURITY;

--
-- Name: CustomerStatements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."CustomerStatements" ENABLE ROW LEVEL SECURITY;

--
-- Name: Customers; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Customers" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningCases; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningCases" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningDeliveryAttempts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningDeliveryAttempts" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningNotices; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningNotices" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningPolicies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningPolicies" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningPolicySteps; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningPolicySteps" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningRunDecisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningRunDecisions" ENABLE ROW LEVEL SECURITY;

--
-- Name: DunningRuns; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."DunningRuns" ENABLE ROW LEVEL SECURITY;

--
-- Name: EmailIngests; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."EmailIngests" ENABLE ROW LEVEL SECURITY;

--
-- Name: Email_Configurations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Email_Configurations" ENABLE ROW LEVEL SECURITY;

--
-- Name: ExtractionCorpusEntries; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ExtractionCorpusEntries" ENABLE ROW LEVEL SECURITY;

--
-- Name: ExtractionJobs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ExtractionJobs" ENABLE ROW LEVEL SECURITY;

--
-- Name: FinanceCommunicationContacts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."FinanceCommunicationContacts" ENABLE ROW LEVEL SECURITY;

--
-- Name: FinanceOutboxMessages; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."FinanceOutboxMessages" ENABLE ROW LEVEL SECURITY;

--
-- Name: FolderIngestionRetryStates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."FolderIngestionRetryStates" ENABLE ROW LEVEL SECURITY;

--
-- Name: FxRateSnapshots; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."FxRateSnapshots" ENABLE ROW LEVEL SECURITY;

--
-- Name: FxRates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."FxRates" ENABLE ROW LEVEL SECURITY;

--
-- Name: IamAuditEvents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."IamAuditEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: Inventory; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Inventory" ENABLE ROW LEVEL SECURITY;

--
-- Name: JournalEntries; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."JournalEntries" ENABLE ROW LEVEL SECURITY;

--
-- Name: JournalEntryLines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."JournalEntryLines" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadIdentityAuditEvents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadIdentityAuditEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadIngestionBatches; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadIngestionBatches" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadIngestionOccurrences; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadIngestionOccurrences" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadItemRevisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadItemRevisions" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadMatchCandidates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadMatchCandidates" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadOccurrenceDocuments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadOccurrenceDocuments" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadReferenceConfigurations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadReferenceConfigurations" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadReviewAudits; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadReviewAudits" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadRevisionDifferences; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadRevisionDifferences" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadRevisionImpacts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadRevisionImpacts" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadRevisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadRevisions" ENABLE ROW LEVEL SECURITY;

--
-- Name: LeadStatusHistories; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LeadStatusHistories" ENABLE ROW LEVEL SECURITY;

--
-- Name: Leads; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Leads" ENABLE ROW LEVEL SECURITY;

--
-- Name: LedgerAccounts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LedgerAccounts" ENABLE ROW LEVEL SECURITY;

--
-- Name: LedgerActorNonces; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LedgerActorNonces" ENABLE ROW LEVEL SECURITY;

--
-- Name: LedgerBooks; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LedgerBooks" ENABLE ROW LEVEL SECURITY;

--
-- Name: LegalDocumentCounters; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."LegalDocumentCounters" ENABLE ROW LEVEL SECURITY;

--
-- Name: MasterDataChangeEvents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."MasterDataChangeEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: MasterDataFieldChanges; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."MasterDataFieldChanges" ENABLE ROW LEVEL SECURITY;

--
-- Name: MetricEvents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."MetricEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: OrderItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."OrderItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: OrderToCashAuditEvents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."OrderToCashAuditEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: OrderToCashDocumentCounters; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."OrderToCashDocumentCounters" ENABLE ROW LEVEL SECURITY;

--
-- Name: Orders; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Orders" ENABLE ROW LEVEL SECURITY;

--
-- Name: PaymentAllocations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."PaymentAllocations" ENABLE ROW LEVEL SECURITY;

--
-- Name: ProductAttachments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ProductAttachments" ENABLE ROW LEVEL SECURITY;

--
-- Name: ProductCategories; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ProductCategories" ENABLE ROW LEVEL SECURITY;

--
-- Name: ProductSubCategories; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ProductSubCategories" ENABLE ROW LEVEL SECURITY;

--
-- Name: Products; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Products" ENABLE ROW LEVEL SECURITY;

--
-- Name: PromisesToPay; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."PromisesToPay" ENABLE ROW LEVEL SECURITY;

--
-- Name: QuoteConfiguration; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."QuoteConfiguration" ENABLE ROW LEVEL SECURITY;

--
-- Name: QuoteItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."QuoteItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: QuotePriceAttestationLines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."QuotePriceAttestationLines" ENABLE ROW LEVEL SECURITY;

--
-- Name: QuotePriceAttestations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."QuotePriceAttestations" ENABLE ROW LEVEL SECURITY;

--
-- Name: QuoteRemovalRecords; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."QuoteRemovalRecords" ENABLE ROW LEVEL SECURITY;

--
-- Name: QuoteValidityExtensions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."QuoteValidityExtensions" ENABLE ROW LEVEL SECURITY;

--
-- Name: Quotes; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Quotes" ENABLE ROW LEVEL SECURITY;

--
-- Name: RFQ; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."RFQ" ENABLE ROW LEVEL SECURITY;

--
-- Name: RFQItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."RFQItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReceivableDocumentLines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReceivableDocumentLines" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReceivableDocuments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReceivableDocuments" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReceivableWriteOffs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReceivableWriteOffs" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReconciliationAllocations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReconciliationAllocations" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReconciliationMatches; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReconciliationMatches" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReconciliationRunRules; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReconciliationRunRules" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReconciliationRuns; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReconciliationRuns" ENABLE ROW LEVEL SECURITY;

--
-- Name: ReportSubscriptions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ReportSubscriptions" ENABLE ROW LEVEL SECURITY;

--
-- Name: RolePermissions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."RolePermissions" ENABLE ROW LEVEL SECURITY;

--
-- Name: SetCity; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SetCity" ENABLE ROW LEVEL SECURITY;

--
-- Name: SetCountry; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SetCountry" ENABLE ROW LEVEL SECURITY;

--
-- Name: SetState; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SetState" ENABLE ROW LEVEL SECURITY;

--
-- Name: Setup_Master; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Setup_Master" ENABLE ROW LEVEL SECURITY;

--
-- Name: ShipmentItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ShipmentItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: ShipmentStatusHistory; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."ShipmentStatusHistory" ENABLE ROW LEVEL SECURITY;

--
-- Name: Shipments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Shipments" ENABLE ROW LEVEL SECURITY;

--
-- Name: SlaEvents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SlaEvents" ENABLE ROW LEVEL SECURITY;

--
-- Name: SlaPolicies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SlaPolicies" ENABLE ROW LEVEL SECURITY;

--
-- Name: SourcingAwards; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SourcingAwards" ENABLE ROW LEVEL SECURITY;

--
-- Name: SupplierPurchaseHistory; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SupplierPurchaseHistory" ENABLE ROW LEVEL SECURITY;

--
-- Name: SupplierQuotedItems; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SupplierQuotedItems" ENABLE ROW LEVEL SECURITY;

--
-- Name: SupplierSolicitations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."SupplierSolicitations" ENABLE ROW LEVEL SECURITY;

--
-- Name: Suppliers; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Suppliers" ENABLE ROW LEVEL SECURITY;

--
-- Name: Taxes; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Taxes" ENABLE ROW LEVEL SECURITY;

--
-- Name: Teams; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Teams" ENABLE ROW LEVEL SECURITY;

--
-- Name: TenantQueueStates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."TenantQueueStates" ENABLE ROW LEVEL SECURITY;

--
-- Name: UserColumnPreferences; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."UserColumnPreferences" ENABLE ROW LEVEL SECURITY;

--
-- Name: UserGroups; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."UserGroups" ENABLE ROW LEVEL SECURITY;

--
-- Name: Users; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Users" ENABLE ROW LEVEL SECURITY;

--
-- Name: Warehouses; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."Warehouses" ENABLE ROW LEVEL SECURITY;

--
-- Name: WriteOffAllocations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."WriteOffAllocations" ENABLE ROW LEVEL SECURITY;

--
-- Name: canonical_inquiries; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.canonical_inquiries ENABLE ROW LEVEL SECURITY;

--
-- Name: canonical_line_items; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.canonical_line_items ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_activities; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_activities ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_demand_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_demand_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_document_classifications; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_document_classifications ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_exception_cases; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_exception_cases ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_exception_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_exception_events ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_exception_operations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_exception_operations ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_exception_outbox; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_exception_outbox ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_lifecycle_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_lifecycle_events ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_opportunity_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_opportunity_events ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_opportunity_feedback; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_opportunity_feedback ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_opportunity_operations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_opportunity_operations ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_opportunity_outbox; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_opportunity_outbox ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_opportunity_outcomes; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_opportunity_outcomes ENABLE ROW LEVEL SECURITY;

--
-- Name: commercial_opportunity_recommendations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.commercial_opportunity_recommendations ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_definitions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_definitions ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_dependencies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_dependencies ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_options; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_options ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_records; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_records ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_rules; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_rules ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_values; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_values ENABLE ROW LEVEL SECURITY;

--
-- Name: custom_field_versions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.custom_field_versions ENABLE ROW LEVEL SECURITY;

--
-- Name: customer_identifiers; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.customer_identifiers ENABLE ROW LEVEL SECURITY;

--
-- Name: customer_ownerships; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.customer_ownerships ENABLE ROW LEVEL SECURITY;

--
-- Name: customer_quote_sourcing_decisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.customer_quote_sourcing_decisions ENABLE ROW LEVEL SECURITY;

--
-- Name: delivery_proof_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.delivery_proof_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: delivery_proofs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.delivery_proofs ENABLE ROW LEVEL SECURITY;

--
-- Name: delivery_shortfall_decisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.delivery_shortfall_decisions ENABLE ROW LEVEL SECURITY;

--
-- Name: document_corpora; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.document_corpora ENABLE ROW LEVEL SECURITY;

--
-- Name: document_pages; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.document_pages ENABLE ROW LEVEL SECURITY;

--
-- Name: document_regions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.document_regions ENABLE ROW LEVEL SECURITY;

--
-- Name: evidence_retention_policies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.evidence_retention_policies ENABLE ROW LEVEL SECURITY;

--
-- Name: extraction_dead_letter_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.extraction_dead_letter_events ENABLE ROW LEVEL SECURITY;

--
-- Name: extraction_runs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.extraction_runs ENABLE ROW LEVEL SECURITY;

--
-- Name: field_evidence; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.field_evidence ENABLE ROW LEVEL SECURITY;

--
-- Name: follow_up_tasks; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.follow_up_tasks ENABLE ROW LEVEL SECURITY;

--
-- Name: follow_up_transition_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.follow_up_transition_events ENABLE ROW LEVEL SECURITY;

--
-- Name: goods_receipt_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.goods_receipt_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: goods_receipts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.goods_receipts ENABLE ROW LEVEL SECURITY;

--
-- Name: governed_artifact_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.governed_artifact_events ENABLE ROW LEVEL SECURITY;

--
-- Name: governed_artifact_versions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.governed_artifact_versions ENABLE ROW LEVEL SECURITY;

--
-- Name: governed_artifacts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.governed_artifacts ENABLE ROW LEVEL SECURITY;

--
-- Name: human_action_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.human_action_events ENABLE ROW LEVEL SECURITY;

--
-- Name: human_action_items; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.human_action_items ENABLE ROW LEVEL SECURITY;

--
-- Name: inbound_logistics_policies; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.inbound_logistics_policies ENABLE ROW LEVEL SECURITY;

--
-- Name: incoming_inventory; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.incoming_inventory ENABLE ROW LEVEL SECURITY;

--
-- Name: inventory_movements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.inventory_movements ENABLE ROW LEVEL SECURITY;

--
-- Name: inventory_reorder_alerts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.inventory_reorder_alerts ENABLE ROW LEVEL SECURITY;

--
-- Name: lead_assignments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.lead_assignments ENABLE ROW LEVEL SECURITY;

--
-- Name: lead_customer_match_candidates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.lead_customer_match_candidates ENABLE ROW LEVEL SECURITY;

--
-- Name: lead_line_commercial_resolutions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.lead_line_commercial_resolutions ENABLE ROW LEVEL SECURITY;

--
-- Name: lead_routing_decisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.lead_routing_decisions ENABLE ROW LEVEL SECURITY;

--
-- Name: learning_governance_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.learning_governance_events ENABLE ROW LEVEL SECURITY;

--
-- Name: lifecycle_outbox_messages; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.lifecycle_outbox_messages ENABLE ROW LEVEL SECURITY;

--
-- Name: material_lot_certificates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.material_lot_certificates ENABLE ROW LEVEL SECURITY;

--
-- Name: material_lot_consumptions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.material_lot_consumptions ENABLE ROW LEVEL SECURITY;

--
-- Name: material_lots; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.material_lots ENABLE ROW LEVEL SECURITY;

--
-- Name: AiProcessingPolicies nexora_ai_default_provisioning; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_ai_default_provisioning'
      AND polrelid = to_regclass('public."AiProcessingPolicies"')
) THEN
CREATE POLICY nexora_ai_default_provisioning ON public."AiProcessingPolicies" FOR INSERT WITH CHECK ((("IsEnabled" = true) AND ("ExternalProcessingAllowed" = false) AND (("AllowedPurposes")::text = 'RfqExtraction,BoqDraft'::text) AND ("AllowedProvider" IS NULL) AND ("AllowedModel" IS NULL) AND ("MonthlySoftTokenLimit" IS NULL) AND ("MonthlyHardTokenLimit" IS NULL) AND ("Version" = 1) AND (("UpdatedBy")::text = 'tenant-provisioning'::text)));
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AccountingPeriods"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AccountingPeriods" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AgentApprovals nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AgentApprovals"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AgentApprovals" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AgentAuditLogs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AgentAuditLogs"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AgentAuditLogs" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AgentMessages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AgentMessages"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AgentMessages" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AgentPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AgentPolicies"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AgentPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AgentSessions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AgentSessions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AgentSessions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AiBudgetPeriods nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AiBudgetPeriods"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AiBudgetPeriods" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AiCallAttempts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AiCallAttempts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AiCallAttempts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AiProcessingPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AiProcessingPolicies"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AiProcessingPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AiProviderAuthorizations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AiProviderAuthorizations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AiProviderAuthorizations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: AiRequests nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."AiRequests"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."AiRequests" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Attachments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Attachments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Attachments" TO nexora_tenant_app USING (((("ParentType")::text = 'Lead'::text) AND (EXISTS ( SELECT 1
   FROM public."Leads" lead
  WHERE ((lead."ID" = "Attachments"."ParentID") AND (lead."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))))) WITH CHECK (((("ParentType")::text = 'Lead'::text) AND (EXISTS ( SELECT 1
   FROM public."Leads" lead
  WHERE ((lead."ID" = "Attachments"."ParentID") AND (lead."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))));
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankAccounts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankAccounts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustmentDistributions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankAdjustmentDistributions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankAdjustmentDistributions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankAdjustments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankAdjustments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BankMatchingRules nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankMatchingRules"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankMatchingRules" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankStatementImports"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankStatementImports" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankStatementLines"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankStatementLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BankStatements"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BankStatements" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BoqAssemblies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BoqAssemblies"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BoqAssemblies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BoqAssemblyComponents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BoqAssemblyComponents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BoqAssemblyComponents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BoqDocuments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BoqDocuments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BoqDocuments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BoqItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BoqItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BoqItems" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BoqSections nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BoqSections"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BoqSections" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: BusinessUnits nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."BusinessUnits"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."BusinessUnits" TO nexora_tenant_app USING (("ID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("ID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CollectionControls"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CollectionControls" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CommercialCases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CommercialCases"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CommercialCases" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CommercialFinanceAudits nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CommercialFinanceAudits"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CommercialFinanceAudits" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CommercialMatchingPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CommercialMatchingPolicies"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CommercialMatchingPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Contacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Contacts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Contacts" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Currency nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Currency"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Currency" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwardLineAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerAwardLineAllocations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerAwardLineAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwards nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerAwards"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerAwards" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerCollectionProfiles"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerCollectionProfiles" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerPayments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerPayments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrderLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerPurchaseOrderLines"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerPurchaseOrderLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrders nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerPurchaseOrders"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerPurchaseOrders" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerRefunds"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerRefunds" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatementLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerStatementLines"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerStatementLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."CustomerStatements"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."CustomerStatements" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Customers nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Customers"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Customers" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningCases"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningCases" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningDeliveryAttempts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningDeliveryAttempts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningNotices"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningNotices" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningPolicies"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicySteps nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningPolicySteps"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningPolicySteps" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningRunDecisions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningRunDecisions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."DunningRuns"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."DunningRuns" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: EmailIngests nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."EmailIngests"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."EmailIngests" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Email_Configurations" configuration
  WHERE ((configuration."ID" = "EmailIngests"."EmailConfigurationID") AND (configuration."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Email_Configurations" configuration
  WHERE ((configuration."ID" = "EmailIngests"."EmailConfigurationID") AND (configuration."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: Email_Configurations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Email_Configurations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Email_Configurations" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionCorpusEntries nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ExtractionCorpusEntries"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ExtractionCorpusEntries" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionJobs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ExtractionJobs"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ExtractionJobs" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."FinanceCommunicationContacts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."FinanceCommunicationContacts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: FinanceOutboxMessages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."FinanceOutboxMessages"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."FinanceOutboxMessages" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: FolderIngestionRetryStates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."FolderIngestionRetryStates"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."FolderIngestionRetryStates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: FxRateSnapshots nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."FxRateSnapshots"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."FxRateSnapshots" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: FxRates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."FxRates"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."FxRates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: IamAuditEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."IamAuditEvents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."IamAuditEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Inventory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Inventory"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Inventory" TO nexora_tenant_app USING ((("Buid" IS NULL) OR ("Buid" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))) WITH CHECK (("Buid" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."JournalEntries"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."JournalEntries" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntryLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."JournalEntryLines"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."JournalEntryLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadIdentityAuditEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadIdentityAuditEvents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadIdentityAuditEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionBatches nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadIngestionBatches"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadIngestionBatches" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionOccurrences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadIngestionOccurrences"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadIngestionOccurrences" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadItemRevisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadItemRevisions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadItemRevisions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Leads" parent
  WHERE ((parent."ID" = "LeadItems"."LeadID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Leads" parent
  WHERE ((parent."ID" = "LeadItems"."LeadID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: LeadMatchCandidates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadMatchCandidates"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadMatchCandidates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadOccurrenceDocuments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadOccurrenceDocuments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadOccurrenceDocuments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadReferenceConfigurations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadReferenceConfigurations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadReferenceConfigurations" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadReviewAudits nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadReviewAudits"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadReviewAudits" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisionDifferences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadRevisionDifferences"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadRevisionDifferences" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisionImpacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadRevisionImpacts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadRevisionImpacts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadRevisions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadRevisions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LeadStatusHistories nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LeadStatusHistories"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LeadStatusHistories" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Leads nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Leads"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Leads" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LedgerAccounts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LedgerAccounts"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LedgerAccounts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LedgerBooks"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LedgerBooks" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: LegalDocumentCounters nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."LegalDocumentCounters"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."LegalDocumentCounters" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataChangeEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."MasterDataChangeEvents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."MasterDataChangeEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataFieldChanges nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."MasterDataFieldChanges"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."MasterDataFieldChanges" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: MetricEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."MetricEvents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."MetricEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: OrderItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."OrderItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."OrderItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Orders" parent
  WHERE ((parent."ID" = "OrderItems"."OrderID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Orders" parent
  WHERE ((parent."ID" = "OrderItems"."OrderID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: OrderToCashAuditEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."OrderToCashAuditEvents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."OrderToCashAuditEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: OrderToCashDocumentCounters nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."OrderToCashDocumentCounters"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."OrderToCashDocumentCounters" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Orders nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Orders"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Orders" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: PaymentAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."PaymentAllocations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."PaymentAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ProductAttachments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ProductAttachments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ProductAttachments" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Products" product
  WHERE ((product."ID" = "ProductAttachments"."InventoryID") AND ((product."BUID" IS NULL) OR (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Products" product
  WHERE ((product."ID" = "ProductAttachments"."InventoryID") AND (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: ProductCategories nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ProductCategories"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ProductCategories" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ProductSubCategories nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ProductSubCategories"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ProductSubCategories" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Products nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Products"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Products" TO nexora_tenant_app USING ((("BUID" IS NULL) OR ("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: PromisesToPay nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."PromisesToPay"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."PromisesToPay" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: QuoteConfiguration nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."QuoteConfiguration"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."QuoteConfiguration" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: QuoteItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."QuoteItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."QuoteItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Quotes" parent
  WHERE ((parent."ID" = "QuoteItems"."QuoteID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Quotes" parent
  WHERE ((parent."ID" = "QuoteItems"."QuoteID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: QuotePriceAttestationLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."QuotePriceAttestationLines"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."QuotePriceAttestationLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: QuotePriceAttestations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."QuotePriceAttestations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."QuotePriceAttestations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: QuoteRemovalRecords nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."QuoteRemovalRecords"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."QuoteRemovalRecords" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: QuoteValidityExtensions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."QuoteValidityExtensions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."QuoteValidityExtensions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Quotes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Quotes"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Quotes" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: RFQ nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."RFQ"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."RFQ" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: RFQItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."RFQItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."RFQItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."RFQ" parent
  WHERE ((parent."ID" = "RFQItems"."RFQID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."RFQ" parent
  WHERE ((parent."ID" = "RFQItems"."RFQID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocumentLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReceivableDocumentLines"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReceivableDocumentLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocuments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReceivableDocuments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReceivableDocuments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableWriteOffs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReceivableWriteOffs"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReceivableWriteOffs" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReconciliationAllocations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReconciliationAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReconciliationMatches"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReconciliationMatches" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRunRules nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReconciliationRunRules"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReconciliationRunRules" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReconciliationRuns"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReconciliationRuns" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ReportSubscriptions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ReportSubscriptions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ReportSubscriptions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: RolePermissions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."RolePermissions"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."RolePermissions" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SetCity nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SetCity"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SetCity" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SetCountry nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SetCountry"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SetCountry" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SetState nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SetState"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SetState" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Setup_Master nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Setup_Master"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Setup_Master" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ShipmentItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ShipmentItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ShipmentItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Shipments" parent
  WHERE ((parent."ID" = "ShipmentItems"."ShipmentID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Shipments" parent
  WHERE ((parent."ID" = "ShipmentItems"."ShipmentID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: ShipmentStatusHistory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."ShipmentStatusHistory"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."ShipmentStatusHistory" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Shipments" shipment
  WHERE ((shipment."ID" = "ShipmentStatusHistory"."ShipmentId") AND (shipment."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Shipments" shipment
  WHERE ((shipment."ID" = "ShipmentStatusHistory"."ShipmentId") AND (shipment."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: Shipments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Shipments"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Shipments" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SlaEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SlaEvents"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SlaEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SlaPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SlaPolicies"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SlaPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SourcingAwards nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SourcingAwards"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SourcingAwards" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SupplierPurchaseHistory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SupplierPurchaseHistory"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SupplierPurchaseHistory" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Products" product,
    public."Suppliers" supplier
  WHERE ((product."ID" = "SupplierPurchaseHistory"."ProductId") AND (supplier."ID" = "SupplierPurchaseHistory"."SupplierId") AND ((product."BUID" IS NULL) OR (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) AND ((supplier."BUID" IS NULL) OR (supplier."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Products" product,
    public."Suppliers" supplier
  WHERE ((product."ID" = "SupplierPurchaseHistory"."ProductId") AND (supplier."ID" = "SupplierPurchaseHistory"."SupplierId") AND ((product."BUID" IS NULL) OR (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) AND ((supplier."BUID" IS NULL) OR (supplier."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) AND ((product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint) OR (supplier."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))));
END IF;
END
$nexora_idem$;



--
-- Name: SupplierQuotedItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SupplierQuotedItems"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SupplierQuotedItems" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: SupplierSolicitations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."SupplierSolicitations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."SupplierSolicitations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Suppliers nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Suppliers"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Suppliers" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Taxes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Taxes"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Taxes" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Teams nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Teams"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Teams" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: TenantQueueStates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."TenantQueueStates"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."TenantQueueStates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: UserColumnPreferences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."UserColumnPreferences"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."UserColumnPreferences" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: UserGroups nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."UserGroups"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."UserGroups" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Users nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Users"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Users" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: Warehouses nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."Warehouses"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."Warehouses" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: WriteOffAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."WriteOffAllocations"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."WriteOffAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: canonical_inquiries nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.canonical_inquiries')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.canonical_inquiries TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: canonical_line_items nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.canonical_line_items')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.canonical_line_items TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_activities nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_activities')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_activities TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_demand_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_demand_lines')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_demand_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_document_classifications nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_document_classifications')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_document_classifications TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_cases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_exception_cases')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_cases TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_exception_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_operations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_exception_operations')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_operations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_outbox nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_exception_outbox')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_outbox TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_lifecycle_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_lifecycle_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_lifecycle_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_opportunity_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_feedback nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_opportunity_feedback')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_feedback TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_operations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_opportunity_operations')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_operations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outbox nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_opportunity_outbox')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_outbox TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outcomes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_opportunity_outcomes')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_outcomes TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_recommendations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.commercial_opportunity_recommendations')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_recommendations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_definitions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_definitions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_definitions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_dependencies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_dependencies')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_dependencies TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_dependencies."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK (((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_dependencies."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))) AND (EXISTS ( SELECT 1
   FROM public.custom_field_definitions dependency
  WHERE ((dependency."Id" = custom_field_dependencies."DependsOnDefinitionId") AND (dependency."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_options nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_options')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_options TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_options."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_options."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_records nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_records')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_records TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_rules nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_rules')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_rules TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_rules."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_rules."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_values nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_values')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_values TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_versions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.custom_field_versions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.custom_field_versions TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public.custom_field_definitions definition
  WHERE ((definition."Id" = custom_field_versions."DefinitionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.custom_field_definitions definition
  WHERE ((definition."Id" = custom_field_versions."DefinitionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));
END IF;
END
$nexora_idem$;



--
-- Name: customer_identifiers nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.customer_identifiers')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.customer_identifiers TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: customer_ownerships nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.customer_ownerships')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.customer_ownerships TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: customer_quote_sourcing_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.customer_quote_sourcing_decisions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.customer_quote_sourcing_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: delivery_proof_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.delivery_proof_lines')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.delivery_proof_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: delivery_proofs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.delivery_proofs')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.delivery_proofs TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: delivery_shortfall_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.delivery_shortfall_decisions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.delivery_shortfall_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: document_corpora nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.document_corpora')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.document_corpora TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: document_pages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.document_pages')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.document_pages TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: document_regions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.document_regions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.document_regions TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: evidence_retention_policies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.evidence_retention_policies')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.evidence_retention_policies TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: extraction_dead_letter_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.extraction_dead_letter_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.extraction_dead_letter_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: extraction_runs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.extraction_runs')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.extraction_runs TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: field_evidence nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.field_evidence')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.field_evidence TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: follow_up_tasks nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.follow_up_tasks')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.follow_up_tasks TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: follow_up_transition_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.follow_up_transition_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.follow_up_transition_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: goods_receipt_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.goods_receipt_lines')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.goods_receipt_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: goods_receipts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.goods_receipts')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.goods_receipts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifact_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.governed_artifact_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.governed_artifact_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifact_versions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.governed_artifact_versions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.governed_artifact_versions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.governed_artifacts')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.governed_artifacts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: human_action_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.human_action_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.human_action_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: human_action_items nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.human_action_items')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.human_action_items TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: inbound_logistics_policies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.inbound_logistics_policies')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.inbound_logistics_policies TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: incoming_inventory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.incoming_inventory')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.incoming_inventory TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: inventory_movements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.inventory_movements')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.inventory_movements TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: inventory_reorder_alerts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.inventory_reorder_alerts')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.inventory_reorder_alerts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: lead_assignments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.lead_assignments')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.lead_assignments TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: lead_customer_match_candidates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.lead_customer_match_candidates')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.lead_customer_match_candidates TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: lead_line_commercial_resolutions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.lead_line_commercial_resolutions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.lead_line_commercial_resolutions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: lead_routing_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.lead_routing_decisions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.lead_routing_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: learning_governance_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.learning_governance_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.learning_governance_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: lifecycle_outbox_messages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.lifecycle_outbox_messages')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.lifecycle_outbox_messages TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: material_lot_certificates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.material_lot_certificates')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.material_lot_certificates TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: material_lot_consumptions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.material_lot_consumptions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.material_lot_consumptions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: material_lots nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.material_lots')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.material_lots TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ports_of_entry nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.ports_of_entry')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.ports_of_entry TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: procurement_callback_receipts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.procurement_callback_receipts')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.procurement_callback_receipts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: procurement_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.procurement_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.procurement_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: procurement_handoffs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.procurement_handoffs')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.procurement_handoffs TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: procurement_outbox nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.procurement_outbox')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.procurement_outbox TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: product_aliases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.product_aliases')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.product_aliases TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: product_supersessions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.product_supersessions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.product_supersessions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: quote_delivery_requests nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.quote_delivery_requests')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.quote_delivery_requests TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: sales_coaching_acknowledgements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.sales_coaching_acknowledgements')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.sales_coaching_acknowledgements TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: sales_contributions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.sales_contributions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.sales_contributions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: sales_rep_profiles nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.sales_rep_profiles')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.sales_rep_profiles TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: sales_team_memberships nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.sales_team_memberships')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.sales_team_memberships TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: setUOM nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public."setUOM"')
) THEN
CREATE POLICY nexora_tenant_isolation ON public."setUOM" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: source_document_occurrences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.source_document_occurrences')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.source_document_occurrences TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: source_documents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.source_documents')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.source_documents TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: sourcing_case_candidates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.sourcing_case_candidates')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.sourcing_case_candidates TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: sourcing_cases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.sourcing_cases')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.sourcing_cases TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: stock_reservations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.stock_reservations')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.stock_reservations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_negotiation_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_negotiation_decisions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_negotiation_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_order_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_purchase_order_lines')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_purchase_order_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_orders nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_purchase_orders')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_purchase_orders TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_field_evidence nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_quote_field_evidence')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_field_evidence TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_quote_lines')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_review_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_quote_review_decisions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_review_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_revisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_quote_revisions')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_revisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quotes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_quotes')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_quotes TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_shipment_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_shipment_lines')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_shipment_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: supplier_shipments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.supplier_shipments')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.supplier_shipments TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: tenant_governance_audit_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.tenant_governance_audit_events')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.tenant_governance_audit_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: unassigned_work_items nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.unassigned_work_items')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.unassigned_work_items TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: validation_findings nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.
IF NOT EXISTS (
    SELECT 1 FROM pg_policy
    WHERE polname = 'nexora_tenant_isolation'
      AND polrelid = to_regclass('public.validation_findings')
) THEN
CREATE POLICY nexora_tenant_isolation ON public.validation_findings TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
END IF;
END
$nexora_idem$;



--
-- Name: ports_of_entry; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.ports_of_entry ENABLE ROW LEVEL SECURITY;

--
-- Name: procurement_callback_receipts; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.procurement_callback_receipts ENABLE ROW LEVEL SECURITY;

--
-- Name: procurement_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.procurement_events ENABLE ROW LEVEL SECURITY;

--
-- Name: procurement_handoffs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.procurement_handoffs ENABLE ROW LEVEL SECURITY;

--
-- Name: procurement_outbox; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.procurement_outbox ENABLE ROW LEVEL SECURITY;

--
-- Name: product_aliases; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.product_aliases ENABLE ROW LEVEL SECURITY;

--
-- Name: product_supersessions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.product_supersessions ENABLE ROW LEVEL SECURITY;

--
-- Name: quote_delivery_requests; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.quote_delivery_requests ENABLE ROW LEVEL SECURITY;

--
-- Name: sales_coaching_acknowledgements; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sales_coaching_acknowledgements ENABLE ROW LEVEL SECURITY;

--
-- Name: sales_contributions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sales_contributions ENABLE ROW LEVEL SECURITY;

--
-- Name: sales_rep_profiles; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sales_rep_profiles ENABLE ROW LEVEL SECURITY;

--
-- Name: sales_team_memberships; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sales_team_memberships ENABLE ROW LEVEL SECURITY;

--
-- Name: setUOM; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public."setUOM" ENABLE ROW LEVEL SECURITY;

--
-- Name: source_document_occurrences; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.source_document_occurrences ENABLE ROW LEVEL SECURITY;

--
-- Name: source_documents; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.source_documents ENABLE ROW LEVEL SECURITY;

--
-- Name: sourcing_case_candidates; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sourcing_case_candidates ENABLE ROW LEVEL SECURITY;

--
-- Name: sourcing_cases; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sourcing_cases ENABLE ROW LEVEL SECURITY;

--
-- Name: stock_reservations; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.stock_reservations ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_negotiation_decisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_negotiation_decisions ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_purchase_order_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_purchase_order_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_purchase_orders; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_purchase_orders ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_quote_field_evidence; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_quote_field_evidence ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_quote_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_quote_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_quote_review_decisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_quote_review_decisions ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_quote_revisions; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_quote_revisions ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_quotes; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_quotes ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_shipment_lines; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_shipment_lines ENABLE ROW LEVEL SECURITY;

--
-- Name: supplier_shipments; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.supplier_shipments ENABLE ROW LEVEL SECURITY;

--
-- Name: tenant_governance_audit_events; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.tenant_governance_audit_events ENABLE ROW LEVEL SECURITY;

--
-- Name: unassigned_work_items; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.unassigned_work_items ENABLE ROW LEVEL SECURITY;

--
-- Name: validation_findings; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.validation_findings ENABLE ROW LEVEL SECURITY;

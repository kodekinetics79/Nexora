-- ==========================================================================
-- ENABLE ROW LEVEL SECURITY + policies
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
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

CREATE POLICY accounting_outbox_platform_fleet ON platform."AccountingOutbox" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: TenantDeletionCertificates deletion_certificates_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY deletion_certificates_platform_fleet ON platform."TenantDeletionCertificates" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: PlatformAuditLogs nexora_ai_policy_audit_insert; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY nexora_ai_policy_audit_insert ON platform."PlatformAuditLogs" FOR INSERT TO nexora_tenant_app WITH CHECK (public.nexora_ai_policy_audit_allowed("ActAsTenantId", ("Action")::text, ("TargetType")::text, ("TargetId")::text));


--
-- Name: TenantDataRecoveryEvidence recovery_evidence_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY recovery_evidence_platform_fleet ON platform."TenantDataRecoveryEvidence" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: SubscriptionRevenueActions subscription_revenue_actions_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY subscription_revenue_actions_platform_fleet ON platform."SubscriptionRevenueActions" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: SubscriptionTaxRules subscription_tax_rules_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY subscription_tax_rules_platform_fleet ON platform."SubscriptionTaxRules" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: TenantMeterSourcePolicies tenant_meter_source_policies_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY tenant_meter_source_policies_platform_fleet ON platform."TenantMeterSourcePolicies" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: UsageCoverageSegments usage_coverage_segments_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY usage_coverage_segments_platform_fleet ON platform."UsageCoverageSegments" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: UsageEventRatings usage_event_ratings_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY usage_event_ratings_platform_fleet ON platform."UsageEventRatings" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: UsageEvents usage_events_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY usage_events_platform_fleet ON platform."UsageEvents" TO nexora_pipeline_app USING (true) WITH CHECK (true);


--
-- Name: UsageMinuteAggregates usage_minutes_platform_fleet; Type: POLICY; Schema: platform; Owner: -
--

CREATE POLICY usage_minutes_platform_fleet ON platform."UsageMinuteAggregates" TO nexora_pipeline_app USING (true) WITH CHECK (true);


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

CREATE POLICY nexora_ai_default_provisioning ON public."AiProcessingPolicies" FOR INSERT WITH CHECK ((("IsEnabled" = true) AND ("ExternalProcessingAllowed" = false) AND (("AllowedPurposes")::text = 'RfqExtraction,BoqDraft'::text) AND ("AllowedProvider" IS NULL) AND ("AllowedModel" IS NULL) AND ("MonthlySoftTokenLimit" IS NULL) AND ("MonthlyHardTokenLimit" IS NULL) AND ("Version" = 1) AND (("UpdatedBy")::text = 'tenant-provisioning'::text)));


--
-- Name: AccountingPeriods nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AccountingPeriods" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AgentApprovals nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AgentApprovals" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AgentAuditLogs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AgentAuditLogs" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AgentMessages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AgentMessages" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AgentPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AgentPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AgentSessions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AgentSessions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AiBudgetPeriods nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AiBudgetPeriods" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AiCallAttempts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AiCallAttempts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AiProcessingPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AiProcessingPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AiProviderAuthorizations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AiProviderAuthorizations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: AiRequests nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."AiRequests" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Attachments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Attachments" TO nexora_tenant_app USING (((("ParentType")::text = 'Lead'::text) AND (EXISTS ( SELECT 1
   FROM public."Leads" lead
  WHERE ((lead."ID" = "Attachments"."ParentID") AND (lead."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))))) WITH CHECK (((("ParentType")::text = 'Lead'::text) AND (EXISTS ( SELECT 1
   FROM public."Leads" lead
  WHERE ((lead."ID" = "Attachments"."ParentID") AND (lead."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))));


--
-- Name: BankAccounts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankAccounts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BankAdjustmentDistributions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankAdjustmentDistributions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BankAdjustments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankAdjustments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BankMatchingRules nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankMatchingRules" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BankStatementImports nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankStatementImports" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BankStatementLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankStatementLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BankStatements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BankStatements" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BoqAssemblies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BoqAssemblies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BoqAssemblyComponents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BoqAssemblyComponents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BoqDocuments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BoqDocuments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BoqItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BoqItems" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BoqSections nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BoqSections" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: BusinessUnits nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."BusinessUnits" TO nexora_tenant_app USING (("ID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("ID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CollectionControls nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CollectionControls" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CommercialCases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CommercialCases" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CommercialFinanceAudits nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CommercialFinanceAudits" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CommercialMatchingPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CommercialMatchingPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Contacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Contacts" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Currency nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Currency" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerAwardLineAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerAwardLineAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerAwards nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerAwards" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerCollectionProfiles nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerCollectionProfiles" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerPayments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerPayments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerPurchaseOrderLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerPurchaseOrderLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerPurchaseOrders nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerPurchaseOrders" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerRefunds nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerRefunds" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerStatementLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerStatementLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: CustomerStatements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."CustomerStatements" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Customers nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Customers" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningCases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningCases" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningDeliveryAttempts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningDeliveryAttempts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningNotices nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningNotices" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningPolicySteps nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningPolicySteps" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningRunDecisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningRunDecisions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: DunningRuns nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."DunningRuns" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: EmailIngests nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."EmailIngests" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Email_Configurations" configuration
  WHERE ((configuration."ID" = "EmailIngests"."EmailConfigurationID") AND (configuration."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Email_Configurations" configuration
  WHERE ((configuration."ID" = "EmailIngests"."EmailConfigurationID") AND (configuration."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: Email_Configurations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Email_Configurations" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ExtractionCorpusEntries nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ExtractionCorpusEntries" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ExtractionJobs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ExtractionJobs" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: FinanceCommunicationContacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."FinanceCommunicationContacts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: FinanceOutboxMessages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."FinanceOutboxMessages" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: FolderIngestionRetryStates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."FolderIngestionRetryStates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: FxRateSnapshots nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."FxRateSnapshots" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: FxRates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."FxRates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: IamAuditEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."IamAuditEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Inventory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Inventory" TO nexora_tenant_app USING ((("Buid" IS NULL) OR ("Buid" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))) WITH CHECK (("Buid" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: JournalEntries nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."JournalEntries" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: JournalEntryLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."JournalEntryLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadIdentityAuditEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadIdentityAuditEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadIngestionBatches nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadIngestionBatches" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadIngestionOccurrences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadIngestionOccurrences" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadItemRevisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadItemRevisions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Leads" parent
  WHERE ((parent."ID" = "LeadItems"."LeadID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Leads" parent
  WHERE ((parent."ID" = "LeadItems"."LeadID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: LeadMatchCandidates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadMatchCandidates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadOccurrenceDocuments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadOccurrenceDocuments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadReferenceConfigurations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadReferenceConfigurations" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadReviewAudits nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadReviewAudits" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadRevisionDifferences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadRevisionDifferences" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadRevisionImpacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadRevisionImpacts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadRevisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadRevisions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LeadStatusHistories nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LeadStatusHistories" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Leads nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Leads" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LedgerAccounts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LedgerAccounts" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LedgerBooks nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LedgerBooks" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: LegalDocumentCounters nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."LegalDocumentCounters" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: MasterDataChangeEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."MasterDataChangeEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: MasterDataFieldChanges nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."MasterDataFieldChanges" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: MetricEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."MetricEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: OrderItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."OrderItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Orders" parent
  WHERE ((parent."ID" = "OrderItems"."OrderID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Orders" parent
  WHERE ((parent."ID" = "OrderItems"."OrderID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: OrderToCashAuditEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."OrderToCashAuditEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: OrderToCashDocumentCounters nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."OrderToCashDocumentCounters" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Orders nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Orders" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: PaymentAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."PaymentAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ProductAttachments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ProductAttachments" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Products" product
  WHERE ((product."ID" = "ProductAttachments"."InventoryID") AND ((product."BUID" IS NULL) OR (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Products" product
  WHERE ((product."ID" = "ProductAttachments"."InventoryID") AND (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: ProductCategories nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ProductCategories" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ProductSubCategories nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ProductSubCategories" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Products nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Products" TO nexora_tenant_app USING ((("BUID" IS NULL) OR ("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: PromisesToPay nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."PromisesToPay" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: QuoteConfiguration nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."QuoteConfiguration" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: QuoteItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."QuoteItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Quotes" parent
  WHERE ((parent."ID" = "QuoteItems"."QuoteID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Quotes" parent
  WHERE ((parent."ID" = "QuoteItems"."QuoteID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: QuotePriceAttestationLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."QuotePriceAttestationLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: QuotePriceAttestations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."QuotePriceAttestations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: QuoteRemovalRecords nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."QuoteRemovalRecords" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: QuoteValidityExtensions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."QuoteValidityExtensions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Quotes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Quotes" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: RFQ nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."RFQ" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: RFQItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."RFQItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."RFQ" parent
  WHERE ((parent."ID" = "RFQItems"."RFQID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."RFQ" parent
  WHERE ((parent."ID" = "RFQItems"."RFQID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: ReceivableDocumentLines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReceivableDocumentLines" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReceivableDocuments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReceivableDocuments" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReceivableWriteOffs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReceivableWriteOffs" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReconciliationAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReconciliationAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReconciliationMatches nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReconciliationMatches" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReconciliationRunRules nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReconciliationRunRules" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReconciliationRuns nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReconciliationRuns" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ReportSubscriptions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ReportSubscriptions" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: RolePermissions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."RolePermissions" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SetCity nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SetCity" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SetCountry nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SetCountry" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SetState nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SetState" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Setup_Master nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Setup_Master" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ShipmentItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ShipmentItems" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Shipments" parent
  WHERE ((parent."ID" = "ShipmentItems"."ShipmentID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Shipments" parent
  WHERE ((parent."ID" = "ShipmentItems"."ShipmentID") AND (parent."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: ShipmentStatusHistory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."ShipmentStatusHistory" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Shipments" shipment
  WHERE ((shipment."ID" = "ShipmentStatusHistory"."ShipmentId") AND (shipment."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Shipments" shipment
  WHERE ((shipment."ID" = "ShipmentStatusHistory"."ShipmentId") AND (shipment."BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: Shipments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Shipments" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SlaEvents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SlaEvents" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SlaPolicies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SlaPolicies" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SourcingAwards nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SourcingAwards" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SupplierPurchaseHistory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SupplierPurchaseHistory" TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public."Products" product,
    public."Suppliers" supplier
  WHERE ((product."ID" = "SupplierPurchaseHistory"."ProductId") AND (supplier."ID" = "SupplierPurchaseHistory"."SupplierId") AND ((product."BUID" IS NULL) OR (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) AND ((supplier."BUID" IS NULL) OR (supplier."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."Products" product,
    public."Suppliers" supplier
  WHERE ((product."ID" = "SupplierPurchaseHistory"."ProductId") AND (supplier."ID" = "SupplierPurchaseHistory"."SupplierId") AND ((product."BUID" IS NULL) OR (product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) AND ((supplier."BUID" IS NULL) OR (supplier."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) AND ((product."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint) OR (supplier."BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))));


--
-- Name: SupplierQuotedItems nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SupplierQuotedItems" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: SupplierSolicitations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."SupplierSolicitations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Suppliers nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Suppliers" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Taxes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Taxes" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Teams nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Teams" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: TenantQueueStates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."TenantQueueStates" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: UserColumnPreferences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."UserColumnPreferences" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: UserGroups nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."UserGroups" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Users nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Users" TO nexora_tenant_app USING (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BUID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: Warehouses nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."Warehouses" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: WriteOffAllocations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."WriteOffAllocations" TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: canonical_inquiries nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.canonical_inquiries TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: canonical_line_items nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.canonical_line_items TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_activities nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_activities TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_demand_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_demand_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_document_classifications nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_document_classifications TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_exception_cases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_cases TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_exception_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_exception_operations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_operations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_exception_outbox nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_outbox TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_lifecycle_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_lifecycle_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_opportunity_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_opportunity_feedback nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_feedback TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_opportunity_operations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_operations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_opportunity_outbox nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_outbox TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_opportunity_outcomes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_outcomes TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: commercial_opportunity_recommendations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_recommendations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: custom_field_definitions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_definitions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: custom_field_dependencies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_dependencies TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_dependencies."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK (((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_dependencies."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))) AND (EXISTS ( SELECT 1
   FROM public.custom_field_definitions dependency
  WHERE ((dependency."Id" = custom_field_dependencies."DependsOnDefinitionId") AND (dependency."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))));


--
-- Name: custom_field_options nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_options TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_options."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_options."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: custom_field_records nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_records TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: custom_field_rules nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_rules TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_rules."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.custom_field_versions version
     JOIN public.custom_field_definitions definition ON ((definition."Id" = version."DefinitionId")))
  WHERE ((version."Id" = custom_field_rules."VersionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: custom_field_values nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_values TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: custom_field_versions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.custom_field_versions TO nexora_tenant_app USING ((EXISTS ( SELECT 1
   FROM public.custom_field_definitions definition
  WHERE ((definition."Id" = custom_field_versions."DefinitionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.custom_field_definitions definition
  WHERE ((definition."Id" = custom_field_versions."DefinitionId") AND (definition."BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)))));


--
-- Name: customer_identifiers nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.customer_identifiers TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: customer_ownerships nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.customer_ownerships TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: customer_quote_sourcing_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.customer_quote_sourcing_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: delivery_proof_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.delivery_proof_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: delivery_proofs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.delivery_proofs TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: delivery_shortfall_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.delivery_shortfall_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: document_corpora nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.document_corpora TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: document_pages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.document_pages TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: document_regions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.document_regions TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: evidence_retention_policies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.evidence_retention_policies TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: extraction_dead_letter_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.extraction_dead_letter_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: extraction_runs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.extraction_runs TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: field_evidence nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.field_evidence TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: follow_up_tasks nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.follow_up_tasks TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: follow_up_transition_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.follow_up_transition_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: goods_receipt_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.goods_receipt_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: goods_receipts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.goods_receipts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: governed_artifact_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.governed_artifact_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: governed_artifact_versions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.governed_artifact_versions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: governed_artifacts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.governed_artifacts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: human_action_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.human_action_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: human_action_items nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.human_action_items TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: inbound_logistics_policies nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.inbound_logistics_policies TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: incoming_inventory nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.incoming_inventory TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: inventory_movements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.inventory_movements TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: inventory_reorder_alerts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.inventory_reorder_alerts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: lead_assignments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.lead_assignments TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: lead_customer_match_candidates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.lead_customer_match_candidates TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: lead_line_commercial_resolutions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.lead_line_commercial_resolutions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: lead_routing_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.lead_routing_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: learning_governance_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.learning_governance_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: lifecycle_outbox_messages nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.lifecycle_outbox_messages TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: material_lot_certificates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.material_lot_certificates TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: material_lot_consumptions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.material_lot_consumptions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: material_lots nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.material_lots TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: ports_of_entry nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.ports_of_entry TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: procurement_callback_receipts nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.procurement_callback_receipts TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: procurement_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.procurement_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: procurement_handoffs nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.procurement_handoffs TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: procurement_outbox nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.procurement_outbox TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: product_aliases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.product_aliases TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: product_supersessions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.product_supersessions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: quote_delivery_requests nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.quote_delivery_requests TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: sales_coaching_acknowledgements nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.sales_coaching_acknowledgements TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: sales_contributions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.sales_contributions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: sales_rep_profiles nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.sales_rep_profiles TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: sales_team_memberships nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.sales_team_memberships TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: setUOM nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public."setUOM" TO nexora_tenant_app USING (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitID" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: source_document_occurrences nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.source_document_occurrences TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: source_documents nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.source_documents TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: sourcing_case_candidates nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.sourcing_case_candidates TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: sourcing_cases nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.sourcing_cases TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: stock_reservations nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.stock_reservations TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_negotiation_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_negotiation_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_purchase_order_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_purchase_order_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_purchase_orders nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_purchase_orders TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_quote_field_evidence nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_field_evidence TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_quote_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_quote_review_decisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_review_decisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_quote_revisions nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_quote_revisions TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_quotes nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_quotes TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_shipment_lines nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_shipment_lines TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: supplier_shipments nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.supplier_shipments TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: tenant_governance_audit_events nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.tenant_governance_audit_events TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: unassigned_work_items nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.unassigned_work_items TO nexora_tenant_app USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


--
-- Name: validation_findings nexora_tenant_isolation; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY nexora_tenant_isolation ON public.validation_findings TO nexora_tenant_app USING ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint)) WITH CHECK ((business_unit_id = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));


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

-- ==========================================================================
-- Foreign keys (incl. NOT VALID)
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
-- ==========================================================================

--
-- Name: AccountingOutbox FK_AccountingOutbox_SubscriptionInvoices_TenantId_Subscription~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."AccountingOutbox"
    ADD CONSTRAINT "FK_AccountingOutbox_SubscriptionInvoices_TenantId_Subscription~" FOREIGN KEY ("TenantId", "SubscriptionInvoiceId") REFERENCES platform."SubscriptionInvoices"("TenantId", "Id") ON DELETE RESTRICT;


--
-- Name: AccountingOutbox FK_AccountingOutbox_SubscriptionRevenueActions_TenantId_Subscr~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."AccountingOutbox"
    ADD CONSTRAINT "FK_AccountingOutbox_SubscriptionRevenueActions_TenantId_Subscr~" FOREIGN KEY ("TenantId", "SubscriptionInvoiceId", "SubscriptionRevenueActionId") REFERENCES platform."SubscriptionRevenueActions"("TenantId", "SubscriptionInvoiceId", "Id") ON DELETE RESTRICT;


--
-- Name: BillingStatementLines FK_BillingStatementLines_BillingStatements_BillingStatementId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."BillingStatementLines"
    ADD CONSTRAINT "FK_BillingStatementLines_BillingStatements_BillingStatementId" FOREIGN KEY ("BillingStatementId") REFERENCES platform."BillingStatements"("Id") ON DELETE CASCADE;


--
-- Name: BillingStatements FK_BillingStatements_RateCards_RateCardId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."BillingStatements"
    ADD CONSTRAINT "FK_BillingStatements_RateCards_RateCardId" FOREIGN KEY ("RateCardId") REFERENCES platform."RateCards"("Id") ON DELETE RESTRICT;


--
-- Name: BillingStatements FK_BillingStatements_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."BillingStatements"
    ADD CONSTRAINT "FK_BillingStatements_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: PlatformBrowserTrusts FK_PlatformBrowserTrusts_PlatformUsers_PlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformBrowserTrusts"
    ADD CONSTRAINT "FK_PlatformBrowserTrusts_PlatformUsers_PlatformUserId" FOREIGN KEY ("PlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: PlatformMfaChallenges FK_PlatformMfaChallenges_PlatformUsers_PlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaChallenges"
    ADD CONSTRAINT "FK_PlatformMfaChallenges_PlatformUsers_PlatformUserId" FOREIGN KEY ("PlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: PlatformMfaCredentials FK_PlatformMfaCredentials_PlatformUsers_PlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaCredentials"
    ADD CONSTRAINT "FK_PlatformMfaCredentials_PlatformUsers_PlatformUserId" FOREIGN KEY ("PlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: PlatformMfaRecoveryCodes FK_PlatformMfaRecoveryCodes_PlatformUsers_PlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaRecoveryCodes"
    ADD CONSTRAINT "FK_PlatformMfaRecoveryCodes_PlatformUsers_PlatformUserId" FOREIGN KEY ("PlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: PlatformSessions FK_PlatformSessions_PlatformUsers_PlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformSessions"
    ADD CONSTRAINT "FK_PlatformSessions_PlatformUsers_PlatformUserId" FOREIGN KEY ("PlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: ProvisioningSteps FK_ProvisioningSteps_ProvisioningExecutions_ExecutionId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."ProvisioningSteps"
    ADD CONSTRAINT "FK_ProvisioningSteps_ProvisioningExecutions_ExecutionId" FOREIGN KEY ("ExecutionId") REFERENCES platform."ProvisioningExecutions"("Id") ON DELETE CASCADE;


--
-- Name: RateCardLines FK_RateCardLines_RateCards_RateCardId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."RateCardLines"
    ADD CONSTRAINT "FK_RateCardLines_RateCards_RateCardId" FOREIGN KEY ("RateCardId") REFERENCES platform."RateCards"("Id") ON DELETE CASCADE;


--
-- Name: SubscriptionCreditNotes FK_SubscriptionCreditNotes_SubscriptionInvoices_SubscriptionIn~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionCreditNotes"
    ADD CONSTRAINT "FK_SubscriptionCreditNotes_SubscriptionInvoices_SubscriptionIn~" FOREIGN KEY ("SubscriptionInvoiceId") REFERENCES platform."SubscriptionInvoices"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionInvoices FK_SubscriptionInvoices_BillingStatements_BillingStatementId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "FK_SubscriptionInvoices_BillingStatements_BillingStatementId" FOREIGN KEY ("BillingStatementId") REFERENCES platform."BillingStatements"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionInvoices FK_SubscriptionInvoices_SubscriptionTaxRules_TaxRuleId_TaxRule~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "FK_SubscriptionInvoices_SubscriptionTaxRules_TaxRuleId_TaxRule~" FOREIGN KEY ("TaxRuleId", "TaxRuleVersion") REFERENCES platform."SubscriptionTaxRules"("Id", "Version") ON DELETE RESTRICT;


--
-- Name: SubscriptionInvoices FK_SubscriptionInvoices_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "FK_SubscriptionInvoices_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionPayments FK_SubscriptionPayments_SubscriptionInvoices_SubscriptionInvoi~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionPayments"
    ADD CONSTRAINT "FK_SubscriptionPayments_SubscriptionInvoices_SubscriptionInvoi~" FOREIGN KEY ("SubscriptionInvoiceId") REFERENCES platform."SubscriptionInvoices"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionRevenueActions FK_SubscriptionRevenueActions_PlatformUsers_ApprovedByPlatform~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "FK_SubscriptionRevenueActions_PlatformUsers_ApprovedByPlatform~" FOREIGN KEY ("ApprovedByPlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionRevenueActions FK_SubscriptionRevenueActions_PlatformUsers_ProposedByPlatform~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "FK_SubscriptionRevenueActions_PlatformUsers_ProposedByPlatform~" FOREIGN KEY ("ProposedByPlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionRevenueActions FK_SubscriptionRevenueActions_SubscriptionInvoices_TenantId_Su~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "FK_SubscriptionRevenueActions_SubscriptionInvoices_TenantId_Su~" FOREIGN KEY ("TenantId", "SubscriptionInvoiceId") REFERENCES platform."SubscriptionInvoices"("TenantId", "Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionTaxRules FK_SubscriptionTaxRules_PlatformUsers_ApprovedByPlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "FK_SubscriptionTaxRules_PlatformUsers_ApprovedByPlatformUserId" FOREIGN KEY ("ApprovedByPlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: SubscriptionTaxRules FK_SubscriptionTaxRules_PlatformUsers_ProposedByPlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "FK_SubscriptionTaxRules_PlatformUsers_ProposedByPlatformUserId" FOREIGN KEY ("ProposedByPlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: SupportTicketLinks FK_SupportTicketLinks_SupportTickets_SupportTicketId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTicketLinks"
    ADD CONSTRAINT "FK_SupportTicketLinks_SupportTickets_SupportTicketId" FOREIGN KEY ("SupportTicketId") REFERENCES platform."SupportTickets"("Id") ON DELETE CASCADE;


--
-- Name: SupportTicketNotes FK_SupportTicketNotes_SupportTickets_SupportTicketId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTicketNotes"
    ADD CONSTRAINT "FK_SupportTicketNotes_SupportTickets_SupportTicketId" FOREIGN KEY ("SupportTicketId") REFERENCES platform."SupportTickets"("Id") ON DELETE CASCADE;


--
-- Name: SupportTickets FK_SupportTickets_PlatformUsers_AssignedToPlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTickets"
    ADD CONSTRAINT "FK_SupportTickets_PlatformUsers_AssignedToPlatformUserId" FOREIGN KEY ("AssignedToPlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: SupportTickets FK_SupportTickets_PlatformUsers_OpenedByPlatformUserId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTickets"
    ADD CONSTRAINT "FK_SupportTickets_PlatformUsers_OpenedByPlatformUserId" FOREIGN KEY ("OpenedByPlatformUserId") REFERENCES platform."PlatformUsers"("Id") ON DELETE RESTRICT;


--
-- Name: SupportTickets FK_SupportTickets_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTickets"
    ADD CONSTRAINT "FK_SupportTickets_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: TenantDataAssets FK_TenantDataAssets_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDataAssets"
    ADD CONSTRAINT "FK_TenantDataAssets_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: TenantDataRecoveryEvidence FK_TenantDataRecoveryEvidence_TenantDataAssets_TenantId_Tenant~; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDataRecoveryEvidence"
    ADD CONSTRAINT "FK_TenantDataRecoveryEvidence_TenantDataAssets_TenantId_Tenant~" FOREIGN KEY ("TenantId", "TenantDataAssetId") REFERENCES platform."TenantDataAssets"("TenantId", "Id") ON DELETE RESTRICT;


--
-- Name: TenantDataRecoveryEvidence FK_TenantDataRecoveryEvidence_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDataRecoveryEvidence"
    ADD CONSTRAINT "FK_TenantDataRecoveryEvidence_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: TenantDeletionCertificates FK_TenantDeletionCertificates_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDeletionCertificates"
    ADD CONSTRAINT "FK_TenantDeletionCertificates_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: TenantMeterSourcePolicies FK_TenantMeterSourcePolicies_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantMeterSourcePolicies"
    ADD CONSTRAINT "FK_TenantMeterSourcePolicies_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: Tenants FK_Tenants_Plans_PlanId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."Tenants"
    ADD CONSTRAINT "FK_Tenants_Plans_PlanId" FOREIGN KEY ("PlanId") REFERENCES platform."Plans"("Id") ON DELETE SET NULL;


--
-- Name: UsageCoverageSegments FK_UsageCoverageSegments_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageCoverageSegments"
    ADD CONSTRAINT "FK_UsageCoverageSegments_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEventRatings FK_UsageEventRatings_Plans_PlanId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEventRatings"
    ADD CONSTRAINT "FK_UsageEventRatings_Plans_PlanId" FOREIGN KEY ("PlanId") REFERENCES platform."Plans"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEventRatings FK_UsageEventRatings_RateCardLines_RateCardLineId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEventRatings"
    ADD CONSTRAINT "FK_UsageEventRatings_RateCardLines_RateCardLineId" FOREIGN KEY ("RateCardLineId") REFERENCES platform."RateCardLines"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEventRatings FK_UsageEventRatings_RateCards_RateCardId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEventRatings"
    ADD CONSTRAINT "FK_UsageEventRatings_RateCards_RateCardId" FOREIGN KEY ("RateCardId") REFERENCES platform."RateCards"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEventRatings FK_UsageEventRatings_UsageEvents_TenantId_UsageEventId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEventRatings"
    ADD CONSTRAINT "FK_UsageEventRatings_UsageEvents_TenantId_UsageEventId" FOREIGN KEY ("TenantId", "UsageEventId") REFERENCES platform."UsageEvents"("TenantId", "UsageEventId") ON DELETE RESTRICT;


--
-- Name: UsageEvents FK_UsageEvents_RateCardLines_RateCardLineId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "FK_UsageEvents_RateCardLines_RateCardLineId" FOREIGN KEY ("RateCardLineId") REFERENCES platform."RateCardLines"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEvents FK_UsageEvents_RateCards_RateCardId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "FK_UsageEvents_RateCards_RateCardId" FOREIGN KEY ("RateCardId") REFERENCES platform."RateCards"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEvents FK_UsageEvents_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "FK_UsageEvents_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: UsageEvents FK_UsageEvents_UsageEvents_TenantId_AdjustsUsageEventId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "FK_UsageEvents_UsageEvents_TenantId_AdjustsUsageEventId" FOREIGN KEY ("TenantId", "AdjustsUsageEventId") REFERENCES platform."UsageEvents"("TenantId", "UsageEventId") ON DELETE RESTRICT;


--
-- Name: UsageMinuteAggregates FK_UsageMinuteAggregates_Tenants_TenantId; Type: FK CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageMinuteAggregates"
    ADD CONSTRAINT "FK_UsageMinuteAggregates_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES platform."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: AgentPolicies FK_AgentPolicies_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentPolicies"
    ADD CONSTRAINT "FK_AgentPolicies_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: AiBudgetPeriods FK_AiBudgetPeriods_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiBudgetPeriods"
    ADD CONSTRAINT "FK_AiBudgetPeriods_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: AiCallAttempts FK_AiCallAttempts_AiRequests_BusinessUnitId_RequestId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiCallAttempts"
    ADD CONSTRAINT "FK_AiCallAttempts_AiRequests_BusinessUnitId_RequestId" FOREIGN KEY ("BusinessUnitId", "RequestId") REFERENCES public."AiRequests"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: AiProcessingPolicies FK_AiProcessingPolicies_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProcessingPolicies"
    ADD CONSTRAINT "FK_AiProcessingPolicies_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: AiProviderAuthorizations FK_AiProviderAuthorizations_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProviderAuthorizations"
    ADD CONSTRAINT "FK_AiProviderAuthorizations_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: AiRequests FK_AiRequests_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "FK_AiRequests_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: AiRequests FK_AiRequests_ExtractionJobs_BusinessUnitId_ExtractionJobId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "FK_AiRequests_ExtractionJobs_BusinessUnitId_ExtractionJobId" FOREIGN KEY ("BusinessUnitId", "ExtractionJobId") REFERENCES public."ExtractionJobs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: AiRequests FK_AiRequests_source_document_occurrences_BusinessUnitId_Sourc~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "FK_AiRequests_source_document_occurrences_BusinessUnitId_Sourc~" FOREIGN KEY ("BusinessUnitId", "SourceDocumentOccurrenceId") REFERENCES public.source_document_occurrences(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: BankAccounts FK_BankAccounts_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "FK_BankAccounts_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: BankAccounts FK_BankAccounts_LedgerAccounts_BusinessUnitId_LedgerAccountId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "FK_BankAccounts_LedgerAccounts_BusinessUnitId_LedgerAccountId" FOREIGN KEY ("BusinessUnitId", "LedgerAccountId") REFERENCES public."LedgerAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustmentDistributions FK_BankAdjustmentDistributions_BankAdjustments_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustmentDistributions"
    ADD CONSTRAINT "FK_BankAdjustmentDistributions_BankAdjustments_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "BankAdjustmentId") REFERENCES public."BankAdjustments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustmentDistributions FK_BankAdjustmentDistributions_LedgerAccounts_BusinessUnitId_L~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustmentDistributions"
    ADD CONSTRAINT "FK_BankAdjustmentDistributions_LedgerAccounts_BusinessUnitId_L~" FOREIGN KEY ("BusinessUnitId", "LedgerAccountId") REFERENCES public."LedgerAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_AccountingPeriods_BusinessUnitId_Accounting~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_AccountingPeriods_BusinessUnitId_Accounting~" FOREIGN KEY ("BusinessUnitId", "AccountingPeriodId") REFERENCES public."AccountingPeriods"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_BankAccounts_BusinessUnitId_BankAccountId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_BankAccounts_BusinessUnitId_BankAccountId" FOREIGN KEY ("BusinessUnitId", "BankAccountId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_BankStatementLines_BusinessUnitId_BankState~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_BankStatementLines_BusinessUnitId_BankState~" FOREIGN KEY ("BusinessUnitId", "BankStatementLineId", "BankAccountId") REFERENCES public."BankStatementLines"("BusinessUnitId", "Id", "BankAccountId") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_JournalEntries_BusinessUnitId_JournalEntryId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_JournalEntries_BusinessUnitId_JournalEntryId" FOREIGN KEY ("BusinessUnitId", "JournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_JournalEntries_BusinessUnitId_ReversalJourn~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_JournalEntries_BusinessUnitId_ReversalJourn~" FOREIGN KEY ("BusinessUnitId", "ReversalJournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_JournalEntryLines_BusinessUnitId_BankJourna~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_JournalEntryLines_BusinessUnitId_BankJourna~" FOREIGN KEY ("BusinessUnitId", "BankJournalEntryLineId") REFERENCES public."JournalEntryLines"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankAdjustments FK_BankAdjustments_JournalEntryLines_BusinessUnitId_ReversalBa~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "FK_BankAdjustments_JournalEntryLines_BusinessUnitId_ReversalBa~" FOREIGN KEY ("BusinessUnitId", "ReversalBankJournalEntryLineId") REFERENCES public."JournalEntryLines"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankMatchingRules FK_BankMatchingRules_BankAccounts_BusinessUnitId_BankAccountId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankMatchingRules"
    ADD CONSTRAINT "FK_BankMatchingRules_BankAccounts_BusinessUnitId_BankAccountId" FOREIGN KEY ("BusinessUnitId", "BankAccountId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankMatchingRules FK_BankMatchingRules_BankMatchingRules_BusinessUnitId_Supersed~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankMatchingRules"
    ADD CONSTRAINT "FK_BankMatchingRules_BankMatchingRules_BusinessUnitId_Supersed~" FOREIGN KEY ("BusinessUnitId", "SupersedesRuleId") REFERENCES public."BankMatchingRules"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankStatementImports FK_BankStatementImports_BankAccounts_BusinessUnitId_BankAccoun~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "FK_BankStatementImports_BankAccounts_BusinessUnitId_BankAccoun~" FOREIGN KEY ("BusinessUnitId", "BankAccountId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: BankStatementLines FK_BankStatementLines_BankStatements_BusinessUnitId_BankStatem~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "FK_BankStatementLines_BankStatements_BusinessUnitId_BankStatem~" FOREIGN KEY ("BusinessUnitId", "BankStatementId", "BankAccountId") REFERENCES public."BankStatements"("BusinessUnitId", "Id", "BankAccountId") ON DELETE RESTRICT;


--
-- Name: BankStatements FK_BankStatements_BankAccounts_BusinessUnitId_BankAccountId_Cu~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "FK_BankStatements_BankAccounts_BusinessUnitId_BankAccountId_Cu~" FOREIGN KEY ("BusinessUnitId", "BankAccountId", "CurrencyId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id", "CurrencyId") ON DELETE RESTRICT;


--
-- Name: BankStatements FK_BankStatements_BankStatementImports_BusinessUnitId_BankStat~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "FK_BankStatements_BankStatementImports_BusinessUnitId_BankStat~" FOREIGN KEY ("BusinessUnitId", "BankStatementImportId", "BankAccountId") REFERENCES public."BankStatementImports"("BusinessUnitId", "Id", "BankAccountId") ON DELETE RESTRICT;


--
-- Name: BankStatements FK_BankStatements_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "FK_BankStatements_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: BoqAssemblyComponents FK_BoqAssemblyComponents_BoqAssemblies_BoqAssemblyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqAssemblyComponents"
    ADD CONSTRAINT "FK_BoqAssemblyComponents_BoqAssemblies_BoqAssemblyId" FOREIGN KEY ("BoqAssemblyId") REFERENCES public."BoqAssemblies"("Id") ON DELETE CASCADE;


--
-- Name: BoqItems FK_BoqItems_BoqSections_BoqSectionId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqItems"
    ADD CONSTRAINT "FK_BoqItems_BoqSections_BoqSectionId" FOREIGN KEY ("BoqSectionId") REFERENCES public."BoqSections"("Id") ON DELETE CASCADE;


--
-- Name: BoqSections FK_BoqSections_BoqDocuments_BoqDocumentId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqSections"
    ADD CONSTRAINT "FK_BoqSections_BoqDocuments_BoqDocumentId" FOREIGN KEY ("BoqDocumentId") REFERENCES public."BoqDocuments"("Id") ON DELETE CASCADE;


--
-- Name: SetCity FK_City_BusinessUnit; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "FK_City_BusinessUnit" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: SetCity FK_City_Country; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "FK_City_Country" FOREIGN KEY ("CountryID") REFERENCES public."SetCountry"("CountryID");


--
-- Name: SetCity FK_City_State; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "FK_City_State" FOREIGN KEY ("StateID") REFERENCES public."SetState"("StateID") ON DELETE CASCADE;


--
-- Name: CollectionControls FK_CollectionControls_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "FK_CollectionControls_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: CollectionControls FK_CollectionControls_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "FK_CollectionControls_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: CollectionControls FK_CollectionControls_ReceivableDocuments_BusinessUnitId_Recei~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "FK_CollectionControls_ReceivableDocuments_BusinessUnitId_Recei~" FOREIGN KEY ("BusinessUnitId", "ReceivableDocumentId") REFERENCES public."ReceivableDocuments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CommercialCases FK_CommercialCases_BusinessUnits_BusinessUnitID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "FK_CommercialCases_BusinessUnits_BusinessUnitID" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: Contacts FK_Contacts_Suppliers_SupplierID_BusinessUnitID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Contacts"
    ADD CONSTRAINT "FK_Contacts_Suppliers_SupplierID_BusinessUnitID" FOREIGN KEY ("SupplierID", "BusinessUnitID") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: SetCountry FK_Country_BusinessUnit; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCountry"
    ADD CONSTRAINT "FK_Country_BusinessUnit" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID") ON DELETE CASCADE;


--
-- Name: CustomerAwardLineAllocations FK_CustomerAwardLineAllocations_CustomerAwards_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwardLineAllocations"
    ADD CONSTRAINT "FK_CustomerAwardLineAllocations_CustomerAwards_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "CustomerAwardId") REFERENCES public."CustomerAwards"("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: CustomerAwardLineAllocations FK_CustomerAwardLineAllocations_CustomerPurchaseOrderLines_Bus~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwardLineAllocations"
    ADD CONSTRAINT "FK_CustomerAwardLineAllocations_CustomerPurchaseOrderLines_Bus~" FOREIGN KEY ("BusinessUnitId", "CustomerPurchaseOrderLineId") REFERENCES public."CustomerPurchaseOrderLines"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerAwardLineAllocations FK_CustomerAwardLineAllocations_QuoteItems_QuoteItemId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwardLineAllocations"
    ADD CONSTRAINT "FK_CustomerAwardLineAllocations_QuoteItems_QuoteItemId" FOREIGN KEY ("QuoteItemId") REFERENCES public."QuoteItems"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerAwards FK_CustomerAwards_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "FK_CustomerAwards_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerAwards FK_CustomerAwards_CommercialCases_BusinessUnitId_CommercialCas~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "FK_CustomerAwards_CommercialCases_BusinessUnitId_CommercialCas~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerAwards FK_CustomerAwards_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "FK_CustomerAwards_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: CustomerAwards FK_CustomerAwards_CustomerPurchaseOrders_BusinessUnitId_Custom~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "FK_CustomerAwards_CustomerPurchaseOrders_BusinessUnitId_Custom~" FOREIGN KEY ("BusinessUnitId", "CustomerPurchaseOrderId") REFERENCES public."CustomerPurchaseOrders"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerAwards FK_CustomerAwards_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "FK_CustomerAwards_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerAwards FK_CustomerAwards_Quotes_BusinessUnitId_QuoteId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "FK_CustomerAwards_Quotes_BusinessUnitId_QuoteId" FOREIGN KEY ("BusinessUnitId", "QuoteId") REFERENCES public."Quotes"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: CustomerCollectionProfiles FK_CustomerCollectionProfiles_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "FK_CustomerCollectionProfiles_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerCollectionProfiles FK_CustomerCollectionProfiles_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "FK_CustomerCollectionProfiles_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerCollectionProfiles FK_CustomerCollectionProfiles_DunningPolicies_BusinessUnitId_D~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "FK_CustomerCollectionProfiles_DunningPolicies_BusinessUnitId_D~" FOREIGN KEY ("BusinessUnitId", "DunningPolicyId") REFERENCES public."DunningPolicies"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerCollectionProfiles FK_CustomerCollectionProfiles_FinanceCommunicationContacts_Bus~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "FK_CustomerCollectionProfiles_FinanceCommunicationContacts_Bus~" FOREIGN KEY ("BusinessUnitId", "FinanceCommunicationContactId") REFERENCES public."FinanceCommunicationContacts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerPayments FK_CustomerPayments_BankAccounts_BusinessUnitId_BankAccountId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "FK_CustomerPayments_BankAccounts_BusinessUnitId_BankAccountId" FOREIGN KEY ("BusinessUnitId", "BankAccountId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerPayments FK_CustomerPayments_CommercialCases_BusinessUnitId_CommercialC~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "FK_CustomerPayments_CommercialCases_BusinessUnitId_CommercialC~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerPayments FK_CustomerPayments_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "FK_CustomerPayments_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerPayments FK_CustomerPayments_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "FK_CustomerPayments_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerPayments FK_CustomerPayments_JournalEntries_BusinessUnitId_JournalEntry~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "FK_CustomerPayments_JournalEntries_BusinessUnitId_JournalEntry~" FOREIGN KEY ("BusinessUnitId", "JournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerPayments FK_CustomerPayments_JournalEntries_BusinessUnitId_ReversalJour~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "FK_CustomerPayments_JournalEntries_BusinessUnitId_ReversalJour~" FOREIGN KEY ("BusinessUnitId", "ReversalJournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerPurchaseOrderLines FK_CustomerPurchaseOrderLines_CustomerPurchaseOrders_BusinessU~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "FK_CustomerPurchaseOrderLines_CustomerPurchaseOrders_BusinessU~" FOREIGN KEY ("BusinessUnitId", "CustomerPurchaseOrderId") REFERENCES public."CustomerPurchaseOrders"("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: CustomerPurchaseOrderLines FK_CustomerPurchaseOrderLines_Products_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "FK_CustomerPurchaseOrderLines_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES public."Products"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerPurchaseOrderLines FK_CustomerPurchaseOrderLines_setUOM_BusinessUnitId_UomId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "FK_CustomerPurchaseOrderLines_setUOM_BusinessUnitId_UomId" FOREIGN KEY ("BusinessUnitId", "UomId") REFERENCES public."setUOM"("BusinessUnitID", "UomID") ON DELETE RESTRICT;


--
-- Name: CustomerPurchaseOrders FK_CustomerPurchaseOrders_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "FK_CustomerPurchaseOrders_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerPurchaseOrders FK_CustomerPurchaseOrders_CommercialCases_BusinessUnitId_Comme~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "FK_CustomerPurchaseOrders_CommercialCases_BusinessUnitId_Comme~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerPurchaseOrders FK_CustomerPurchaseOrders_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "FK_CustomerPurchaseOrders_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: CustomerPurchaseOrders FK_CustomerPurchaseOrders_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "FK_CustomerPurchaseOrders_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerRefunds FK_CustomerRefunds_BankAccounts_BusinessUnitId_BankAccountId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "FK_CustomerRefunds_BankAccounts_BusinessUnitId_BankAccountId" FOREIGN KEY ("BusinessUnitId", "BankAccountId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerRefunds FK_CustomerRefunds_CommercialCases_BusinessUnitId_CommercialCa~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "FK_CustomerRefunds_CommercialCases_BusinessUnitId_CommercialCa~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerRefunds FK_CustomerRefunds_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "FK_CustomerRefunds_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerRefunds FK_CustomerRefunds_CustomerPayments_BusinessUnitId_SourcePayme~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "FK_CustomerRefunds_CustomerPayments_BusinessUnitId_SourcePayme~" FOREIGN KEY ("BusinessUnitId", "SourcePaymentId") REFERENCES public."CustomerPayments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerRefunds FK_CustomerRefunds_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "FK_CustomerRefunds_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerRefunds FK_CustomerRefunds_JournalEntries_BusinessUnitId_JournalEntryId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "FK_CustomerRefunds_JournalEntries_BusinessUnitId_JournalEntryId" FOREIGN KEY ("BusinessUnitId", "JournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerStatementLines FK_CustomerStatementLines_CustomerStatements_BusinessUnitId_Cu~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatementLines"
    ADD CONSTRAINT "FK_CustomerStatementLines_CustomerStatements_BusinessUnitId_Cu~" FOREIGN KEY ("BusinessUnitId", "CustomerStatementId") REFERENCES public."CustomerStatements"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerStatements FK_CustomerStatements_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "FK_CustomerStatements_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: CustomerStatements FK_CustomerStatements_CustomerStatements_BusinessUnitId_Supers~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "FK_CustomerStatements_CustomerStatements_BusinessUnitId_Supers~" FOREIGN KEY ("BusinessUnitId", "SupersedesStatementId") REFERENCES public."CustomerStatements"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: CustomerStatements FK_CustomerStatements_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "FK_CustomerStatements_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: Customers FK_Customers_AccountTeam; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "FK_Customers_AccountTeam" FOREIGN KEY ("AccountTeamId") REFERENCES public."Teams"("ID") ON DELETE RESTRICT;


--
-- Name: Customers FK_Customers_RegionState; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "FK_Customers_RegionState" FOREIGN KEY ("RegionStateId") REFERENCES public."SetState"("StateID") ON DELETE RESTRICT;


--
-- Name: DunningCases FK_DunningCases_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "FK_DunningCases_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: DunningCases FK_DunningCases_CustomerStatements_BusinessUnitId_CustomerStat~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "FK_DunningCases_CustomerStatements_BusinessUnitId_CustomerStat~" FOREIGN KEY ("BusinessUnitId", "CustomerStatementId") REFERENCES public."CustomerStatements"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningCases FK_DunningCases_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "FK_DunningCases_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: DunningCases FK_DunningCases_DunningPolicies_BusinessUnitId_DunningPolicyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "FK_DunningCases_DunningPolicies_BusinessUnitId_DunningPolicyId" FOREIGN KEY ("BusinessUnitId", "DunningPolicyId") REFERENCES public."DunningPolicies"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningDeliveryAttempts FK_DunningDeliveryAttempts_DunningNotices_BusinessUnitId_Dunni~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningDeliveryAttempts"
    ADD CONSTRAINT "FK_DunningDeliveryAttempts_DunningNotices_BusinessUnitId_Dunni~" FOREIGN KEY ("BusinessUnitId", "DunningNoticeId") REFERENCES public."DunningNotices"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningNotices FK_DunningNotices_CustomerStatements_BusinessUnitId_CustomerSt~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "FK_DunningNotices_CustomerStatements_BusinessUnitId_CustomerSt~" FOREIGN KEY ("BusinessUnitId", "CustomerStatementId") REFERENCES public."CustomerStatements"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningNotices FK_DunningNotices_DunningCases_BusinessUnitId_DunningCaseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "FK_DunningNotices_DunningCases_BusinessUnitId_DunningCaseId" FOREIGN KEY ("BusinessUnitId", "DunningCaseId") REFERENCES public."DunningCases"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningNotices FK_DunningNotices_FinanceCommunicationContacts_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "FK_DunningNotices_FinanceCommunicationContacts_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "FinanceCommunicationContactId") REFERENCES public."FinanceCommunicationContacts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningPolicySteps FK_DunningPolicySteps_DunningPolicies_BusinessUnitId_DunningPo~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningPolicySteps"
    ADD CONSTRAINT "FK_DunningPolicySteps_DunningPolicies_BusinessUnitId_DunningPo~" FOREIGN KEY ("BusinessUnitId", "DunningPolicyId") REFERENCES public."DunningPolicies"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_CustomerCollectionProfiles_BusinessUnit~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_CustomerCollectionProfiles_BusinessUnit~" FOREIGN KEY ("BusinessUnitId", "CustomerCollectionProfileId") REFERENCES public."CustomerCollectionProfiles"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_CustomerStatements_BusinessUnitId_Custom; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_CustomerStatements_BusinessUnitId_Custom" FOREIGN KEY ("BusinessUnitId", "CustomerStatementId") REFERENCES public."CustomerStatements"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_DunningCases_BusinessUnitId_DunningCaseI; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_DunningCases_BusinessUnitId_DunningCaseI" FOREIGN KEY ("BusinessUnitId", "DunningCaseId") REFERENCES public."DunningCases"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_DunningNotices_BusinessUnitId_DunningNot; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_DunningNotices_BusinessUnitId_DunningNot" FOREIGN KEY ("BusinessUnitId", "DunningNoticeId") REFERENCES public."DunningNotices"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningRunDecisions FK_DunningRunDecisions_DunningRuns_BusinessUnitId_DunningRunId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "FK_DunningRunDecisions_DunningRuns_BusinessUnitId_DunningRunId" FOREIGN KEY ("BusinessUnitId", "DunningRunId") REFERENCES public."DunningRuns"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: DunningRuns FK_DunningRuns_DunningPolicies_BusinessUnitId_DunningPolicyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRuns"
    ADD CONSTRAINT "FK_DunningRuns_DunningPolicies_BusinessUnitId_DunningPolicyId" FOREIGN KEY ("BusinessUnitId", "DunningPolicyId") REFERENCES public."DunningPolicies"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ExtractionCorpusEntries FK_ExtractionCorpusEntries_LeadReviewAudits_LeadReviewAuditId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ExtractionCorpusEntries"
    ADD CONSTRAINT "FK_ExtractionCorpusEntries_LeadReviewAudits_LeadReviewAuditId" FOREIGN KEY ("LeadReviewAuditId") REFERENCES public."LeadReviewAudits"("Id") ON DELETE CASCADE;


--
-- Name: ExtractionJobs FK_ExtractionJobs_source_document_occurrences_BusinessUnitId_S~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ExtractionJobs"
    ADD CONSTRAINT "FK_ExtractionJobs_source_document_occurrences_BusinessUnitId_S~" FOREIGN KEY ("BusinessUnitId", "SourceDocumentOccurrenceId") REFERENCES public.source_document_occurrences(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: FinanceCommunicationContacts FK_FinanceCommunicationContacts_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FinanceCommunicationContacts"
    ADD CONSTRAINT "FK_FinanceCommunicationContacts_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: FinanceOutboxMessages FK_FinanceOutboxMessages_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FinanceOutboxMessages"
    ADD CONSTRAINT "FK_FinanceOutboxMessages_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: IamAuditEvents FK_IamAuditEvents_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."IamAuditEvents"
    ADD CONSTRAINT "FK_IamAuditEvents_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: Inventory FK_Inventory_Products_Buid_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Inventory"
    ADD CONSTRAINT "FK_Inventory_Products_Buid_ProductId" FOREIGN KEY ("Buid", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: JournalEntries FK_JournalEntries_AccountingPeriods_BusinessUnitId_AccountingP~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "FK_JournalEntries_AccountingPeriods_BusinessUnitId_AccountingP~" FOREIGN KEY ("BusinessUnitId", "AccountingPeriodId") REFERENCES public."AccountingPeriods"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: JournalEntries FK_JournalEntries_Currency_FunctionalCurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "FK_JournalEntries_Currency_FunctionalCurrencyId" FOREIGN KEY ("FunctionalCurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: JournalEntries FK_JournalEntries_Currency_Tenant; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "FK_JournalEntries_Currency_Tenant" FOREIGN KEY ("BusinessUnitId", "FunctionalCurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: JournalEntries FK_JournalEntries_JournalEntries_BusinessUnitId_ReversesJourna~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "FK_JournalEntries_JournalEntries_BusinessUnitId_ReversesJourna~" FOREIGN KEY ("BusinessUnitId", "ReversesJournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: JournalEntryLines FK_JournalEntryLines_Currency_Tenant; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "FK_JournalEntryLines_Currency_Tenant" FOREIGN KEY ("BusinessUnitId", "TransactionCurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: JournalEntryLines FK_JournalEntryLines_Currency_TransactionCurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "FK_JournalEntryLines_Currency_TransactionCurrencyId" FOREIGN KEY ("TransactionCurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: JournalEntryLines FK_JournalEntryLines_JournalEntries_BusinessUnitId_JournalEntr~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "FK_JournalEntryLines_JournalEntries_BusinessUnitId_JournalEntr~" FOREIGN KEY ("BusinessUnitId", "JournalEntryId") REFERENCES public."JournalEntries"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: JournalEntryLines FK_JournalEntryLines_LedgerAccounts_BusinessUnitId_LedgerAccou~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "FK_JournalEntryLines_LedgerAccounts_BusinessUnitId_LedgerAccou~" FOREIGN KEY ("BusinessUnitId", "LedgerAccountId") REFERENCES public."LedgerAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadIngestionOccurrences FK_LeadIngestionOccurrences_ExtractionJobs_BusinessUnitId_Extr~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "FK_LeadIngestionOccurrences_ExtractionJobs_BusinessUnitId_Extr~" FOREIGN KEY ("BusinessUnitId", "ExtractionJobId") REFERENCES public."ExtractionJobs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadIngestionOccurrences FK_LeadIngestionOccurrences_LeadIngestionBatches_BusinessUnitI~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "FK_LeadIngestionOccurrences_LeadIngestionBatches_BusinessUnitI~" FOREIGN KEY ("BusinessUnitId", "BatchId") REFERENCES public."LeadIngestionBatches"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadIngestionOccurrences FK_LeadIngestionOccurrences_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "FK_LeadIngestionOccurrences_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LeadIngestionOccurrences FK_LeadIngestionOccurrences_source_document_occurrences_Busine~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "FK_LeadIngestionOccurrences_source_document_occurrences_Busine~" FOREIGN KEY ("BusinessUnitId", "SourceDocumentOccurrenceId") REFERENCES public.source_document_occurrences(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: LeadIngestionOccurrences FK_LeadIngestionOccurrences_source_documents_BusinessUnitId_So~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "FK_LeadIngestionOccurrences_source_documents_BusinessUnitId_So~" FOREIGN KEY ("BusinessUnitId", "SourceDocumentId") REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: LeadItemRevisions FK_LeadItemRevisions_LeadRevisions_BusinessUnitId_LeadRevision~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadItemRevisions"
    ADD CONSTRAINT "FK_LeadItemRevisions_LeadRevisions_BusinessUnitId_LeadRevision~" FOREIGN KEY ("BusinessUnitId", "LeadRevisionId") REFERENCES public."LeadRevisions"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadItems FK_LeadItems_Leads; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadItems"
    ADD CONSTRAINT "FK_LeadItems_Leads" FOREIGN KEY ("LeadID") REFERENCES public."Leads"("ID") ON DELETE CASCADE;


--
-- Name: LeadMatchCandidates FK_LeadMatchCandidates_LeadIngestionOccurrences_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadMatchCandidates"
    ADD CONSTRAINT "FK_LeadMatchCandidates_LeadIngestionOccurrences_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "OccurrenceId") REFERENCES public."LeadIngestionOccurrences"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadMatchCandidates FK_LeadMatchCandidates_Leads_BusinessUnitId_CandidateLeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadMatchCandidates"
    ADD CONSTRAINT "FK_LeadMatchCandidates_Leads_BusinessUnitId_CandidateLeadId" FOREIGN KEY ("BusinessUnitId", "CandidateLeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LeadOccurrenceDocuments FK_LeadOccurrenceDocuments_LeadIngestionOccurrences_BusinessUn~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadOccurrenceDocuments"
    ADD CONSTRAINT "FK_LeadOccurrenceDocuments_LeadIngestionOccurrences_BusinessUn~" FOREIGN KEY ("BusinessUnitId", "OccurrenceId") REFERENCES public."LeadIngestionOccurrences"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadOccurrenceDocuments FK_LeadOccurrenceDocuments_source_documents_BusinessUnitId_Sou~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadOccurrenceDocuments"
    ADD CONSTRAINT "FK_LeadOccurrenceDocuments_source_documents_BusinessUnitId_Sou~" FOREIGN KEY ("BusinessUnitId", "SourceDocumentId") REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: LeadReferenceConfigurations FK_LeadReferenceConfigurations_BusinessUnits_BusinessUnitID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadReferenceConfigurations"
    ADD CONSTRAINT "FK_LeadReferenceConfigurations_BusinessUnits_BusinessUnitID" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: LeadReviewAudits FK_LeadReviewAudits_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadReviewAudits"
    ADD CONSTRAINT "FK_LeadReviewAudits_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LeadRevisionDifferences FK_LeadRevisionDifferences_LeadRevisions_BusinessUnitId_LeadRe~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisionDifferences"
    ADD CONSTRAINT "FK_LeadRevisionDifferences_LeadRevisions_BusinessUnitId_LeadRe~" FOREIGN KEY ("BusinessUnitId", "LeadRevisionId") REFERENCES public."LeadRevisions"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadRevisionImpacts FK_LeadRevisionImpacts_LeadRevisions_BusinessUnitId_LeadRevisi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisionImpacts"
    ADD CONSTRAINT "FK_LeadRevisionImpacts_LeadRevisions_BusinessUnitId_LeadRevisi~" FOREIGN KEY ("BusinessUnitId", "LeadRevisionId") REFERENCES public."LeadRevisions"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadRevisionImpacts FK_LeadRevisionImpacts_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisionImpacts"
    ADD CONSTRAINT "FK_LeadRevisionImpacts_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LeadRevisions FK_LeadRevisions_LeadIngestionOccurrences_BusinessUnitId_Estab~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisions"
    ADD CONSTRAINT "FK_LeadRevisions_LeadIngestionOccurrences_BusinessUnitId_Estab~" FOREIGN KEY ("BusinessUnitId", "EstablishedByOccurrenceId") REFERENCES public."LeadIngestionOccurrences"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LeadRevisions FK_LeadRevisions_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisions"
    ADD CONSTRAINT "FK_LeadRevisions_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LeadStatusHistories FK_LeadStatusHistories_BusinessUnits_BusinessUnitID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadStatusHistories"
    ADD CONSTRAINT "FK_LeadStatusHistories_BusinessUnits_BusinessUnitID" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: LeadStatusHistories FK_LeadStatusHistories_CommercialCases_CommercialCaseID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadStatusHistories"
    ADD CONSTRAINT "FK_LeadStatusHistories_CommercialCases_CommercialCaseID" FOREIGN KEY ("CommercialCaseID") REFERENCES public."CommercialCases"("Id") ON DELETE RESTRICT;


--
-- Name: LeadStatusHistories FK_LeadStatusHistories_Leads_LeadID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadStatusHistories"
    ADD CONSTRAINT "FK_LeadStatusHistories_Leads_LeadID" FOREIGN KEY ("LeadID") REFERENCES public."Leads"("ID") ON DELETE RESTRICT;


--
-- Name: Leads FK_Leads_CommercialCases_CommercialCaseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_CommercialCases_CommercialCaseId" FOREIGN KEY ("CommercialCaseId") REFERENCES public."CommercialCases"("Id") ON DELETE RESTRICT;


--
-- Name: Leads FK_Leads_Contacts_BusinessUnitID_ContactID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_Contacts_BusinessUnitID_ContactID" FOREIGN KEY ("BusinessUnitID", "ContactID") REFERENCES public."Contacts"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: Leads FK_Leads_Customers_BusinessUnitID_CustomerID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_Customers_BusinessUnitID_CustomerID" FOREIGN KEY ("BusinessUnitID", "CustomerID") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: Leads FK_Leads_LeadRejectedReason; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_LeadRejectedReason" FOREIGN KEY ("LeadRejectedReasonID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Leads FK_Leads_LeadRevisions_BusinessUnitID_CurrentRevisionId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_LeadRevisions_BusinessUnitID_CurrentRevisionId" FOREIGN KEY ("BusinessUnitID", "CurrentRevisionId") REFERENCES public."LeadRevisions"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: Leads FK_Leads_Setup_Master; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_Setup_Master" FOREIGN KEY ("LeadStatusId") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Leads FK_Leads_Users; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK_Leads_Users" FOREIGN KEY ("AssignTo") REFERENCES public."Users"("ID");


--
-- Name: LedgerAccounts FK_LedgerAccounts_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerAccounts"
    ADD CONSTRAINT "FK_LedgerAccounts_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: LedgerAccounts FK_LedgerAccounts_Currency_Tenant; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerAccounts"
    ADD CONSTRAINT "FK_LedgerAccounts_Currency_Tenant" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LedgerBooks FK_LedgerBooks_Currency_FunctionalCurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "FK_LedgerBooks_Currency_FunctionalCurrencyId" FOREIGN KEY ("FunctionalCurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: LedgerBooks FK_LedgerBooks_Currency_Tenant; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "FK_LedgerBooks_Currency_Tenant" FOREIGN KEY ("BusinessUnitId", "FunctionalCurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: LedgerBooks FK_LedgerBooks_LedgerAccounts_BusinessUnitId_ReceivablesContro~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "FK_LedgerBooks_LedgerAccounts_BusinessUnitId_ReceivablesContro~" FOREIGN KEY ("BusinessUnitId", "ReceivablesControlAccountId") REFERENCES public."LedgerAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: LedgerBooks FK_LedgerBooks_LedgerAccounts_BusinessUnitId_UnappliedCashAcco~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "FK_LedgerBooks_LedgerAccounts_BusinessUnitId_UnappliedCashAcco~" FOREIGN KEY ("BusinessUnitId", "UnappliedCashAccountId") REFERENCES public."LedgerAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: MasterDataChangeEvents FK_MasterDataChangeEvents_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."MasterDataChangeEvents"
    ADD CONSTRAINT "FK_MasterDataChangeEvents_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: MasterDataFieldChanges FK_MasterDataFieldChanges_MasterDataChangeEvents_BusinessUnitI~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."MasterDataFieldChanges"
    ADD CONSTRAINT "FK_MasterDataFieldChanges_MasterDataChangeEvents_BusinessUnitI~" FOREIGN KEY ("BusinessUnitId", "ChangeEventId") REFERENCES public."MasterDataChangeEvents"("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: OrderItems FK_OrderItems_CustomerAwardLineAllocations_CustomerAwardLineAl~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "FK_OrderItems_CustomerAwardLineAllocations_CustomerAwardLineAl~" FOREIGN KEY ("CustomerAwardLineAllocationID") REFERENCES public."CustomerAwardLineAllocations"("Id") ON DELETE RESTRICT;


--
-- Name: OrderToCashAuditEvents FK_OrderToCashAuditEvents_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderToCashAuditEvents"
    ADD CONSTRAINT "FK_OrderToCashAuditEvents_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: OrderToCashDocumentCounters FK_OrderToCashDocumentCounters_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderToCashDocumentCounters"
    ADD CONSTRAINT "FK_OrderToCashDocumentCounters_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: Orders FK_Orders_CommercialCases_BusinessUnitID_CommercialCaseID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK_Orders_CommercialCases_BusinessUnitID_CommercialCaseID" FOREIGN KEY ("BusinessUnitID", "CommercialCaseID") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: Orders FK_Orders_CustomerAwards_BusinessUnitID_CustomerAwardID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK_Orders_CustomerAwards_BusinessUnitID_CustomerAwardID" FOREIGN KEY ("BusinessUnitID", "CustomerAwardID") REFERENCES public."CustomerAwards"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: PaymentAllocations FK_PaymentAllocations_CustomerPayments_BusinessUnitId_Customer~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PaymentAllocations"
    ADD CONSTRAINT "FK_PaymentAllocations_CustomerPayments_BusinessUnitId_Customer~" FOREIGN KEY ("BusinessUnitId", "CustomerPaymentId") REFERENCES public."CustomerPayments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: PaymentAllocations FK_PaymentAllocations_ReceivableDocuments_BusinessUnitId_Recei~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PaymentAllocations"
    ADD CONSTRAINT "FK_PaymentAllocations_ReceivableDocuments_BusinessUnitId_Recei~" FOREIGN KEY ("BusinessUnitId", "ReceivableDocumentId") REFERENCES public."ReceivableDocuments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ProductSubCategories FK_ProductSubCategories_BusinessUnits; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductSubCategories"
    ADD CONSTRAINT "FK_ProductSubCategories_BusinessUnits" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Products FK_Products_ProductSubCategories; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "FK_Products_ProductSubCategories" FOREIGN KEY ("SubCategoryID") REFERENCES public."ProductSubCategories"("ID");


--
-- Name: Products FK_Products_setUOM; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "FK_Products_setUOM" FOREIGN KEY ("UomID") REFERENCES public."setUOM"("UomID");


--
-- Name: PromisesToPay FK_PromisesToPay_CustomerPayments_BusinessUnitId_MatchedPayment; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PromisesToPay"
    ADD CONSTRAINT "FK_PromisesToPay_CustomerPayments_BusinessUnitId_MatchedPayment" FOREIGN KEY ("BusinessUnitId", "MatchedPaymentId") REFERENCES public."CustomerPayments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: PromisesToPay FK_PromisesToPay_DunningCases_BusinessUnitId_DunningCaseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PromisesToPay"
    ADD CONSTRAINT "FK_PromisesToPay_DunningCases_BusinessUnitId_DunningCaseId" FOREIGN KEY ("BusinessUnitId", "DunningCaseId") REFERENCES public."DunningCases"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: QuoteConfiguration FK_QuoteConfiguration_BusinessUnit; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteConfiguration"
    ADD CONSTRAINT "FK_QuoteConfiguration_BusinessUnit" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID");


--
-- Name: QuoteItems FK_QuoteItem_DiscountType; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "FK_QuoteItem_DiscountType" FOREIGN KEY ("DiscountTypeId") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: QuoteItems FK_QuoteItems_Products; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "FK_QuoteItems_Products" FOREIGN KEY ("ProductID") REFERENCES public."Products"("ID");


--
-- Name: QuoteItems FK_QuoteItems_Quotes; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "FK_QuoteItems_Quotes" FOREIGN KEY ("QuoteID") REFERENCES public."Quotes"("ID") ON DELETE CASCADE;


--
-- Name: QuoteItems FK_QuoteItems_RFQItems; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "FK_QuoteItems_RFQItems" FOREIGN KEY ("RFQItemID") REFERENCES public."RFQItems"("ID");


--
-- Name: QuotePriceAttestationLines FK_QuotePriceAttestationLines_QuotePriceAttestations_BusinessU~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuotePriceAttestationLines"
    ADD CONSTRAINT "FK_QuotePriceAttestationLines_QuotePriceAttestations_BusinessU~" FOREIGN KEY ("BusinessUnitId", "AttestationId") REFERENCES public."QuotePriceAttestations"("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: QuotePriceAttestations FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuotePriceAttestations"
    ADD CONSTRAINT "FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId" FOREIGN KEY ("BusinessUnitId", "QuoteId") REFERENCES public."Quotes"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: QuoteRemovalRecords FK_QuoteRemovalRecords_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteRemovalRecords"
    ADD CONSTRAINT "FK_QuoteRemovalRecords_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: QuoteValidityExtensions FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteValidityExtensions"
    ADD CONSTRAINT "FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId" FOREIGN KEY ("BusinessUnitId", "QuoteId") REFERENCES public."Quotes"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: Quotes FK_Quote_DiscountType; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quote_DiscountType" FOREIGN KEY ("DiscountTypeId") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Quotes FK_Quotes_BusinessUnits; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quotes_BusinessUnits" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Quotes FK_Quotes_CommercialCases_BusinessUnitID_CommercialCaseID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quotes_CommercialCases_BusinessUnitID_CommercialCaseID" FOREIGN KEY ("BusinessUnitID", "CommercialCaseID") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: Quotes FK_Quotes_Currency; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quotes_Currency" FOREIGN KEY ("CurrencyID") REFERENCES public."Currency"("ID");


--
-- Name: Quotes FK_Quotes_Customers; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quotes_Customers" FOREIGN KEY ("CustomerID") REFERENCES public."Customers"("ID");


--
-- Name: Quotes FK_Quotes_RFQ; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quotes_RFQ" FOREIGN KEY ("RFQID") REFERENCES public."RFQ"("ID");


--
-- Name: Quotes FK_Quotes_Status; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "FK_Quotes_Status" FOREIGN KEY ("StatusID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: RFQItems FK_RFQItems_Currency; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_RFQItems_Currency" FOREIGN KEY ("CurrencyID") REFERENCES public."Currency"("ID");


--
-- Name: RFQItems FK_RFQItems_Product; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_RFQItems_Product" FOREIGN KEY ("ProductID") REFERENCES public."Products"("ID");


--
-- Name: RFQItems FK_RFQItems_RFQ; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_RFQItems_RFQ" FOREIGN KEY ("RFQID") REFERENCES public."RFQ"("ID") ON DELETE CASCADE;


--
-- Name: RFQItems FK_RFQItems_Supplier; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_RFQItems_Supplier" FOREIGN KEY ("SupplierID") REFERENCES public."Suppliers"("ID");


--
-- Name: RFQItems FK_RFQItems_UOM; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_RFQItems_UOM" FOREIGN KEY ("UomId") REFERENCES public."setUOM"("UomID");


--
-- Name: RFQItems FK_RFQItems_Warehouse; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_RFQItems_Warehouse" FOREIGN KEY ("WarehouseID") REFERENCES public."Warehouses"("ID");


--
-- Name: setUOM FK_RFQ_BusinessUnit; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."setUOM"
    ADD CONSTRAINT "FK_RFQ_BusinessUnit" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: RFQ FK_RFQ_BusinessUnitID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "FK_RFQ_BusinessUnitID" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: RFQ FK_RFQ_CommercialCases_BusinessUnitID_CommercialCaseID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "FK_RFQ_CommercialCases_BusinessUnitID_CommercialCaseID" FOREIGN KEY ("BusinessUnitID", "CommercialCaseID") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: RFQ FK_RFQ_Customers_CustomerID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "FK_RFQ_Customers_CustomerID" FOREIGN KEY ("CustomerID") REFERENCES public."Customers"("ID");


--
-- Name: RFQ FK_RFQ_LeadID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "FK_RFQ_LeadID" FOREIGN KEY ("LeadID") REFERENCES public."Leads"("ID");


--
-- Name: RFQ FK_RFQ_StatusID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "FK_RFQ_StatusID" FOREIGN KEY ("RFQStatusID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: RFQ FK_RFQ_TypeID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "FK_RFQ_TypeID" FOREIGN KEY ("RFQTypeID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: ReceivableDocumentLines FK_ReceivableDocumentLines_OrderItems_OrderItemId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "FK_ReceivableDocumentLines_OrderItems_OrderItemId" FOREIGN KEY ("OrderItemId") REFERENCES public."OrderItems"("ID") ON DELETE RESTRICT;


--
-- Name: ReceivableDocumentLines FK_ReceivableDocumentLines_ReceivableDocumentLines_BusinessUni~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "FK_ReceivableDocumentLines_ReceivableDocumentLines_BusinessUni~" FOREIGN KEY ("BusinessUnitId", "ParentDocumentLineId") REFERENCES public."ReceivableDocumentLines"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReceivableDocumentLines FK_ReceivableDocumentLines_ReceivableDocuments_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "FK_ReceivableDocumentLines_ReceivableDocuments_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "ReceivableDocumentId") REFERENCES public."ReceivableDocuments"("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: ReceivableDocuments FK_ReceivableDocuments_CommercialCases_BusinessUnitId_Commerci~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "FK_ReceivableDocuments_CommercialCases_BusinessUnitId_Commerci~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: ReceivableDocuments FK_ReceivableDocuments_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "FK_ReceivableDocuments_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: ReceivableDocuments FK_ReceivableDocuments_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "FK_ReceivableDocuments_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: ReceivableDocuments FK_ReceivableDocuments_Orders_BusinessUnitId_OrderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "FK_ReceivableDocuments_Orders_BusinessUnitId_OrderId" FOREIGN KEY ("BusinessUnitId", "OrderId") REFERENCES public."Orders"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: ReceivableDocuments FK_ReceivableDocuments_ReceivableDocuments_BusinessUnitId_Paren; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "FK_ReceivableDocuments_ReceivableDocuments_BusinessUnitId_Paren" FOREIGN KEY ("BusinessUnitId", "ParentDocumentId") REFERENCES public."ReceivableDocuments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReceivableWriteOffs FK_ReceivableWriteOffs_CommercialCases_BusinessUnitId_Commerci~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "FK_ReceivableWriteOffs_CommercialCases_BusinessUnitId_Commerci~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: ReceivableWriteOffs FK_ReceivableWriteOffs_Currency_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "FK_ReceivableWriteOffs_Currency_CurrencyId" FOREIGN KEY ("CurrencyId") REFERENCES public."Currency"("ID") ON DELETE RESTRICT;


--
-- Name: ReceivableWriteOffs FK_ReceivableWriteOffs_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "FK_ReceivableWriteOffs_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: ReconciliationAllocations FK_ReconciliationAllocations_BankStatementLines_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationAllocations"
    ADD CONSTRAINT "FK_ReconciliationAllocations_BankStatementLines_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "BankStatementLineId") REFERENCES public."BankStatementLines"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationAllocations FK_ReconciliationAllocations_JournalEntryLines_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationAllocations"
    ADD CONSTRAINT "FK_ReconciliationAllocations_JournalEntryLines_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "JournalEntryLineId") REFERENCES public."JournalEntryLines"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationAllocations FK_ReconciliationAllocations_ReconciliationMatches_BusinessUni~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationAllocations"
    ADD CONSTRAINT "FK_ReconciliationAllocations_ReconciliationMatches_BusinessUni~" FOREIGN KEY ("BusinessUnitId", "ReconciliationMatchId") REFERENCES public."ReconciliationMatches"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationMatches FK_ReconciliationMatches_BankMatchingRules_BusinessUnitId_Bank~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationMatches"
    ADD CONSTRAINT "FK_ReconciliationMatches_BankMatchingRules_BusinessUnitId_Bank~" FOREIGN KEY ("BusinessUnitId", "BankMatchingRuleId") REFERENCES public."BankMatchingRules"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationMatches FK_ReconciliationMatches_ReconciliationRuns_BusinessUnitId_Rec~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationMatches"
    ADD CONSTRAINT "FK_ReconciliationMatches_ReconciliationRuns_BusinessUnitId_Rec~" FOREIGN KEY ("BusinessUnitId", "ReconciliationRunId") REFERENCES public."ReconciliationRuns"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationRunRules FK_ReconciliationRunRules_BankMatchingRules_BusinessUnitId_Ban~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRunRules"
    ADD CONSTRAINT "FK_ReconciliationRunRules_BankMatchingRules_BusinessUnitId_Ban~" FOREIGN KEY ("BusinessUnitId", "BankMatchingRuleId") REFERENCES public."BankMatchingRules"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationRunRules FK_ReconciliationRunRules_ReconciliationRuns_BusinessUnitId_Re~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRunRules"
    ADD CONSTRAINT "FK_ReconciliationRunRules_ReconciliationRuns_BusinessUnitId_Re~" FOREIGN KEY ("BusinessUnitId", "ReconciliationRunId") REFERENCES public."ReconciliationRuns"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationRuns FK_ReconciliationRuns_BankAccounts_BusinessUnitId_BankAccountId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRuns"
    ADD CONSTRAINT "FK_ReconciliationRuns_BankAccounts_BusinessUnitId_BankAccountId" FOREIGN KEY ("BusinessUnitId", "BankAccountId") REFERENCES public."BankAccounts"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ReconciliationRuns FK_ReconciliationRuns_BankStatements_BusinessUnitId_BankStatem~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRuns"
    ADD CONSTRAINT "FK_ReconciliationRuns_BankStatements_BusinessUnitId_BankStatem~" FOREIGN KEY ("BusinessUnitId", "BankStatementId", "BankAccountId") REFERENCES public."BankStatements"("BusinessUnitId", "Id", "BankAccountId") ON DELETE RESTRICT;


--
-- Name: RFQItems FK_Rfqitems_SupplierQuotedItems; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "FK_Rfqitems_SupplierQuotedItems" FOREIGN KEY ("SupplierQuotedItemId") REFERENCES public."SupplierQuotedItems"("Id");


--
-- Name: ShipmentItems FK_ShipmentItems_OrderItems; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentItems"
    ADD CONSTRAINT "FK_ShipmentItems_OrderItems" FOREIGN KEY ("OrderItemID") REFERENCES public."OrderItems"("ID");


--
-- Name: ShipmentItems FK_ShipmentItems_Shipments; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentItems"
    ADD CONSTRAINT "FK_ShipmentItems_Shipments" FOREIGN KEY ("ShipmentID") REFERENCES public."Shipments"("ID");


--
-- Name: ShipmentStatusHistory FK_ShipmentStatusHistory_NewStatus; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentStatusHistory"
    ADD CONSTRAINT "FK_ShipmentStatusHistory_NewStatus" FOREIGN KEY ("NewStatusId") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: ShipmentStatusHistory FK_ShipmentStatusHistory_PreviousStatus; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentStatusHistory"
    ADD CONSTRAINT "FK_ShipmentStatusHistory_PreviousStatus" FOREIGN KEY ("PreviousStatusId") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: ShipmentStatusHistory FK_ShipmentStatusHistory_Shipments; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentStatusHistory"
    ADD CONSTRAINT "FK_ShipmentStatusHistory_Shipments" FOREIGN KEY ("ShipmentId") REFERENCES public."Shipments"("ID") ON DELETE CASCADE;


--
-- Name: Shipments FK_Shipments_BusinessUnits; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "FK_Shipments_BusinessUnits" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Shipments FK_Shipments_CommercialCases_BusinessUnitID_CommercialCaseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "FK_Shipments_CommercialCases_BusinessUnitID_CommercialCaseId" FOREIGN KEY ("BusinessUnitID", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: Shipments FK_Shipments_Orders; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "FK_Shipments_Orders" FOREIGN KEY ("OrderID") REFERENCES public."Orders"("ID");


--
-- Name: Shipments FK_Shipments_SetCity_BusinessUnitID_DeliveryCityID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "FK_Shipments_SetCity_BusinessUnitID_DeliveryCityID" FOREIGN KEY ("BusinessUnitID", "DeliveryCityID") REFERENCES public."SetCity"("BUID", "CityID") ON DELETE RESTRICT;


--
-- Name: Shipments FK_Shipments_Status; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "FK_Shipments_Status" FOREIGN KEY ("StatusID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: SourcingAwards FK_SourcingAwards_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "FK_SourcingAwards_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: SourcingAwards FK_SourcingAwards_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "FK_SourcingAwards_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: SourcingAwards FK_SourcingAwards_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "FK_SourcingAwards_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: SourcingAwards FK_SourcingAwards_SupplierQuotedItems_BusinessUnitId_SupplierQ~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "FK_SourcingAwards_SupplierQuotedItems_BusinessUnitId_SupplierQ~" FOREIGN KEY ("BusinessUnitId", "SupplierQuotedItemId") REFERENCES public."SupplierQuotedItems"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SourcingAwards FK_SourcingAwards_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "FK_SourcingAwards_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: SetState FK_State_BusinessUnit; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetState"
    ADD CONSTRAINT "FK_State_BusinessUnit" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: SetState FK_State_Country; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetState"
    ADD CONSTRAINT "FK_State_Country" FOREIGN KEY ("CountryID") REFERENCES public."SetCountry"("CountryID");


--
-- Name: SupplierPurchaseHistory FK_SupplierPurchaseHistory_Products; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierPurchaseHistory"
    ADD CONSTRAINT "FK_SupplierPurchaseHistory_Products" FOREIGN KEY ("ProductId") REFERENCES public."Products"("ID");


--
-- Name: SupplierPurchaseHistory FK_SupplierPurchaseHistory_Suppliers; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierPurchaseHistory"
    ADD CONSTRAINT "FK_SupplierPurchaseHistory_Suppliers" FOREIGN KEY ("SupplierId") REFERENCES public."Suppliers"("ID");


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_BusinessUnits; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_BusinessUnits" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE CASCADE;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_Currency; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_Currency" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_Products_BusinessUnitId_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_Products_BusinessUnitId_ProductId" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_SetUoms; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_SetUoms" FOREIGN KEY ("UomId") REFERENCES public."setUOM"("UomID");


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_SupplierSolicitations_BusinessUnitId_Su~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_SupplierSolicitations_BusinessUnitId_Su~" FOREIGN KEY ("BusinessUnitId", "SupplierSolicitationId") REFERENCES public."SupplierSolicitations"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_Suppliers; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_Suppliers" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_commercial_demand_lines_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_commercial_demand_lines_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "CommercialDemandLineId") REFERENCES public.commercial_demand_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_sourcing_cases_BusinessUnitId_SourcingC~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_sourcing_cases_BusinessUnitId_SourcingC~" FOREIGN KEY ("BusinessUnitId", "SourcingCaseId") REFERENCES public.sourcing_cases("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_supplier_quote_lines_BusinessUnitId_Sou~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_supplier_quote_lines_BusinessUnitId_Sou~" FOREIGN KEY ("BusinessUnitId", "SourceSupplierQuoteLineId") REFERENCES public.supplier_quote_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_supplier_quote_revisions_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_supplier_quote_revisions_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "SourceSupplierQuoteRevisionId") REFERENCES public.supplier_quote_revisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierQuotedItems FK_SupplierQuotedItems_supplier_quotes_BusinessUnitId_SourceSu~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "FK_SupplierQuotedItems_supplier_quotes_BusinessUnitId_SourceSu~" FOREIGN KEY ("BusinessUnitId", "SourceSupplierQuoteId") REFERENCES public.supplier_quotes("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierSolicitations FK_SupplierSolicitations_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "FK_SupplierSolicitations_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: SupplierSolicitations FK_SupplierSolicitations_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "FK_SupplierSolicitations_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: SupplierSolicitations FK_SupplierSolicitations_commercial_demand_lines_BusinessUnitI~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "FK_SupplierSolicitations_commercial_demand_lines_BusinessUnitI~" FOREIGN KEY ("BusinessUnitId", "CommercialDemandLineId") REFERENCES public.commercial_demand_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: SupplierSolicitations FK_SupplierSolicitations_sourcing_cases_BusinessUnitId_Sourcin~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "FK_SupplierSolicitations_sourcing_cases_BusinessUnitId_Sourcin~" FOREIGN KEY ("BusinessUnitId", "SourcingCaseId") REFERENCES public.sourcing_cases("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: Suppliers FK_Suppliers_City; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "FK_Suppliers_City" FOREIGN KEY ("CityID") REFERENCES public."SetCity"("CityID");


--
-- Name: Suppliers FK_Suppliers_Country; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "FK_Suppliers_Country" FOREIGN KEY ("CountryID") REFERENCES public."SetCountry"("CountryID");


--
-- Name: Taxes FK_Taxes_BusinessUnits; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Taxes"
    ADD CONSTRAINT "FK_Taxes_BusinessUnits" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Teams FK_Teams_Users; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Teams"
    ADD CONSTRAINT "FK_Teams_Users" FOREIGN KEY ("ManagerID") REFERENCES public."Users"("ID");


--
-- Name: UserColumnPreferences FK_UserColumnPreferences_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserColumnPreferences"
    ADD CONSTRAINT "FK_UserColumnPreferences_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE CASCADE;


--
-- Name: UserColumnPreferences FK_UserColumnPreferences_Users_UserId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserColumnPreferences"
    ADD CONSTRAINT "FK_UserColumnPreferences_Users_UserId" FOREIGN KEY ("UserId") REFERENCES public."Users"("ID") ON DELETE CASCADE;


--
-- Name: Users FK_Users_Manager; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "FK_Users_Manager" FOREIGN KEY ("ManagerID") REFERENCES public."Users"("ID");


--
-- Name: Warehouses FK_Warehouses_BusinessUnits; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Warehouses"
    ADD CONSTRAINT "FK_Warehouses_BusinessUnits" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: WriteOffAllocations FK_WriteOffAllocations_ReceivableDocuments_BusinessUnitId_Rece~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."WriteOffAllocations"
    ADD CONSTRAINT "FK_WriteOffAllocations_ReceivableDocuments_BusinessUnitId_Rece~" FOREIGN KEY ("BusinessUnitId", "ReceivableDocumentId") REFERENCES public."ReceivableDocuments"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: WriteOffAllocations FK_WriteOffAllocations_ReceivableWriteOffs_BusinessUnitId_Rece~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."WriteOffAllocations"
    ADD CONSTRAINT "FK_WriteOffAllocations_ReceivableWriteOffs_BusinessUnitId_Rece~" FOREIGN KEY ("BusinessUnitId", "ReceivableWriteOffId") REFERENCES public."ReceivableWriteOffs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: Contacts FK__Contacts__Custom__17F790F9; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Contacts"
    ADD CONSTRAINT "FK__Contacts__Custom__17F790F9" FOREIGN KEY ("BusinessUnitID", "CustomerID") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: Currency FK__Currency__Busine__4E88ABD4; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Currency"
    ADD CONSTRAINT "FK__Currency__Busine__4E88ABD4" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Customers FK__Customers__BUID__0D7A0286; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "FK__Customers__BUID__0D7A0286" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID") ON DELETE CASCADE;


--
-- Name: EmailIngests FK__EmailInge__Email__503BEA1C; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."EmailIngests"
    ADD CONSTRAINT "FK__EmailInge__Email__503BEA1C" FOREIGN KEY ("EmailConfigurationID") REFERENCES public."Email_Configurations"("ID");


--
-- Name: Email_Configurations FK__Email_Con__Busin__489AC854; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Email_Configurations"
    ADD CONSTRAINT "FK__Email_Con__Busin__489AC854" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: ProductCategories FK__Inventory__Busin__534D60F1; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductCategories"
    ADD CONSTRAINT "FK__Inventory__Busin__534D60F1" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: ProductAttachments FK__Inventory__Inven__42E1EEFE; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductAttachments"
    ADD CONSTRAINT "FK__Inventory__Inven__42E1EEFE" FOREIGN KEY ("InventoryID") REFERENCES public."Products"("ID") ON DELETE CASCADE;


--
-- Name: ProductCategories FK__Inventory__Paren__52593CB8; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductCategories"
    ADD CONSTRAINT "FK__Inventory__Paren__52593CB8" FOREIGN KEY ("ParentCategoryID") REFERENCES public."ProductCategories"("ID");


--
-- Name: ProductAttachments FK__Inventory__Uploa__44CA3770; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductAttachments"
    ADD CONSTRAINT "FK__Inventory__Uploa__44CA3770" FOREIGN KEY ("UploadedBy") REFERENCES public."Users"("ID");


--
-- Name: Leads FK__Leads__BusinessU__55009F39; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK__Leads__BusinessU__55009F39" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Leads FK__Leads__EmailInge__55F4C372; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "FK__Leads__EmailInge__55F4C372" FOREIGN KEY ("EmailIngestsID") REFERENCES public."EmailIngests"("ID");


--
-- Name: OrderItems FK__OrderItem__Order__4A18FC72; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "FK__OrderItem__Order__4A18FC72" FOREIGN KEY ("OrderID") REFERENCES public."Orders"("ID") ON DELETE CASCADE;


--
-- Name: OrderItems FK__OrderItem__Produ__4B0D20AB; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "FK__OrderItem__Produ__4B0D20AB" FOREIGN KEY ("ProductID") REFERENCES public."Products"("ID");


--
-- Name: OrderItems FK__OrderItem__UomID__4C0144E4; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "FK__OrderItem__UomID__4C0144E4" FOREIGN KEY ("UomID") REFERENCES public."setUOM"("UomID");


--
-- Name: OrderItems FK__OrderItem__Wareh__4CF5691D; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "FK__OrderItem__Wareh__4CF5691D" FOREIGN KEY ("WarehouseID") REFERENCES public."Warehouses"("ID");


--
-- Name: Orders FK__Orders__Business__3F9B6DFF; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__Business__3F9B6DFF" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Orders FK__Orders__Currency__436BFEE3; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__Currency__436BFEE3" FOREIGN KEY ("CurrencyID") REFERENCES public."Currency"("ID");


--
-- Name: Orders FK__Orders__Customer__3EA749C6; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__Customer__3EA749C6" FOREIGN KEY ("CustomerID") REFERENCES public."Customers"("ID");


--
-- Name: Orders FK__Orders__LeadID__3CBF0154; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__LeadID__3CBF0154" FOREIGN KEY ("LeadID") REFERENCES public."Leads"("ID");


--
-- Name: Orders FK__Orders__PaymentM__4183B671; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__PaymentM__4183B671" FOREIGN KEY ("PaymentMethodID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Orders FK__Orders__PaymentS__4277DAAA; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__PaymentS__4277DAAA" FOREIGN KEY ("PaymentStatusID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Orders FK__Orders__QuoteID__3BCADD1B; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__QuoteID__3BCADD1B" FOREIGN KEY ("QuoteID") REFERENCES public."Quotes"("ID");


--
-- Name: Orders FK__Orders__RFQID__3DB3258D; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__RFQID__3DB3258D" FOREIGN KEY ("RFQID") REFERENCES public."RFQ"("ID");


--
-- Name: Orders FK__Orders__StatusID__408F9238; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "FK__Orders__StatusID__408F9238" FOREIGN KEY ("StatusID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Products FK__Products__BUID; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "FK__Products__BUID" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: Products FK__Products__Categ; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "FK__Products__Categ" FOREIGN KEY ("CategoryID") REFERENCES public."ProductCategories"("ID");


--
-- Name: Products FK__Products__Prefe; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "FK__Products__Prefe" FOREIGN KEY ("PreferredSupplierID") REFERENCES public."Suppliers"("ID");


--
-- Name: Products FK__Products__Wareh; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "FK__Products__Wareh" FOREIGN KEY ("WarehouseID") REFERENCES public."Warehouses"("ID");


--
-- Name: RolePermissions FK__RolePermi__Busin__05D8E0BE; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RolePermissions"
    ADD CONSTRAINT "FK__RolePermi__Busin__05D8E0BE" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: RolePermissions FK__RolePermi__Modul__04E4BC85; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RolePermissions"
    ADD CONSTRAINT "FK__RolePermi__Modul__04E4BC85" FOREIGN KEY ("ModuleID") REFERENCES public."Module"("ID");


--
-- Name: RolePermissions FK__RolePermi__RoleI__03F0984C; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RolePermissions"
    ADD CONSTRAINT "FK__RolePermi__RoleI__03F0984C" FOREIGN KEY ("BusinessUnitID", "RoleID") REFERENCES public."Setup_Master"("BusinessUnitID", "SetupID") NOT VALID;


--
-- Name: Setup_Master FK__Setup_Mas__Busin__68487DD7; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Setup_Master"
    ADD CONSTRAINT "FK__Setup_Mas__Busin__68487DD7" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Setup_Master FK__Setup_Mas__Paren__6754599E; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Setup_Master"
    ADD CONSTRAINT "FK__Setup_Mas__Paren__6754599E" FOREIGN KEY ("ParentSetupID") REFERENCES public."Setup_Master"("SetupID");


--
-- Name: Suppliers FK__Suppliers__BUID__1332DBDC; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "FK__Suppliers__BUID__1332DBDC" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID") ON DELETE CASCADE;


--
-- Name: Suppliers FK__Suppliers__Curre__123EB7A3; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "FK__Suppliers__Curre__123EB7A3" FOREIGN KEY ("CurrencyID") REFERENCES public."Currency"("ID");


--
-- Name: Teams FK__Teams__BusinessU__70DDC3D8; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Teams"
    ADD CONSTRAINT "FK__Teams__BusinessU__70DDC3D8" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Teams FK__Teams__SubTeamID__6FE99F9F; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Teams"
    ADD CONSTRAINT "FK__Teams__SubTeamID__6FE99F9F" FOREIGN KEY ("SubTeamID") REFERENCES public."Teams"("ID");


--
-- Name: UserGroups FK__UserGroup__Busin__73BA3083; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserGroups"
    ADD CONSTRAINT "FK__UserGroup__Busin__73BA3083" FOREIGN KEY ("BusinessUnitID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Users FK__Users__BUID__7D439ABD; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "FK__Users__BUID__7D439ABD" FOREIGN KEY ("BUID") REFERENCES public."BusinessUnits"("ID");


--
-- Name: Users FK__Users__RoleID__7B5B524B; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "FK__Users__RoleID__7B5B524B" FOREIGN KEY ("BUID", "RoleID") REFERENCES public."Setup_Master"("BusinessUnitID", "SetupID") NOT VALID;


--
-- Name: Users FK__Users__TeamID__7C4F7684; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "FK__Users__TeamID__7C4F7684" FOREIGN KEY ("TeamID") REFERENCES public."Teams"("ID");


--
-- Name: Users FK__Users__UserGroup__7E37BEF6; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "FK__Users__UserGroup__7E37BEF6" FOREIGN KEY ("UserGroupID") REFERENCES public."UserGroups"("ID");


--
-- Name: canonical_inquiries FK_canonical_inquiries_Leads_business_unit_id_lead_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_inquiries
    ADD CONSTRAINT "FK_canonical_inquiries_Leads_business_unit_id_lead_id" FOREIGN KEY (business_unit_id, lead_id) REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: canonical_inquiries FK_canonical_inquiries_document_corpora_business_unit_id_corpu~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_inquiries
    ADD CONSTRAINT "FK_canonical_inquiries_document_corpora_business_unit_id_corpu~" FOREIGN KEY (business_unit_id, corpus_id) REFERENCES public.document_corpora(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: canonical_line_items FK_canonical_line_items_canonical_inquiries_business_unit_id_i~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_line_items
    ADD CONSTRAINT "FK_canonical_line_items_canonical_inquiries_business_unit_id_i~" FOREIGN KEY (business_unit_id, inquiry_id) REFERENCES public.canonical_inquiries(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: commercial_demand_lines FK_commercial_demand_lines_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_demand_lines
    ADD CONSTRAINT "FK_commercial_demand_lines_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: commercial_demand_lines FK_commercial_demand_lines_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_demand_lines
    ADD CONSTRAINT "FK_commercial_demand_lines_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: commercial_document_classifications FK_commercial_document_classifications_source_documents_busine~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_document_classifications
    ADD CONSTRAINT "FK_commercial_document_classifications_source_documents_busine~" FOREIGN KEY (business_unit_id, source_document_id) REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: commercial_exception_cases FK_commercial_exception_cases_CommercialCases_BusinessUnitId_C~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "FK_commercial_exception_cases_CommercialCases_BusinessUnitId_C~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId", "NexoraSerial") REFERENCES public."CommercialCases"("BusinessUnitID", "Id", "MasterReference") ON DELETE RESTRICT;


--
-- Name: commercial_exception_cases FK_commercial_exception_cases_delivery_proof_lines_BusinessUni~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "FK_commercial_exception_cases_delivery_proof_lines_BusinessUni~" FOREIGN KEY ("BusinessUnitId", "DeliveryProofLineId") REFERENCES public.delivery_proof_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_exception_cases FK_commercial_exception_cases_follow_up_tasks_BusinessUnitId_F~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "FK_commercial_exception_cases_follow_up_tasks_BusinessUnitId_F~" FOREIGN KEY ("BusinessUnitId", "FollowUpTaskId") REFERENCES public.follow_up_tasks("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_exception_cases FK_commercial_exception_cases_unassigned_work_items_BusinessUn~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "FK_commercial_exception_cases_unassigned_work_items_BusinessUn~" FOREIGN KEY ("BusinessUnitId", "UnassignedWorkItemId") REFERENCES public.unassigned_work_items("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_exception_events FK_commercial_exception_events_commercial_exception_cases_Busi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_events
    ADD CONSTRAINT "FK_commercial_exception_events_commercial_exception_cases_Busi~" FOREIGN KEY ("BusinessUnitId", "CommercialExceptionCaseId") REFERENCES public.commercial_exception_cases("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_exception_operations FK_commercial_exception_operations_commercial_exception_cases_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_operations
    ADD CONSTRAINT "FK_commercial_exception_operations_commercial_exception_cases_~" FOREIGN KEY ("BusinessUnitId", "CommercialExceptionCaseId") REFERENCES public.commercial_exception_cases("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_exception_outbox FK_commercial_exception_outbox_commercial_exception_events_Bus~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_outbox
    ADD CONSTRAINT "FK_commercial_exception_outbox_commercial_exception_events_Bus~" FOREIGN KEY ("BusinessUnitId", "CommercialExceptionEventId") REFERENCES public.commercial_exception_events("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_lifecycle_events FK_commercial_lifecycle_events_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "FK_commercial_lifecycle_events_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: commercial_lifecycle_events FK_commercial_lifecycle_events_CommercialCases_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "FK_commercial_lifecycle_events_CommercialCases_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId", "CommercialCaseReference") REFERENCES public."CommercialCases"("BusinessUnitID", "Id", "MasterReference") ON DELETE RESTRICT;


--
-- Name: commercial_lifecycle_events FK_commercial_lifecycle_events_Setup_Master_BusinessUnitId_New~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "FK_commercial_lifecycle_events_Setup_Master_BusinessUnitId_New~" FOREIGN KEY ("BusinessUnitId", "NewStatusId") REFERENCES public."Setup_Master"("BusinessUnitID", "SetupID") ON DELETE RESTRICT;


--
-- Name: commercial_lifecycle_events FK_commercial_lifecycle_events_Setup_Master_BusinessUnitId_Pre~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "FK_commercial_lifecycle_events_Setup_Master_BusinessUnitId_Pre~" FOREIGN KEY ("BusinessUnitId", "PreviousStatusId") REFERENCES public."Setup_Master"("BusinessUnitID", "SetupID") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_events FK_commercial_opportunity_events_commercial_opportunity_recomm~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_events
    ADD CONSTRAINT "FK_commercial_opportunity_events_commercial_opportunity_recomm~" FOREIGN KEY ("BusinessUnitId", "OpportunityRecommendationId") REFERENCES public.commercial_opportunity_recommendations("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_feedback FK_commercial_opportunity_feedback_commercial_opportunity_feed~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_feedback
    ADD CONSTRAINT "FK_commercial_opportunity_feedback_commercial_opportunity_feed~" FOREIGN KEY ("BusinessUnitId", "SupersedesFeedbackId") REFERENCES public.commercial_opportunity_feedback("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_feedback FK_commercial_opportunity_feedback_commercial_opportunity_reco~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_feedback
    ADD CONSTRAINT "FK_commercial_opportunity_feedback_commercial_opportunity_reco~" FOREIGN KEY ("BusinessUnitId", "OpportunityRecommendationId") REFERENCES public.commercial_opportunity_recommendations("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_operations FK_commercial_opportunity_operations_commercial_opportunity_re~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_operations
    ADD CONSTRAINT "FK_commercial_opportunity_operations_commercial_opportunity_re~" FOREIGN KEY ("BusinessUnitId", "OpportunityRecommendationId") REFERENCES public.commercial_opportunity_recommendations("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_outbox FK_commercial_opportunity_outbox_commercial_opportunity_events~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_outbox
    ADD CONSTRAINT "FK_commercial_opportunity_outbox_commercial_opportunity_events~" FOREIGN KEY ("BusinessUnitId", "OpportunityEventId") REFERENCES public.commercial_opportunity_events("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_outcomes FK_commercial_opportunity_outcomes_commercial_opportunity_reco~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_outcomes
    ADD CONSTRAINT "FK_commercial_opportunity_outcomes_commercial_opportunity_reco~" FOREIGN KEY ("BusinessUnitId", "OpportunityRecommendationId") REFERENCES public.commercial_opportunity_recommendations("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_recommendations FK_commercial_opportunity_recommendations_CommercialCases_Busi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "FK_commercial_opportunity_recommendations_CommercialCases_Busi~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId", "NexoraSerial") REFERENCES public."CommercialCases"("BusinessUnitID", "Id", "MasterReference") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_recommendations FK_commercial_opportunity_recommendations_Leads_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "FK_commercial_opportunity_recommendations_Leads_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: commercial_opportunity_recommendations FK_commercial_opportunity_recommendations_commercial_opportuni~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "FK_commercial_opportunity_recommendations_commercial_opportuni~" FOREIGN KEY ("BusinessUnitId", "SupersedesRecommendationId") REFERENCES public.commercial_opportunity_recommendations("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: custom_field_dependencies FK_custom_field_dependencies_custom_field_definitions_DependsO~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_dependencies
    ADD CONSTRAINT "FK_custom_field_dependencies_custom_field_definitions_DependsO~" FOREIGN KEY ("DependsOnDefinitionId") REFERENCES public.custom_field_definitions("Id") ON DELETE RESTRICT;


--
-- Name: custom_field_dependencies FK_custom_field_dependencies_custom_field_versions_VersionId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_dependencies
    ADD CONSTRAINT "FK_custom_field_dependencies_custom_field_versions_VersionId" FOREIGN KEY ("VersionId") REFERENCES public.custom_field_versions("Id") ON DELETE RESTRICT;


--
-- Name: custom_field_options FK_custom_field_options_custom_field_versions_VersionId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_options
    ADD CONSTRAINT "FK_custom_field_options_custom_field_versions_VersionId" FOREIGN KEY ("VersionId") REFERENCES public.custom_field_versions("Id") ON DELETE RESTRICT;


--
-- Name: custom_field_rules FK_custom_field_rules_custom_field_versions_VersionId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_rules
    ADD CONSTRAINT "FK_custom_field_rules_custom_field_versions_VersionId" FOREIGN KEY ("VersionId") REFERENCES public.custom_field_versions("Id") ON DELETE RESTRICT;


--
-- Name: custom_field_values FK_custom_field_values_custom_field_definitions_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "FK_custom_field_values_custom_field_definitions_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "DefinitionId") REFERENCES public.custom_field_definitions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: custom_field_values FK_custom_field_values_custom_field_records_BusinessUnitId_Rec~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "FK_custom_field_values_custom_field_records_BusinessUnitId_Rec~" FOREIGN KEY ("BusinessUnitId", "RecordId") REFERENCES public.custom_field_records("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: custom_field_values FK_custom_field_values_custom_field_versions_DefinitionId_Defi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "FK_custom_field_values_custom_field_versions_DefinitionId_Defi~" FOREIGN KEY ("DefinitionId", "DefinitionVersion") REFERENCES public.custom_field_versions("DefinitionId", "VersionNumber") ON DELETE RESTRICT;


--
-- Name: custom_field_versions FK_custom_field_versions_custom_field_definitions_DefinitionId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_versions
    ADD CONSTRAINT "FK_custom_field_versions_custom_field_definitions_DefinitionId" FOREIGN KEY ("DefinitionId") REFERENCES public.custom_field_definitions("Id") ON DELETE RESTRICT;


--
-- Name: customer_identifiers FK_customer_identifiers_Customers_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_identifiers
    ADD CONSTRAINT "FK_customer_identifiers_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES public."Customers"("ID") ON DELETE RESTRICT;


--
-- Name: customer_ownerships FK_customer_owner_tenant_backup_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "FK_customer_owner_tenant_backup_user" FOREIGN KEY ("BusinessUnitId", "BackupUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: customer_ownerships FK_customer_owner_tenant_primary_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "FK_customer_owner_tenant_primary_user" FOREIGN KEY ("BusinessUnitId", "PrimaryUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: customer_ownerships FK_customer_ownerships_Customers_BusinessUnitId_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "FK_customer_ownerships_Customers_BusinessUnitId_CustomerId" FOREIGN KEY ("BusinessUnitId", "CustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_QuoteItems_QuoteItemId_Qu~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_QuoteItems_QuoteItemId_Qu~" FOREIGN KEY ("QuoteItemId", "QuoteId") REFERENCES public."QuoteItems"("ID", "QuoteID") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_Quotes_BusinessUnitId_Quo~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_Quotes_BusinessUnitId_Quo~" FOREIGN KEY ("BusinessUnitId", "QuoteId") REFERENCES public."Quotes"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_SourcingAwards_BusinessUn~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_SourcingAwards_BusinessUn~" FOREIGN KEY ("BusinessUnitId", "SourcingAwardId") REFERENCES public."SourcingAwards"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_SupplierQuotedItems_Busin~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_SupplierQuotedItems_Busin~" FOREIGN KEY ("BusinessUnitId", "SupplierQuotedItemId") REFERENCES public."SupplierQuotedItems"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_commercial_demand_lines_B~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_commercial_demand_lines_B~" FOREIGN KEY ("BusinessUnitId", "CommercialDemandLineId") REFERENCES public.commercial_demand_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_sourcing_cases_BusinessUn~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_sourcing_cases_BusinessUn~" FOREIGN KEY ("BusinessUnitId", "SourcingCaseId") REFERENCES public.sourcing_cases("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_supplier_quote_lines_Busi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_supplier_quote_lines_Busi~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteLineId") REFERENCES public.supplier_quote_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_supplier_quote_revisions_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_supplier_quote_revisions_~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteRevisionId") REFERENCES public.supplier_quote_revisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: customer_quote_sourcing_decisions FK_customer_quote_sourcing_decisions_supplier_quotes_BusinessU~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "FK_customer_quote_sourcing_decisions_supplier_quotes_BusinessU~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteId") REFERENCES public.supplier_quotes("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: delivery_proof_lines FK_delivery_proof_lines_OrderItems_OrderItemId_OrderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "FK_delivery_proof_lines_OrderItems_OrderItemId_OrderId" FOREIGN KEY ("OrderItemId", "OrderId") REFERENCES public."OrderItems"("ID", "OrderID") ON DELETE RESTRICT;


--
-- Name: delivery_proof_lines FK_delivery_proof_lines_Orders_BusinessUnitId_OrderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "FK_delivery_proof_lines_Orders_BusinessUnitId_OrderId" FOREIGN KEY ("BusinessUnitId", "OrderId") REFERENCES public."Orders"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: delivery_proof_lines FK_delivery_proof_lines_ShipmentItems_ShipmentItemId_ShipmentId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "FK_delivery_proof_lines_ShipmentItems_ShipmentItemId_ShipmentId" FOREIGN KEY ("ShipmentItemId", "ShipmentId") REFERENCES public."ShipmentItems"("ID", "ShipmentID") ON DELETE RESTRICT;


--
-- Name: delivery_proof_lines FK_delivery_proof_lines_Shipments_BusinessUnitId_ShipmentId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "FK_delivery_proof_lines_Shipments_BusinessUnitId_ShipmentId" FOREIGN KEY ("BusinessUnitId", "ShipmentId") REFERENCES public."Shipments"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: delivery_proof_lines FK_delivery_proof_lines_delivery_proofs_BusinessUnitId_Deliver~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "FK_delivery_proof_lines_delivery_proofs_BusinessUnitId_Deliver~" FOREIGN KEY ("BusinessUnitId", "DeliveryProofId") REFERENCES public.delivery_proofs("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: delivery_proofs FK_delivery_proofs_Attachments_PhotoEvidenceId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "FK_delivery_proofs_Attachments_PhotoEvidenceId" FOREIGN KEY ("PhotoEvidenceId") REFERENCES public."Attachments"("ID") ON DELETE RESTRICT;


--
-- Name: delivery_proofs FK_delivery_proofs_Attachments_SignatureEvidenceId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "FK_delivery_proofs_Attachments_SignatureEvidenceId" FOREIGN KEY ("SignatureEvidenceId") REFERENCES public."Attachments"("ID") ON DELETE RESTRICT;


--
-- Name: delivery_proofs FK_delivery_proofs_Attachments_StampEvidenceId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "FK_delivery_proofs_Attachments_StampEvidenceId" FOREIGN KEY ("StampEvidenceId") REFERENCES public."Attachments"("ID") ON DELETE RESTRICT;


--
-- Name: delivery_proofs FK_delivery_proofs_CommercialCases_BusinessUnitId_CommercialCa~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "FK_delivery_proofs_CommercialCases_BusinessUnitId_CommercialCa~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: delivery_proofs FK_delivery_proofs_Shipments_BusinessUnitId_ShipmentId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "FK_delivery_proofs_Shipments_BusinessUnitId_ShipmentId" FOREIGN KEY ("BusinessUnitId", "ShipmentId") REFERENCES public."Shipments"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: delivery_shortfall_decisions FK_delivery_shortfall_decisions_Shipments_BusinessUnitId_Shipm~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_shortfall_decisions
    ADD CONSTRAINT "FK_delivery_shortfall_decisions_Shipments_BusinessUnitId_Shipm~" FOREIGN KEY ("BusinessUnitId", "ShipmentId") REFERENCES public."Shipments"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: delivery_shortfall_decisions FK_delivery_shortfall_decisions_delivery_proof_lines_BusinessU~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_shortfall_decisions
    ADD CONSTRAINT "FK_delivery_shortfall_decisions_delivery_proof_lines_BusinessU~" FOREIGN KEY ("BusinessUnitId", "DeliveryProofLineId") REFERENCES public.delivery_proof_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: document_pages FK_document_pages_source_documents_business_unit_id_document_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_pages
    ADD CONSTRAINT "FK_document_pages_source_documents_business_unit_id_document_id" FOREIGN KEY (business_unit_id, document_id) REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: document_regions FK_document_regions_document_pages_business_unit_id_page_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_regions
    ADD CONSTRAINT "FK_document_regions_document_pages_business_unit_id_page_id" FOREIGN KEY (business_unit_id, page_id) REFERENCES public.document_pages(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: evidence_retention_policies FK_evidence_retention_policies_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evidence_retention_policies
    ADD CONSTRAINT "FK_evidence_retention_policies_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: extraction_dead_letter_events FK_extraction_dead_letter_events_ExtractionJobs_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_dead_letter_events
    ADD CONSTRAINT "FK_extraction_dead_letter_events_ExtractionJobs_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "ExtractionJobId") REFERENCES public."ExtractionJobs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: extraction_runs FK_extraction_runs_ExtractionJobs_business_unit_id_extraction_j; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT "FK_extraction_runs_ExtractionJobs_business_unit_id_extraction_j" FOREIGN KEY (business_unit_id, extraction_job_id) REFERENCES public."ExtractionJobs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: extraction_runs FK_extraction_runs_source_documents_business_unit_id_source_do~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT "FK_extraction_runs_source_documents_business_unit_id_source_do~" FOREIGN KEY (business_unit_id, source_document_id) REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: field_evidence FK_field_evidence_canonical_inquiries_business_unit_id_inquiry~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.field_evidence
    ADD CONSTRAINT "FK_field_evidence_canonical_inquiries_business_unit_id_inquiry~" FOREIGN KEY (business_unit_id, inquiry_id) REFERENCES public.canonical_inquiries(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: field_evidence FK_field_evidence_canonical_line_items_business_unit_id_line_i~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.field_evidence
    ADD CONSTRAINT "FK_field_evidence_canonical_line_items_business_unit_id_line_i~" FOREIGN KEY (business_unit_id, line_item_id) REFERENCES public.canonical_line_items(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: field_evidence FK_field_evidence_document_regions_business_unit_id_region_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.field_evidence
    ADD CONSTRAINT "FK_field_evidence_document_regions_business_unit_id_region_id" FOREIGN KEY (business_unit_id, region_id) REFERENCES public.document_regions(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: field_evidence FK_field_evidence_extraction_runs_business_unit_id_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.field_evidence
    ADD CONSTRAINT "FK_field_evidence_extraction_runs_business_unit_id_run_id" FOREIGN KEY (business_unit_id, run_id) REFERENCES public.extraction_runs(business_unit_id, run_id) ON DELETE RESTRICT;


--
-- Name: follow_up_transition_events FK_follow_up_event_tenant_task; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.follow_up_transition_events
    ADD CONSTRAINT "FK_follow_up_event_tenant_task" FOREIGN KEY ("BusinessUnitId", "FollowUpTaskId") REFERENCES public.follow_up_tasks("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: follow_up_tasks FK_follow_up_tenant_customer; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.follow_up_tasks
    ADD CONSTRAINT "FK_follow_up_tenant_customer" FOREIGN KEY ("BusinessUnitId", "CustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: follow_up_tasks FK_follow_up_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.follow_up_tasks
    ADD CONSTRAINT "FK_follow_up_tenant_user" FOREIGN KEY ("BusinessUnitId", "AssignedToUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: goods_receipt_lines FK_goods_receipt_lines_goods_receipts_BusinessUnitId_GoodsRece~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipt_lines
    ADD CONSTRAINT "FK_goods_receipt_lines_goods_receipts_BusinessUnitId_GoodsRece~" FOREIGN KEY ("BusinessUnitId", "GoodsReceiptId") REFERENCES public.goods_receipts("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: goods_receipt_lines FK_goods_receipt_lines_inventory_movements_BusinessUnitId_Inve~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipt_lines
    ADD CONSTRAINT "FK_goods_receipt_lines_inventory_movements_BusinessUnitId_Inve~" FOREIGN KEY ("BusinessUnitId", "InventoryMovementId", "ProductId", "InventoryId", "WarehouseId") REFERENCES public.inventory_movements("BusinessUnitId", "Id", "ProductId", "InventoryId", "WarehouseId") ON DELETE RESTRICT;


--
-- Name: goods_receipt_lines FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipt_lines
    ADD CONSTRAINT "FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderLineId", "ProductId", "WarehouseId") REFERENCES public.supplier_purchase_order_lines("BusinessUnitId", "Id", "ProductId", "WarehouseId") ON DELETE RESTRICT;


--
-- Name: goods_receipts FK_goods_receipts_Warehouses_BusinessUnitId_WarehouseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipts
    ADD CONSTRAINT "FK_goods_receipts_Warehouses_BusinessUnitId_WarehouseId" FOREIGN KEY ("BusinessUnitId", "WarehouseId") REFERENCES public."Warehouses"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: goods_receipts FK_goods_receipts_supplier_purchase_orders_BusinessUnitId_Supp~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipts
    ADD CONSTRAINT "FK_goods_receipts_supplier_purchase_orders_BusinessUnitId_Supp~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderId") REFERENCES public.supplier_purchase_orders("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: governed_artifact_events FK_governed_artifact_events_governed_artifacts_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifact_events
    ADD CONSTRAINT "FK_governed_artifact_events_governed_artifacts_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "GovernedArtifactId") REFERENCES public.governed_artifacts("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: governed_artifact_versions FK_governed_artifact_versions_governed_artifacts_BusinessUnitI~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifact_versions
    ADD CONSTRAINT "FK_governed_artifact_versions_governed_artifacts_BusinessUnitI~" FOREIGN KEY ("BusinessUnitId", "GovernedArtifactId") REFERENCES public.governed_artifacts("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: governed_artifacts FK_governed_artifacts_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifacts
    ADD CONSTRAINT "FK_governed_artifacts_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: human_action_events FK_human_action_events_human_action_items_BusinessUnitId_Human~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.human_action_events
    ADD CONSTRAINT "FK_human_action_events_human_action_items_BusinessUnitId_Human~" FOREIGN KEY ("BusinessUnitId", "HumanActionItemId") REFERENCES public.human_action_items("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: human_action_items FK_human_action_items_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.human_action_items
    ADD CONSTRAINT "FK_human_action_items_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: inbound_logistics_policies FK_inbound_logistics_policies_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inbound_logistics_policies
    ADD CONSTRAINT "FK_inbound_logistics_policies_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: incoming_inventory FK_incoming_inventory_Inventory_BusinessUnitId_InventoryId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "FK_incoming_inventory_Inventory_BusinessUnitId_InventoryId" FOREIGN KEY ("BusinessUnitId", "InventoryId") REFERENCES public."Inventory"("Buid", "Id") ON DELETE RESTRICT;


--
-- Name: incoming_inventory FK_incoming_inventory_Products_BusinessUnitId_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "FK_incoming_inventory_Products_BusinessUnitId_ProductId" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: incoming_inventory FK_incoming_inventory_Warehouses_BusinessUnitId_WarehouseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "FK_incoming_inventory_Warehouses_BusinessUnitId_WarehouseId" FOREIGN KEY ("BusinessUnitId", "WarehouseId") REFERENCES public."Warehouses"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: inventory_movements FK_inventory_movements_Inventory_BusinessUnitId_InventoryId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "FK_inventory_movements_Inventory_BusinessUnitId_InventoryId" FOREIGN KEY ("BusinessUnitId", "InventoryId") REFERENCES public."Inventory"("Buid", "Id") ON DELETE RESTRICT;


--
-- Name: inventory_movements FK_inventory_movements_Products_BusinessUnitId_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "FK_inventory_movements_Products_BusinessUnitId_ProductId" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: inventory_movements FK_inventory_movements_Warehouses_BusinessUnitId_WarehouseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "FK_inventory_movements_Warehouses_BusinessUnitId_WarehouseId" FOREIGN KEY ("BusinessUnitId", "WarehouseId") REFERENCES public."Warehouses"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: inventory_reorder_alerts FK_inventory_reorder_alerts_Inventory_BusinessUnitId_Inventory~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_reorder_alerts
    ADD CONSTRAINT "FK_inventory_reorder_alerts_Inventory_BusinessUnitId_Inventory~" FOREIGN KEY ("BusinessUnitId", "InventoryId") REFERENCES public."Inventory"("Buid", "Id") ON DELETE RESTRICT;


--
-- Name: lead_assignments FK_lead_assignment_tenant_actor; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "FK_lead_assignment_tenant_actor" FOREIGN KEY ("BusinessUnitId", "AssignedByUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_assignments FK_lead_assignment_tenant_from_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "FK_lead_assignment_tenant_from_user" FOREIGN KEY ("BusinessUnitId", "FromUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_assignments FK_lead_assignment_tenant_to_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "FK_lead_assignment_tenant_to_user" FOREIGN KEY ("BusinessUnitId", "ToUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_assignments FK_lead_assignments_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "FK_lead_assignments_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_assignments FK_lead_assignments_customer_ownerships_BusinessUnitId_Ownersh~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "FK_lead_assignments_customer_ownerships_BusinessUnitId_Ownersh~" FOREIGN KEY ("BusinessUnitId", "OwnershipId") REFERENCES public.customer_ownerships("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: lead_assignments FK_lead_assignments_lead_routing_decisions_BusinessUnitId_Rout~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "FK_lead_assignments_lead_routing_decisions_BusinessUnitId_Rout~" FOREIGN KEY ("BusinessUnitId", "RoutingDecisionId") REFERENCES public.lead_routing_decisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: lead_customer_match_candidates FK_lead_customer_match_candidates_Customers_BusinessUnitId_Cus~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_customer_match_candidates
    ADD CONSTRAINT "FK_lead_customer_match_candidates_Customers_BusinessUnitId_Cus~" FOREIGN KEY ("BusinessUnitId", "CustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_customer_match_candidates FK_lead_customer_match_candidates_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_customer_match_candidates
    ADD CONSTRAINT "FK_lead_customer_match_candidates_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE CASCADE;


--
-- Name: lead_routing_decisions FK_lead_decision_tenant_selected_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "FK_lead_decision_tenant_selected_user" FOREIGN KEY ("BusinessUnitId", "SelectedUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_routing_decisions FK_lead_decision_tenant_suggested_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "FK_lead_decision_tenant_suggested_user" FOREIGN KEY ("BusinessUnitId", "SuggestedUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_line_commercial_resolutions FK_lead_line_commercial_resolutions_LeadItemRevisions_Business~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "FK_lead_line_commercial_resolutions_LeadItemRevisions_Business~" FOREIGN KEY ("BusinessUnitId", "LeadLineId") REFERENCES public."LeadItemRevisions"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: lead_line_commercial_resolutions FK_lead_line_commercial_resolutions_LeadRevisions_BusinessUnit~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "FK_lead_line_commercial_resolutions_LeadRevisions_BusinessUnit~" FOREIGN KEY ("BusinessUnitId", "LeadRevisionId") REFERENCES public."LeadRevisions"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: lead_line_commercial_resolutions FK_lead_line_commercial_resolutions_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "FK_lead_line_commercial_resolutions_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_line_commercial_resolutions FK_lead_line_commercial_resolutions_Products_BusinessUnitId_Pr~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "FK_lead_line_commercial_resolutions_Products_BusinessUnitId_Pr~" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_line_commercial_resolutions FK_lead_line_commercial_resolutions_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "FK_lead_line_commercial_resolutions_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: lead_line_commercial_resolutions FK_lead_line_commercial_resolutions_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "FK_lead_line_commercial_resolutions_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_routing_decisions FK_lead_routing_decisions_Customers_BusinessUnitId_CustomerId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "FK_lead_routing_decisions_Customers_BusinessUnitId_CustomerId" FOREIGN KEY ("BusinessUnitId", "CustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_routing_decisions FK_lead_routing_decisions_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "FK_lead_routing_decisions_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: lead_routing_decisions FK_lead_routing_decisions_customer_identifiers_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "FK_lead_routing_decisions_customer_identifiers_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "MatchedIdentifierId") REFERENCES public.customer_identifiers("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: lead_routing_decisions FK_lead_routing_decisions_customer_ownerships_BusinessUnitId_O~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "FK_lead_routing_decisions_customer_ownerships_BusinessUnitId_O~" FOREIGN KEY ("BusinessUnitId", "OwnershipId") REFERENCES public.customer_ownerships("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: learning_governance_events FK_learning_governance_events_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.learning_governance_events
    ADD CONSTRAINT "FK_learning_governance_events_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: lifecycle_outbox_messages FK_lifecycle_outbox_messages_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lifecycle_outbox_messages
    ADD CONSTRAINT "FK_lifecycle_outbox_messages_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: lifecycle_outbox_messages FK_lifecycle_outbox_messages_commercial_lifecycle_events_Busin~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lifecycle_outbox_messages
    ADD CONSTRAINT "FK_lifecycle_outbox_messages_commercial_lifecycle_events_Busin~" FOREIGN KEY ("BusinessUnitId", "LifecycleEventId") REFERENCES public.commercial_lifecycle_events("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: material_lot_certificates FK_material_lot_certificates_Attachments_AttachmentId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_certificates
    ADD CONSTRAINT "FK_material_lot_certificates_Attachments_AttachmentId" FOREIGN KEY ("AttachmentId") REFERENCES public."Attachments"("ID") ON DELETE RESTRICT;


--
-- Name: material_lot_certificates FK_material_lot_certificates_material_lots_BusinessUnitId_Mate~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_certificates
    ADD CONSTRAINT "FK_material_lot_certificates_material_lots_BusinessUnitId_Mate~" FOREIGN KEY ("BusinessUnitId", "MaterialLotId") REFERENCES public.material_lots("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: material_lot_consumptions FK_material_lot_consumptions_CommercialCases_BusinessUnitId_Co~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "FK_material_lot_consumptions_CommercialCases_BusinessUnitId_Co~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: material_lot_consumptions FK_material_lot_consumptions_OrderItems_OrderItemId_OrderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "FK_material_lot_consumptions_OrderItems_OrderItemId_OrderId" FOREIGN KEY ("OrderItemId", "OrderId") REFERENCES public."OrderItems"("ID", "OrderID") ON DELETE RESTRICT;


--
-- Name: material_lot_consumptions FK_material_lot_consumptions_Orders_BusinessUnitId_OrderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "FK_material_lot_consumptions_Orders_BusinessUnitId_OrderId" FOREIGN KEY ("BusinessUnitId", "OrderId") REFERENCES public."Orders"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: material_lot_consumptions FK_material_lot_consumptions_Shipments_BusinessUnitId_Shipment~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "FK_material_lot_consumptions_Shipments_BusinessUnitId_Shipment~" FOREIGN KEY ("BusinessUnitId", "ShipmentId") REFERENCES public."Shipments"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: material_lot_consumptions FK_material_lot_consumptions_material_lots_BusinessUnitId_Mate~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "FK_material_lot_consumptions_material_lots_BusinessUnitId_Mate~" FOREIGN KEY ("BusinessUnitId", "MaterialLotId") REFERENCES public.material_lots("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_CommercialCases_BusinessUnitId_CommercialCase~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_CommercialCases_BusinessUnitId_CommercialCase~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_Inventory_BusinessUnitId_InventoryId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_Inventory_BusinessUnitId_InventoryId" FOREIGN KEY ("BusinessUnitId", "InventoryId") REFERENCES public."Inventory"("Buid", "Id") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_Products_BusinessUnitId_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_Products_BusinessUnitId_ProductId" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_Warehouses_BusinessUnitId_WarehouseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_Warehouses_BusinessUnitId_WarehouseId" FOREIGN KEY ("BusinessUnitId", "WarehouseId") REFERENCES public."Warehouses"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_goods_receipts_BusinessUnitId_GoodsReceiptId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_goods_receipts_BusinessUnitId_GoodsReceiptId" FOREIGN KEY ("BusinessUnitId", "GoodsReceiptId") REFERENCES public.goods_receipts("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_supplier_purchase_order_lines_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_supplier_purchase_order_lines_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderLineId") REFERENCES public.supplier_purchase_order_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: material_lots FK_material_lots_supplier_purchase_orders_BusinessUnitId_Suppl~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "FK_material_lots_supplier_purchase_orders_BusinessUnitId_Suppl~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderId") REFERENCES public.supplier_purchase_orders("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: ports_of_entry FK_ports_of_entry_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ports_of_entry
    ADD CONSTRAINT "FK_ports_of_entry_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: procurement_callback_receipts FK_procurement_callback_receipts_procurement_handoffs_Business~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_callback_receipts
    ADD CONSTRAINT "FK_procurement_callback_receipts_procurement_handoffs_Business~" FOREIGN KEY ("BusinessUnitId", "ProcurementHandoffId") REFERENCES public.procurement_handoffs("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: procurement_events FK_procurement_events_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_events
    ADD CONSTRAINT "FK_procurement_events_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_OrderItems_CustomerOrderLineId_Custome~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_OrderItems_CustomerOrderLineId_Custome~" FOREIGN KEY ("CustomerOrderLineId", "CustomerOrderId") REFERENCES public."OrderItems"("ID", "OrderID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_Orders_BusinessUnitId_CustomerOrderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_Orders_BusinessUnitId_CustomerOrderId" FOREIGN KEY ("BusinessUnitId", "CustomerOrderId") REFERENCES public."Orders"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_SourcingAwards_BusinessUnitId_Sourcing~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_SourcingAwards_BusinessUnitId_Sourcing~" FOREIGN KEY ("BusinessUnitId", "SourcingAwardId") REFERENCES public."SourcingAwards"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_SupplierQuotedItems_BusinessUnitId_Sup~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_SupplierQuotedItems_BusinessUnitId_Sup~" FOREIGN KEY ("BusinessUnitId", "SupplierQuotedItemId") REFERENCES public."SupplierQuotedItems"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_Warehouses_BusinessUnitId_WarehouseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_Warehouses_BusinessUnitId_WarehouseId" FOREIGN KEY ("BusinessUnitId", "WarehouseId") REFERENCES public."Warehouses"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: procurement_handoffs FK_procurement_handoffs_commercial_demand_lines_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "FK_procurement_handoffs_commercial_demand_lines_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "CommercialDemandLineId") REFERENCES public.commercial_demand_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: procurement_outbox FK_procurement_outbox_SupplierSolicitations_BusinessUnitId_Sup~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_outbox
    ADD CONSTRAINT "FK_procurement_outbox_SupplierSolicitations_BusinessUnitId_Sup~" FOREIGN KEY ("BusinessUnitId", "SupplierSolicitationId") REFERENCES public."SupplierSolicitations"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: product_aliases FK_product_aliases_Products_BusinessUnitId_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.product_aliases
    ADD CONSTRAINT "FK_product_aliases_Products_BusinessUnitId_ProductId" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: product_supersessions FK_product_supersessions_Products_BusinessUnitId_ReplacementPr~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.product_supersessions
    ADD CONSTRAINT "FK_product_supersessions_Products_BusinessUnitId_ReplacementPr~" FOREIGN KEY ("BusinessUnitId", "ReplacementProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: product_supersessions FK_product_supersessions_Products_BusinessUnitId_SupersededPro~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.product_supersessions
    ADD CONSTRAINT "FK_product_supersessions_Products_BusinessUnitId_SupersededPro~" FOREIGN KEY ("BusinessUnitId", "SupersededProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: quote_delivery_requests FK_quote_delivery_requests_Quotes_BusinessUnitId_QuoteId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.quote_delivery_requests
    ADD CONSTRAINT "FK_quote_delivery_requests_Quotes_BusinessUnitId_QuoteId" FOREIGN KEY ("BusinessUnitId", "QuoteId") REFERENCES public."Quotes"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: commercial_activities FK_sales_activity_tenant_assignment; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_activities
    ADD CONSTRAINT "FK_sales_activity_tenant_assignment" FOREIGN KEY ("BusinessUnitId", "LeadAssignmentId") REFERENCES public.lead_assignments("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: commercial_activities FK_sales_activity_tenant_customer; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_activities
    ADD CONSTRAINT "FK_sales_activity_tenant_customer" FOREIGN KEY ("BusinessUnitId", "CustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: commercial_activities FK_sales_activity_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_activities
    ADD CONSTRAINT "FK_sales_activity_tenant_user" FOREIGN KEY ("BusinessUnitId", "SalesRepUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_coaching_acknowledgements FK_sales_coaching_ack_manager_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "FK_sales_coaching_ack_manager_tenant_user" FOREIGN KEY ("BusinessUnitId", "ManagerUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_coaching_acknowledgements FK_sales_coaching_ack_rep_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "FK_sales_coaching_ack_rep_tenant_user" FOREIGN KEY ("BusinessUnitId", "SalesRepUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_coaching_acknowledgements FK_sales_coaching_acknowledgements_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "FK_sales_coaching_acknowledgements_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: sales_contributions FK_sales_contribution_tenant_customer; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_contributions
    ADD CONSTRAINT "FK_sales_contribution_tenant_customer" FOREIGN KEY ("BusinessUnitId", "CustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_contributions FK_sales_contribution_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_contributions
    ADD CONSTRAINT "FK_sales_contribution_tenant_user" FOREIGN KEY ("BusinessUnitId", "SalesRepUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_team_memberships FK_sales_membership_tenant_team; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_team_memberships
    ADD CONSTRAINT "FK_sales_membership_tenant_team" FOREIGN KEY ("BusinessUnitId", "TeamId") REFERENCES public."Teams"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_team_memberships FK_sales_membership_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_team_memberships
    ADD CONSTRAINT "FK_sales_membership_tenant_user" FOREIGN KEY ("BusinessUnitId", "UserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: sales_rep_profiles FK_sales_profile_tenant_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_rep_profiles
    ADD CONSTRAINT "FK_sales_profile_tenant_user" FOREIGN KEY ("BusinessUnitId", "UserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: source_document_occurrences FK_source_document_occurrences_ExtractionJobs_business_unit_id_; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT "FK_source_document_occurrences_ExtractionJobs_business_unit_id_" FOREIGN KEY (business_unit_id, extraction_job_id) REFERENCES public."ExtractionJobs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: source_document_occurrences FK_source_document_occurrences_document_corpora_business_unit_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT "FK_source_document_occurrences_document_corpora_business_unit_~" FOREIGN KEY (business_unit_id, corpus_id) REFERENCES public.document_corpora(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: source_document_occurrences FK_source_document_occurrences_source_document_occurrences_bus~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT "FK_source_document_occurrences_source_document_occurrences_bus~" FOREIGN KEY (business_unit_id, original_occurrence_id) REFERENCES public.source_document_occurrences(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: source_document_occurrences FK_source_document_occurrences_source_documents_business_unit_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT "FK_source_document_occurrences_source_documents_business_unit_~" FOREIGN KEY (business_unit_id, source_document_id) REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: source_documents FK_source_documents_ExtractionJobs_business_unit_id_extraction_; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_documents
    ADD CONSTRAINT "FK_source_documents_ExtractionJobs_business_unit_id_extraction_" FOREIGN KEY (business_unit_id, extraction_job_id) REFERENCES public."ExtractionJobs"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: source_documents FK_source_documents_document_corpora_business_unit_id_corpus_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_documents
    ADD CONSTRAINT "FK_source_documents_document_corpora_business_unit_id_corpus_id" FOREIGN KEY (business_unit_id, corpus_id) REFERENCES public.document_corpora(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: sourcing_case_candidates FK_sourcing_case_candidates_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_case_candidates
    ADD CONSTRAINT "FK_sourcing_case_candidates_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: sourcing_case_candidates FK_sourcing_case_candidates_sourcing_cases_BusinessUnitId_Sour~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_case_candidates
    ADD CONSTRAINT "FK_sourcing_case_candidates_sourcing_cases_BusinessUnitId_Sour~" FOREIGN KEY ("BusinessUnitId", "SourcingCaseId") REFERENCES public.sourcing_cases("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: sourcing_cases FK_sourcing_cases_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "FK_sourcing_cases_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: sourcing_cases FK_sourcing_cases_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "FK_sourcing_cases_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: sourcing_cases FK_sourcing_cases_commercial_demand_lines_BusinessUnitId_Comme~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "FK_sourcing_cases_commercial_demand_lines_BusinessUnitId_Comme~" FOREIGN KEY ("BusinessUnitId", "CommercialDemandLineId") REFERENCES public.commercial_demand_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: stock_reservations FK_stock_reservations_Inventory_BusinessUnitId_InventoryId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_reservations
    ADD CONSTRAINT "FK_stock_reservations_Inventory_BusinessUnitId_InventoryId" FOREIGN KEY ("BusinessUnitId", "InventoryId") REFERENCES public."Inventory"("Buid", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_negotiation_decisions FK_supplier_negotiation_decisions_supplier_quote_revisions_Bus~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_negotiation_decisions
    ADD CONSTRAINT "FK_supplier_negotiation_decisions_supplier_quote_revisions_Bus~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteId", "SupplierQuoteRevisionId") REFERENCES public.supplier_quote_revisions("BusinessUnitId", "SupplierQuoteId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_negotiation_decisions FK_supplier_negotiation_decisions_supplier_quotes_BusinessUnit~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_negotiation_decisions
    ADD CONSTRAINT "FK_supplier_negotiation_decisions_supplier_quotes_BusinessUnit~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteId") REFERENCES public.supplier_quotes("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_Inventory_BusinessUnitId_Inve~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_Inventory_BusinessUnitId_Inve~" FOREIGN KEY ("BusinessUnitId", "InventoryId") REFERENCES public."Inventory"("Buid", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_Products_BusinessUnitId_Produ~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_Products_BusinessUnitId_Produ~" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_RFQItems_RfqItemId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_RFQItems_RfqItemId_RfqId" FOREIGN KEY ("RfqItemId", "RfqId") REFERENCES public."RFQItems"("ID", "RFQID") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_SourcingAwards_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_SourcingAwards_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "SourcingAwardId") REFERENCES public."SourcingAwards"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_SupplierQuotedItems_BusinessU~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_SupplierQuotedItems_BusinessU~" FOREIGN KEY ("BusinessUnitId", "SupplierQuotedItemId") REFERENCES public."SupplierQuotedItems"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_Warehouses_BusinessUnitId_War~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_Warehouses_BusinessUnitId_War~" FOREIGN KEY ("BusinessUnitId", "WarehouseId") REFERENCES public."Warehouses"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_incoming_inventory_BusinessUn~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_incoming_inventory_BusinessUn~" FOREIGN KEY ("BusinessUnitId", "IncomingInventoryId") REFERENCES public.incoming_inventory("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_order_lines FK_supplier_purchase_order_lines_supplier_purchase_orders_Busi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "FK_supplier_purchase_order_lines_supplier_purchase_orders_Busi~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderId") REFERENCES public.supplier_purchase_orders("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_orders FK_supplier_purchase_orders_CommercialCases_BusinessUnitId_Com~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "FK_supplier_purchase_orders_CommercialCases_BusinessUnitId_Com~" FOREIGN KEY ("BusinessUnitId", "CommercialCaseId") REFERENCES public."CommercialCases"("BusinessUnitID", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_orders FK_supplier_purchase_orders_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "FK_supplier_purchase_orders_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_orders FK_supplier_purchase_orders_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "FK_supplier_purchase_orders_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_purchase_orders FK_supplier_purchase_orders_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "FK_supplier_purchase_orders_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: supplier_quote_field_evidence FK_supplier_quote_field_evidence_supplier_quote_lines_Business~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_field_evidence
    ADD CONSTRAINT "FK_supplier_quote_field_evidence_supplier_quote_lines_Business~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteLineId") REFERENCES public.supplier_quote_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quote_field_evidence FK_supplier_quote_field_evidence_supplier_quote_revisions_Busi~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_field_evidence
    ADD CONSTRAINT "FK_supplier_quote_field_evidence_supplier_quote_revisions_Busi~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteRevisionId") REFERENCES public.supplier_quote_revisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quote_lines FK_supplier_quote_lines_commercial_demand_lines_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_lines
    ADD CONSTRAINT "FK_supplier_quote_lines_commercial_demand_lines_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "CommercialDemandLineId") REFERENCES public.commercial_demand_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quote_lines FK_supplier_quote_lines_supplier_quote_revisions_BusinessUnitI~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_lines
    ADD CONSTRAINT "FK_supplier_quote_lines_supplier_quote_revisions_BusinessUnitI~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteRevisionId") REFERENCES public.supplier_quote_revisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quote_review_decisions FK_supplier_quote_review_decisions_supplier_quote_field_eviden~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_review_decisions
    ADD CONSTRAINT "FK_supplier_quote_review_decisions_supplier_quote_field_eviden~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteFieldEvidenceId") REFERENCES public.supplier_quote_field_evidence("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quote_review_decisions FK_supplier_quote_review_decisions_supplier_quote_revisions_Bu~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_review_decisions
    ADD CONSTRAINT "FK_supplier_quote_review_decisions_supplier_quote_revisions_Bu~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteRevisionId") REFERENCES public.supplier_quote_revisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quote_revisions FK_supplier_quote_revisions_Currency_BusinessUnitId_CurrencyId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "FK_supplier_quote_revisions_Currency_BusinessUnitId_CurrencyId" FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_quote_revisions FK_supplier_quote_revisions_source_documents_BusinessUnitId_So~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "FK_supplier_quote_revisions_source_documents_BusinessUnitId_So~" FOREIGN KEY ("BusinessUnitId", "SourceDocumentId") REFERENCES public.source_documents(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: supplier_quote_revisions FK_supplier_quote_revisions_supplier_quotes_BusinessUnitId_Sup~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "FK_supplier_quote_revisions_supplier_quotes_BusinessUnitId_Sup~" FOREIGN KEY ("BusinessUnitId", "SupplierQuoteId") REFERENCES public.supplier_quotes("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quotes FK_supplier_quotes_RFQ_BusinessUnitId_RfqId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "FK_supplier_quotes_RFQ_BusinessUnitId_RfqId" FOREIGN KEY ("BusinessUnitId", "RfqId") REFERENCES public."RFQ"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_quotes FK_supplier_quotes_SupplierSolicitations_BusinessUnitId_Suppli~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "FK_supplier_quotes_SupplierSolicitations_BusinessUnitId_Suppli~" FOREIGN KEY ("BusinessUnitId", "SupplierSolicitationId") REFERENCES public."SupplierSolicitations"("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_quotes FK_supplier_quotes_Suppliers_SupplierId_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "FK_supplier_quotes_Suppliers_SupplierId_BusinessUnitId" FOREIGN KEY ("SupplierId", "BusinessUnitId") REFERENCES public."Suppliers"("ID", "BUID") ON DELETE RESTRICT;


--
-- Name: supplier_quotes FK_supplier_quotes_sourcing_cases_BusinessUnitId_SourcingCaseId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "FK_supplier_quotes_sourcing_cases_BusinessUnitId_SourcingCaseId" FOREIGN KEY ("BusinessUnitId", "SourcingCaseId") REFERENCES public.sourcing_cases("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_shipment_lines FK_supplier_shipment_lines_Products_BusinessUnitId_ProductId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "FK_supplier_shipment_lines_Products_BusinessUnitId_ProductId" FOREIGN KEY ("BusinessUnitId", "ProductId") REFERENCES public."Products"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: supplier_shipment_lines FK_supplier_shipment_lines_supplier_purchase_order_lines_Busin~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "FK_supplier_shipment_lines_supplier_purchase_order_lines_Busin~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderLineId") REFERENCES public.supplier_purchase_order_lines("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_shipment_lines FK_supplier_shipment_lines_supplier_shipments_BusinessUnitId_S~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "FK_supplier_shipment_lines_supplier_shipments_BusinessUnitId_S~" FOREIGN KEY ("BusinessUnitId", "SupplierShipmentId") REFERENCES public.supplier_shipments("BusinessUnitId", "Id") ON DELETE CASCADE;


--
-- Name: supplier_shipments FK_supplier_shipments_ports_of_entry_BusinessUnitId_PortOfEntr~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipments
    ADD CONSTRAINT "FK_supplier_shipments_ports_of_entry_BusinessUnitId_PortOfEntr~" FOREIGN KEY ("BusinessUnitId", "PortOfEntryId") REFERENCES public.ports_of_entry("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: supplier_shipments FK_supplier_shipments_supplier_purchase_orders_BusinessUnitId_~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipments
    ADD CONSTRAINT "FK_supplier_shipments_supplier_purchase_orders_BusinessUnitId_~" FOREIGN KEY ("BusinessUnitId", "SupplierPurchaseOrderId") REFERENCES public.supplier_purchase_orders("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: tenant_governance_audit_events FK_tenant_governance_audit_events_BusinessUnits_BusinessUnitId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tenant_governance_audit_events
    ADD CONSTRAINT "FK_tenant_governance_audit_events_BusinessUnits_BusinessUnitId" FOREIGN KEY ("BusinessUnitId") REFERENCES public."BusinessUnits"("ID") ON DELETE RESTRICT;


--
-- Name: unassigned_work_items FK_unassigned_tenant_claimed_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "FK_unassigned_tenant_claimed_user" FOREIGN KEY ("BusinessUnitId", "ClaimedByUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: unassigned_work_items FK_unassigned_tenant_suggested_customer; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "FK_unassigned_tenant_suggested_customer" FOREIGN KEY ("BusinessUnitId", "SuggestedCustomerId") REFERENCES public."Customers"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: unassigned_work_items FK_unassigned_tenant_suggested_user; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "FK_unassigned_tenant_suggested_user" FOREIGN KEY ("BusinessUnitId", "SuggestedUserId") REFERENCES public."Users"("BUID", "ID") ON DELETE RESTRICT;


--
-- Name: unassigned_work_items FK_unassigned_work_items_Leads_BusinessUnitId_LeadId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "FK_unassigned_work_items_Leads_BusinessUnitId_LeadId" FOREIGN KEY ("BusinessUnitId", "LeadId") REFERENCES public."Leads"("BusinessUnitID", "ID") ON DELETE RESTRICT;


--
-- Name: unassigned_work_items FK_unassigned_work_items_lead_routing_decisions_BusinessUnitId~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "FK_unassigned_work_items_lead_routing_decisions_BusinessUnitId~" FOREIGN KEY ("BusinessUnitId", "RoutingDecisionId") REFERENCES public.lead_routing_decisions("BusinessUnitId", "Id") ON DELETE RESTRICT;


--
-- Name: validation_findings FK_validation_findings_canonical_inquiries_business_unit_id_in~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.validation_findings
    ADD CONSTRAINT "FK_validation_findings_canonical_inquiries_business_unit_id_in~" FOREIGN KEY (business_unit_id, inquiry_id) REFERENCES public.canonical_inquiries(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: validation_findings FK_validation_findings_canonical_line_items_business_unit_id_l~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.validation_findings
    ADD CONSTRAINT "FK_validation_findings_canonical_line_items_business_unit_id_l~" FOREIGN KEY (business_unit_id, line_item_id) REFERENCES public.canonical_line_items(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: validation_findings FK_validation_findings_document_regions_business_unit_id_regio~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.validation_findings
    ADD CONSTRAINT "FK_validation_findings_document_regions_business_unit_id_regio~" FOREIGN KEY (business_unit_id, region_id) REFERENCES public.document_regions(business_unit_id, id) ON DELETE RESTRICT;


--
-- Name: validation_findings FK_validation_findings_extraction_runs_business_unit_id_extrac~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.validation_findings
    ADD CONSTRAINT "FK_validation_findings_extraction_runs_business_unit_id_extrac~" FOREIGN KEY (business_unit_id, extraction_run_id) REFERENCES public.extraction_runs(business_unit_id, id) ON DELETE RESTRICT;

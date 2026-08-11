-- ==========================================================================
-- Primary keys, unique keys, CHECK and EXCLUDE constraints
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
-- ==========================================================================

--
-- Name: SubscriptionInvoices AK_SubscriptionInvoices_TenantId_Id; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "AK_SubscriptionInvoices_TenantId_Id" UNIQUE ("TenantId", "Id");


--
-- Name: SubscriptionRevenueActions AK_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_Id; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "AK_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_Id" UNIQUE ("TenantId", "SubscriptionInvoiceId", "Id");


--
-- Name: SubscriptionTaxRules AK_SubscriptionTaxRules_Id_Version; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "AK_SubscriptionTaxRules_Id_Version" UNIQUE ("Id", "Version");


--
-- Name: TenantDataAssets AK_TenantDataAssets_TenantId_Id; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDataAssets"
    ADD CONSTRAINT "AK_TenantDataAssets_TenantId_Id" UNIQUE ("TenantId", "Id");


--
-- Name: UsageEvents AK_UsageEvents_TenantId_UsageEventId; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "AK_UsageEvents_TenantId_UsageEventId" UNIQUE ("TenantId", "UsageEventId");


--
-- Name: SubscriptionTaxRules EX_SubscriptionTaxRules_ApprovedInterval; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "EX_SubscriptionTaxRules_ApprovedInterval" EXCLUDE USING gist ("JurisdictionCode" WITH =, "BuyerCountryCode" WITH =, "Currency" WITH =, tstzrange("EffectiveFromUtc", COALESCE("EffectiveToUtc", 'infinity'::timestamp with time zone), '[)'::text) WITH &&) WHERE ((("Status")::text = 'Approved'::text));


--
-- Name: UsageCoverageSegments EX_UsageCoverageSegments_NoAuthoritativeOverlap; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageCoverageSegments"
    ADD CONSTRAINT "EX_UsageCoverageSegments_NoAuthoritativeOverlap" EXCLUDE USING gist ("TenantId" WITH =, "MeterKey" WITH =, tstzrange("StartUtc", "EndUtc", '[)'::text) WITH &&) WHERE ((("Completeness")::text = 'Complete'::text));


--
-- Name: AccountingOutbox PK_AccountingOutbox; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."AccountingOutbox"
    ADD CONSTRAINT "PK_AccountingOutbox" PRIMARY KEY ("Id");


--
-- Name: BillingStatementLines PK_BillingStatementLines; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."BillingStatementLines"
    ADD CONSTRAINT "PK_BillingStatementLines" PRIMARY KEY ("Id");


--
-- Name: BillingStatements PK_BillingStatements; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."BillingStatements"
    ADD CONSTRAINT "PK_BillingStatements" PRIMARY KEY ("Id");


--
-- Name: ImpersonationSessions PK_ImpersonationSessions; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."ImpersonationSessions"
    ADD CONSTRAINT "PK_ImpersonationSessions" PRIMARY KEY ("Id");


--
-- Name: Plans PK_Plans; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."Plans"
    ADD CONSTRAINT "PK_Plans" PRIMARY KEY ("Id");


--
-- Name: PlatformAuditLogs PK_PlatformAuditLogs; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformAuditLogs"
    ADD CONSTRAINT "PK_PlatformAuditLogs" PRIMARY KEY ("Id");


--
-- Name: PlatformBrowserTrusts PK_PlatformBrowserTrusts; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformBrowserTrusts"
    ADD CONSTRAINT "PK_PlatformBrowserTrusts" PRIMARY KEY ("Id");


--
-- Name: PlatformEmailSettings PK_PlatformEmailSettings; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformEmailSettings"
    ADD CONSTRAINT "PK_PlatformEmailSettings" PRIMARY KEY ("Id");


--
-- Name: PlatformMfaChallenges PK_PlatformMfaChallenges; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaChallenges"
    ADD CONSTRAINT "PK_PlatformMfaChallenges" PRIMARY KEY ("Id");


--
-- Name: PlatformMfaCredentials PK_PlatformMfaCredentials; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaCredentials"
    ADD CONSTRAINT "PK_PlatformMfaCredentials" PRIMARY KEY ("PlatformUserId");


--
-- Name: PlatformMfaPolicies PK_PlatformMfaPolicies; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaPolicies"
    ADD CONSTRAINT "PK_PlatformMfaPolicies" PRIMARY KEY ("Id");


--
-- Name: PlatformMfaRecoveryCodes PK_PlatformMfaRecoveryCodes; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformMfaRecoveryCodes"
    ADD CONSTRAINT "PK_PlatformMfaRecoveryCodes" PRIMARY KEY ("Id");


--
-- Name: PlatformSessions PK_PlatformSessions; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformSessions"
    ADD CONSTRAINT "PK_PlatformSessions" PRIMARY KEY ("Id");


--
-- Name: PlatformUsers PK_PlatformUsers; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."PlatformUsers"
    ADD CONSTRAINT "PK_PlatformUsers" PRIMARY KEY ("Id");


--
-- Name: ProvisioningDrafts PK_ProvisioningDrafts; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."ProvisioningDrafts"
    ADD CONSTRAINT "PK_ProvisioningDrafts" PRIMARY KEY ("Id");


--
-- Name: ProvisioningExecutions PK_ProvisioningExecutions; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."ProvisioningExecutions"
    ADD CONSTRAINT "PK_ProvisioningExecutions" PRIMARY KEY ("Id");


--
-- Name: ProvisioningSteps PK_ProvisioningSteps; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."ProvisioningSteps"
    ADD CONSTRAINT "PK_ProvisioningSteps" PRIMARY KEY ("Id");


--
-- Name: RateCardLines PK_RateCardLines; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."RateCardLines"
    ADD CONSTRAINT "PK_RateCardLines" PRIMARY KEY ("Id");


--
-- Name: RateCards PK_RateCards; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."RateCards"
    ADD CONSTRAINT "PK_RateCards" PRIMARY KEY ("Id");


--
-- Name: SubscriptionCreditNotes PK_SubscriptionCreditNotes; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionCreditNotes"
    ADD CONSTRAINT "PK_SubscriptionCreditNotes" PRIMARY KEY ("Id");


--
-- Name: SubscriptionInvoices PK_SubscriptionInvoices; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionInvoices"
    ADD CONSTRAINT "PK_SubscriptionInvoices" PRIMARY KEY ("Id");


--
-- Name: SubscriptionPayments PK_SubscriptionPayments; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionPayments"
    ADD CONSTRAINT "PK_SubscriptionPayments" PRIMARY KEY ("Id");


--
-- Name: SubscriptionRevenueActions PK_SubscriptionRevenueActions; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionRevenueActions"
    ADD CONSTRAINT "PK_SubscriptionRevenueActions" PRIMARY KEY ("Id");


--
-- Name: SubscriptionTaxRules PK_SubscriptionTaxRules; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SubscriptionTaxRules"
    ADD CONSTRAINT "PK_SubscriptionTaxRules" PRIMARY KEY ("Id");


--
-- Name: SupportTicketLinks PK_SupportTicketLinks; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTicketLinks"
    ADD CONSTRAINT "PK_SupportTicketLinks" PRIMARY KEY ("Id");


--
-- Name: SupportTicketNotes PK_SupportTicketNotes; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTicketNotes"
    ADD CONSTRAINT "PK_SupportTicketNotes" PRIMARY KEY ("Id");


--
-- Name: SupportTickets PK_SupportTickets; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."SupportTickets"
    ADD CONSTRAINT "PK_SupportTickets" PRIMARY KEY ("Id");


--
-- Name: TenantAdminInvitations PK_TenantAdminInvitations; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantAdminInvitations"
    ADD CONSTRAINT "PK_TenantAdminInvitations" PRIMARY KEY ("Id");


--
-- Name: TenantDataAssets PK_TenantDataAssets; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDataAssets"
    ADD CONSTRAINT "PK_TenantDataAssets" PRIMARY KEY ("Id");


--
-- Name: TenantDataRecoveryEvidence PK_TenantDataRecoveryEvidence; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDataRecoveryEvidence"
    ADD CONSTRAINT "PK_TenantDataRecoveryEvidence" PRIMARY KEY ("Id");


--
-- Name: TenantDeletionCertificates PK_TenantDeletionCertificates; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantDeletionCertificates"
    ADD CONSTRAINT "PK_TenantDeletionCertificates" PRIMARY KEY ("Id");


--
-- Name: TenantExportReceipts PK_TenantExportReceipts; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantExportReceipts"
    ADD CONSTRAINT "PK_TenantExportReceipts" PRIMARY KEY ("Id");


--
-- Name: TenantLegalHolds PK_TenantLegalHolds; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantLegalHolds"
    ADD CONSTRAINT "PK_TenantLegalHolds" PRIMARY KEY ("Id");


--
-- Name: TenantLifecycleEvents PK_TenantLifecycleEvents; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantLifecycleEvents"
    ADD CONSTRAINT "PK_TenantLifecycleEvents" PRIMARY KEY ("Id");


--
-- Name: TenantMeterSourcePolicies PK_TenantMeterSourcePolicies; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantMeterSourcePolicies"
    ADD CONSTRAINT "PK_TenantMeterSourcePolicies" PRIMARY KEY ("TenantId", "MeterKey");


--
-- Name: TenantOffboardings PK_TenantOffboardings; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."TenantOffboardings"
    ADD CONSTRAINT "PK_TenantOffboardings" PRIMARY KEY ("Id");


--
-- Name: Tenants PK_Tenants; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."Tenants"
    ADD CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id");


--
-- Name: UsageCoverageSegments PK_UsageCoverageSegments; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageCoverageSegments"
    ADD CONSTRAINT "PK_UsageCoverageSegments" PRIMARY KEY ("Id");


--
-- Name: UsageEventRatings PK_UsageEventRatings; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEventRatings"
    ADD CONSTRAINT "PK_UsageEventRatings" PRIMARY KEY ("Id");


--
-- Name: UsageEvents PK_UsageEvents; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageEvents"
    ADD CONSTRAINT "PK_UsageEvents" PRIMARY KEY ("UsageEventId");


--
-- Name: UsageMinuteAggregates PK_UsageMinuteAggregates; Type: CONSTRAINT; Schema: platform; Owner: -
--

ALTER TABLE ONLY platform."UsageMinuteAggregates"
    ADD CONSTRAINT "PK_UsageMinuteAggregates" PRIMARY KEY ("Id");


--
-- Name: AccountingPeriods AK_AccountingPeriods_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AccountingPeriods"
    ADD CONSTRAINT "AK_AccountingPeriods_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: AiRequests AK_AiRequests_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "AK_AiRequests_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankAccounts AK_BankAccounts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "AK_BankAccounts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankAccounts AK_BankAccounts_BusinessUnitId_Id_CurrencyId; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "AK_BankAccounts_BusinessUnitId_Id_CurrencyId" UNIQUE ("BusinessUnitId", "Id", "CurrencyId");


--
-- Name: BankAdjustments AK_BankAdjustments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "AK_BankAdjustments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankMatchingRules AK_BankMatchingRules_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankMatchingRules"
    ADD CONSTRAINT "AK_BankMatchingRules_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankStatementImports AK_BankStatementImports_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "AK_BankStatementImports_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankStatementImports AK_BankStatementImports_BusinessUnitId_Id_BankAccountId; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "AK_BankStatementImports_BusinessUnitId_Id_BankAccountId" UNIQUE ("BusinessUnitId", "Id", "BankAccountId");


--
-- Name: BankStatementLines AK_BankStatementLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "AK_BankStatementLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankStatementLines AK_BankStatementLines_BusinessUnitId_Id_BankAccountId; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "AK_BankStatementLines_BusinessUnitId_Id_BankAccountId" UNIQUE ("BusinessUnitId", "Id", "BankAccountId");


--
-- Name: BankStatements AK_BankStatements_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "AK_BankStatements_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: BankStatements AK_BankStatements_BusinessUnitId_Id_BankAccountId; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "AK_BankStatements_BusinessUnitId_Id_BankAccountId" UNIQUE ("BusinessUnitId", "Id", "BankAccountId");


--
-- Name: CollectionControls AK_CollectionControls_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "AK_CollectionControls_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CommercialCases AK_CommercialCases_BusinessUnitID_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "AK_CommercialCases_BusinessUnitID_Id" UNIQUE ("BusinessUnitID", "Id");


--
-- Name: CommercialCases AK_CommercialCases_BusinessUnitID_Id_MasterReference; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "AK_CommercialCases_BusinessUnitID_Id_MasterReference" UNIQUE ("BusinessUnitID", "Id", "MasterReference");


--
-- Name: CommercialMatchingPolicies AK_CommercialMatchingPolicies_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialMatchingPolicies"
    ADD CONSTRAINT "AK_CommercialMatchingPolicies_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: Contacts AK_Contacts_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Contacts"
    ADD CONSTRAINT "AK_Contacts_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: Currency AK_Currency_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Currency"
    ADD CONSTRAINT "AK_Currency_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: CustomerAwards AK_CustomerAwards_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "AK_CustomerAwards_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CustomerCollectionProfiles AK_CustomerCollectionProfiles_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "AK_CustomerCollectionProfiles_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CustomerPayments AK_CustomerPayments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "AK_CustomerPayments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CustomerPurchaseOrderLines AK_CustomerPurchaseOrderLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "AK_CustomerPurchaseOrderLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CustomerPurchaseOrders AK_CustomerPurchaseOrders_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "AK_CustomerPurchaseOrders_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CustomerRefunds AK_CustomerRefunds_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "AK_CustomerRefunds_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: CustomerStatements AK_CustomerStatements_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "AK_CustomerStatements_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: Customers AK_Customers_BUID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "AK_Customers_BUID_ID" UNIQUE ("BUID", "ID");


--
-- Name: DunningCases AK_DunningCases_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "AK_DunningCases_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: DunningNotices AK_DunningNotices_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "AK_DunningNotices_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: DunningPolicies AK_DunningPolicies_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningPolicies"
    ADD CONSTRAINT "AK_DunningPolicies_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: DunningRuns AK_DunningRuns_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRuns"
    ADD CONSTRAINT "AK_DunningRuns_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: ExtractionJobs AK_ExtractionJobs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ExtractionJobs"
    ADD CONSTRAINT "AK_ExtractionJobs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: FinanceCommunicationContacts AK_FinanceCommunicationContacts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FinanceCommunicationContacts"
    ADD CONSTRAINT "AK_FinanceCommunicationContacts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: Inventory AK_Inventory_Buid_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Inventory"
    ADD CONSTRAINT "AK_Inventory_Buid_Id" UNIQUE ("Buid", "Id");


--
-- Name: JournalEntries AK_JournalEntries_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "AK_JournalEntries_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: JournalEntryLines AK_JournalEntryLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "AK_JournalEntryLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: LeadIngestionBatches AK_LeadIngestionBatches_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionBatches"
    ADD CONSTRAINT "AK_LeadIngestionBatches_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: LeadIngestionOccurrences AK_LeadIngestionOccurrences_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "AK_LeadIngestionOccurrences_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: LeadItemRevisions AK_LeadItemRevisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadItemRevisions"
    ADD CONSTRAINT "AK_LeadItemRevisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: LeadRevisions AK_LeadRevisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisions"
    ADD CONSTRAINT "AK_LeadRevisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: Leads AK_Leads_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "AK_Leads_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: LedgerAccounts AK_LedgerAccounts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerAccounts"
    ADD CONSTRAINT "AK_LedgerAccounts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: LedgerBooks AK_LedgerBooks_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "AK_LedgerBooks_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: MasterDataChangeEvents AK_MasterDataChangeEvents_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."MasterDataChangeEvents"
    ADD CONSTRAINT "AK_MasterDataChangeEvents_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: OrderItems AK_OrderItems_ID_OrderID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "AK_OrderItems_ID_OrderID" UNIQUE ("ID", "OrderID");


--
-- Name: Orders AK_Orders_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "AK_Orders_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: Products AK_Products_BUID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "AK_Products_BUID_ID" UNIQUE ("BUID", "ID");


--
-- Name: QuoteItems AK_QuoteItems_ID_QuoteID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "AK_QuoteItems_ID_QuoteID" UNIQUE ("ID", "QuoteID");


--
-- Name: QuotePriceAttestations AK_QuotePriceAttestations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuotePriceAttestations"
    ADD CONSTRAINT "AK_QuotePriceAttestations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: QuoteValidityExtensions AK_QuoteValidityExtensions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteValidityExtensions"
    ADD CONSTRAINT "AK_QuoteValidityExtensions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: Quotes AK_Quotes_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "AK_Quotes_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: RFQItems AK_RFQItems_ID_RFQID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "AK_RFQItems_ID_RFQID" UNIQUE ("ID", "RFQID");


--
-- Name: RFQ AK_RFQ_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "AK_RFQ_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: ReceivableDocumentLines AK_ReceivableDocumentLines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "AK_ReceivableDocumentLines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: ReceivableDocuments AK_ReceivableDocuments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "AK_ReceivableDocuments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: ReceivableWriteOffs AK_ReceivableWriteOffs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "AK_ReceivableWriteOffs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: ReconciliationMatches AK_ReconciliationMatches_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationMatches"
    ADD CONSTRAINT "AK_ReconciliationMatches_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: ReconciliationRuns AK_ReconciliationRuns_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRuns"
    ADD CONSTRAINT "AK_ReconciliationRuns_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: SetCity AK_SetCity_BUID_CityID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "AK_SetCity_BUID_CityID" UNIQUE ("BUID", "CityID");


--
-- Name: Setup_Master AK_Setup_Master_BusinessUnitID_SetupID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Setup_Master"
    ADD CONSTRAINT "AK_Setup_Master_BusinessUnitID_SetupID" UNIQUE ("BusinessUnitID", "SetupID");


--
-- Name: ShipmentItems AK_ShipmentItems_ID_ShipmentID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentItems"
    ADD CONSTRAINT "AK_ShipmentItems_ID_ShipmentID" UNIQUE ("ID", "ShipmentID");


--
-- Name: Shipments AK_Shipments_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "AK_Shipments_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: SourcingAwards AK_SourcingAwards_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "AK_SourcingAwards_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: SupplierQuotedItems AK_SupplierQuotedItems_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "AK_SupplierQuotedItems_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: SupplierSolicitations AK_SupplierSolicitations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "AK_SupplierSolicitations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: Suppliers AK_Suppliers_ID_BUID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "AK_Suppliers_ID_BUID" UNIQUE ("ID", "BUID");


--
-- Name: Warehouses AK_Warehouses_BusinessUnitID_ID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Warehouses"
    ADD CONSTRAINT "AK_Warehouses_BusinessUnitID_ID" UNIQUE ("BusinessUnitID", "ID");


--
-- Name: commercial_demand_lines AK_commercial_demand_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_demand_lines
    ADD CONSTRAINT "AK_commercial_demand_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_exception_cases AK_commercial_exception_cases_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "AK_commercial_exception_cases_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_exception_events AK_commercial_exception_events_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_events
    ADD CONSTRAINT "AK_commercial_exception_events_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_exception_operations AK_commercial_exception_operations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_operations
    ADD CONSTRAINT "AK_commercial_exception_operations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_lifecycle_events AK_commercial_lifecycle_events_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "AK_commercial_lifecycle_events_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_opportunity_events AK_commercial_opportunity_events_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_events
    ADD CONSTRAINT "AK_commercial_opportunity_events_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_opportunity_feedback AK_commercial_opportunity_feedback_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_feedback
    ADD CONSTRAINT "AK_commercial_opportunity_feedback_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_opportunity_operations AK_commercial_opportunity_operations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_operations
    ADD CONSTRAINT "AK_commercial_opportunity_operations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_opportunity_outbox AK_commercial_opportunity_outbox_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_outbox
    ADD CONSTRAINT "AK_commercial_opportunity_outbox_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_opportunity_outcomes AK_commercial_opportunity_outcomes_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_outcomes
    ADD CONSTRAINT "AK_commercial_opportunity_outcomes_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: commercial_opportunity_recommendations AK_commercial_opportunity_recommendations_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "AK_commercial_opportunity_recommendations_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: custom_field_definitions AK_custom_field_definitions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_definitions
    ADD CONSTRAINT "AK_custom_field_definitions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: custom_field_records AK_custom_field_records_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_records
    ADD CONSTRAINT "AK_custom_field_records_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: custom_field_values AK_custom_field_values_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "AK_custom_field_values_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: custom_field_versions AK_custom_field_versions_DefinitionId_VersionNumber; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_versions
    ADD CONSTRAINT "AK_custom_field_versions_DefinitionId_VersionNumber" UNIQUE ("DefinitionId", "VersionNumber");


--
-- Name: customer_identifiers AK_customer_identifiers_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_identifiers
    ADD CONSTRAINT "AK_customer_identifiers_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: customer_ownerships AK_customer_ownerships_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "AK_customer_ownerships_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: customer_quote_sourcing_decisions AK_customer_quote_sourcing_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "AK_customer_quote_sourcing_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: delivery_proof_lines AK_delivery_proof_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "AK_delivery_proof_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: delivery_proofs AK_delivery_proofs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "AK_delivery_proofs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: delivery_shortfall_decisions AK_delivery_shortfall_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_shortfall_decisions
    ADD CONSTRAINT "AK_delivery_shortfall_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: goods_receipts AK_goods_receipts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipts
    ADD CONSTRAINT "AK_goods_receipts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: governed_artifact_versions AK_governed_artifact_versions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifact_versions
    ADD CONSTRAINT "AK_governed_artifact_versions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: governed_artifacts AK_governed_artifacts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifacts
    ADD CONSTRAINT "AK_governed_artifacts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: human_action_items AK_human_action_items_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.human_action_items
    ADD CONSTRAINT "AK_human_action_items_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: inbound_logistics_policies AK_inbound_logistics_policies_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inbound_logistics_policies
    ADD CONSTRAINT "AK_inbound_logistics_policies_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: incoming_inventory AK_incoming_inventory_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "AK_incoming_inventory_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: inventory_movements AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "AK_inventory_movements_BusinessUnitId_Id_ProductId_InventoryId~" UNIQUE ("BusinessUnitId", "Id", "ProductId", "InventoryId", "WarehouseId");


--
-- Name: lead_routing_decisions AK_lead_routing_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "AK_lead_routing_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: material_lot_certificates AK_material_lot_certificates_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_certificates
    ADD CONSTRAINT "AK_material_lot_certificates_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: material_lot_consumptions AK_material_lot_consumptions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "AK_material_lot_consumptions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: material_lots AK_material_lots_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "AK_material_lots_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: ports_of_entry AK_ports_of_entry_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ports_of_entry
    ADD CONSTRAINT "AK_ports_of_entry_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: procurement_callback_receipts AK_procurement_callback_receipts_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_callback_receipts
    ADD CONSTRAINT "AK_procurement_callback_receipts_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: procurement_handoffs AK_procurement_handoffs_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "AK_procurement_handoffs_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: sales_coaching_acknowledgements AK_sales_coaching_acknowledgements_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "AK_sales_coaching_acknowledgements_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: setUOM AK_setUOM_BusinessUnitID_UomID; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."setUOM"
    ADD CONSTRAINT "AK_setUOM_BusinessUnitID_UomID" UNIQUE ("BusinessUnitID", "UomID");


--
-- Name: sourcing_cases AK_sourcing_cases_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "AK_sourcing_cases_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_negotiation_decisions AK_supplier_negotiation_decisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_negotiation_decisions
    ADD CONSTRAINT "AK_supplier_negotiation_decisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_purchase_order_lines AK_supplier_purchase_order_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "AK_supplier_purchase_order_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_purchase_order_lines AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "AK_supplier_purchase_order_lines_BusinessUnitId_Id_ProductId_W~" UNIQUE ("BusinessUnitId", "Id", "ProductId", "WarehouseId");


--
-- Name: supplier_purchase_orders AK_supplier_purchase_orders_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "AK_supplier_purchase_orders_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_quote_field_evidence AK_supplier_quote_field_evidence_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_field_evidence
    ADD CONSTRAINT "AK_supplier_quote_field_evidence_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_quote_lines AK_supplier_quote_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_lines
    ADD CONSTRAINT "AK_supplier_quote_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_quote_revisions AK_supplier_quote_revisions_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "AK_supplier_quote_revisions_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_quote_revisions AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id" UNIQUE ("BusinessUnitId", "SupplierQuoteId", "Id");


--
-- Name: supplier_quotes AK_supplier_quotes_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "AK_supplier_quotes_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_shipment_lines AK_supplier_shipment_lines_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "AK_supplier_shipment_lines_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: supplier_shipments AK_supplier_shipments_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipments
    ADD CONSTRAINT "AK_supplier_shipments_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: unassigned_work_items AK_unassigned_work_items_BusinessUnitId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "AK_unassigned_work_items_BusinessUnitId_Id" UNIQUE ("BusinessUnitId", "Id");


--
-- Name: DunningRunDecisions CK_DunningRunDecisions_ProfileCheckpoint; Type: CHECK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE public."DunningRunDecisions"
    ADD CONSTRAINT "CK_DunningRunDecisions_ProfileCheckpoint" CHECK (("CustomerCollectionProfileId" IS NOT NULL)) NOT VALID;


--
-- Name: FinanceProviderSecrets FinanceProviderSecrets_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FinanceProviderSecrets"
    ADD CONSTRAINT "FinanceProviderSecrets_pkey" PRIMARY KEY ("Name");


--
-- Name: LedgerActorNonces LedgerActorNonces_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerActorNonces"
    ADD CONSTRAINT "LedgerActorNonces_pkey" PRIMARY KEY ("Nonce");


--
-- Name: AccountingPeriods PK_AccountingPeriods; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AccountingPeriods"
    ADD CONSTRAINT "PK_AccountingPeriods" PRIMARY KEY ("Id");


--
-- Name: AgentApprovals PK_AgentApprovals; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentApprovals"
    ADD CONSTRAINT "PK_AgentApprovals" PRIMARY KEY ("Id");


--
-- Name: AgentAuditLogs PK_AgentAuditLogs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentAuditLogs"
    ADD CONSTRAINT "PK_AgentAuditLogs" PRIMARY KEY ("Id");


--
-- Name: AgentMessages PK_AgentMessages; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentMessages"
    ADD CONSTRAINT "PK_AgentMessages" PRIMARY KEY ("Id");


--
-- Name: AgentPolicies PK_AgentPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentPolicies"
    ADD CONSTRAINT "PK_AgentPolicies" PRIMARY KEY ("Id");


--
-- Name: AgentSessions PK_AgentSessions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentSessions"
    ADD CONSTRAINT "PK_AgentSessions" PRIMARY KEY ("Id");


--
-- Name: AiBudgetPeriods PK_AiBudgetPeriods; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiBudgetPeriods"
    ADD CONSTRAINT "PK_AiBudgetPeriods" PRIMARY KEY ("BusinessUnitId", "PeriodStartUtc");


--
-- Name: AiCallAttempts PK_AiCallAttempts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiCallAttempts"
    ADD CONSTRAINT "PK_AiCallAttempts" PRIMARY KEY ("Id");


--
-- Name: AiProcessingPolicies PK_AiProcessingPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProcessingPolicies"
    ADD CONSTRAINT "PK_AiProcessingPolicies" PRIMARY KEY ("BusinessUnitId");


--
-- Name: AiProviderAuthorizations PK_AiProviderAuthorizations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProviderAuthorizations"
    ADD CONSTRAINT "PK_AiProviderAuthorizations" PRIMARY KEY ("Id");


--
-- Name: AiRequests PK_AiRequests; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiRequests"
    ADD CONSTRAINT "PK_AiRequests" PRIMARY KEY ("Id");


--
-- Name: BankAccounts PK_BankAccounts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAccounts"
    ADD CONSTRAINT "PK_BankAccounts" PRIMARY KEY ("Id");


--
-- Name: BankAdjustmentDistributions PK_BankAdjustmentDistributions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustmentDistributions"
    ADD CONSTRAINT "PK_BankAdjustmentDistributions" PRIMARY KEY ("Id");


--
-- Name: BankAdjustments PK_BankAdjustments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankAdjustments"
    ADD CONSTRAINT "PK_BankAdjustments" PRIMARY KEY ("Id");


--
-- Name: BankMatchingRules PK_BankMatchingRules; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankMatchingRules"
    ADD CONSTRAINT "PK_BankMatchingRules" PRIMARY KEY ("Id");


--
-- Name: BankStatementImports PK_BankStatementImports; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementImports"
    ADD CONSTRAINT "PK_BankStatementImports" PRIMARY KEY ("Id");


--
-- Name: BankStatementLines PK_BankStatementLines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatementLines"
    ADD CONSTRAINT "PK_BankStatementLines" PRIMARY KEY ("Id");


--
-- Name: BankStatements PK_BankStatements; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BankStatements"
    ADD CONSTRAINT "PK_BankStatements" PRIMARY KEY ("Id");


--
-- Name: BoqAssemblies PK_BoqAssemblies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqAssemblies"
    ADD CONSTRAINT "PK_BoqAssemblies" PRIMARY KEY ("Id");


--
-- Name: BoqAssemblyComponents PK_BoqAssemblyComponents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqAssemblyComponents"
    ADD CONSTRAINT "PK_BoqAssemblyComponents" PRIMARY KEY ("Id");


--
-- Name: BoqDocuments PK_BoqDocuments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqDocuments"
    ADD CONSTRAINT "PK_BoqDocuments" PRIMARY KEY ("Id");


--
-- Name: BoqItems PK_BoqItems; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqItems"
    ADD CONSTRAINT "PK_BoqItems" PRIMARY KEY ("Id");


--
-- Name: BoqSections PK_BoqSections; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BoqSections"
    ADD CONSTRAINT "PK_BoqSections" PRIMARY KEY ("Id");


--
-- Name: CollectionControls PK_CollectionControls; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CollectionControls"
    ADD CONSTRAINT "PK_CollectionControls" PRIMARY KEY ("Id");


--
-- Name: CommercialCases PK_CommercialCases; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialCases"
    ADD CONSTRAINT "PK_CommercialCases" PRIMARY KEY ("Id");


--
-- Name: CommercialFinanceAudits PK_CommercialFinanceAudits; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialFinanceAudits"
    ADD CONSTRAINT "PK_CommercialFinanceAudits" PRIMARY KEY ("Id");


--
-- Name: CommercialMatchingPolicies PK_CommercialMatchingPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CommercialMatchingPolicies"
    ADD CONSTRAINT "PK_CommercialMatchingPolicies" PRIMARY KEY ("Id");


--
-- Name: CustomerAwardLineAllocations PK_CustomerAwardLineAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwardLineAllocations"
    ADD CONSTRAINT "PK_CustomerAwardLineAllocations" PRIMARY KEY ("Id");


--
-- Name: CustomerAwards PK_CustomerAwards; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerAwards"
    ADD CONSTRAINT "PK_CustomerAwards" PRIMARY KEY ("Id");


--
-- Name: CustomerCollectionProfiles PK_CustomerCollectionProfiles; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerCollectionProfiles"
    ADD CONSTRAINT "PK_CustomerCollectionProfiles" PRIMARY KEY ("Id");


--
-- Name: CustomerPayments PK_CustomerPayments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPayments"
    ADD CONSTRAINT "PK_CustomerPayments" PRIMARY KEY ("Id");


--
-- Name: CustomerPurchaseOrderLines PK_CustomerPurchaseOrderLines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrderLines"
    ADD CONSTRAINT "PK_CustomerPurchaseOrderLines" PRIMARY KEY ("Id");


--
-- Name: CustomerPurchaseOrders PK_CustomerPurchaseOrders; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerPurchaseOrders"
    ADD CONSTRAINT "PK_CustomerPurchaseOrders" PRIMARY KEY ("Id");


--
-- Name: CustomerRefunds PK_CustomerRefunds; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerRefunds"
    ADD CONSTRAINT "PK_CustomerRefunds" PRIMARY KEY ("Id");


--
-- Name: CustomerStatementLines PK_CustomerStatementLines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatementLines"
    ADD CONSTRAINT "PK_CustomerStatementLines" PRIMARY KEY ("Id");


--
-- Name: CustomerStatements PK_CustomerStatements; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."CustomerStatements"
    ADD CONSTRAINT "PK_CustomerStatements" PRIMARY KEY ("Id");


--
-- Name: DunningCases PK_DunningCases; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningCases"
    ADD CONSTRAINT "PK_DunningCases" PRIMARY KEY ("Id");


--
-- Name: DunningDeliveryAttempts PK_DunningDeliveryAttempts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningDeliveryAttempts"
    ADD CONSTRAINT "PK_DunningDeliveryAttempts" PRIMARY KEY ("Id");


--
-- Name: DunningNotices PK_DunningNotices; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningNotices"
    ADD CONSTRAINT "PK_DunningNotices" PRIMARY KEY ("Id");


--
-- Name: DunningPolicies PK_DunningPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningPolicies"
    ADD CONSTRAINT "PK_DunningPolicies" PRIMARY KEY ("Id");


--
-- Name: DunningPolicySteps PK_DunningPolicySteps; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningPolicySteps"
    ADD CONSTRAINT "PK_DunningPolicySteps" PRIMARY KEY ("Id");


--
-- Name: DunningRunDecisions PK_DunningRunDecisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRunDecisions"
    ADD CONSTRAINT "PK_DunningRunDecisions" PRIMARY KEY ("Id");


--
-- Name: DunningRuns PK_DunningRuns; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DunningRuns"
    ADD CONSTRAINT "PK_DunningRuns" PRIMARY KEY ("Id");


--
-- Name: ExtractionCorpusEntries PK_ExtractionCorpusEntries; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ExtractionCorpusEntries"
    ADD CONSTRAINT "PK_ExtractionCorpusEntries" PRIMARY KEY ("Id");


--
-- Name: ExtractionJobs PK_ExtractionJobs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ExtractionJobs"
    ADD CONSTRAINT "PK_ExtractionJobs" PRIMARY KEY ("Id");


--
-- Name: FinanceCommunicationContacts PK_FinanceCommunicationContacts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FinanceCommunicationContacts"
    ADD CONSTRAINT "PK_FinanceCommunicationContacts" PRIMARY KEY ("Id");


--
-- Name: FinanceOutboxMessages PK_FinanceOutboxMessages; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FinanceOutboxMessages"
    ADD CONSTRAINT "PK_FinanceOutboxMessages" PRIMARY KEY ("Id");


--
-- Name: FolderIngestionRetryStates PK_FolderIngestionRetryStates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FolderIngestionRetryStates"
    ADD CONSTRAINT "PK_FolderIngestionRetryStates" PRIMARY KEY ("Id");


--
-- Name: FxRateSnapshots PK_FxRateSnapshots; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FxRateSnapshots"
    ADD CONSTRAINT "PK_FxRateSnapshots" PRIMARY KEY ("Id");


--
-- Name: FxRates PK_FxRates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."FxRates"
    ADD CONSTRAINT "PK_FxRates" PRIMARY KEY ("Id");


--
-- Name: IamAuditEvents PK_IamAuditEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."IamAuditEvents"
    ADD CONSTRAINT "PK_IamAuditEvents" PRIMARY KEY ("Id");


--
-- Name: Inventory PK_Inventory; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Inventory"
    ADD CONSTRAINT "PK_Inventory" PRIMARY KEY ("Id");


--
-- Name: JournalEntries PK_JournalEntries; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntries"
    ADD CONSTRAINT "PK_JournalEntries" PRIMARY KEY ("Id");


--
-- Name: JournalEntryLines PK_JournalEntryLines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."JournalEntryLines"
    ADD CONSTRAINT "PK_JournalEntryLines" PRIMARY KEY ("Id");


--
-- Name: LeadIdentityAuditEvents PK_LeadIdentityAuditEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIdentityAuditEvents"
    ADD CONSTRAINT "PK_LeadIdentityAuditEvents" PRIMARY KEY ("Id");


--
-- Name: LeadIngestionBatches PK_LeadIngestionBatches; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionBatches"
    ADD CONSTRAINT "PK_LeadIngestionBatches" PRIMARY KEY ("Id");


--
-- Name: LeadIngestionOccurrences PK_LeadIngestionOccurrences; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadIngestionOccurrences"
    ADD CONSTRAINT "PK_LeadIngestionOccurrences" PRIMARY KEY ("Id");


--
-- Name: LeadItemRevisions PK_LeadItemRevisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadItemRevisions"
    ADD CONSTRAINT "PK_LeadItemRevisions" PRIMARY KEY ("Id");


--
-- Name: LeadMatchCandidates PK_LeadMatchCandidates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadMatchCandidates"
    ADD CONSTRAINT "PK_LeadMatchCandidates" PRIMARY KEY ("Id");


--
-- Name: LeadOccurrenceDocuments PK_LeadOccurrenceDocuments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadOccurrenceDocuments"
    ADD CONSTRAINT "PK_LeadOccurrenceDocuments" PRIMARY KEY ("Id");


--
-- Name: LeadReferenceConfigurations PK_LeadReferenceConfigurations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadReferenceConfigurations"
    ADD CONSTRAINT "PK_LeadReferenceConfigurations" PRIMARY KEY ("BusinessUnitID");


--
-- Name: LeadReviewAudits PK_LeadReviewAudits; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadReviewAudits"
    ADD CONSTRAINT "PK_LeadReviewAudits" PRIMARY KEY ("Id");


--
-- Name: LeadRevisionDifferences PK_LeadRevisionDifferences; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisionDifferences"
    ADD CONSTRAINT "PK_LeadRevisionDifferences" PRIMARY KEY ("Id");


--
-- Name: LeadRevisionImpacts PK_LeadRevisionImpacts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisionImpacts"
    ADD CONSTRAINT "PK_LeadRevisionImpacts" PRIMARY KEY ("Id");


--
-- Name: LeadRevisions PK_LeadRevisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadRevisions"
    ADD CONSTRAINT "PK_LeadRevisions" PRIMARY KEY ("Id");


--
-- Name: LeadStatusHistories PK_LeadStatusHistories; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadStatusHistories"
    ADD CONSTRAINT "PK_LeadStatusHistories" PRIMARY KEY ("Id");


--
-- Name: LedgerAccounts PK_LedgerAccounts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerAccounts"
    ADD CONSTRAINT "PK_LedgerAccounts" PRIMARY KEY ("Id");


--
-- Name: LedgerBooks PK_LedgerBooks; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LedgerBooks"
    ADD CONSTRAINT "PK_LedgerBooks" PRIMARY KEY ("Id");


--
-- Name: LegalDocumentCounters PK_LegalDocumentCounters; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LegalDocumentCounters"
    ADD CONSTRAINT "PK_LegalDocumentCounters" PRIMARY KEY ("BusinessUnitId", "DocumentType", "FiscalYear");


--
-- Name: LoginAttempts PK_LoginAttempts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LoginAttempts"
    ADD CONSTRAINT "PK_LoginAttempts" PRIMARY KEY ("Id");


--
-- Name: MasterDataChangeEvents PK_MasterDataChangeEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."MasterDataChangeEvents"
    ADD CONSTRAINT "PK_MasterDataChangeEvents" PRIMARY KEY ("Id");


--
-- Name: MasterDataFieldChanges PK_MasterDataFieldChanges; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."MasterDataFieldChanges"
    ADD CONSTRAINT "PK_MasterDataFieldChanges" PRIMARY KEY ("Id");


--
-- Name: MetricEvents PK_MetricEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."MetricEvents"
    ADD CONSTRAINT "PK_MetricEvents" PRIMARY KEY ("Id");


--
-- Name: OrderToCashAuditEvents PK_OrderToCashAuditEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderToCashAuditEvents"
    ADD CONSTRAINT "PK_OrderToCashAuditEvents" PRIMARY KEY ("Id");


--
-- Name: OrderToCashDocumentCounters PK_OrderToCashDocumentCounters; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderToCashDocumentCounters"
    ADD CONSTRAINT "PK_OrderToCashDocumentCounters" PRIMARY KEY ("BusinessUnitId", "DocumentType", "CalendarYear");


--
-- Name: PaymentAllocations PK_PaymentAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PaymentAllocations"
    ADD CONSTRAINT "PK_PaymentAllocations" PRIMARY KEY ("Id");


--
-- Name: PromisesToPay PK_PromisesToPay; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PromisesToPay"
    ADD CONSTRAINT "PK_PromisesToPay" PRIMARY KEY ("Id");


--
-- Name: QuoteConfiguration PK_QuoteConfiguration; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteConfiguration"
    ADD CONSTRAINT "PK_QuoteConfiguration" PRIMARY KEY ("Id");


--
-- Name: QuotePriceAttestationLines PK_QuotePriceAttestationLines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuotePriceAttestationLines"
    ADD CONSTRAINT "PK_QuotePriceAttestationLines" PRIMARY KEY ("Id");


--
-- Name: QuotePriceAttestations PK_QuotePriceAttestations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuotePriceAttestations"
    ADD CONSTRAINT "PK_QuotePriceAttestations" PRIMARY KEY ("Id");


--
-- Name: QuoteRemovalRecords PK_QuoteRemovalRecords; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteRemovalRecords"
    ADD CONSTRAINT "PK_QuoteRemovalRecords" PRIMARY KEY ("Id");


--
-- Name: QuoteValidityExtensions PK_QuoteValidityExtensions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteValidityExtensions"
    ADD CONSTRAINT "PK_QuoteValidityExtensions" PRIMARY KEY ("Id");


--
-- Name: ReceivableDocumentLines PK_ReceivableDocumentLines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocumentLines"
    ADD CONSTRAINT "PK_ReceivableDocumentLines" PRIMARY KEY ("Id");


--
-- Name: ReceivableDocuments PK_ReceivableDocuments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableDocuments"
    ADD CONSTRAINT "PK_ReceivableDocuments" PRIMARY KEY ("Id");


--
-- Name: ReceivableWriteOffs PK_ReceivableWriteOffs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReceivableWriteOffs"
    ADD CONSTRAINT "PK_ReceivableWriteOffs" PRIMARY KEY ("Id");


--
-- Name: ReconciliationAllocations PK_ReconciliationAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationAllocations"
    ADD CONSTRAINT "PK_ReconciliationAllocations" PRIMARY KEY ("Id");


--
-- Name: ReconciliationMatches PK_ReconciliationMatches; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationMatches"
    ADD CONSTRAINT "PK_ReconciliationMatches" PRIMARY KEY ("Id");


--
-- Name: ReconciliationRunRules PK_ReconciliationRunRules; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRunRules"
    ADD CONSTRAINT "PK_ReconciliationRunRules" PRIMARY KEY ("Id");


--
-- Name: ReconciliationRuns PK_ReconciliationRuns; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReconciliationRuns"
    ADD CONSTRAINT "PK_ReconciliationRuns" PRIMARY KEY ("Id");


--
-- Name: ReportSubscriptions PK_ReportSubscriptions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ReportSubscriptions"
    ADD CONSTRAINT "PK_ReportSubscriptions" PRIMARY KEY ("Id");


--
-- Name: SlaEvents PK_SlaEvents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SlaEvents"
    ADD CONSTRAINT "PK_SlaEvents" PRIMARY KEY ("Id");


--
-- Name: SlaPolicies PK_SlaPolicies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SlaPolicies"
    ADD CONSTRAINT "PK_SlaPolicies" PRIMARY KEY ("Id");


--
-- Name: SourcingAwards PK_SourcingAwards; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SourcingAwards"
    ADD CONSTRAINT "PK_SourcingAwards" PRIMARY KEY ("Id");


--
-- Name: SupplierPurchaseHistory PK_SupplierPurchaseHistory; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierPurchaseHistory"
    ADD CONSTRAINT "PK_SupplierPurchaseHistory" PRIMARY KEY ("Id");


--
-- Name: SupplierQuotedItems PK_SupplierQuotedItems; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierQuotedItems"
    ADD CONSTRAINT "PK_SupplierQuotedItems" PRIMARY KEY ("Id");


--
-- Name: SupplierSolicitations PK_SupplierSolicitations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SupplierSolicitations"
    ADD CONSTRAINT "PK_SupplierSolicitations" PRIMARY KEY ("Id");


--
-- Name: Taxes PK_Taxes; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Taxes"
    ADD CONSTRAINT "PK_Taxes" PRIMARY KEY ("ID");


--
-- Name: TenantQueueStates PK_TenantQueueStates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TenantQueueStates"
    ADD CONSTRAINT "PK_TenantQueueStates" PRIMARY KEY ("BusinessUnitId");


--
-- Name: UserColumnPreferences PK_UserColumnPreferences; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserColumnPreferences"
    ADD CONSTRAINT "PK_UserColumnPreferences" PRIMARY KEY ("Id");


--
-- Name: WriteOffAllocations PK_WriteOffAllocations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."WriteOffAllocations"
    ADD CONSTRAINT "PK_WriteOffAllocations" PRIMARY KEY ("Id");


--
-- Name: Attachments PK__Attachme__3214EC2740D763DA; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Attachments"
    ADD CONSTRAINT "PK__Attachme__3214EC2740D763DA" PRIMARY KEY ("ID");


--
-- Name: BusinessUnits PK__Business__3214EC27B5E4A97A; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."BusinessUnits"
    ADD CONSTRAINT "PK__Business__3214EC27B5E4A97A" PRIMARY KEY ("ID");


--
-- Name: Contacts PK__Contacts__3214EC274B89BAF3; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Contacts"
    ADD CONSTRAINT "PK__Contacts__3214EC274B89BAF3" PRIMARY KEY ("ID");


--
-- Name: Currency PK__Currency__3214EC2734927EB0; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Currency"
    ADD CONSTRAINT "PK__Currency__3214EC2734927EB0" PRIMARY KEY ("ID");


--
-- Name: Customers PK__Customer__3214EC27D6DB6FD1; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Customers"
    ADD CONSTRAINT "PK__Customer__3214EC27D6DB6FD1" PRIMARY KEY ("ID");


--
-- Name: EmailIngests PK__EmailIng__3214EC2728D6F6B3; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."EmailIngests"
    ADD CONSTRAINT "PK__EmailIng__3214EC2728D6F6B3" PRIMARY KEY ("ID");


--
-- Name: Email_Configurations PK__Email_Co__3214EC278A1BB987; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Email_Configurations"
    ADD CONSTRAINT "PK__Email_Co__3214EC278A1BB987" PRIMARY KEY ("ID");


--
-- Name: Images PK__Images__3214EC27B2D5CCF9; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Images"
    ADD CONSTRAINT "PK__Images__3214EC27B2D5CCF9" PRIMARY KEY ("ID");


--
-- Name: Products PK__Inventor__3214EC27426EF885; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Products"
    ADD CONSTRAINT "PK__Inventor__3214EC27426EF885" PRIMARY KEY ("ID");


--
-- Name: ProductCategories PK__Inventor__3214EC27EA9C64B5; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductCategories"
    ADD CONSTRAINT "PK__Inventor__3214EC27EA9C64B5" PRIMARY KEY ("ID");


--
-- Name: ProductAttachments PK__Inventor__442C64DEB528BA1B; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductAttachments"
    ADD CONSTRAINT "PK__Inventor__442C64DEB528BA1B" PRIMARY KEY ("AttachmentID");


--
-- Name: LeadItems PK__LeadItem__3214EC2776894FBF; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."LeadItems"
    ADD CONSTRAINT "PK__LeadItem__3214EC2776894FBF" PRIMARY KEY ("ID");


--
-- Name: Leads PK__Leads__3214EC2705035004; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Leads"
    ADD CONSTRAINT "PK__Leads__3214EC2705035004" PRIMARY KEY ("ID");


--
-- Name: Module PK__Module__3214EC276837F46D; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Module"
    ADD CONSTRAINT "PK__Module__3214EC276837F46D" PRIMARY KEY ("ID");


--
-- Name: OrderItems PK__OrderIte__3214EC27F54B0F5F; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderItems"
    ADD CONSTRAINT "PK__OrderIte__3214EC27F54B0F5F" PRIMARY KEY ("ID");


--
-- Name: Orders PK__Orders__3214EC27F30500C1; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Orders"
    ADD CONSTRAINT "PK__Orders__3214EC27F30500C1" PRIMARY KEY ("ID");


--
-- Name: ProductSubCategories PK__ProductS__3214EC2758B5F2D2; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProductSubCategories"
    ADD CONSTRAINT "PK__ProductS__3214EC2758B5F2D2" PRIMARY KEY ("ID");


--
-- Name: QuoteItems PK__QuoteIte__3214EC27B021232E; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."QuoteItems"
    ADD CONSTRAINT "PK__QuoteIte__3214EC27B021232E" PRIMARY KEY ("ID");


--
-- Name: Quotes PK__Quotes__3214EC27B0FC1337; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Quotes"
    ADD CONSTRAINT "PK__Quotes__3214EC27B0FC1337" PRIMARY KEY ("ID");


--
-- Name: RFQItems PK__RFQItems__3214EC2712F05C03; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQItems"
    ADD CONSTRAINT "PK__RFQItems__3214EC2712F05C03" PRIMARY KEY ("ID");


--
-- Name: RFQ PK__RFQ__3214EC27E71B0249; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RFQ"
    ADD CONSTRAINT "PK__RFQ__3214EC27E71B0249" PRIMARY KEY ("ID");


--
-- Name: RolePermissions PK__RolePerm__3214EC27212832A0; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RolePermissions"
    ADD CONSTRAINT "PK__RolePerm__3214EC27212832A0" PRIMARY KEY ("ID");


--
-- Name: SetCity PK__SetCity__F2D21A961487DC00; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCity"
    ADD CONSTRAINT "PK__SetCity__F2D21A961487DC00" PRIMARY KEY ("CityID");


--
-- Name: SetCountry PK__SetCount__10D160BF33E5BD3A; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetCountry"
    ADD CONSTRAINT "PK__SetCount__10D160BF33E5BD3A" PRIMARY KEY ("CountryID");


--
-- Name: SetState PK__SetState__C3BA3B5A26295488; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SetState"
    ADD CONSTRAINT "PK__SetState__C3BA3B5A26295488" PRIMARY KEY ("StateID");


--
-- Name: Setup_Master PK__Setup_Ma__C9C734B31BDDC1E2; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Setup_Master"
    ADD CONSTRAINT "PK__Setup_Ma__C9C734B31BDDC1E2" PRIMARY KEY ("SetupID");


--
-- Name: ShipmentStatusHistory PK__Shipment__3214EC0749B79ADB; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentStatusHistory"
    ADD CONSTRAINT "PK__Shipment__3214EC0749B79ADB" PRIMARY KEY ("Id");


--
-- Name: Shipments PK__Shipment__3214EC2732EE97FF; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Shipments"
    ADD CONSTRAINT "PK__Shipment__3214EC2732EE97FF" PRIMARY KEY ("ID");


--
-- Name: ShipmentItems PK__Shipment__3214EC27B4DD8C7A; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ShipmentItems"
    ADD CONSTRAINT "PK__Shipment__3214EC27B4DD8C7A" PRIMARY KEY ("ID");


--
-- Name: Suppliers PK__Supplier__3214EC2782495266; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Suppliers"
    ADD CONSTRAINT "PK__Supplier__3214EC2782495266" PRIMARY KEY ("ID");


--
-- Name: Teams PK__Teams__3214EC27A735D5D4; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Teams"
    ADD CONSTRAINT "PK__Teams__3214EC27A735D5D4" PRIMARY KEY ("ID");


--
-- Name: UserGroups PK__UserGrou__3214EC277F8DF4F8; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."UserGroups"
    ADD CONSTRAINT "PK__UserGrou__3214EC277F8DF4F8" PRIMARY KEY ("ID");


--
-- Name: Users PK__Users__3214EC279AB429D5; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Users"
    ADD CONSTRAINT "PK__Users__3214EC279AB429D5" PRIMARY KEY ("ID");


--
-- Name: Warehouses PK__Warehous__3214EC27E9A0A7EE; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Warehouses"
    ADD CONSTRAINT "PK__Warehous__3214EC27E9A0A7EE" PRIMARY KEY ("ID");


--
-- Name: __EFMigrationsHistory PK___EFMigrationsHistory; Type: CONSTRAINT; Schema: public; Owner: -
--

-- ADD CONSTRAINT "PK___EFMigrationsHistory" omitted: created with the table by EF Core.


--
-- Name: setUOM PK__setUOM__F6F8D59E4737F405; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."setUOM"
    ADD CONSTRAINT "PK__setUOM__F6F8D59E4737F405" PRIMARY KEY ("UomID");


--
-- Name: canonical_inquiries PK_canonical_inquiries; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_inquiries
    ADD CONSTRAINT "PK_canonical_inquiries" PRIMARY KEY (id);


--
-- Name: canonical_line_items PK_canonical_line_items; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_line_items
    ADD CONSTRAINT "PK_canonical_line_items" PRIMARY KEY (id);


--
-- Name: commercial_activities PK_commercial_activities; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_activities
    ADD CONSTRAINT "PK_commercial_activities" PRIMARY KEY ("Id");


--
-- Name: commercial_demand_lines PK_commercial_demand_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_demand_lines
    ADD CONSTRAINT "PK_commercial_demand_lines" PRIMARY KEY ("Id");


--
-- Name: commercial_document_classifications PK_commercial_document_classifications; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_document_classifications
    ADD CONSTRAINT "PK_commercial_document_classifications" PRIMARY KEY (id);


--
-- Name: commercial_exception_cases PK_commercial_exception_cases; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_cases
    ADD CONSTRAINT "PK_commercial_exception_cases" PRIMARY KEY ("Id");


--
-- Name: commercial_exception_events PK_commercial_exception_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_events
    ADD CONSTRAINT "PK_commercial_exception_events" PRIMARY KEY ("Id");


--
-- Name: commercial_exception_operations PK_commercial_exception_operations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_operations
    ADD CONSTRAINT "PK_commercial_exception_operations" PRIMARY KEY ("Id");


--
-- Name: commercial_exception_outbox PK_commercial_exception_outbox; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_exception_outbox
    ADD CONSTRAINT "PK_commercial_exception_outbox" PRIMARY KEY ("Id");


--
-- Name: commercial_lifecycle_events PK_commercial_lifecycle_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_lifecycle_events
    ADD CONSTRAINT "PK_commercial_lifecycle_events" PRIMARY KEY ("Id");


--
-- Name: commercial_opportunity_events PK_commercial_opportunity_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_events
    ADD CONSTRAINT "PK_commercial_opportunity_events" PRIMARY KEY ("Id");


--
-- Name: commercial_opportunity_feedback PK_commercial_opportunity_feedback; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_feedback
    ADD CONSTRAINT "PK_commercial_opportunity_feedback" PRIMARY KEY ("Id");


--
-- Name: commercial_opportunity_operations PK_commercial_opportunity_operations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_operations
    ADD CONSTRAINT "PK_commercial_opportunity_operations" PRIMARY KEY ("Id");


--
-- Name: commercial_opportunity_outbox PK_commercial_opportunity_outbox; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_outbox
    ADD CONSTRAINT "PK_commercial_opportunity_outbox" PRIMARY KEY ("Id");


--
-- Name: commercial_opportunity_outcomes PK_commercial_opportunity_outcomes; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_outcomes
    ADD CONSTRAINT "PK_commercial_opportunity_outcomes" PRIMARY KEY ("Id");


--
-- Name: commercial_opportunity_recommendations PK_commercial_opportunity_recommendations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_opportunity_recommendations
    ADD CONSTRAINT "PK_commercial_opportunity_recommendations" PRIMARY KEY ("Id");


--
-- Name: custom_field_definitions PK_custom_field_definitions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_definitions
    ADD CONSTRAINT "PK_custom_field_definitions" PRIMARY KEY ("Id");


--
-- Name: custom_field_dependencies PK_custom_field_dependencies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_dependencies
    ADD CONSTRAINT "PK_custom_field_dependencies" PRIMARY KEY ("Id");


--
-- Name: custom_field_options PK_custom_field_options; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_options
    ADD CONSTRAINT "PK_custom_field_options" PRIMARY KEY ("Id");


--
-- Name: custom_field_records PK_custom_field_records; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_records
    ADD CONSTRAINT "PK_custom_field_records" PRIMARY KEY ("Id");


--
-- Name: custom_field_rules PK_custom_field_rules; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_rules
    ADD CONSTRAINT "PK_custom_field_rules" PRIMARY KEY ("Id");


--
-- Name: custom_field_values PK_custom_field_values; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_values
    ADD CONSTRAINT "PK_custom_field_values" PRIMARY KEY ("Id");


--
-- Name: custom_field_versions PK_custom_field_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.custom_field_versions
    ADD CONSTRAINT "PK_custom_field_versions" PRIMARY KEY ("Id");


--
-- Name: customer_identifiers PK_customer_identifiers; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_identifiers
    ADD CONSTRAINT "PK_customer_identifiers" PRIMARY KEY ("Id");


--
-- Name: customer_ownerships PK_customer_ownerships; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_ownerships
    ADD CONSTRAINT "PK_customer_ownerships" PRIMARY KEY ("Id");


--
-- Name: customer_quote_sourcing_decisions PK_customer_quote_sourcing_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.customer_quote_sourcing_decisions
    ADD CONSTRAINT "PK_customer_quote_sourcing_decisions" PRIMARY KEY ("Id");


--
-- Name: delivery_proof_lines PK_delivery_proof_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proof_lines
    ADD CONSTRAINT "PK_delivery_proof_lines" PRIMARY KEY ("Id");


--
-- Name: delivery_proofs PK_delivery_proofs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_proofs
    ADD CONSTRAINT "PK_delivery_proofs" PRIMARY KEY ("Id");


--
-- Name: delivery_shortfall_decisions PK_delivery_shortfall_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.delivery_shortfall_decisions
    ADD CONSTRAINT "PK_delivery_shortfall_decisions" PRIMARY KEY ("Id");


--
-- Name: document_corpora PK_document_corpora; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_corpora
    ADD CONSTRAINT "PK_document_corpora" PRIMARY KEY (id);


--
-- Name: document_pages PK_document_pages; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_pages
    ADD CONSTRAINT "PK_document_pages" PRIMARY KEY (id);


--
-- Name: document_regions PK_document_regions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_regions
    ADD CONSTRAINT "PK_document_regions" PRIMARY KEY (id);


--
-- Name: evidence_retention_policies PK_evidence_retention_policies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evidence_retention_policies
    ADD CONSTRAINT "PK_evidence_retention_policies" PRIMARY KEY ("Id");


--
-- Name: extraction_dead_letter_events PK_extraction_dead_letter_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_dead_letter_events
    ADD CONSTRAINT "PK_extraction_dead_letter_events" PRIMARY KEY ("Id");


--
-- Name: extraction_runs PK_extraction_runs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT "PK_extraction_runs" PRIMARY KEY (id);


--
-- Name: field_evidence PK_field_evidence; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.field_evidence
    ADD CONSTRAINT "PK_field_evidence" PRIMARY KEY (id);


--
-- Name: follow_up_tasks PK_follow_up_tasks; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.follow_up_tasks
    ADD CONSTRAINT "PK_follow_up_tasks" PRIMARY KEY ("Id");


--
-- Name: follow_up_transition_events PK_follow_up_transition_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.follow_up_transition_events
    ADD CONSTRAINT "PK_follow_up_transition_events" PRIMARY KEY ("Id");


--
-- Name: goods_receipt_lines PK_goods_receipt_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipt_lines
    ADD CONSTRAINT "PK_goods_receipt_lines" PRIMARY KEY ("Id");


--
-- Name: goods_receipts PK_goods_receipts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.goods_receipts
    ADD CONSTRAINT "PK_goods_receipts" PRIMARY KEY ("Id");


--
-- Name: governed_artifact_events PK_governed_artifact_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifact_events
    ADD CONSTRAINT "PK_governed_artifact_events" PRIMARY KEY ("Id");


--
-- Name: governed_artifact_versions PK_governed_artifact_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifact_versions
    ADD CONSTRAINT "PK_governed_artifact_versions" PRIMARY KEY ("Id");


--
-- Name: governed_artifacts PK_governed_artifacts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.governed_artifacts
    ADD CONSTRAINT "PK_governed_artifacts" PRIMARY KEY ("Id");


--
-- Name: human_action_events PK_human_action_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.human_action_events
    ADD CONSTRAINT "PK_human_action_events" PRIMARY KEY ("Id");


--
-- Name: human_action_items PK_human_action_items; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.human_action_items
    ADD CONSTRAINT "PK_human_action_items" PRIMARY KEY ("Id");


--
-- Name: inbound_logistics_policies PK_inbound_logistics_policies; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inbound_logistics_policies
    ADD CONSTRAINT "PK_inbound_logistics_policies" PRIMARY KEY ("Id");


--
-- Name: incoming_inventory PK_incoming_inventory; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incoming_inventory
    ADD CONSTRAINT "PK_incoming_inventory" PRIMARY KEY ("Id");


--
-- Name: inventory_movements PK_inventory_movements; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_movements
    ADD CONSTRAINT "PK_inventory_movements" PRIMARY KEY ("Id");


--
-- Name: inventory_reorder_alerts PK_inventory_reorder_alerts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventory_reorder_alerts
    ADD CONSTRAINT "PK_inventory_reorder_alerts" PRIMARY KEY ("Id");


--
-- Name: lead_assignments PK_lead_assignments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_assignments
    ADD CONSTRAINT "PK_lead_assignments" PRIMARY KEY ("Id");


--
-- Name: lead_customer_match_candidates PK_lead_customer_match_candidates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_customer_match_candidates
    ADD CONSTRAINT "PK_lead_customer_match_candidates" PRIMARY KEY ("Id");


--
-- Name: lead_line_commercial_resolutions PK_lead_line_commercial_resolutions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_line_commercial_resolutions
    ADD CONSTRAINT "PK_lead_line_commercial_resolutions" PRIMARY KEY ("Id");


--
-- Name: lead_routing_decisions PK_lead_routing_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lead_routing_decisions
    ADD CONSTRAINT "PK_lead_routing_decisions" PRIMARY KEY ("Id");


--
-- Name: learning_governance_events PK_learning_governance_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.learning_governance_events
    ADD CONSTRAINT "PK_learning_governance_events" PRIMARY KEY ("Id");


--
-- Name: lifecycle_outbox_messages PK_lifecycle_outbox_messages; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lifecycle_outbox_messages
    ADD CONSTRAINT "PK_lifecycle_outbox_messages" PRIMARY KEY ("Id");


--
-- Name: material_lot_certificates PK_material_lot_certificates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_certificates
    ADD CONSTRAINT "PK_material_lot_certificates" PRIMARY KEY ("Id");


--
-- Name: material_lot_consumptions PK_material_lot_consumptions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lot_consumptions
    ADD CONSTRAINT "PK_material_lot_consumptions" PRIMARY KEY ("Id");


--
-- Name: material_lots PK_material_lots; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.material_lots
    ADD CONSTRAINT "PK_material_lots" PRIMARY KEY ("Id");


--
-- Name: ports_of_entry PK_ports_of_entry; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ports_of_entry
    ADD CONSTRAINT "PK_ports_of_entry" PRIMARY KEY ("Id");


--
-- Name: procurement_callback_receipts PK_procurement_callback_receipts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_callback_receipts
    ADD CONSTRAINT "PK_procurement_callback_receipts" PRIMARY KEY ("Id");


--
-- Name: procurement_events PK_procurement_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_events
    ADD CONSTRAINT "PK_procurement_events" PRIMARY KEY ("Id");


--
-- Name: procurement_handoffs PK_procurement_handoffs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_handoffs
    ADD CONSTRAINT "PK_procurement_handoffs" PRIMARY KEY ("Id");


--
-- Name: procurement_outbox PK_procurement_outbox; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.procurement_outbox
    ADD CONSTRAINT "PK_procurement_outbox" PRIMARY KEY ("Id");


--
-- Name: product_aliases PK_product_aliases; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.product_aliases
    ADD CONSTRAINT "PK_product_aliases" PRIMARY KEY ("Id");


--
-- Name: product_supersessions PK_product_supersessions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.product_supersessions
    ADD CONSTRAINT "PK_product_supersessions" PRIMARY KEY ("Id");


--
-- Name: quote_delivery_requests PK_quote_delivery_requests; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.quote_delivery_requests
    ADD CONSTRAINT "PK_quote_delivery_requests" PRIMARY KEY ("Id");


--
-- Name: sales_coaching_acknowledgements PK_sales_coaching_acknowledgements; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_coaching_acknowledgements
    ADD CONSTRAINT "PK_sales_coaching_acknowledgements" PRIMARY KEY ("Id");


--
-- Name: sales_contributions PK_sales_contributions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_contributions
    ADD CONSTRAINT "PK_sales_contributions" PRIMARY KEY ("Id");


--
-- Name: sales_rep_profiles PK_sales_rep_profiles; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_rep_profiles
    ADD CONSTRAINT "PK_sales_rep_profiles" PRIMARY KEY ("Id");


--
-- Name: sales_team_memberships PK_sales_team_memberships; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sales_team_memberships
    ADD CONSTRAINT "PK_sales_team_memberships" PRIMARY KEY ("Id");


--
-- Name: source_document_occurrences PK_source_document_occurrences; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT "PK_source_document_occurrences" PRIMARY KEY (id);


--
-- Name: source_documents PK_source_documents; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_documents
    ADD CONSTRAINT "PK_source_documents" PRIMARY KEY (id);


--
-- Name: sourcing_case_candidates PK_sourcing_case_candidates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_case_candidates
    ADD CONSTRAINT "PK_sourcing_case_candidates" PRIMARY KEY ("Id");


--
-- Name: sourcing_cases PK_sourcing_cases; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sourcing_cases
    ADD CONSTRAINT "PK_sourcing_cases" PRIMARY KEY ("Id");


--
-- Name: stock_reservations PK_stock_reservations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.stock_reservations
    ADD CONSTRAINT "PK_stock_reservations" PRIMARY KEY ("Id");


--
-- Name: supplier_negotiation_decisions PK_supplier_negotiation_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_negotiation_decisions
    ADD CONSTRAINT "PK_supplier_negotiation_decisions" PRIMARY KEY ("Id");


--
-- Name: supplier_purchase_order_lines PK_supplier_purchase_order_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_order_lines
    ADD CONSTRAINT "PK_supplier_purchase_order_lines" PRIMARY KEY ("Id");


--
-- Name: supplier_purchase_orders PK_supplier_purchase_orders; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_purchase_orders
    ADD CONSTRAINT "PK_supplier_purchase_orders" PRIMARY KEY ("Id");


--
-- Name: supplier_quote_field_evidence PK_supplier_quote_field_evidence; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_field_evidence
    ADD CONSTRAINT "PK_supplier_quote_field_evidence" PRIMARY KEY ("Id");


--
-- Name: supplier_quote_lines PK_supplier_quote_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_lines
    ADD CONSTRAINT "PK_supplier_quote_lines" PRIMARY KEY ("Id");


--
-- Name: supplier_quote_review_decisions PK_supplier_quote_review_decisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_review_decisions
    ADD CONSTRAINT "PK_supplier_quote_review_decisions" PRIMARY KEY ("Id");


--
-- Name: supplier_quote_revisions PK_supplier_quote_revisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quote_revisions
    ADD CONSTRAINT "PK_supplier_quote_revisions" PRIMARY KEY ("Id");


--
-- Name: supplier_quotes PK_supplier_quotes; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_quotes
    ADD CONSTRAINT "PK_supplier_quotes" PRIMARY KEY ("Id");


--
-- Name: supplier_shipment_lines PK_supplier_shipment_lines; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipment_lines
    ADD CONSTRAINT "PK_supplier_shipment_lines" PRIMARY KEY ("Id");


--
-- Name: supplier_shipments PK_supplier_shipments; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.supplier_shipments
    ADD CONSTRAINT "PK_supplier_shipments" PRIMARY KEY ("Id");


--
-- Name: tenant_governance_audit_events PK_tenant_governance_audit_events; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tenant_governance_audit_events
    ADD CONSTRAINT "PK_tenant_governance_audit_events" PRIMARY KEY ("Id");


--
-- Name: unassigned_work_items PK_unassigned_work_items; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.unassigned_work_items
    ADD CONSTRAINT "PK_unassigned_work_items" PRIMARY KEY ("Id");


--
-- Name: validation_findings PK_validation_findings; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.validation_findings
    ADD CONSTRAINT "PK_validation_findings" PRIMARY KEY (id);


--
-- Name: canonical_inquiries ak_canonical_inquiries_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_inquiries
    ADD CONSTRAINT ak_canonical_inquiries_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: canonical_line_items ak_canonical_line_items_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.canonical_line_items
    ADD CONSTRAINT ak_canonical_line_items_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: commercial_document_classifications ak_commercial_document_classifications_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commercial_document_classifications
    ADD CONSTRAINT ak_commercial_document_classifications_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: document_corpora ak_document_corpora_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_corpora
    ADD CONSTRAINT ak_document_corpora_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: document_pages ak_document_pages_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_pages
    ADD CONSTRAINT ak_document_pages_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: document_regions ak_document_regions_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.document_regions
    ADD CONSTRAINT ak_document_regions_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: extraction_runs ak_extraction_runs_run_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT ak_extraction_runs_run_id UNIQUE (run_id);


--
-- Name: extraction_runs ak_extraction_runs_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT ak_extraction_runs_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: extraction_runs ak_extraction_runs_tenant_run_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.extraction_runs
    ADD CONSTRAINT ak_extraction_runs_tenant_run_id UNIQUE (business_unit_id, run_id);


--
-- Name: source_document_occurrences ak_source_document_occurrences_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_document_occurrences
    ADD CONSTRAINT ak_source_document_occurrences_tenant_id UNIQUE (business_unit_id, id);


--
-- Name: source_documents ak_source_documents_tenant_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_documents
    ADD CONSTRAINT ak_source_documents_tenant_id UNIQUE (business_unit_id, id);

-- ==========================================================================
-- Indexes (incl. partial and expression indexes)
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
-- Name: IX_AccountingOutbox_Invoice_MessageType; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AccountingOutbox_Invoice_MessageType" ON platform."AccountingOutbox" USING btree ("SubscriptionInvoiceId", "MessageType");


--
-- Name: IX_AccountingOutbox_Status_AvailableAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AccountingOutbox_Status_AvailableAtUtc" ON platform."AccountingOutbox" USING btree ("Status", "AvailableAtUtc");


--
-- Name: IX_AccountingOutbox_SubscriptionRevenueActionId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AccountingOutbox_SubscriptionRevenueActionId" ON platform."AccountingOutbox" USING btree ("SubscriptionRevenueActionId");


--
-- Name: IX_AccountingOutbox_TenantId_SubscriptionInvoiceId_Subscriptio~; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AccountingOutbox_TenantId_SubscriptionInvoiceId_Subscriptio~" ON platform."AccountingOutbox" USING btree ("TenantId", "SubscriptionInvoiceId", "SubscriptionRevenueActionId");


--
-- Name: IX_BillingStatementLines_Statement; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BillingStatementLines_Statement" ON platform."BillingStatementLines" USING btree ("BillingStatementId");


--
-- Name: IX_BillingStatements_RateCard; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BillingStatements_RateCard" ON platform."BillingStatements" USING btree ("RateCardId");


--
-- Name: IX_BillingStatements_Tenant_Status; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BillingStatements_Tenant_Status" ON platform."BillingStatements" USING btree ("TenantId", "Status");


--
-- Name: IX_ImpersonationSessions_ExpiresAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ImpersonationSessions_ExpiresAtUtc" ON platform."ImpersonationSessions" USING btree ("ExpiresAtUtc");


--
-- Name: IX_ImpersonationSessions_Jti; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImpersonationSessions_Jti" ON platform."ImpersonationSessions" USING btree ("Jti");


--
-- Name: IX_ImpersonationSessions_TenantId_IssuedAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ImpersonationSessions_TenantId_IssuedAtUtc" ON platform."ImpersonationSessions" USING btree ("TenantId", "IssuedAtUtc");


--
-- Name: IX_Plans_Code; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Plans_Code" ON platform."Plans" USING btree ("Code");


--
-- Name: IX_PlatformAuditLogs_ActAsTenantId_CreatedOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PlatformAuditLogs_ActAsTenantId_CreatedOn" ON platform."PlatformAuditLogs" USING btree ("ActAsTenantId", "CreatedOn");


--
-- Name: IX_PlatformAuditLogs_ActorPlatformUserId_CreatedOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PlatformAuditLogs_ActorPlatformUserId_CreatedOn" ON platform."PlatformAuditLogs" USING btree ("ActorPlatformUserId", "CreatedOn");


--
-- Name: IX_PlatformBrowserTrusts_PlatformUserId_RevokedAtUtc_ExpiresAt~; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PlatformBrowserTrusts_PlatformUserId_RevokedAtUtc_ExpiresAt~" ON platform."PlatformBrowserTrusts" USING btree ("PlatformUserId", "RevokedAtUtc", "ExpiresAtUtc");


--
-- Name: IX_PlatformBrowserTrusts_TokenHash; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlatformBrowserTrusts_TokenHash" ON platform."PlatformBrowserTrusts" USING btree ("TokenHash");


--
-- Name: IX_PlatformMfaChallenges_PlatformUserId_ExpiresAtUtc_ConsumedA~; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PlatformMfaChallenges_PlatformUserId_ExpiresAtUtc_ConsumedA~" ON platform."PlatformMfaChallenges" USING btree ("PlatformUserId", "ExpiresAtUtc", "ConsumedAtUtc");


--
-- Name: IX_PlatformMfaRecoveryCodes_PlatformUserId_CodeHash; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlatformMfaRecoveryCodes_PlatformUserId_CodeHash" ON platform."PlatformMfaRecoveryCodes" USING btree ("PlatformUserId", "CodeHash");


--
-- Name: IX_PlatformSessions_Jti; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlatformSessions_Jti" ON platform."PlatformSessions" USING btree ("Jti");


--
-- Name: IX_PlatformSessions_PlatformUserId_RevokedAtUtc_ExpiresAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PlatformSessions_PlatformUserId_RevokedAtUtc_ExpiresAtUtc" ON platform."PlatformSessions" USING btree ("PlatformUserId", "RevokedAtUtc", "ExpiresAtUtc");


--
-- Name: IX_PlatformUsers_Email; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlatformUsers_Email" ON platform."PlatformUsers" USING btree ("Email");


--
-- Name: IX_ProvisioningDrafts_Owner_UpdatedOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProvisioningDrafts_Owner_UpdatedOn" ON platform."ProvisioningDrafts" USING btree ("OwnerEmail", "UpdatedOn");


--
-- Name: IX_ProvisioningExecutions_Claim; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProvisioningExecutions_Claim" ON platform."ProvisioningExecutions" USING btree ("State", "LeaseUntil", "CreatedOn");


--
-- Name: IX_ProvisioningExecutions_CreatedOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProvisioningExecutions_CreatedOn" ON platform."ProvisioningExecutions" USING btree ("CreatedOn");


--
-- Name: IX_ProvisioningExecutions_TenantId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProvisioningExecutions_TenantId" ON platform."ProvisioningExecutions" USING btree ("TenantId");


--
-- Name: IX_ProvisioningSteps_Execution_Ordinal; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProvisioningSteps_Execution_Ordinal" ON platform."ProvisioningSteps" USING btree ("ExecutionId", "Ordinal");


--
-- Name: IX_RateCards_Active_EffectiveFrom; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RateCards_Active_EffectiveFrom" ON platform."RateCards" USING btree ("IsActive", "EffectiveFromUtc");


--
-- Name: IX_SubscriptionCreditNotes_CreditNumber; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionCreditNotes_CreditNumber" ON platform."SubscriptionCreditNotes" USING btree ("CreditNumber");


--
-- Name: IX_SubscriptionCreditNotes_IdempotencyKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionCreditNotes_IdempotencyKey" ON platform."SubscriptionCreditNotes" USING btree ("IdempotencyKey");


--
-- Name: IX_SubscriptionCreditNotes_SubscriptionInvoiceId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionCreditNotes_SubscriptionInvoiceId" ON platform."SubscriptionCreditNotes" USING btree ("SubscriptionInvoiceId");


--
-- Name: IX_SubscriptionInvoices_BillingStatementId; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionInvoices_BillingStatementId" ON platform."SubscriptionInvoices" USING btree ("BillingStatementId");


--
-- Name: IX_SubscriptionInvoices_InvoiceNumber; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionInvoices_InvoiceNumber" ON platform."SubscriptionInvoices" USING btree ("InvoiceNumber");


--
-- Name: IX_SubscriptionInvoices_TaxRuleId_TaxRuleVersion; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionInvoices_TaxRuleId_TaxRuleVersion" ON platform."SubscriptionInvoices" USING btree ("TaxRuleId", "TaxRuleVersion");


--
-- Name: IX_SubscriptionInvoices_TenantId_Status_DueAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionInvoices_TenantId_Status_DueAtUtc" ON platform."SubscriptionInvoices" USING btree ("TenantId", "Status", "DueAtUtc");


--
-- Name: IX_SubscriptionPayments_ExternalReference; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionPayments_ExternalReference" ON platform."SubscriptionPayments" USING btree ("ExternalReference");


--
-- Name: IX_SubscriptionPayments_SubscriptionInvoiceId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionPayments_SubscriptionInvoiceId" ON platform."SubscriptionPayments" USING btree ("SubscriptionInvoiceId");


--
-- Name: IX_SubscriptionRevenueActions_ApprovedByPlatformUserId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionRevenueActions_ApprovedByPlatformUserId" ON platform."SubscriptionRevenueActions" USING btree ("ApprovedByPlatformUserId");


--
-- Name: IX_SubscriptionRevenueActions_IdempotencyKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionRevenueActions_IdempotencyKey" ON platform."SubscriptionRevenueActions" USING btree ("IdempotencyKey");


--
-- Name: IX_SubscriptionRevenueActions_ProposedByPlatformUserId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionRevenueActions_ProposedByPlatformUserId" ON platform."SubscriptionRevenueActions" USING btree ("ProposedByPlatformUserId");


--
-- Name: IX_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_K~; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_K~" ON platform."SubscriptionRevenueActions" USING btree ("TenantId", "SubscriptionInvoiceId", "Kind", "Status");


--
-- Name: IX_SubscriptionTaxRules_ApprovedByPlatformUserId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionTaxRules_ApprovedByPlatformUserId" ON platform."SubscriptionTaxRules" USING btree ("ApprovedByPlatformUserId");


--
-- Name: IX_SubscriptionTaxRules_JurisdictionCode_BuyerCountryCode_Curr~; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionTaxRules_JurisdictionCode_BuyerCountryCode_Curr~" ON platform."SubscriptionTaxRules" USING btree ("JurisdictionCode", "BuyerCountryCode", "Currency", "EffectiveFromUtc");


--
-- Name: IX_SubscriptionTaxRules_ProposedByPlatformUserId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionTaxRules_ProposedByPlatformUserId" ON platform."SubscriptionTaxRules" USING btree ("ProposedByPlatformUserId");


--
-- Name: IX_SubscriptionTaxRules_Status_BuyerCountryCode_Currency; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SubscriptionTaxRules_Status_BuyerCountryCode_Currency" ON platform."SubscriptionTaxRules" USING btree ("Status", "BuyerCountryCode", "Currency");


--
-- Name: IX_SupportTicketLinks_Kind_Target; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupportTicketLinks_Kind_Target" ON platform."SupportTicketLinks" USING btree ("Kind", "TargetKey");


--
-- Name: IX_SupportTicketNotes_Ticket_Created; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupportTicketNotes_Ticket_Created" ON platform."SupportTicketNotes" USING btree ("SupportTicketId", "CreatedAtUtc");


--
-- Name: IX_SupportTickets_Assignee_Status_Updated; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupportTickets_Assignee_Status_Updated" ON platform."SupportTickets" USING btree ("AssignedToPlatformUserId", "Status", "UpdatedAtUtc");


--
-- Name: IX_SupportTickets_OpenedByPlatformUserId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupportTickets_OpenedByPlatformUserId" ON platform."SupportTickets" USING btree ("OpenedByPlatformUserId");


--
-- Name: IX_SupportTickets_Status_Severity_Created; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupportTickets_Status_Severity_Created" ON platform."SupportTickets" USING btree ("Status", "Severity", "CreatedAtUtc");


--
-- Name: IX_SupportTickets_Tenant_Status_Updated; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupportTickets_Tenant_Status_Updated" ON platform."SupportTickets" USING btree ("TenantId", "Status", "UpdatedAtUtc");


--
-- Name: IX_TenantAdminInvitations_ExpiresAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantAdminInvitations_ExpiresAtUtc" ON platform."TenantAdminInvitations" USING btree ("ExpiresAtUtc");


--
-- Name: IX_TenantAdminInvitations_TenantId_IssuedAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantAdminInvitations_TenantId_IssuedAtUtc" ON platform."TenantAdminInvitations" USING btree ("TenantId", "IssuedAtUtc");


--
-- Name: IX_TenantAdminInvitations_UserId_Live; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantAdminInvitations_UserId_Live" ON platform."TenantAdminInvitations" USING btree ("UserId", "RedeemedAtUtc", "RevokedAtUtc");


--
-- Name: IX_TenantDataAssets_TenantId_LogicalKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TenantDataAssets_TenantId_LogicalKey" ON platform."TenantDataAssets" USING btree ("TenantId", "LogicalKey");


--
-- Name: IX_TenantDataRecoveryEvidence_TenantId_IdempotencyKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TenantDataRecoveryEvidence_TenantId_IdempotencyKey" ON platform."TenantDataRecoveryEvidence" USING btree ("TenantId", "IdempotencyKey");


--
-- Name: IX_TenantDataRecoveryEvidence_TenantId_ScopeKey_EvidenceType_C~; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantDataRecoveryEvidence_TenantId_ScopeKey_EvidenceType_C~" ON platform."TenantDataRecoveryEvidence" USING btree ("TenantId", "ScopeKey", "EvidenceType", "CompletedUtc");


--
-- Name: IX_TenantDataRecoveryEvidence_TenantId_TenantDataAssetId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantDataRecoveryEvidence_TenantId_TenantDataAssetId" ON platform."TenantDataRecoveryEvidence" USING btree ("TenantId", "TenantDataAssetId");


--
-- Name: IX_TenantDeletionCertificates_TenantId; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TenantDeletionCertificates_TenantId" ON platform."TenantDeletionCertificates" USING btree ("TenantId");


--
-- Name: IX_TenantExportReceipts_TenantId_CompletedOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantExportReceipts_TenantId_CompletedOn" ON platform."TenantExportReceipts" USING btree ("TenantId", "CompletedOn");


--
-- Name: IX_TenantLegalHolds_TenantId_ReleasedOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantLegalHolds_TenantId_ReleasedOn" ON platform."TenantLegalHolds" USING btree ("TenantId", "ReleasedOn");


--
-- Name: IX_TenantLifecycleEvents_Action_OccurredOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantLifecycleEvents_Action_OccurredOn" ON platform."TenantLifecycleEvents" USING btree ("Action", "OccurredOn");


--
-- Name: IX_TenantLifecycleEvents_TenantId_OccurredOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantLifecycleEvents_TenantId_OccurredOn" ON platform."TenantLifecycleEvents" USING btree ("TenantId", "OccurredOn", "Id");


--
-- Name: IX_TenantOffboardings_Stage_PurgeEligibleOn; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_TenantOffboardings_Stage_PurgeEligibleOn" ON platform."TenantOffboardings" USING btree ("Stage", "PurgeEligibleOn");


--
-- Name: IX_Tenants_PlanId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Tenants_PlanId" ON platform."Tenants" USING btree ("PlanId");


--
-- Name: IX_Tenants_RateCardId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Tenants_RateCardId" ON platform."Tenants" USING btree ("RateCardId");


--
-- Name: IX_Tenants_Slug; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tenants_Slug" ON platform."Tenants" USING btree ("Slug");


--
-- Name: IX_Tenants_Status_BillingMode; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Tenants_Status_BillingMode" ON platform."Tenants" USING btree ("Status", "BillingMode");


--
-- Name: IX_UsageEventRatings_PlanId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEventRatings_PlanId" ON platform."UsageEventRatings" USING btree ("PlanId");


--
-- Name: IX_UsageEventRatings_RateCardId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEventRatings_RateCardId" ON platform."UsageEventRatings" USING btree ("RateCardId");


--
-- Name: IX_UsageEventRatings_RateCardLineId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEventRatings_RateCardLineId" ON platform."UsageEventRatings" USING btree ("RateCardLineId");


--
-- Name: IX_UsageEvents_AdjustsUsageEventId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEvents_AdjustsUsageEventId" ON platform."UsageEvents" USING btree ("AdjustsUsageEventId");


--
-- Name: IX_UsageEvents_RateCardId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEvents_RateCardId" ON platform."UsageEvents" USING btree ("RateCardId");


--
-- Name: IX_UsageEvents_RateCardLineId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEvents_RateCardLineId" ON platform."UsageEvents" USING btree ("RateCardLineId");


--
-- Name: IX_UsageEvents_TenantId_AdjustsUsageEventId; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEvents_TenantId_AdjustsUsageEventId" ON platform."UsageEvents" USING btree ("TenantId", "AdjustsUsageEventId");


--
-- Name: IX_UsageEvents_TenantId_EventType_OccurredAtUtc; Type: INDEX; Schema: platform; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UsageEvents_TenantId_EventType_OccurredAtUtc" ON platform."UsageEvents" USING btree ("TenantId", "EventType", "OccurredAtUtc");


--
-- Name: UX_AccountingOutbox_IdempotencyKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AccountingOutbox_IdempotencyKey" ON platform."AccountingOutbox" USING btree ("IdempotencyKey");


--
-- Name: UX_BillingStatements_Tenant_PeriodStart; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BillingStatements_Tenant_PeriodStart" ON platform."BillingStatements" USING btree ("TenantId", "PeriodStartUtc");


--
-- Name: UX_ProvisioningExecutions_IdempotencyKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProvisioningExecutions_IdempotencyKey" ON platform."ProvisioningExecutions" USING btree ("IdempotencyKey");


--
-- Name: UX_ProvisioningExecutions_LiveSlug; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProvisioningExecutions_LiveSlug" ON platform."ProvisioningExecutions" USING btree ("Slug") WHERE ("State"  = ANY (ARRAY['Pending'::character varying, 'Running'::character varying, 'Failed'::character varying]));


--
-- Name: UX_ProvisioningSteps_Execution_StepCode; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProvisioningSteps_Execution_StepCode" ON platform."ProvisioningSteps" USING btree ("ExecutionId", "StepCode");


--
-- Name: UX_RateCardLines_RateCard_MeterKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_RateCardLines_RateCard_MeterKey" ON platform."RateCardLines" USING btree ("RateCardId", "MeterKey");


--
-- Name: UX_RateCards_Code; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_RateCards_Code" ON platform."RateCards" USING btree ("Code");


--
-- Name: UX_SupportTicketLinks_Ticket_Kind_Target; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_SupportTicketLinks_Ticket_Kind_Target" ON platform."SupportTicketLinks" USING btree ("SupportTicketId", "Kind", "TargetKey");


--
-- Name: UX_TenantAdminInvitations_TokenHash; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_TenantAdminInvitations_TokenHash" ON platform."TenantAdminInvitations" USING btree ("TokenHash");


--
-- Name: UX_TenantLegalHolds_ActiveScope; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_TenantLegalHolds_ActiveScope" ON platform."TenantLegalHolds" USING btree ("TenantId", "Scope") WHERE ("ReleasedOn" IS NULL);


--
-- Name: UX_TenantOffboardings_TenantId; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_TenantOffboardings_TenantId" ON platform."TenantOffboardings" USING btree ("TenantId");


--
-- Name: UX_UsageCoverageSegments_Tenant_Meter_Range; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageCoverageSegments_Tenant_Meter_Range" ON platform."UsageCoverageSegments" USING btree ("TenantId", "MeterKey", "StartUtc", "EndUtc");


--
-- Name: UX_UsageEventRatings_Event_Attempt; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageEventRatings_Event_Attempt" ON platform."UsageEventRatings" USING btree ("TenantId", "UsageEventId", "AttemptNumber");


--
-- Name: UX_UsageEventRatings_Tenant_Idempotency; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageEventRatings_Tenant_Idempotency" ON platform."UsageEventRatings" USING btree ("TenantId", "IdempotencyKey");


--
-- Name: UX_UsageEvents_Tenant_IdempotencyKey; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageEvents_Tenant_IdempotencyKey" ON platform."UsageEvents" USING btree ("TenantId", "IdempotencyKey");


--
-- Name: UX_UsageMinuteAggregates_Bucket; Type: INDEX; Schema: platform; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageMinuteAggregates_Bucket" ON platform."UsageMinuteAggregates" USING btree ("TenantId", "EventType", "Unit", "MinuteUtc");


--
-- Name: IX_AgentApprovals_BU_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AgentApprovals_BU_Status" ON public."AgentApprovals" USING btree ("BusinessUnitId", "Status");


--
-- Name: IX_AgentAuditLogs_BU_CreatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AgentAuditLogs_BU_CreatedOn" ON public."AgentAuditLogs" USING btree ("BusinessUnitId", "CreatedOn");


--
-- Name: IX_AgentMessages_Session_Sequence; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AgentMessages_Session_Sequence" ON public."AgentMessages" USING btree ("SessionId", "Sequence");


--
-- Name: IX_AgentPolicies_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AgentPolicies_BusinessUnitId_CurrencyId" ON public."AgentPolicies" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_AgentSessions_BU_UpdatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AgentSessions_BU_UpdatedOn" ON public."AgentSessions" USING btree ("BusinessUnitId", "UpdatedOn");


--
-- Name: IX_AiCallAttempts_BU_StartedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiCallAttempts_BU_StartedOn" ON public."AiCallAttempts" USING btree ("BusinessUnitId", "StartedOn");


--
-- Name: IX_AiCallAttempts_BusinessUnitId_RequestId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiCallAttempts_BusinessUnitId_RequestId" ON public."AiCallAttempts" USING btree ("BusinessUnitId", "RequestId");


--
-- Name: IX_AiProviderAuthorizations_BU_Endpoint; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiProviderAuthorizations_BU_Endpoint" ON public."AiProviderAuthorizations" USING btree ("BusinessUnitId", "Endpoint");


--
-- Name: IX_AiRequests_BU_CreatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiRequests_BU_CreatedOn" ON public."AiRequests" USING btree ("BusinessUnitId", "CreatedOn");


--
-- Name: IX_AiRequests_BusinessUnitId_ExtractionJobId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiRequests_BusinessUnitId_ExtractionJobId" ON public."AiRequests" USING btree ("BusinessUnitId", "ExtractionJobId");


--
-- Name: IX_AiRequests_BusinessUnitId_ProviderClass_CreatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiRequests_BusinessUnitId_ProviderClass_CreatedOn" ON public."AiRequests" USING btree ("BusinessUnitId", "ProviderClass", "CreatedOn");


--
-- Name: IX_AiRequests_BusinessUnitId_SourceDocumentOccurrenceId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiRequests_BusinessUnitId_SourceDocumentOccurrenceId" ON public."AiRequests" USING btree ("BusinessUnitId", "SourceDocumentOccurrenceId");


--
-- Name: IX_AiRequests_Unresolved_CreatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_AiRequests_Unresolved_CreatedOn" ON public."AiRequests" USING btree ("CreatedOn") WHERE (("CompletedOn" IS NULL) AND ("Status"  = ANY (ARRAY['Reserved'::character varying, 'Running'::character varying])));


--
-- Name: IX_Attachments_ParentTypeID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Attachments_ParentTypeID" ON public."Attachments" USING btree ("ParentType", "ParentID");


--
-- Name: IX_BankAccounts_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAccounts_BusinessUnitId_CurrencyId" ON public."BankAccounts" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_BankAccounts_BusinessUnitId_LedgerAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAccounts_BusinessUnitId_LedgerAccountId" ON public."BankAccounts" USING btree ("BusinessUnitId", "LedgerAccountId");


--
-- Name: IX_BankAdjustmentDistributions_BusinessUnitId_LedgerAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustmentDistributions_BusinessUnitId_LedgerAccountId" ON public."BankAdjustmentDistributions" USING btree ("BusinessUnitId", "LedgerAccountId");


--
-- Name: IX_BankAdjustments_BusinessUnitId_AccountingPeriodId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustments_BusinessUnitId_AccountingPeriodId" ON public."BankAdjustments" USING btree ("BusinessUnitId", "AccountingPeriodId");


--
-- Name: IX_BankAdjustments_BusinessUnitId_BankAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustments_BusinessUnitId_BankAccountId" ON public."BankAdjustments" USING btree ("BusinessUnitId", "BankAccountId");


--
-- Name: IX_BankAdjustments_BusinessUnitId_BankJournalEntryLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustments_BusinessUnitId_BankJournalEntryLineId" ON public."BankAdjustments" USING btree ("BusinessUnitId", "BankJournalEntryLineId");


--
-- Name: IX_BankAdjustments_BusinessUnitId_BankStatementLineId_BankAcco~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustments_BusinessUnitId_BankStatementLineId_BankAcco~" ON public."BankAdjustments" USING btree ("BusinessUnitId", "BankStatementLineId", "BankAccountId");


--
-- Name: IX_BankAdjustments_BusinessUnitId_ReversalBankJournalEntryLine~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustments_BusinessUnitId_ReversalBankJournalEntryLine~" ON public."BankAdjustments" USING btree ("BusinessUnitId", "ReversalBankJournalEntryLineId");


--
-- Name: IX_BankAdjustments_BusinessUnitId_ReversalJournalEntryId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankAdjustments_BusinessUnitId_ReversalJournalEntryId" ON public."BankAdjustments" USING btree ("BusinessUnitId", "ReversalJournalEntryId");


--
-- Name: IX_BankMatchingRules_BusinessUnitId_BankAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankMatchingRules_BusinessUnitId_BankAccountId" ON public."BankMatchingRules" USING btree ("BusinessUnitId", "BankAccountId");


--
-- Name: IX_BankMatchingRules_BusinessUnitId_SupersedesRuleId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankMatchingRules_BusinessUnitId_SupersedesRuleId" ON public."BankMatchingRules" USING btree ("BusinessUnitId", "SupersedesRuleId");


--
-- Name: IX_BankStatementLines_BusinessUnitId_BankStatementId_BankAccou~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankStatementLines_BusinessUnitId_BankStatementId_BankAccou~" ON public."BankStatementLines" USING btree ("BusinessUnitId", "BankStatementId", "BankAccountId");


--
-- Name: IX_BankStatements_BusinessUnitId_BankAccountId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankStatements_BusinessUnitId_BankAccountId_CurrencyId" ON public."BankStatements" USING btree ("BusinessUnitId", "BankAccountId", "CurrencyId");


--
-- Name: IX_BankStatements_BusinessUnitId_BankStatementImportId_BankAcc~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankStatements_BusinessUnitId_BankStatementImportId_BankAcc~" ON public."BankStatements" USING btree ("BusinessUnitId", "BankStatementImportId", "BankAccountId");


--
-- Name: IX_BankStatements_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BankStatements_BusinessUnitId_CurrencyId" ON public."BankStatements" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_BoqAssemblyComponents_Assembly_Seq; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BoqAssemblyComponents_Assembly_Seq" ON public."BoqAssemblyComponents" USING btree ("BoqAssemblyId", "Seq");


--
-- Name: IX_BoqDocuments_BU_Lead; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BoqDocuments_BU_Lead" ON public."BoqDocuments" USING btree ("BusinessUnitId", "LeadId");


--
-- Name: IX_BoqDocuments_BU_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BoqDocuments_BU_Status" ON public."BoqDocuments" USING btree ("BusinessUnitId", "Status");


--
-- Name: IX_BoqItems_Section_Seq; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BoqItems_Section_Seq" ON public."BoqItems" USING btree ("BoqSectionId", "Seq");


--
-- Name: IX_BoqSections_Doc_Seq; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_BoqSections_Doc_Seq" ON public."BoqSections" USING btree ("BoqDocumentId", "Seq");


--
-- Name: IX_CollectionControls_BU_Customer_Status_Type; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CollectionControls_BU_Customer_Status_Type" ON public."CollectionControls" USING btree ("BusinessUnitId", "CustomerId", "Status", "ControlType");


--
-- Name: IX_CollectionControls_BusinessUnitId_ReceivableDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CollectionControls_BusinessUnitId_ReceivableDocumentId" ON public."CollectionControls" USING btree ("BusinessUnitId", "ReceivableDocumentId");


--
-- Name: IX_CollectionControls_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CollectionControls_CurrencyId" ON public."CollectionControls" USING btree ("CurrencyId");


--
-- Name: IX_CollectionControls_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CollectionControls_CustomerId" ON public."CollectionControls" USING btree ("CustomerId");


--
-- Name: IX_CommercialFinanceAudits_BusinessUnitId_AggregateType_Aggreg~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CommercialFinanceAudits_BusinessUnitId_AggregateType_Aggreg~" ON public."CommercialFinanceAudits" USING btree ("BusinessUnitId", "AggregateType", "AggregateId", "OccurredOn");


--
-- Name: IX_CommercialMatchingPolicies_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_CommercialMatchingPolicies_BusinessUnitId" ON public."CommercialMatchingPolicies" USING btree ("BusinessUnitId");


--
-- Name: IX_Contacts_BusinessUnitID_Email; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Contacts_BusinessUnitID_Email" ON public."Contacts" USING btree ("BusinessUnitID", "Email");


--
-- Name: IX_Contacts_CustomerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Contacts_CustomerID" ON public."Contacts" USING btree ("CustomerID");


--
-- Name: IX_Contacts_SupplierID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Contacts_SupplierID" ON public."Contacts" USING btree ("SupplierID");


--
-- Name: IX_Contacts_SupplierID_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Contacts_SupplierID_BusinessUnitID" ON public."Contacts" USING btree ("SupplierID", "BusinessUnitID");


--
-- Name: IX_Currency_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Currency_BusinessUnitID" ON public."Currency" USING btree ("BusinessUnitID");


--
-- Name: IX_CustomerAwardAllocations_BU_QuoteItem; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwardAllocations_BU_QuoteItem" ON public."CustomerAwardLineAllocations" USING btree ("BusinessUnitId", "QuoteItemId");


--
-- Name: IX_CustomerAwardLineAllocations_BusinessUnitId_CustomerAwardId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwardLineAllocations_BusinessUnitId_CustomerAwardId" ON public."CustomerAwardLineAllocations" USING btree ("BusinessUnitId", "CustomerAwardId");


--
-- Name: IX_CustomerAwardLineAllocations_BusinessUnitId_CustomerPurchas~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwardLineAllocations_BusinessUnitId_CustomerPurchas~" ON public."CustomerAwardLineAllocations" USING btree ("BusinessUnitId", "CustomerPurchaseOrderLineId");


--
-- Name: IX_CustomerAwardLineAllocations_QuoteItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwardLineAllocations_QuoteItemId" ON public."CustomerAwardLineAllocations" USING btree ("QuoteItemId");


--
-- Name: IX_CustomerAwards_BU_PO_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwards_BU_PO_Status" ON public."CustomerAwards" USING btree ("BusinessUnitId", "CustomerPurchaseOrderId", "Status");


--
-- Name: IX_CustomerAwards_BU_Quote_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwards_BU_Quote_Status" ON public."CustomerAwards" USING btree ("BusinessUnitId", "QuoteId", "Status");


--
-- Name: IX_CustomerAwards_BusinessUnitId_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwards_BusinessUnitId_CommercialCaseId" ON public."CustomerAwards" USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_CustomerAwards_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwards_BusinessUnitId_CurrencyId" ON public."CustomerAwards" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_CustomerAwards_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerAwards_CustomerId" ON public."CustomerAwards" USING btree ("CustomerId");


--
-- Name: IX_CustomerCollectionProfiles_BusinessUnitId_DunningPolicyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerCollectionProfiles_BusinessUnitId_DunningPolicyId" ON public."CustomerCollectionProfiles" USING btree ("BusinessUnitId", "DunningPolicyId");


--
-- Name: IX_CustomerCollectionProfiles_BusinessUnitId_FinanceCommunicat~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerCollectionProfiles_BusinessUnitId_FinanceCommunicat~" ON public."CustomerCollectionProfiles" USING btree ("BusinessUnitId", "FinanceCommunicationContactId");


--
-- Name: IX_CustomerCollectionProfiles_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerCollectionProfiles_CurrencyId" ON public."CustomerCollectionProfiles" USING btree ("CurrencyId");


--
-- Name: IX_CustomerCollectionProfiles_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerCollectionProfiles_CustomerId" ON public."CustomerCollectionProfiles" USING btree ("CustomerId");


--
-- Name: IX_CustomerPayments_BusinessUnitId_BankAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPayments_BusinessUnitId_BankAccountId" ON public."CustomerPayments" USING btree ("BusinessUnitId", "BankAccountId");


--
-- Name: IX_CustomerPayments_BusinessUnitId_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPayments_BusinessUnitId_CommercialCaseId" ON public."CustomerPayments" USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_CustomerPayments_BusinessUnitId_ReversalJournalEntryId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPayments_BusinessUnitId_ReversalJournalEntryId" ON public."CustomerPayments" USING btree ("BusinessUnitId", "ReversalJournalEntryId");


--
-- Name: IX_CustomerPayments_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPayments_CurrencyId" ON public."CustomerPayments" USING btree ("CurrencyId");


--
-- Name: IX_CustomerPayments_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPayments_CustomerId" ON public."CustomerPayments" USING btree ("CustomerId");


--
-- Name: IX_CustomerPurchaseOrderLines_BusinessUnitId_UomId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPurchaseOrderLines_BusinessUnitId_UomId" ON public."CustomerPurchaseOrderLines" USING btree ("BusinessUnitId", "UomId");


--
-- Name: IX_CustomerPurchaseOrderLines_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPurchaseOrderLines_ProductId" ON public."CustomerPurchaseOrderLines" USING btree ("ProductId");


--
-- Name: IX_CustomerPurchaseOrders_BU_Case_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPurchaseOrders_BU_Case_Status" ON public."CustomerPurchaseOrders" USING btree ("BusinessUnitId", "CommercialCaseId", "Status");


--
-- Name: IX_CustomerPurchaseOrders_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPurchaseOrders_BusinessUnitId_CurrencyId" ON public."CustomerPurchaseOrders" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_CustomerPurchaseOrders_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerPurchaseOrders_CustomerId" ON public."CustomerPurchaseOrders" USING btree ("CustomerId");


--
-- Name: IX_CustomerRefunds_BusinessUnitId_BankAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerRefunds_BusinessUnitId_BankAccountId" ON public."CustomerRefunds" USING btree ("BusinessUnitId", "BankAccountId");


--
-- Name: IX_CustomerRefunds_BusinessUnitId_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerRefunds_BusinessUnitId_CommercialCaseId" ON public."CustomerRefunds" USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_CustomerRefunds_BusinessUnitId_SourcePaymentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerRefunds_BusinessUnitId_SourcePaymentId" ON public."CustomerRefunds" USING btree ("BusinessUnitId", "SourcePaymentId");


--
-- Name: IX_CustomerRefunds_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerRefunds_CurrencyId" ON public."CustomerRefunds" USING btree ("CurrencyId");


--
-- Name: IX_CustomerRefunds_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerRefunds_CustomerId" ON public."CustomerRefunds" USING btree ("CustomerId");


--
-- Name: IX_CustomerStatements_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerStatements_CurrencyId" ON public."CustomerStatements" USING btree ("CurrencyId");


--
-- Name: IX_CustomerStatements_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_CustomerStatements_CustomerId" ON public."CustomerStatements" USING btree ("CustomerId");


--
-- Name: IX_Customers_AccountTeamId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_AccountTeamId" ON public."Customers" USING btree ("AccountTeamId");


--
-- Name: IX_Customers_BU_AccountTeam; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_BU_AccountTeam" ON public."Customers" USING btree ("BUID", "AccountTeamId") WHERE ("AccountTeamId" IS NOT NULL);


--
-- Name: IX_Customers_BU_CommercialRegistrationNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_BU_CommercialRegistrationNumber" ON public."Customers" USING btree ("BUID", "CommercialRegistrationNumber") WHERE ("CommercialRegistrationNumber" IS NOT NULL);


--
-- Name: IX_Customers_BU_RegionState; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_BU_RegionState" ON public."Customers" USING btree ("BUID", "RegionStateId") WHERE ("RegionStateId" IS NOT NULL);


--
-- Name: IX_Customers_BU_TaxRegistrationNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_BU_TaxRegistrationNumber" ON public."Customers" USING btree ("BUID", "TaxRegistrationNumber") WHERE ("TaxRegistrationNumber" IS NOT NULL);


--
-- Name: IX_Customers_Name; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_Name" ON public."Customers" USING btree ("Name");


--
-- Name: IX_Customers_RegionStateId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Customers_RegionStateId" ON public."Customers" USING btree ("RegionStateId");


--
-- Name: IX_DunningCases_BusinessUnitId_CustomerStatementId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningCases_BusinessUnitId_CustomerStatementId" ON public."DunningCases" USING btree ("BusinessUnitId", "CustomerStatementId");


--
-- Name: IX_DunningCases_BusinessUnitId_DunningPolicyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningCases_BusinessUnitId_DunningPolicyId" ON public."DunningCases" USING btree ("BusinessUnitId", "DunningPolicyId");


--
-- Name: IX_DunningCases_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningCases_CurrencyId" ON public."DunningCases" USING btree ("CurrencyId");


--
-- Name: IX_DunningCases_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningCases_CustomerId" ON public."DunningCases" USING btree ("CustomerId");


--
-- Name: IX_DunningNotices_BusinessUnitId_CustomerStatementId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningNotices_BusinessUnitId_CustomerStatementId" ON public."DunningNotices" USING btree ("BusinessUnitId", "CustomerStatementId");


--
-- Name: IX_DunningNotices_BusinessUnitId_FinanceCommunicationContactId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningNotices_BusinessUnitId_FinanceCommunicationContactId" ON public."DunningNotices" USING btree ("BusinessUnitId", "FinanceCommunicationContactId");


--
-- Name: IX_DunningRunDecisions_BusinessUnitId_CustomerCollectionProfil~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRunDecisions_BusinessUnitId_CustomerCollectionProfil~" ON public."DunningRunDecisions" USING btree ("BusinessUnitId", "CustomerCollectionProfileId");


--
-- Name: IX_DunningRunDecisions_BusinessUnitId_CustomerStatementId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRunDecisions_BusinessUnitId_CustomerStatementId" ON public."DunningRunDecisions" USING btree ("BusinessUnitId", "CustomerStatementId");


--
-- Name: IX_DunningRunDecisions_BusinessUnitId_DunningCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRunDecisions_BusinessUnitId_DunningCaseId" ON public."DunningRunDecisions" USING btree ("BusinessUnitId", "DunningCaseId");


--
-- Name: IX_DunningRunDecisions_BusinessUnitId_DunningNoticeId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRunDecisions_BusinessUnitId_DunningNoticeId" ON public."DunningRunDecisions" USING btree ("BusinessUnitId", "DunningNoticeId");


--
-- Name: IX_DunningRunDecisions_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRunDecisions_CurrencyId" ON public."DunningRunDecisions" USING btree ("CurrencyId");


--
-- Name: IX_DunningRunDecisions_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRunDecisions_CustomerId" ON public."DunningRunDecisions" USING btree ("CustomerId");


--
-- Name: IX_DunningRuns_BusinessUnitId_DunningPolicyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_DunningRuns_BusinessUnitId_DunningPolicyId" ON public."DunningRuns" USING btree ("BusinessUnitId", "DunningPolicyId");


--
-- Name: IX_Email_Configurations_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Email_Configurations_BusinessUnitID" ON public."Email_Configurations" USING btree ("BusinessUnitID");


--
-- Name: IX_ExtractionCorpusEntries_BU_Path_Field; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ExtractionCorpusEntries_BU_Path_Field" ON public."ExtractionCorpusEntries" USING btree ("BusinessUnitId", "ExtractionPath", "Scope", "FieldName");


--
-- Name: IX_ExtractionCorpusEntries_LeadReviewAuditId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ExtractionCorpusEntries_LeadReviewAuditId" ON public."ExtractionCorpusEntries" USING btree ("LeadReviewAuditId");


--
-- Name: IX_ExtractionJobs_BU_ContentHash; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ExtractionJobs_BU_ContentHash" ON public."ExtractionJobs" USING btree ("BusinessUnitId", "ContentHash");


--
-- Name: IX_ExtractionJobs_BU_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ExtractionJobs_BU_Status" ON public."ExtractionJobs" USING btree ("BusinessUnitId", "Status");


--
-- Name: IX_ExtractionJobs_BatchId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ExtractionJobs_BatchId" ON public."ExtractionJobs" USING btree ("BatchId");


--
-- Name: IX_ExtractionJobs_Claim; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ExtractionJobs_Claim" ON public."ExtractionJobs" USING btree ("Status", "NextAttemptAt", "Priority", "SchedulerTag");


--
-- Name: IX_FinanceCommunicationContacts_BU_Customer_Purpose; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_FinanceCommunicationContacts_BU_Customer_Purpose" ON public."FinanceCommunicationContacts" USING btree ("BusinessUnitId", "CustomerId", "Purpose", "IsActive");


--
-- Name: IX_FinanceCommunicationContacts_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_FinanceCommunicationContacts_CustomerId" ON public."FinanceCommunicationContacts" USING btree ("CustomerId");


--
-- Name: IX_FinanceOutbox_Ready; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_FinanceOutbox_Ready" ON public."FinanceOutboxMessages" USING btree ("AvailableOn", "LeaseUntil", "OccurredOn", "Id") WHERE (("ProcessedOn" IS NULL) AND ("DeadLetteredOn" IS NULL));


--
-- Name: IX_FxRates_BU_Pair_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_FxRates_BU_Pair_Status" ON public."FxRates" USING btree ("BusinessUnitId", "FromCurrencyId", "ToCurrencyId", "Status");


--
-- Name: IX_IamAuditEvents_BU_OccurredOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_IamAuditEvents_BU_OccurredOn" ON public."IamAuditEvents" USING btree ("BusinessUnitId", "OccurredOn");


--
-- Name: IX_IamAuditEvents_BU_Target; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_IamAuditEvents_BU_Target" ON public."IamAuditEvents" USING btree ("BusinessUnitId", "TargetType", "TargetId");


--
-- Name: IX_Images_Resource; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Images_Resource" ON public."Images" USING btree ("ResourceType", "ResourceID");


--
-- Name: IX_InventoryAttachments_InventoryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_InventoryAttachments_InventoryID" ON public."ProductAttachments" USING btree ("InventoryID");


--
-- Name: IX_InventoryCategories_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_InventoryCategories_BusinessUnitID" ON public."ProductCategories" USING btree ("BusinessUnitID");


--
-- Name: IX_InventoryCategories_CategoryName; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_InventoryCategories_CategoryName" ON public."ProductCategories" USING btree ("CategoryName");


--
-- Name: IX_Inventory_BU_PartNo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Inventory_BU_PartNo" ON public."Inventory" USING btree ("Buid", "PartNo");


--
-- Name: IX_Inventory_CategoryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Inventory_CategoryID" ON public."Products" USING btree ("CategoryID");


--
-- Name: IX_Inventory_PartNo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Inventory_PartNo" ON public."Products" USING btree ("PartNo");


--
-- Name: IX_Inventory_PreferredSupplierID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Inventory_PreferredSupplierID" ON public."Products" USING btree ("PreferredSupplierID");


--
-- Name: IX_Inventory_WarehouseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Inventory_WarehouseID" ON public."Products" USING btree ("WarehouseID");


--
-- Name: IX_JournalEntries_BusinessUnitId_AccountingPeriodId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_JournalEntries_BusinessUnitId_AccountingPeriodId" ON public."JournalEntries" USING btree ("BusinessUnitId", "AccountingPeriodId");


--
-- Name: IX_JournalEntries_FunctionalCurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_JournalEntries_FunctionalCurrencyId" ON public."JournalEntries" USING btree ("FunctionalCurrencyId");


--
-- Name: IX_JournalEntryLines_BusinessUnitId_LedgerAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_JournalEntryLines_BusinessUnitId_LedgerAccountId" ON public."JournalEntryLines" USING btree ("BusinessUnitId", "LedgerAccountId");


--
-- Name: IX_JournalEntryLines_TransactionCurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_JournalEntryLines_TransactionCurrencyId" ON public."JournalEntryLines" USING btree ("TransactionCurrencyId");


--
-- Name: IX_LeadIdentityAuditEvents_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadIdentityAuditEvents_BusinessUnitId_IdempotencyKey" ON public."LeadIdentityAuditEvents" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_LeadIdentityAuditEvents_BusinessUnitId_LeadId_OccurredAtUtc; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIdentityAuditEvents_BusinessUnitId_LeadId_OccurredAtUtc" ON public."LeadIdentityAuditEvents" USING btree ("BusinessUnitId", "LeadId", "OccurredAtUtc");


--
-- Name: IX_LeadIngestionBatches_BusinessUnitId_CreatedAtUtc; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionBatches_BusinessUnitId_CreatedAtUtc" ON public."LeadIngestionBatches" USING btree ("BusinessUnitId", "CreatedAtUtc");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_BatchId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_BatchId" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "BatchId");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_Classification_Crea~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_Classification_Crea~" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "Classification", "CreatedAtUtc");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_ContentHash_Custome~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_ContentHash_Custome~" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "ContentHash", "CustomerScopeKey");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_ExtractionJobId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_ExtractionJobId" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "ExtractionJobId");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_IdempotencyKey" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_LeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_LeadId" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "LeadId");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_LogicalInquiryFinge~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_LogicalInquiryFinge~" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "LogicalInquiryFingerprint");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_RecordKind; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_RecordKind" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "RecordKind");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentId" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "SourceDocumentId");


--
-- Name: IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentOccur~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentOccur~" ON public."LeadIngestionOccurrences" USING btree ("BusinessUnitId", "SourceDocumentOccurrenceId");


--
-- Name: IX_LeadItemRevisions_BusinessUnitId_LeadRevisionId_LineNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadItemRevisions_BusinessUnitId_LeadRevisionId_LineNumber" ON public."LeadItemRevisions" USING btree ("BusinessUnitId", "LeadRevisionId", "LineNumber");


--
-- Name: IX_LeadItems_BidClosingDateLine; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadItems_BidClosingDateLine" ON public."LeadItems" USING btree ("BidClosingDateLine");


--
-- Name: IX_LeadItems_BuyerName; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadItems_BuyerName" ON public."LeadItems" USING btree ("BuyerName");


--
-- Name: IX_LeadItems_CustomerRFQNo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadItems_CustomerRFQNo" ON public."LeadItems" USING btree ("CustomerRFQNo");


--
-- Name: IX_LeadItems_LeadID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadItems_LeadID" ON public."LeadItems" USING btree ("LeadID");


--
-- Name: IX_LeadItems_RFQ_Include; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadItems_RFQ_Include" ON public."LeadItems" USING btree ("CustomerRFQNo");


--
-- Name: IX_LeadItems_ReceivedDate; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadItems_ReceivedDate" ON public."LeadItems" USING btree ("ReceivedDate");


--
-- Name: IX_LeadMatchCandidates_BusinessUnitId_CandidateLeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadMatchCandidates_BusinessUnitId_CandidateLeadId" ON public."LeadMatchCandidates" USING btree ("BusinessUnitId", "CandidateLeadId");


--
-- Name: IX_LeadMatchCandidates_BusinessUnitId_OccurrenceId_CandidateLe~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadMatchCandidates_BusinessUnitId_OccurrenceId_CandidateLe~" ON public."LeadMatchCandidates" USING btree ("BusinessUnitId", "OccurrenceId", "CandidateLeadId");


--
-- Name: IX_LeadOccurrenceDocuments_BusinessUnitId_OccurrenceId_SourceD~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadOccurrenceDocuments_BusinessUnitId_OccurrenceId_SourceD~" ON public."LeadOccurrenceDocuments" USING btree ("BusinessUnitId", "OccurrenceId", "SourceDocumentId");


--
-- Name: IX_LeadOccurrenceDocuments_BusinessUnitId_SourceDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadOccurrenceDocuments_BusinessUnitId_SourceDocumentId" ON public."LeadOccurrenceDocuments" USING btree ("BusinessUnitId", "SourceDocumentId");


--
-- Name: IX_LeadRevisionDifferences_BusinessUnitId_LeadRevisionId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadRevisionDifferences_BusinessUnitId_LeadRevisionId" ON public."LeadRevisionDifferences" USING btree ("BusinessUnitId", "LeadRevisionId");


--
-- Name: IX_LeadRevisionImpacts_BusinessUnitId_LeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadRevisionImpacts_BusinessUnitId_LeadId" ON public."LeadRevisionImpacts" USING btree ("BusinessUnitId", "LeadId");


--
-- Name: IX_LeadRevisionImpacts_BusinessUnitId_LeadRevisionId_Aggregate~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadRevisionImpacts_BusinessUnitId_LeadRevisionId_Aggregate~" ON public."LeadRevisionImpacts" USING btree ("BusinessUnitId", "LeadRevisionId", "AggregateType", "AggregateId", "ImpactType");


--
-- Name: IX_LeadRevisions_BusinessUnitId_EstablishedByOccurrenceId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadRevisions_BusinessUnitId_EstablishedByOccurrenceId" ON public."LeadRevisions" USING btree ("BusinessUnitId", "EstablishedByOccurrenceId");


--
-- Name: IX_LeadRevisions_BusinessUnitId_LeadId_RevisionNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeadRevisions_BusinessUnitId_LeadId_RevisionNumber" ON public."LeadRevisions" USING btree ("BusinessUnitId", "LeadId", "RevisionNumber");


--
-- Name: IX_LeadRevisions_BusinessUnitId_LogicalInquiryFingerprint; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadRevisions_BusinessUnitId_LogicalInquiryFingerprint" ON public."LeadRevisions" USING btree ("BusinessUnitId", "LogicalInquiryFingerprint");


--
-- Name: IX_LeadStatusHistories_CommercialCaseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadStatusHistories_CommercialCaseID" ON public."LeadStatusHistories" USING btree ("CommercialCaseID");


--
-- Name: IX_LeadStatusHistories_LeadID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadStatusHistories_LeadID" ON public."LeadStatusHistories" USING btree ("LeadID");


--
-- Name: IX_LeadStatusHistory_BU_Lead_ChangedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LeadStatusHistory_BU_Lead_ChangedOn" ON public."LeadStatusHistories" USING btree ("BusinessUnitID", "LeadID", "ChangedOn");


--
-- Name: IX_Lead_BU_DuplicateStatus; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Lead_BU_DuplicateStatus" ON public."Leads" USING btree ("BusinessUnitID", "DuplicateStatus");


--
-- Name: IX_Leads_AssignTo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_AssignTo" ON public."Leads" USING btree ("AssignTo");


--
-- Name: IX_Leads_BusinessUnitID_ContactID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_BusinessUnitID_ContactID" ON public."Leads" USING btree ("BusinessUnitID", "ContactID");


--
-- Name: IX_Leads_BusinessUnitID_CurrentInquiryFingerprint; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_BusinessUnitID_CurrentInquiryFingerprint" ON public."Leads" USING btree ("BusinessUnitID", "CurrentInquiryFingerprint");


--
-- Name: IX_Leads_BusinessUnitID_CurrentRevisionId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_BusinessUnitID_CurrentRevisionId" ON public."Leads" USING btree ("BusinessUnitID", "CurrentRevisionId");


--
-- Name: IX_Leads_BusinessUnitID_CustomerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_BusinessUnitID_CustomerID" ON public."Leads" USING btree ("BusinessUnitID", "CustomerID");


--
-- Name: IX_Leads_EmailIngestsID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_EmailIngestsID" ON public."Leads" USING btree ("EmailIngestsID");


--
-- Name: IX_Leads_LeadRejectedReasonID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_LeadRejectedReasonID" ON public."Leads" USING btree ("LeadRejectedReasonID");


--
-- Name: IX_Leads_LeadStatusId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_LeadStatusId" ON public."Leads" USING btree ("LeadStatusId");


--
-- Name: IX_Leads_RFQNo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_RFQNo" ON public."Leads" USING btree ("RFQNo");


--
-- Name: IX_Leads_RecDate; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Leads_RecDate" ON public."Leads" USING btree ("RecDate");


--
-- Name: IX_LedgerAccounts_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LedgerAccounts_CurrencyId" ON public."LedgerAccounts" USING btree ("CurrencyId");


--
-- Name: IX_LedgerActorNonces_ExpiresOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LedgerActorNonces_ExpiresOn" ON public."LedgerActorNonces" USING btree ("ExpiresOn");


--
-- Name: IX_LedgerBooks_BusinessUnitId_ReceivablesControlAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LedgerBooks_BusinessUnitId_ReceivablesControlAccountId" ON public."LedgerBooks" USING btree ("BusinessUnitId", "ReceivablesControlAccountId");


--
-- Name: IX_LedgerBooks_BusinessUnitId_UnappliedCashAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LedgerBooks_BusinessUnitId_UnappliedCashAccountId" ON public."LedgerBooks" USING btree ("BusinessUnitId", "UnappliedCashAccountId");


--
-- Name: IX_LedgerBooks_FunctionalCurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LedgerBooks_FunctionalCurrencyId" ON public."LedgerBooks" USING btree ("FunctionalCurrencyId");


--
-- Name: IX_LoginAttempts_LockedUntilUtc; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_LoginAttempts_LockedUntilUtc" ON public."LoginAttempts" USING btree ("LockedUntilUtc");


--
-- Name: IX_MasterDataChangeEvents_BU_Entity_OccurredOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_MasterDataChangeEvents_BU_Entity_OccurredOn" ON public."MasterDataChangeEvents" USING btree ("BusinessUnitId", "EntityType", "EntityId", "OccurredOn");


--
-- Name: IX_MasterDataChangeEvents_BU_OccurredOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_MasterDataChangeEvents_BU_OccurredOn" ON public."MasterDataChangeEvents" USING btree ("BusinessUnitId", "OccurredOn");


--
-- Name: IX_MasterDataFieldChanges_BU_ChangeEvent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_MasterDataFieldChanges_BU_ChangeEvent" ON public."MasterDataFieldChanges" USING btree ("BusinessUnitId", "ChangeEventId");


--
-- Name: IX_MasterDataFieldChanges_BU_FieldName; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_MasterDataFieldChanges_BU_FieldName" ON public."MasterDataFieldChanges" USING btree ("BusinessUnitId", "FieldName");


--
-- Name: IX_MetricEvents_BU_Type_CreatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_MetricEvents_BU_Type_CreatedOn" ON public."MetricEvents" USING btree ("BusinessUnitId", "Type", "CreatedOn");


--
-- Name: IX_Module_ModuleName; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Module_ModuleName" ON public."Module" USING btree ("ModuleName");


--
-- Name: IX_OrderItems_OrderID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderID" ON public."OrderItems" USING btree ("OrderID");


--
-- Name: IX_OrderItems_ProductID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_OrderItems_ProductID" ON public."OrderItems" USING btree ("ProductID");


--
-- Name: IX_OrderItems_UomID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_OrderItems_UomID" ON public."OrderItems" USING btree ("UomID");


--
-- Name: IX_OrderItems_WarehouseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_OrderItems_WarehouseID" ON public."OrderItems" USING btree ("WarehouseID");


--
-- Name: IX_OrderToCashAudit_BU_Correlation; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_OrderToCashAudit_BU_Correlation" ON public."OrderToCashAuditEvents" USING btree ("BusinessUnitId", "CorrelationId");


--
-- Name: IX_Orders_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessUnitID" ON public."Orders" USING btree ("BusinessUnitID");


--
-- Name: IX_Orders_BusinessUnitID_CommercialCaseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessUnitID_CommercialCaseID" ON public."Orders" USING btree ("BusinessUnitID", "CommercialCaseID");


--
-- Name: IX_Orders_BusinessUnitID_NexoraSerial; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessUnitID_NexoraSerial" ON public."Orders" USING btree ("BusinessUnitID", "NexoraSerial");


--
-- Name: IX_Orders_CurrencyID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_CurrencyID" ON public."Orders" USING btree ("CurrencyID");


--
-- Name: IX_Orders_CustomerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_CustomerID" ON public."Orders" USING btree ("CustomerID");


--
-- Name: IX_Orders_LeadID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_LeadID" ON public."Orders" USING btree ("LeadID");


--
-- Name: IX_Orders_OrderNo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_OrderNo" ON public."Orders" USING btree ("OrderNo");


--
-- Name: IX_Orders_PaymentMethodID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_PaymentMethodID" ON public."Orders" USING btree ("PaymentMethodID");


--
-- Name: IX_Orders_PaymentStatusID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_PaymentStatusID" ON public."Orders" USING btree ("PaymentStatusID");


--
-- Name: IX_Orders_QuoteID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_QuoteID" ON public."Orders" USING btree ("QuoteID");


--
-- Name: IX_Orders_RFQID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_RFQID" ON public."Orders" USING btree ("RFQID");


--
-- Name: IX_Orders_StatusID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Orders_StatusID" ON public."Orders" USING btree ("StatusID");


--
-- Name: IX_PaymentAllocations_BusinessUnitId_ReceivableDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PaymentAllocations_BusinessUnitId_ReceivableDocumentId" ON public."PaymentAllocations" USING btree ("BusinessUnitId", "ReceivableDocumentId");


--
-- Name: IX_ProductAttachments_UploadedBy; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProductAttachments_UploadedBy" ON public."ProductAttachments" USING btree ("UploadedBy");


--
-- Name: IX_ProductCategories_ParentCategoryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProductCategories_ParentCategoryID" ON public."ProductCategories" USING btree ("ParentCategoryID");


--
-- Name: IX_ProductSubCategories_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProductSubCategories_BusinessUnitID" ON public."ProductSubCategories" USING btree ("BusinessUnitID");


--
-- Name: IX_ProductSubCategories_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ProductSubCategories_IsActive" ON public."ProductSubCategories" USING btree ("IsActive");


--
-- Name: IX_Products_SubCategoryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Products_SubCategoryID" ON public."Products" USING btree ("SubCategoryID");


--
-- Name: IX_Products_UomID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Products_UomID" ON public."Products" USING btree ("UomID");


--
-- Name: IX_PromisesToPay_BusinessUnitId_DunningCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_PromisesToPay_BusinessUnitId_DunningCaseId" ON public."PromisesToPay" USING btree ("BusinessUnitId", "DunningCaseId");


--
-- Name: IX_QuoteItems_DiscountTypeId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteItems_DiscountTypeId" ON public."QuoteItems" USING btree ("DiscountTypeId");


--
-- Name: IX_QuoteItems_ProductID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteItems_ProductID" ON public."QuoteItems" USING btree ("ProductID");


--
-- Name: IX_QuoteItems_QuoteID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteItems_QuoteID" ON public."QuoteItems" USING btree ("QuoteID");


--
-- Name: IX_QuoteItems_RFQItemID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteItems_RFQItemID" ON public."QuoteItems" USING btree ("RFQItemID");


--
-- Name: IX_QuotePriceAttestationLines_BusinessUnitId_AttestationId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuotePriceAttestationLines_BusinessUnitId_AttestationId" ON public."QuotePriceAttestationLines" USING btree ("BusinessUnitId", "AttestationId");


--
-- Name: IX_QuotePriceAttestations_BU_Quote_ConfirmedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuotePriceAttestations_BU_Quote_ConfirmedOn" ON public."QuotePriceAttestations" USING btree ("BusinessUnitId", "QuoteId", "ConfirmedOn");


--
-- Name: IX_QuoteRemovalRecords_BU_Quote; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteRemovalRecords_BU_Quote" ON public."QuoteRemovalRecords" USING btree ("BusinessUnitId", "QuoteId");


--
-- Name: IX_QuoteRemovalRecords_BU_RemovedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteRemovalRecords_BU_RemovedOn" ON public."QuoteRemovalRecords" USING btree ("BusinessUnitId", "RemovedOn");


--
-- Name: IX_QuoteValidityExtensions_BU_Quote_ExtendedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_QuoteValidityExtensions_BU_Quote_ExtendedOn" ON public."QuoteValidityExtensions" USING btree ("BusinessUnitId", "QuoteId", "ExtendedOn");


--
-- Name: IX_Quotes_BU_RemovedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_BU_RemovedOn" ON public."Quotes" USING btree ("BusinessUnitID", "RemovedOn");


--
-- Name: IX_Quotes_BU_ValidityExtendedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_BU_ValidityExtendedOn" ON public."Quotes" USING btree ("BusinessUnitID", "ValidityExtendedOn") WHERE ("ValidityExtendedOn" IS NOT NULL);


--
-- Name: IX_Quotes_BusinessUnitID_CommercialCaseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_BusinessUnitID_CommercialCaseID" ON public."Quotes" USING btree ("BusinessUnitID", "CommercialCaseID");


--
-- Name: IX_Quotes_BusinessUnitID_NexoraSerial; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_BusinessUnitID_NexoraSerial" ON public."Quotes" USING btree ("BusinessUnitID", "NexoraSerial");


--
-- Name: IX_Quotes_CurrencyID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_CurrencyID" ON public."Quotes" USING btree ("CurrencyID");


--
-- Name: IX_Quotes_CustomerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_CustomerID" ON public."Quotes" USING btree ("CustomerID");


--
-- Name: IX_Quotes_DiscountTypeId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_DiscountTypeId" ON public."Quotes" USING btree ("DiscountTypeId");


--
-- Name: IX_Quotes_Helper; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_Helper" ON public."Quotes" USING btree ("RFQID", "CustomerID", "StatusID");


--
-- Name: IX_Quotes_QuoteNo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_QuoteNo" ON public."Quotes" USING btree ("QuoteNo");


--
-- Name: IX_Quotes_StatusID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Quotes_StatusID" ON public."Quotes" USING btree ("StatusID");


--
-- Name: IX_RFQItems_CurrencyID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_CurrencyID" ON public."RFQItems" USING btree ("CurrencyID");


--
-- Name: IX_RFQItems_ProductID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_ProductID" ON public."RFQItems" USING btree ("ProductID");


--
-- Name: IX_RFQItems_Rfqid_Participation; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_Rfqid_Participation" ON public."RFQItems" USING btree ("RFQID", "ParticipationDecision");


--
-- Name: IX_RFQItems_SupplierID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_SupplierID" ON public."RFQItems" USING btree ("SupplierID");


--
-- Name: IX_RFQItems_SupplierQuotedItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_SupplierQuotedItemId" ON public."RFQItems" USING btree ("SupplierQuotedItemId");


--
-- Name: IX_RFQItems_UomId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_UomId" ON public."RFQItems" USING btree ("UomId");


--
-- Name: IX_RFQItems_WarehouseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQItems_WarehouseID" ON public."RFQItems" USING btree ("WarehouseID");


--
-- Name: IX_RFQ_BusinessUnitID_CommercialCaseID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQ_BusinessUnitID_CommercialCaseID" ON public."RFQ" USING btree ("BusinessUnitID", "CommercialCaseID");


--
-- Name: IX_RFQ_BusinessUnitID_NexoraSerial; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQ_BusinessUnitID_NexoraSerial" ON public."RFQ" USING btree ("BusinessUnitID", "NexoraSerial");


--
-- Name: IX_RFQ_CustomerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQ_CustomerID" ON public."RFQ" USING btree ("CustomerID");


--
-- Name: IX_RFQ_LeadID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQ_LeadID" ON public."RFQ" USING btree ("LeadID");


--
-- Name: IX_RFQ_RFQStatusID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQ_RFQStatusID" ON public."RFQ" USING btree ("RFQStatusID");


--
-- Name: IX_RFQ_RFQTypeID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RFQ_RFQTypeID" ON public."RFQ" USING btree ("RFQTypeID");


--
-- Name: IX_ReceivableDocumentLines_BusinessUnitId_ParentDocumentLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocumentLines_BusinessUnitId_ParentDocumentLineId" ON public."ReceivableDocumentLines" USING btree ("BusinessUnitId", "ParentDocumentLineId");


--
-- Name: IX_ReceivableDocumentLines_BusinessUnitId_ReceivableDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocumentLines_BusinessUnitId_ReceivableDocumentId" ON public."ReceivableDocumentLines" USING btree ("BusinessUnitId", "ReceivableDocumentId");


--
-- Name: IX_ReceivableDocumentLines_OrderItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocumentLines_OrderItemId" ON public."ReceivableDocumentLines" USING btree ("OrderItemId");


--
-- Name: IX_ReceivableDocuments_BU_Status_Due; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocuments_BU_Status_Due" ON public."ReceivableDocuments" USING btree ("BusinessUnitId", "Status", "DueDate");


--
-- Name: IX_ReceivableDocuments_BusinessUnitId_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocuments_BusinessUnitId_CommercialCaseId" ON public."ReceivableDocuments" USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_ReceivableDocuments_BusinessUnitId_OrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocuments_BusinessUnitId_OrderId" ON public."ReceivableDocuments" USING btree ("BusinessUnitId", "OrderId");


--
-- Name: IX_ReceivableDocuments_BusinessUnitId_ParentDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocuments_BusinessUnitId_ParentDocumentId" ON public."ReceivableDocuments" USING btree ("BusinessUnitId", "ParentDocumentId");


--
-- Name: IX_ReceivableDocuments_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocuments_CurrencyId" ON public."ReceivableDocuments" USING btree ("CurrencyId");


--
-- Name: IX_ReceivableDocuments_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableDocuments_CustomerId" ON public."ReceivableDocuments" USING btree ("CustomerId");


--
-- Name: IX_ReceivableWriteOffs_BusinessUnitId_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableWriteOffs_BusinessUnitId_CommercialCaseId" ON public."ReceivableWriteOffs" USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_ReceivableWriteOffs_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableWriteOffs_CurrencyId" ON public."ReceivableWriteOffs" USING btree ("CurrencyId");


--
-- Name: IX_ReceivableWriteOffs_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReceivableWriteOffs_CustomerId" ON public."ReceivableWriteOffs" USING btree ("CustomerId");


--
-- Name: IX_ReconciliationAllocations_BusinessUnitId_BankStatementLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationAllocations_BusinessUnitId_BankStatementLineId" ON public."ReconciliationAllocations" USING btree ("BusinessUnitId", "BankStatementLineId");


--
-- Name: IX_ReconciliationAllocations_BusinessUnitId_JournalEntryLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationAllocations_BusinessUnitId_JournalEntryLineId" ON public."ReconciliationAllocations" USING btree ("BusinessUnitId", "JournalEntryLineId");


--
-- Name: IX_ReconciliationMatches_BusinessUnitId_BankMatchingRuleId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationMatches_BusinessUnitId_BankMatchingRuleId" ON public."ReconciliationMatches" USING btree ("BusinessUnitId", "BankMatchingRuleId");


--
-- Name: IX_ReconciliationMatches_BusinessUnitId_ReconciliationRunId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationMatches_BusinessUnitId_ReconciliationRunId" ON public."ReconciliationMatches" USING btree ("BusinessUnitId", "ReconciliationRunId");


--
-- Name: IX_ReconciliationRunRules_BusinessUnitId_BankMatchingRuleId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationRunRules_BusinessUnitId_BankMatchingRuleId" ON public."ReconciliationRunRules" USING btree ("BusinessUnitId", "BankMatchingRuleId");


--
-- Name: IX_ReconciliationRuns_BusinessUnitId_BankAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationRuns_BusinessUnitId_BankAccountId" ON public."ReconciliationRuns" USING btree ("BusinessUnitId", "BankAccountId");


--
-- Name: IX_ReconciliationRuns_BusinessUnitId_BankStatementId_BankAccou~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReconciliationRuns_BusinessUnitId_BankStatementId_BankAccou~" ON public."ReconciliationRuns" USING btree ("BusinessUnitId", "BankStatementId", "BankAccountId");


--
-- Name: IX_ReportSubscriptions_BU_Active_NextRun; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ReportSubscriptions_BU_Active_NextRun" ON public."ReportSubscriptions" USING btree ("BusinessUnitId", "IsActive", "NextRunOn");


--
-- Name: IX_RolePermissions_BusinessUnitID_RoleID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_RolePermissions_BusinessUnitID_RoleID" ON public."RolePermissions" USING btree ("BusinessUnitID", "RoleID");


--
-- Name: IX_SetCity_CountryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SetCity_CountryID" ON public."SetCity" USING btree ("CountryID");


--
-- Name: IX_SetCity_StateID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SetCity_StateID" ON public."SetCity" USING btree ("StateID");


--
-- Name: IX_SetCountry_BUID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SetCountry_BUID" ON public."SetCountry" USING btree ("BUID");


--
-- Name: IX_SetState_BUID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SetState_BUID" ON public."SetState" USING btree ("BUID");


--
-- Name: IX_SetState_CountryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SetState_CountryID" ON public."SetState" USING btree ("CountryID");


--
-- Name: IX_Setup_Master_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Setup_Master_BusinessUnitID" ON public."Setup_Master" USING btree ("BusinessUnitID");


--
-- Name: IX_Setup_Master_ParentSetupID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Setup_Master_ParentSetupID" ON public."Setup_Master" USING btree ("ParentSetupID");


--
-- Name: IX_ShipmentItems_OrderItemID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ShipmentItems_OrderItemID" ON public."ShipmentItems" USING btree ("OrderItemID");


--
-- Name: IX_ShipmentItems_ShipmentID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ShipmentItems_ShipmentID" ON public."ShipmentItems" USING btree ("ShipmentID");


--
-- Name: IX_ShipmentStatusHistory_NewStatusId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ShipmentStatusHistory_NewStatusId" ON public."ShipmentStatusHistory" USING btree ("NewStatusId");


--
-- Name: IX_ShipmentStatusHistory_PreviousStatusId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ShipmentStatusHistory_PreviousStatusId" ON public."ShipmentStatusHistory" USING btree ("PreviousStatusId");


--
-- Name: IX_ShipmentStatusHistory_ShipmentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ShipmentStatusHistory_ShipmentId" ON public."ShipmentStatusHistory" USING btree ("ShipmentId");


--
-- Name: IX_Shipments_BU_DeliveryStatus; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Shipments_BU_DeliveryStatus" ON public."Shipments" USING btree ("BusinessUnitID", "DeliveryStatus");


--
-- Name: IX_Shipments_BusinessUnitID_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Shipments_BusinessUnitID_CommercialCaseId" ON public."Shipments" USING btree ("BusinessUnitID", "CommercialCaseId");


--
-- Name: IX_Shipments_BusinessUnitID_DeliveryCityID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Shipments_BusinessUnitID_DeliveryCityID" ON public."Shipments" USING btree ("BusinessUnitID", "DeliveryCityID");


--
-- Name: IX_Shipments_OrderID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Shipments_OrderID" ON public."Shipments" USING btree ("OrderID");


--
-- Name: IX_Shipments_StatusID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Shipments_StatusID" ON public."Shipments" USING btree ("StatusID");


--
-- Name: IX_SlaEvents_BU_Entity_Level; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SlaEvents_BU_Entity_Level" ON public."SlaEvents" USING btree ("BusinessUnitId", "EntityType", "EntityId", "Level");


--
-- Name: IX_SourcingAwards_BU_Rfq; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SourcingAwards_BU_Rfq" ON public."SourcingAwards" USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_SourcingAwards_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SourcingAwards_BusinessUnitId_CurrencyId" ON public."SourcingAwards" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_SourcingAwards_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SourcingAwards_BusinessUnitId_IdempotencyKey" ON public."SourcingAwards" USING btree ("BusinessUnitId", "IdempotencyKey") WHERE ("IdempotencyKey" IS NOT NULL);


--
-- Name: IX_SourcingAwards_BusinessUnitId_RfqItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SourcingAwards_BusinessUnitId_RfqItemId" ON public."SourcingAwards" USING btree ("BusinessUnitId", "RfqItemId") WHERE (("RfqItemId" IS NOT NULL) AND ("Status"  = ANY (ARRAY['PROPOSED'::character varying, 'APPROVED'::character varying])));


--
-- Name: IX_SourcingAwards_BusinessUnitId_SupplierQuotedItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SourcingAwards_BusinessUnitId_SupplierQuotedItemId" ON public."SourcingAwards" USING btree ("BusinessUnitId", "SupplierQuotedItemId");


--
-- Name: IX_SourcingAwards_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SourcingAwards_RfqItemId_RfqId" ON public."SourcingAwards" USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_SourcingAwards_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SourcingAwards_SupplierId_BusinessUnitId" ON public."SourcingAwards" USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_SupplierPurchaseHistory_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierPurchaseHistory_ProductId" ON public."SupplierPurchaseHistory" USING btree ("ProductId");


--
-- Name: IX_SupplierPurchaseHistory_SupplierId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierPurchaseHistory_SupplierId" ON public."SupplierPurchaseHistory" USING btree ("SupplierId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_CommercialDemandLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_CommercialDemandLineId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "CommercialDemandLineId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_CurrencyId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_ProductId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "ProductId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_RfqId_RfqItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_RfqId_RfqItemId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "RfqId", "RfqItemId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "SourceSupplierQuoteId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteRevis~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteRevis~" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "SourceSupplierQuoteRevisionId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_SourcingCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_SourcingCaseId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "SourcingCaseId");


--
-- Name: IX_SupplierQuotedItems_BusinessUnitId_SupplierSolicitationId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_BusinessUnitId_SupplierSolicitationId" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "SupplierSolicitationId");


--
-- Name: IX_SupplierQuotedItems_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_RfqItemId_RfqId" ON public."SupplierQuotedItems" USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_SupplierQuotedItems_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_SupplierId_BusinessUnitId" ON public."SupplierQuotedItems" USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_SupplierQuotedItems_UomId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierQuotedItems_UomId" ON public."SupplierQuotedItems" USING btree ("UomId");


--
-- Name: IX_SupplierSolicitations_BU_DueOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BU_DueOn" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "DueOn") WHERE ("DueOn" IS NOT NULL);


--
-- Name: IX_SupplierSolicitations_BU_Rfq; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BU_Rfq" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_SupplierSolicitations_BU_Rfq_Supplier; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BU_Rfq_Supplier" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "RfqId", "SupplierId");


--
-- Name: IX_SupplierSolicitations_BusinessUnitId_CommercialDemandLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BusinessUnitId_CommercialDemandLineId" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "CommercialDemandLineId");


--
-- Name: IX_SupplierSolicitations_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BusinessUnitId_IdempotencyKey" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_SupplierSolicitations_BusinessUnitId_SourcingCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BusinessUnitId_SourcingCaseId" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "SourcingCaseId");


--
-- Name: IX_SupplierSolicitations_BusinessUnitId_SupplierRfqNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SupplierSolicitations_BusinessUnitId_SupplierRfqNumber" ON public."SupplierSolicitations" USING btree ("BusinessUnitId", "SupplierRfqNumber") WHERE ("SupplierRfqNumber" IS NOT NULL);


--
-- Name: IX_SupplierSolicitations_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_SupplierSolicitations_SupplierId_BusinessUnitId" ON public."SupplierSolicitations" USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_Suppliers_BU_Governance_Readiness; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_BU_Governance_Readiness" ON public."Suppliers" USING btree ("BUID", "GovernanceStatus", "ReadinessStatus");


--
-- Name: IX_Suppliers_BU_TaxRegistrationNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_BU_TaxRegistrationNumber" ON public."Suppliers" USING btree ("BUID", "TaxRegistrationNumber") WHERE (("TaxRegistrationNumber" IS NOT NULL) AND ("BUID" IS NOT NULL));


--
-- Name: IX_Suppliers_CityID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_CityID" ON public."Suppliers" USING btree ("CityID");


--
-- Name: IX_Suppliers_ContactEmail; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_ContactEmail" ON public."Suppliers" USING btree ("ContactEmail");


--
-- Name: IX_Suppliers_CountryID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_CountryID" ON public."Suppliers" USING btree ("CountryID");


--
-- Name: IX_Suppliers_CurrencyID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_CurrencyID" ON public."Suppliers" USING btree ("CurrencyID");


--
-- Name: IX_Suppliers_Name; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Suppliers_Name" ON public."Suppliers" USING btree ("Name");


--
-- Name: IX_Taxes_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Taxes_BusinessUnitID" ON public."Taxes" USING btree ("BusinessUnitID");


--
-- Name: IX_Teams_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Teams_BusinessUnitID" ON public."Teams" USING btree ("BusinessUnitID");


--
-- Name: IX_Teams_ManagerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Teams_ManagerID" ON public."Teams" USING btree ("ManagerID");


--
-- Name: IX_Teams_SubTeamID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Teams_SubTeamID" ON public."Teams" USING btree ("SubTeamID");


--
-- Name: IX_UserColumnPreferences_UserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UserColumnPreferences_UserId" ON public."UserColumnPreferences" USING btree ("UserId");


--
-- Name: IX_UserGroups_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UserGroups_BusinessUnitID" ON public."UserGroups" USING btree ("BusinessUnitID");


--
-- Name: IX_UserPermissions_BusinessUnitID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UserPermissions_BusinessUnitID" ON public."RolePermissions" USING btree ("BusinessUnitID");


--
-- Name: IX_UserPermissions_ModuleID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_UserPermissions_ModuleID" ON public."RolePermissions" USING btree ("ModuleID");


--
-- Name: IX_Users_BUID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_BUID" ON public."Users" USING btree ("BUID");


--
-- Name: IX_Users_BUID_RoleID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_BUID_RoleID" ON public."Users" USING btree ("BUID", "RoleID");


--
-- Name: IX_Users_Email; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_Email" ON public."Users" USING btree ("Email");


--
-- Name: IX_Users_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_IsActive" ON public."Users" USING btree ("IsActive");


--
-- Name: IX_Users_ManagerID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_ManagerID" ON public."Users" USING btree ("ManagerID");


--
-- Name: IX_Users_TeamID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_TeamID" ON public."Users" USING btree ("TeamID");


--
-- Name: IX_Users_UserGroupID; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_Users_UserGroupID" ON public."Users" USING btree ("UserGroupID");


--
-- Name: IX_WriteOffAllocations_BusinessUnitId_ReceivableDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_WriteOffAllocations_BusinessUnitId_ReceivableDocumentId" ON public."WriteOffAllocations" USING btree ("BusinessUnitId", "ReceivableDocumentId");


--
-- Name: IX_canonical_inquiries_business_unit_id_corpus_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_canonical_inquiries_business_unit_id_corpus_id" ON public.canonical_inquiries USING btree (business_unit_id, corpus_id);


--
-- Name: IX_canonical_inquiries_business_unit_id_lead_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_canonical_inquiries_business_unit_id_lead_id" ON public.canonical_inquiries USING btree (business_unit_id, lead_id);


--
-- Name: IX_commercial_activities_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_activities_BusinessUnitId_IdempotencyKey" ON public.commercial_activities USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_activities_BusinessUnitId_SalesRepUserId_Occurre~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_activities_BusinessUnitId_SalesRepUserId_Occurre~" ON public.commercial_activities USING btree ("BusinessUnitId", "SalesRepUserId", "OccurredAtUtc");


--
-- Name: IX_commercial_activities_SalesRepUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_activities_SalesRepUserId" ON public.commercial_activities USING btree ("SalesRepUserId");


--
-- Name: IX_commercial_demand_lines_BusinessUnitId_IdentityKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_demand_lines_BusinessUnitId_IdentityKey" ON public.commercial_demand_lines USING btree ("BusinessUnitId", "IdentityKey");


--
-- Name: IX_commercial_demand_lines_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_demand_lines_BusinessUnitId_RfqId" ON public.commercial_demand_lines USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_commercial_demand_lines_BusinessUnitId_RfqItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_demand_lines_BusinessUnitId_RfqItemId" ON public.commercial_demand_lines USING btree ("BusinessUnitId", "RfqItemId");


--
-- Name: IX_commercial_demand_lines_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_demand_lines_RfqItemId_RfqId" ON public.commercial_demand_lines USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_commercial_exception_cases_BusinessUnitId_CommercialCaseId_~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_cases_BusinessUnitId_CommercialCaseId_~" ON public.commercial_exception_cases USING btree ("BusinessUnitId", "CommercialCaseId", "NexoraSerial");


--
-- Name: IX_commercial_exception_cases_BusinessUnitId_DeliveryProofLine~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_cases_BusinessUnitId_DeliveryProofLine~" ON public.commercial_exception_cases USING btree ("BusinessUnitId", "DeliveryProofLineId");


--
-- Name: IX_commercial_exception_cases_BusinessUnitId_ExceptionKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_exception_cases_BusinessUnitId_ExceptionKey" ON public.commercial_exception_cases USING btree ("BusinessUnitId", "ExceptionKey");


--
-- Name: IX_commercial_exception_cases_BusinessUnitId_FollowUpTaskId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_cases_BusinessUnitId_FollowUpTaskId" ON public.commercial_exception_cases USING btree ("BusinessUnitId", "FollowUpTaskId");


--
-- Name: IX_commercial_exception_cases_BusinessUnitId_Status_Severity_S~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_cases_BusinessUnitId_Status_Severity_S~" ON public.commercial_exception_cases USING btree ("BusinessUnitId", "Status", "Severity", "SlaDueAtUtc");


--
-- Name: IX_commercial_exception_cases_BusinessUnitId_UnassignedWorkIte~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_cases_BusinessUnitId_UnassignedWorkIte~" ON public.commercial_exception_cases USING btree ("BusinessUnitId", "UnassignedWorkItemId");


--
-- Name: IX_commercial_exception_events_BusinessUnitId_CommercialExcept~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_events_BusinessUnitId_CommercialExcept~" ON public.commercial_exception_events USING btree ("BusinessUnitId", "CommercialExceptionCaseId", "OccurredAtUtc");


--
-- Name: IX_commercial_exception_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_exception_events_BusinessUnitId_IdempotencyKey" ON public.commercial_exception_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_exception_operations_BusinessUnitId_CommercialEx~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_operations_BusinessUnitId_CommercialEx~" ON public.commercial_exception_operations USING btree ("BusinessUnitId", "CommercialExceptionCaseId", "OccurredAtUtc");


--
-- Name: IX_commercial_exception_operations_BusinessUnitId_IdempotencyK~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_exception_operations_BusinessUnitId_IdempotencyK~" ON public.commercial_exception_operations USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_exception_outbox_BusinessUnitId_CommercialExcept~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_exception_outbox_BusinessUnitId_CommercialExcept~" ON public.commercial_exception_outbox USING btree ("BusinessUnitId", "CommercialExceptionEventId");


--
-- Name: IX_commercial_exception_outbox_CommercialExceptionEventId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_exception_outbox_CommercialExceptionEventId" ON public.commercial_exception_outbox USING btree ("CommercialExceptionEventId");


--
-- Name: IX_commercial_exception_outbox_ProcessedAtUtc_AvailableAtUtc; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_exception_outbox_ProcessedAtUtc_AvailableAtUtc" ON public.commercial_exception_outbox USING btree ("ProcessedAtUtc", "AvailableAtUtc");


--
-- Name: IX_commercial_lifecycle_events_BusinessUnitId_AggregateType_Ag~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_lifecycle_events_BusinessUnitId_AggregateType_Ag~" ON public.commercial_lifecycle_events USING btree ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion");


--
-- Name: IX_commercial_lifecycle_events_BusinessUnitId_CommercialCaseId~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_lifecycle_events_BusinessUnitId_CommercialCaseId~" ON public.commercial_lifecycle_events USING btree ("BusinessUnitId", "CommercialCaseId", "CommercialCaseReference");


--
-- Name: IX_commercial_lifecycle_events_BusinessUnitId_CommercialCaseI~1; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_lifecycle_events_BusinessUnitId_CommercialCaseI~1" ON public.commercial_lifecycle_events USING btree ("BusinessUnitId", "CommercialCaseId", "OccurredOn");


--
-- Name: IX_commercial_lifecycle_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_lifecycle_events_BusinessUnitId_IdempotencyKey" ON public.commercial_lifecycle_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_lifecycle_events_BusinessUnitId_NewStatusId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_lifecycle_events_BusinessUnitId_NewStatusId" ON public.commercial_lifecycle_events USING btree ("BusinessUnitId", "NewStatusId");


--
-- Name: IX_commercial_lifecycle_events_BusinessUnitId_PreviousStatusId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_lifecycle_events_BusinessUnitId_PreviousStatusId" ON public.commercial_lifecycle_events USING btree ("BusinessUnitId", "PreviousStatusId");


--
-- Name: IX_commercial_opportunity_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_events_BusinessUnitId_IdempotencyKey" ON public.commercial_opportunity_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_opportunity_events_BusinessUnitId_OpportunityRec~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_events_BusinessUnitId_OpportunityRec~" ON public.commercial_opportunity_events USING btree ("BusinessUnitId", "OpportunityRecommendationId", "OccurredAtUtc");


--
-- Name: IX_commercial_opportunity_feedback_BusinessUnitId_IdempotencyK~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_feedback_BusinessUnitId_IdempotencyK~" ON public.commercial_opportunity_feedback USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_opportunity_feedback_BusinessUnitId_OpportunityR~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_feedback_BusinessUnitId_OpportunityR~" ON public.commercial_opportunity_feedback USING btree ("BusinessUnitId", "OpportunityRecommendationId", "OccurredAtUtc");


--
-- Name: IX_commercial_opportunity_feedback_BusinessUnitId_SupersedesFe~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_feedback_BusinessUnitId_SupersedesFe~" ON public.commercial_opportunity_feedback USING btree ("BusinessUnitId", "SupersedesFeedbackId");


--
-- Name: IX_commercial_opportunity_operations_BusinessUnitId_Commercial~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_operations_BusinessUnitId_Commercial~" ON public.commercial_opportunity_operations USING btree ("BusinessUnitId", "CommercialCaseId", "OccurredAtUtc");


--
-- Name: IX_commercial_opportunity_operations_BusinessUnitId_Idempotenc~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_operations_BusinessUnitId_Idempotenc~" ON public.commercial_opportunity_operations USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_commercial_opportunity_operations_BusinessUnitId_Opportunit~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_operations_BusinessUnitId_Opportunit~" ON public.commercial_opportunity_operations USING btree ("BusinessUnitId", "OpportunityRecommendationId");


--
-- Name: IX_commercial_opportunity_outbox_BusinessUnitId_OpportunityEve~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_outbox_BusinessUnitId_OpportunityEve~" ON public.commercial_opportunity_outbox USING btree ("BusinessUnitId", "OpportunityEventId");


--
-- Name: IX_commercial_opportunity_outbox_BusinessUnitId_ProcessedAtUtc~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_outbox_BusinessUnitId_ProcessedAtUtc~" ON public.commercial_opportunity_outbox USING btree ("BusinessUnitId", "ProcessedAtUtc", "AvailableAtUtc");


--
-- Name: IX_commercial_opportunity_outcomes_BusinessUnitId_OpportunityR~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_outcomes_BusinessUnitId_OpportunityR~" ON public.commercial_opportunity_outcomes USING btree ("BusinessUnitId", "OpportunityRecommendationId", "SourceType", "SourceId", "SourceVersion", "OutcomeCode");


--
-- Name: IX_commercial_opportunity_recommendations_BusinessUnitId_Comme~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_recommendations_BusinessUnitId_Comme~" ON public.commercial_opportunity_recommendations USING btree ("BusinessUnitId", "CommercialCaseId", "NexoraSerial");


--
-- Name: IX_commercial_opportunity_recommendations_BusinessUnitId_Comm~1; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_recommendations_BusinessUnitId_Comm~1" ON public.commercial_opportunity_recommendations USING btree ("BusinessUnitId", "CommercialCaseId", "PolicyVersion", "EvidenceHash");


--
-- Name: IX_commercial_opportunity_recommendations_BusinessUnitId_Gener~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_recommendations_BusinessUnitId_Gener~" ON public.commercial_opportunity_recommendations USING btree ("BusinessUnitId", "GeneratedAtUtc", "PriorityScore");


--
-- Name: IX_commercial_opportunity_recommendations_BusinessUnitId_LeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_recommendations_BusinessUnitId_LeadId" ON public.commercial_opportunity_recommendations USING btree ("BusinessUnitId", "LeadId");


--
-- Name: IX_commercial_opportunity_recommendations_BusinessUnitId_Recom~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_commercial_opportunity_recommendations_BusinessUnitId_Recom~" ON public.commercial_opportunity_recommendations USING btree ("BusinessUnitId", "RecommendationKey");


--
-- Name: IX_commercial_opportunity_recommendations_BusinessUnitId_Super~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_commercial_opportunity_recommendations_BusinessUnitId_Super~" ON public.commercial_opportunity_recommendations USING btree ("BusinessUnitId", "SupersedesRecommendationId");


--
-- Name: IX_custom_field_definitions_BusinessUnitId_EntityType_StableKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_custom_field_definitions_BusinessUnitId_EntityType_StableKey" ON public.custom_field_definitions USING btree ("BusinessUnitId", "EntityType", "StableKey");


--
-- Name: IX_custom_field_dependencies_DependsOnDefinitionId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_dependencies_DependsOnDefinitionId" ON public.custom_field_dependencies USING btree ("DependsOnDefinitionId");


--
-- Name: IX_custom_field_dependencies_VersionId_DependsOnDefinitionId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_custom_field_dependencies_VersionId_DependsOnDefinitionId" ON public.custom_field_dependencies USING btree ("VersionId", "DependsOnDefinitionId");


--
-- Name: IX_custom_field_options_VersionId_StableKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_custom_field_options_VersionId_StableKey" ON public.custom_field_options USING btree ("VersionId", "StableKey");


--
-- Name: IX_custom_field_records_BusinessUnitId_EntityType_EntityId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_custom_field_records_BusinessUnitId_EntityType_EntityId" ON public.custom_field_records USING btree ("BusinessUnitId", "EntityType", "EntityId");


--
-- Name: IX_custom_field_rules_VersionId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_rules_VersionId" ON public.custom_field_rules USING btree ("VersionId");


--
-- Name: IX_custom_field_values_BusinessUnitId_DefinitionId_DateValue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_values_BusinessUnitId_DefinitionId_DateValue" ON public.custom_field_values USING btree ("BusinessUnitId", "DefinitionId", "DateValue");


--
-- Name: IX_custom_field_values_BusinessUnitId_DefinitionId_DecimalValue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_values_BusinessUnitId_DefinitionId_DecimalValue" ON public.custom_field_values USING btree ("BusinessUnitId", "DefinitionId", "DecimalValue");


--
-- Name: IX_custom_field_values_BusinessUnitId_DefinitionId_IntegerValue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_values_BusinessUnitId_DefinitionId_IntegerValue" ON public.custom_field_values USING btree ("BusinessUnitId", "DefinitionId", "IntegerValue");


--
-- Name: IX_custom_field_values_BusinessUnitId_DefinitionId_TextValue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_values_BusinessUnitId_DefinitionId_TextValue" ON public.custom_field_values USING btree ("BusinessUnitId", "DefinitionId", "TextValue");


--
-- Name: IX_custom_field_values_BusinessUnitId_RecordId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_values_BusinessUnitId_RecordId" ON public.custom_field_values USING btree ("BusinessUnitId", "RecordId");


--
-- Name: IX_custom_field_values_DefinitionId_DefinitionVersion; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_custom_field_values_DefinitionId_DefinitionVersion" ON public.custom_field_values USING btree ("DefinitionId", "DefinitionVersion");


--
-- Name: IX_custom_field_values_RecordId_DefinitionId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_custom_field_values_RecordId_DefinitionId" ON public.custom_field_values USING btree ("RecordId", "DefinitionId");


--
-- Name: IX_custom_field_versions_DefinitionId_VersionNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_custom_field_versions_DefinitionId_VersionNumber" ON public.custom_field_versions USING btree ("DefinitionId", "VersionNumber");


--
-- Name: IX_customer_identifiers_BusinessUnitId_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_identifiers_BusinessUnitId_CustomerId" ON public.customer_identifiers USING btree ("BusinessUnitId", "CustomerId");


--
-- Name: IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~" ON public.customer_identifiers USING btree ("BusinessUnitId", "IdentifierType", "NormalizedValue", "CustomerId") WHERE ("EffectiveTo" IS NULL);


--
-- Name: IX_customer_identifiers_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_identifiers_CustomerId" ON public.customer_identifiers USING btree ("CustomerId");


--
-- Name: IX_customer_identifiers_learned_from_lead; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_identifiers_learned_from_lead" ON public.customer_identifiers USING btree ("BusinessUnitId", "LearnedFromLeadId") WHERE ("LearnedFromLeadId" IS NOT NULL);


--
-- Name: IX_customer_ownerships_BackupUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_ownerships_BackupUserId" ON public.customer_ownerships USING btree ("BackupUserId");


--
-- Name: IX_customer_ownerships_BusinessUnitId_CustomerId_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_ownerships_BusinessUnitId_CustomerId_IsActive" ON public.customer_ownerships USING btree ("BusinessUnitId", "CustomerId", "IsActive");


--
-- Name: IX_customer_ownerships_BusinessUnitId_MutationIdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_customer_ownerships_BusinessUnitId_MutationIdempotencyKey" ON public.customer_ownerships USING btree ("BusinessUnitId", "MutationIdempotencyKey") WHERE ("MutationIdempotencyKey" IS NOT NULL);


--
-- Name: IX_customer_ownerships_BusinessUnitId_Scope_ScopeKey; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_ownerships_BusinessUnitId_Scope_ScopeKey" ON public.customer_ownerships USING btree ("BusinessUnitId", "Scope", "ScopeKey");


--
-- Name: IX_customer_ownerships_PrimaryUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_ownerships_PrimaryUserId" ON public.customer_ownerships USING btree ("PrimaryUserId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_Commercial~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_Commercial~" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "CommercialDemandLineId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_Idempotenc~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_Idempotenc~" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_QuoteId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_QuoteId" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "QuoteId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_QuoteItemI~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_QuoteItemI~" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "QuoteItemId", "CreatedOn");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_SourcingAw~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_SourcingAw~" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "SourcingAwardId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_SourcingCa~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_SourcingCa~" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "SourcingCaseId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQu~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQu~" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "SupplierQuoteId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~1; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~1" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "SupplierQuoteLineId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~2; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~2" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "SupplierQuoteRevisionId");


--
-- Name: IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~3; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~3" ON public.customer_quote_sourcing_decisions USING btree ("BusinessUnitId", "SupplierQuotedItemId");


--
-- Name: IX_customer_quote_sourcing_decisions_QuoteItemId_QuoteId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_customer_quote_sourcing_decisions_QuoteItemId_QuoteId" ON public.customer_quote_sourcing_decisions USING btree ("QuoteItemId", "QuoteId");


--
-- Name: IX_delivery_proof_lines_BU_Order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proof_lines_BU_Order" ON public.delivery_proof_lines USING btree ("BusinessUnitId", "OrderId");


--
-- Name: IX_delivery_proof_lines_BU_OrderItem; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proof_lines_BU_OrderItem" ON public.delivery_proof_lines USING btree ("BusinessUnitId", "OrderItemId");


--
-- Name: IX_delivery_proof_lines_BU_Shipment; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proof_lines_BU_Shipment" ON public.delivery_proof_lines USING btree ("BusinessUnitId", "ShipmentId");


--
-- Name: IX_delivery_proof_lines_OrderItemId_OrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proof_lines_OrderItemId_OrderId" ON public.delivery_proof_lines USING btree ("OrderItemId", "OrderId");


--
-- Name: IX_delivery_proof_lines_ShipmentItemId_ShipmentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proof_lines_ShipmentItemId_ShipmentId" ON public.delivery_proof_lines USING btree ("ShipmentItemId", "ShipmentId");


--
-- Name: IX_delivery_proofs_BU_CommercialCase; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proofs_BU_CommercialCase" ON public.delivery_proofs USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_delivery_proofs_PhotoEvidenceId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proofs_PhotoEvidenceId" ON public.delivery_proofs USING btree ("PhotoEvidenceId");


--
-- Name: IX_delivery_proofs_SignatureEvidenceId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proofs_SignatureEvidenceId" ON public.delivery_proofs USING btree ("SignatureEvidenceId");


--
-- Name: IX_delivery_proofs_StampEvidenceId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_proofs_StampEvidenceId" ON public.delivery_proofs USING btree ("StampEvidenceId");


--
-- Name: IX_delivery_shortfall_decisions_BU_Shipment; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_delivery_shortfall_decisions_BU_Shipment" ON public.delivery_shortfall_decisions USING btree ("BusinessUnitId", "ShipmentId");


--
-- Name: IX_extraction_dead_letter_events_BusinessUnitId_ExtractionJobI~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_extraction_dead_letter_events_BusinessUnitId_ExtractionJobI~" ON public.extraction_dead_letter_events USING btree ("BusinessUnitId", "ExtractionJobId", "AttemptNumber", "CreatedOn");


--
-- Name: IX_extraction_runs_business_unit_id_source_document_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_extraction_runs_business_unit_id_source_document_id" ON public.extraction_runs USING btree (business_unit_id, source_document_id);


--
-- Name: IX_field_evidence_business_unit_id_region_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_field_evidence_business_unit_id_region_id" ON public.field_evidence USING btree (business_unit_id, region_id);


--
-- Name: IX_follow_up_tasks_AssignedToUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_follow_up_tasks_AssignedToUserId" ON public.follow_up_tasks USING btree ("AssignedToUserId");


--
-- Name: IX_follow_up_tasks_BusinessUnitId_AssignedToUserId_DueAtUtc; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_follow_up_tasks_BusinessUnitId_AssignedToUserId_DueAtUtc" ON public.follow_up_tasks USING btree ("BusinessUnitId", "AssignedToUserId", "DueAtUtc");


--
-- Name: IX_follow_up_tasks_BusinessUnitId_CreationIdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_follow_up_tasks_BusinessUnitId_CreationIdempotencyKey" ON public.follow_up_tasks USING btree ("BusinessUnitId", "CreationIdempotencyKey");


--
-- Name: IX_follow_up_transition_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_follow_up_transition_events_BusinessUnitId_IdempotencyKey" ON public.follow_up_transition_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_follow_up_transition_events_FollowUpTaskId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_follow_up_transition_events_FollowUpTaskId" ON public.follow_up_transition_events USING btree ("FollowUpTaskId");


--
-- Name: IX_goods_receipt_lines_BusinessUnitId_GoodsReceiptId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_goods_receipt_lines_BusinessUnitId_GoodsReceiptId" ON public.goods_receipt_lines USING btree ("BusinessUnitId", "GoodsReceiptId");


--
-- Name: IX_goods_receipt_lines_BusinessUnitId_InventoryMovementId_Prod~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_goods_receipt_lines_BusinessUnitId_InventoryMovementId_Prod~" ON public.goods_receipt_lines USING btree ("BusinessUnitId", "InventoryMovementId", "ProductId", "InventoryId", "WarehouseId");


--
-- Name: IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~" ON public.goods_receipt_lines USING btree ("BusinessUnitId", "SupplierPurchaseOrderLineId", "ProductId", "WarehouseId");


--
-- Name: IX_goods_receipts_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_goods_receipts_BusinessUnitId_IdempotencyKey" ON public.goods_receipts USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_goods_receipts_BusinessUnitId_ReceiptNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_goods_receipts_BusinessUnitId_ReceiptNumber" ON public.goods_receipts USING btree ("BusinessUnitId", "ReceiptNumber");


--
-- Name: IX_goods_receipts_BusinessUnitId_SupplierPurchaseOrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_goods_receipts_BusinessUnitId_SupplierPurchaseOrderId" ON public.goods_receipts USING btree ("BusinessUnitId", "SupplierPurchaseOrderId");


--
-- Name: IX_goods_receipts_BusinessUnitId_WarehouseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_goods_receipts_BusinessUnitId_WarehouseId" ON public.goods_receipts USING btree ("BusinessUnitId", "WarehouseId");


--
-- Name: IX_governed_artifact_events_BusinessUnitId_GovernedArtifactId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_governed_artifact_events_BusinessUnitId_GovernedArtifactId" ON public.governed_artifact_events USING btree ("BusinessUnitId", "GovernedArtifactId");


--
-- Name: IX_governed_artifact_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_governed_artifact_events_BusinessUnitId_IdempotencyKey" ON public.governed_artifact_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_governed_artifact_versions_BusinessUnitId_GovernedArtifactI~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_governed_artifact_versions_BusinessUnitId_GovernedArtifactI~" ON public.governed_artifact_versions USING btree ("BusinessUnitId", "GovernedArtifactId", "VersionNumber");


--
-- Name: IX_governed_artifacts_BusinessUnitId_ArtifactType_ArtifactKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_governed_artifacts_BusinessUnitId_ArtifactType_ArtifactKey" ON public.governed_artifacts USING btree ("BusinessUnitId", "ArtifactType", "ArtifactKey");


--
-- Name: IX_human_action_events_BusinessUnitId_HumanActionItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_human_action_events_BusinessUnitId_HumanActionItemId" ON public.human_action_events USING btree ("BusinessUnitId", "HumanActionItemId");


--
-- Name: IX_human_action_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_human_action_events_BusinessUnitId_IdempotencyKey" ON public.human_action_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_human_action_items_BusinessUnitId_Status_Priority_DueOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_human_action_items_BusinessUnitId_Status_Priority_DueOn" ON public.human_action_items USING btree ("BusinessUnitId", "Status", "Priority", "DueOn");


--
-- Name: IX_inbound_logistics_policies_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_inbound_logistics_policies_BusinessUnitId" ON public.inbound_logistics_policies USING btree ("BusinessUnitId");


--
-- Name: IX_incoming_inventory_BusinessUnitId_InventoryId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_incoming_inventory_BusinessUnitId_InventoryId" ON public.incoming_inventory USING btree ("BusinessUnitId", "InventoryId");


--
-- Name: IX_incoming_inventory_BusinessUnitId_ProductId_ExpectedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_incoming_inventory_BusinessUnitId_ProductId_ExpectedOn" ON public.incoming_inventory USING btree ("BusinessUnitId", "ProductId", "ExpectedOn");


--
-- Name: IX_incoming_inventory_BusinessUnitId_SourceType_SourceId_Produ~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_incoming_inventory_BusinessUnitId_SourceType_SourceId_Produ~" ON public.incoming_inventory USING btree ("BusinessUnitId", "SourceType", "SourceId", "ProductId", "WarehouseId");


--
-- Name: IX_incoming_inventory_BusinessUnitId_WarehouseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_incoming_inventory_BusinessUnitId_WarehouseId" ON public.incoming_inventory USING btree ("BusinessUnitId", "WarehouseId");


--
-- Name: IX_inventory_movements_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_inventory_movements_BusinessUnitId_IdempotencyKey" ON public.inventory_movements USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_inventory_movements_BusinessUnitId_InventoryId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_inventory_movements_BusinessUnitId_InventoryId" ON public.inventory_movements USING btree ("BusinessUnitId", "InventoryId");


--
-- Name: IX_inventory_movements_BusinessUnitId_ProductId_OccurredOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_inventory_movements_BusinessUnitId_ProductId_OccurredOn" ON public.inventory_movements USING btree ("BusinessUnitId", "ProductId", "OccurredOn");


--
-- Name: IX_inventory_movements_BusinessUnitId_WarehouseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_inventory_movements_BusinessUnitId_WarehouseId" ON public.inventory_movements USING btree ("BusinessUnitId", "WarehouseId");


--
-- Name: IX_inventory_reorder_alerts_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_inventory_reorder_alerts_status" ON public.inventory_reorder_alerts USING btree ("BusinessUnitId", "Status", "RaisedOn");


--
-- Name: IX_lead_assignments_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_lead_assignments_BusinessUnitId_IdempotencyKey" ON public.lead_assignments USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_lead_assignments_BusinessUnitId_LeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_lead_assignments_BusinessUnitId_LeadId" ON public.lead_assignments USING btree ("BusinessUnitId", "LeadId") WHERE ("EffectiveTo" IS NULL);


--
-- Name: IX_lead_assignments_BusinessUnitId_LeadId_EffectiveTo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_assignments_BusinessUnitId_LeadId_EffectiveTo" ON public.lead_assignments USING btree ("BusinessUnitId", "LeadId", "EffectiveTo");


--
-- Name: IX_lead_assignments_BusinessUnitId_OwnershipId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_assignments_BusinessUnitId_OwnershipId" ON public.lead_assignments USING btree ("BusinessUnitId", "OwnershipId");


--
-- Name: IX_lead_assignments_BusinessUnitId_RoutingDecisionId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_assignments_BusinessUnitId_RoutingDecisionId" ON public.lead_assignments USING btree ("BusinessUnitId", "RoutingDecisionId");


--
-- Name: IX_lead_assignments_ToUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_assignments_ToUserId" ON public.lead_assignments USING btree ("ToUserId");


--
-- Name: IX_lead_customer_match_candidates_customer; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_customer_match_candidates_customer" ON public.lead_customer_match_candidates USING btree ("BusinessUnitId", "CustomerId");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_LeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadId" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "LeadId");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_LeadLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadLineId" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "LeadLineId");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisio~" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "LeadRevisionId", "LeadLineId", "ResolutionBatchId");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisi~1; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_LeadRevisi~1" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "LeadRevisionId", "LeadLineId", "ResolvedOn");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_ProductId" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "ProductId");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId_RfqIt~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_BusinessUnitId_RfqId_RfqIt~" ON public.lead_line_commercial_resolutions USING btree ("BusinessUnitId", "RfqId", "RfqItemId");


--
-- Name: IX_lead_line_commercial_resolutions_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_line_commercial_resolutions_RfqItemId_RfqId" ON public.lead_line_commercial_resolutions USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_lead_routing_decisions_BusinessUnitId_CustomerId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_routing_decisions_BusinessUnitId_CustomerId" ON public.lead_routing_decisions USING btree ("BusinessUnitId", "CustomerId");


--
-- Name: IX_lead_routing_decisions_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_lead_routing_decisions_BusinessUnitId_IdempotencyKey" ON public.lead_routing_decisions USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_lead_routing_decisions_BusinessUnitId_LeadId_CreatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_routing_decisions_BusinessUnitId_LeadId_CreatedOn" ON public.lead_routing_decisions USING btree ("BusinessUnitId", "LeadId", "CreatedOn");


--
-- Name: IX_lead_routing_decisions_BusinessUnitId_MatchedIdentifierId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_routing_decisions_BusinessUnitId_MatchedIdentifierId" ON public.lead_routing_decisions USING btree ("BusinessUnitId", "MatchedIdentifierId");


--
-- Name: IX_lead_routing_decisions_BusinessUnitId_OwnershipId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lead_routing_decisions_BusinessUnitId_OwnershipId" ON public.lead_routing_decisions USING btree ("BusinessUnitId", "OwnershipId");


--
-- Name: IX_learning_governance_events_BU_Signal_OccurredOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_learning_governance_events_BU_Signal_OccurredOn" ON public.learning_governance_events USING btree ("BusinessUnitId", "SignalId", "OccurredOn");


--
-- Name: IX_lifecycle_outbox_messages_AvailableOn_LockedUntil_OccurredO~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_lifecycle_outbox_messages_AvailableOn_LockedUntil_OccurredO~" ON public.lifecycle_outbox_messages USING btree ("AvailableOn", "LockedUntil", "OccurredOn", "Id") WHERE (("ProcessedOn" IS NULL) AND ("DeadLetteredOn" IS NULL));


--
-- Name: IX_lifecycle_outbox_messages_BusinessUnitId_LifecycleEventId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_lifecycle_outbox_messages_BusinessUnitId_LifecycleEventId" ON public.lifecycle_outbox_messages USING btree ("BusinessUnitId", "LifecycleEventId");


--
-- Name: IX_lifecycle_outbox_messages_LifecycleEventId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_lifecycle_outbox_messages_LifecycleEventId" ON public.lifecycle_outbox_messages USING btree ("LifecycleEventId");


--
-- Name: IX_material_lot_certificates_AttachmentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_certificates_AttachmentId" ON public.material_lot_certificates USING btree ("AttachmentId");


--
-- Name: IX_material_lot_certificates_BU_ExpiresOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_certificates_BU_ExpiresOn" ON public.material_lot_certificates USING btree ("BusinessUnitId", "ExpiresOn");


--
-- Name: IX_material_lot_certificates_BU_Lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_certificates_BU_Lot" ON public.material_lot_certificates USING btree ("BusinessUnitId", "MaterialLotId");


--
-- Name: IX_material_lot_consumptions_BU_CommercialCase; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_consumptions_BU_CommercialCase" ON public.material_lot_consumptions USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_material_lot_consumptions_BU_Lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_consumptions_BU_Lot" ON public.material_lot_consumptions USING btree ("BusinessUnitId", "MaterialLotId");


--
-- Name: IX_material_lot_consumptions_BU_Order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_consumptions_BU_Order" ON public.material_lot_consumptions USING btree ("BusinessUnitId", "OrderId");


--
-- Name: IX_material_lot_consumptions_BU_OrderItem; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_consumptions_BU_OrderItem" ON public.material_lot_consumptions USING btree ("BusinessUnitId", "OrderItemId");


--
-- Name: IX_material_lot_consumptions_BU_Shipment; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_consumptions_BU_Shipment" ON public.material_lot_consumptions USING btree ("BusinessUnitId", "ShipmentId");


--
-- Name: IX_material_lot_consumptions_OrderItemId_OrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lot_consumptions_OrderItemId_OrderId" ON public.material_lot_consumptions USING btree ("OrderItemId", "OrderId");


--
-- Name: IX_material_lots_BU_CommercialCase; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_BU_CommercialCase" ON public.material_lots USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_material_lots_BU_Inventory_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_BU_Inventory_Status" ON public.material_lots USING btree ("BusinessUnitId", "InventoryId", "Status");


--
-- Name: IX_material_lots_BU_Product_LotNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_material_lots_BU_Product_LotNumber" ON public.material_lots USING btree ("BusinessUnitId", "ProductId", "LotNumber") WHERE (("TrackingMode")::text = 'SERIAL'::text);


--
-- Name: IX_material_lots_BU_SupplierPurchaseOrder; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_BU_SupplierPurchaseOrder" ON public.material_lots USING btree ("BusinessUnitId", "SupplierPurchaseOrderId");


--
-- Name: IX_material_lots_BU_Supplier_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_BU_Supplier_Status" ON public.material_lots USING btree ("BusinessUnitId", "SupplierId", "Status");


--
-- Name: IX_material_lots_BusinessUnitId_SupplierPurchaseOrderLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_BusinessUnitId_SupplierPurchaseOrderLineId" ON public.material_lots USING btree ("BusinessUnitId", "SupplierPurchaseOrderLineId");


--
-- Name: IX_material_lots_BusinessUnitId_WarehouseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_BusinessUnitId_WarehouseId" ON public.material_lots USING btree ("BusinessUnitId", "WarehouseId");


--
-- Name: IX_material_lots_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_material_lots_SupplierId_BusinessUnitId" ON public.material_lots USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_ports_of_entry_BusinessUnitId_Code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_ports_of_entry_BusinessUnitId_Code" ON public.ports_of_entry USING btree ("BusinessUnitId", "Code");


--
-- Name: IX_ports_of_entry_BusinessUnitId_IsActive_Kind; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_ports_of_entry_BusinessUnitId_IsActive_Kind" ON public.ports_of_entry USING btree ("BusinessUnitId", "IsActive", "Kind");


--
-- Name: IX_procurement_callback_receipts_BusinessUnitId_ProcurementHan~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_callback_receipts_BusinessUnitId_ProcurementHan~" ON public.procurement_callback_receipts USING btree ("BusinessUnitId", "ProcurementHandoffId", "ReceivedOn");


--
-- Name: IX_procurement_callback_receipts_BusinessUnitId_SourceSystem_E~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_procurement_callback_receipts_BusinessUnitId_SourceSystem_E~" ON public.procurement_callback_receipts USING btree ("BusinessUnitId", "SourceSystem", "ExternalEventId");


--
-- Name: IX_procurement_events_BusinessUnitId_AggregateType_AggregateId~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_events_BusinessUnitId_AggregateType_AggregateId~" ON public.procurement_events USING btree ("BusinessUnitId", "AggregateType", "AggregateId", "OccurredOn");


--
-- Name: IX_procurement_events_BusinessUnitId_EventType_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_procurement_events_BusinessUnitId_EventType_IdempotencyKey" ON public.procurement_events USING btree ("BusinessUnitId", "EventType", "IdempotencyKey");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_CommercialDemandLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_CommercialDemandLineId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "CommercialDemandLineId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_CurrencyId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_CustomerOrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_CustomerOrderId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "CustomerOrderId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_CustomerOrderLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_CustomerOrderLineId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "CustomerOrderLineId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_ExternalSupplierPoNumbe~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_ExternalSupplierPoNumbe~" ON public.procurement_handoffs USING btree ("BusinessUnitId", "ExternalSupplierPoNumber", "ExternalSupplierPoLineNumber") WHERE ("ExternalSupplierPoNumber" IS NOT NULL);


--
-- Name: IX_procurement_handoffs_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_IdempotencyKey" ON public.procurement_handoffs USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_RfqId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_SourcingAwardId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_SourcingAwardId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "SourcingAwardId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_SupplierQuotedItemId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_SupplierQuotedItemId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "SupplierQuotedItemId");


--
-- Name: IX_procurement_handoffs_BusinessUnitId_WarehouseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_BusinessUnitId_WarehouseId" ON public.procurement_handoffs USING btree ("BusinessUnitId", "WarehouseId");


--
-- Name: IX_procurement_handoffs_CustomerOrderLineId_CustomerOrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_CustomerOrderLineId_CustomerOrderId" ON public.procurement_handoffs USING btree ("CustomerOrderLineId", "CustomerOrderId");


--
-- Name: IX_procurement_handoffs_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_RfqItemId_RfqId" ON public.procurement_handoffs USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_procurement_handoffs_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_handoffs_SupplierId_BusinessUnitId" ON public.procurement_handoffs USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_procurement_outbox_BusinessUnitId_DeadLetteredOn_NextAttemp~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_outbox_BusinessUnitId_DeadLetteredOn_NextAttemp~" ON public.procurement_outbox USING btree ("BusinessUnitId", "DeadLetteredOn", "NextAttemptOn");


--
-- Name: IX_procurement_outbox_BusinessUnitId_SupplierSolicitationId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_procurement_outbox_BusinessUnitId_SupplierSolicitationId" ON public.procurement_outbox USING btree ("BusinessUnitId", "SupplierSolicitationId");


--
-- Name: IX_procurement_outbox_Status_NextAttemptOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_procurement_outbox_Status_NextAttemptOn" ON public.procurement_outbox USING btree ("Status", "NextAttemptOn");


--
-- Name: IX_product_aliases_BusinessUnitId_Kind_NormalizedValue_Account~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_product_aliases_BusinessUnitId_Kind_NormalizedValue_Account~" ON public.product_aliases USING btree ("BusinessUnitId", "Kind", "NormalizedValue", "AccountId");


--
-- Name: IX_product_aliases_BusinessUnitId_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_product_aliases_BusinessUnitId_ProductId" ON public.product_aliases USING btree ("BusinessUnitId", "ProductId");


--
-- Name: IX_product_supersessions_BusinessUnitId_ReplacementProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_product_supersessions_BusinessUnitId_ReplacementProductId" ON public.product_supersessions USING btree ("BusinessUnitId", "ReplacementProductId");


--
-- Name: IX_product_supersessions_BusinessUnitId_SupersededProductId_Re~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_product_supersessions_BusinessUnitId_SupersededProductId_Re~" ON public.product_supersessions USING btree ("BusinessUnitId", "SupersededProductId", "ReplacementProductId", "EffectiveOn");


--
-- Name: IX_quote_delivery_requests_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_quote_delivery_requests_BusinessUnitId_IdempotencyKey" ON public.quote_delivery_requests USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_quote_delivery_requests_BusinessUnitId_QuoteId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_quote_delivery_requests_BusinessUnitId_QuoteId" ON public.quote_delivery_requests USING btree ("BusinessUnitId", "QuoteId");


--
-- Name: IX_quote_delivery_requests_CompletedOn_DeadLetteredOn_Availabl~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_quote_delivery_requests_CompletedOn_DeadLetteredOn_Availabl~" ON public.quote_delivery_requests USING btree ("CompletedOn", "DeadLetteredOn", "AvailableOn", "LeaseUntil");


--
-- Name: IX_sales_coaching_acknowledgements_BusinessUnitId_FindingKey_C~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_coaching_acknowledgements_BusinessUnitId_FindingKey_C~" ON public.sales_coaching_acknowledgements USING btree ("BusinessUnitId", "FindingKey", "CreatedAtUtc");


--
-- Name: IX_sales_coaching_acknowledgements_BusinessUnitId_IdempotencyK~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sales_coaching_acknowledgements_BusinessUnitId_IdempotencyK~" ON public.sales_coaching_acknowledgements USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_sales_coaching_acknowledgements_BusinessUnitId_SalesRepUser~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_coaching_acknowledgements_BusinessUnitId_SalesRepUser~" ON public.sales_coaching_acknowledgements USING btree ("BusinessUnitId", "SalesRepUserId", "CreatedAtUtc");


--
-- Name: IX_sales_contributions_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sales_contributions_BusinessUnitId_IdempotencyKey" ON public.sales_contributions USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_sales_contributions_BusinessUnitId_SalesRepUserId_Recognize~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_contributions_BusinessUnitId_SalesRepUserId_Recognize~" ON public.sales_contributions USING btree ("BusinessUnitId", "SalesRepUserId", "RecognizedAtUtc");


--
-- Name: IX_sales_contributions_SalesRepUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_contributions_SalesRepUserId" ON public.sales_contributions USING btree ("SalesRepUserId");


--
-- Name: IX_sales_rep_profiles_BusinessUnitId_LastMutationIdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sales_rep_profiles_BusinessUnitId_LastMutationIdempotencyKey" ON public.sales_rep_profiles USING btree ("BusinessUnitId", "LastMutationIdempotencyKey");


--
-- Name: IX_sales_rep_profiles_BusinessUnitId_UserId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sales_rep_profiles_BusinessUnitId_UserId" ON public.sales_rep_profiles USING btree ("BusinessUnitId", "UserId");


--
-- Name: IX_sales_rep_profiles_UserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_rep_profiles_UserId" ON public.sales_rep_profiles USING btree ("UserId");


--
-- Name: IX_sales_team_memberships_BusinessUnitId_UserId_TeamId_Effecti~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_team_memberships_BusinessUnitId_UserId_TeamId_Effecti~" ON public.sales_team_memberships USING btree ("BusinessUnitId", "UserId", "TeamId", "EffectiveToUtc");


--
-- Name: IX_sales_team_memberships_UserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sales_team_memberships_UserId" ON public.sales_team_memberships USING btree ("UserId");


--
-- Name: IX_source_document_occurrences_business_unit_id_corpus_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_source_document_occurrences_business_unit_id_corpus_id" ON public.source_document_occurrences USING btree (business_unit_id, corpus_id);


--
-- Name: IX_sourcing_case_candidates_BusinessUnitId_SourcingCaseId_Rank; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sourcing_case_candidates_BusinessUnitId_SourcingCaseId_Rank" ON public.sourcing_case_candidates USING btree ("BusinessUnitId", "SourcingCaseId", "Rank");


--
-- Name: IX_sourcing_case_candidates_BusinessUnitId_SourcingCaseId_Supp~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sourcing_case_candidates_BusinessUnitId_SourcingCaseId_Supp~" ON public.sourcing_case_candidates USING btree ("BusinessUnitId", "SourcingCaseId", "SupplierId");


--
-- Name: IX_sourcing_case_candidates_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sourcing_case_candidates_SupplierId_BusinessUnitId" ON public.sourcing_case_candidates USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_sourcing_cases_BusinessUnitId_CommercialDemandLineId_Shorta~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sourcing_cases_BusinessUnitId_CommercialDemandLineId_Shorta~" ON public.sourcing_cases USING btree ("BusinessUnitId", "CommercialDemandLineId", "ShortageDecisionKey");


--
-- Name: IX_sourcing_cases_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_sourcing_cases_BusinessUnitId_IdempotencyKey" ON public.sourcing_cases USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_sourcing_cases_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sourcing_cases_BusinessUnitId_RfqId" ON public.sourcing_cases USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_sourcing_cases_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_sourcing_cases_RfqItemId_RfqId" ON public.sourcing_cases USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_stock_reservations_availability; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_stock_reservations_availability" ON public.stock_reservations USING btree ("BusinessUnitId", "InventoryId", "Status");


--
-- Name: IX_stock_reservations_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_stock_reservations_lot" ON public.stock_reservations USING btree ("BusinessUnitId", "MaterialLotId", "Status") WHERE ("MaterialLotId" IS NOT NULL);


--
-- Name: IX_stock_reservations_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_stock_reservations_order" ON public.stock_reservations USING btree ("BusinessUnitId", "OrderId");


--
-- Name: IX_supplier_negotiation_decisions_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_negotiation_decisions_BusinessUnitId_IdempotencyKey" ON public.supplier_negotiation_decisions USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_supplier_negotiation_decisions_BusinessUnitId_SupplierQuote~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_negotiation_decisions_BusinessUnitId_SupplierQuote~" ON public.supplier_negotiation_decisions USING btree ("BusinessUnitId", "SupplierQuoteId", "SupplierQuoteRevisionId", "DecidedOn");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_IncomingInvent~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_IncomingInvent~" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "IncomingInventoryId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_InventoryId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_InventoryId" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "InventoryId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_ProductId" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "ProductId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_RfqId" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_SourcingAwardId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_SourcingAwardId" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "SourcingAwardId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_SupplierPurcha~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_SupplierPurcha~" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "SupplierPurchaseOrderId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_SupplierQuoted~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_SupplierQuoted~" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "SupplierQuotedItemId");


--
-- Name: IX_supplier_purchase_order_lines_BusinessUnitId_WarehouseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_BusinessUnitId_WarehouseId" ON public.supplier_purchase_order_lines USING btree ("BusinessUnitId", "WarehouseId");


--
-- Name: IX_supplier_purchase_order_lines_RfqItemId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_order_lines_RfqItemId_RfqId" ON public.supplier_purchase_order_lines USING btree ("RfqItemId", "RfqId");


--
-- Name: IX_supplier_purchase_orders_BusinessUnitId_CommercialCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_orders_BusinessUnitId_CommercialCaseId" ON public.supplier_purchase_orders USING btree ("BusinessUnitId", "CommercialCaseId");


--
-- Name: IX_supplier_purchase_orders_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_orders_BusinessUnitId_CurrencyId" ON public.supplier_purchase_orders USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_supplier_purchase_orders_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_purchase_orders_BusinessUnitId_IdempotencyKey" ON public.supplier_purchase_orders USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_supplier_purchase_orders_BusinessUnitId_PurchaseOrderNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_purchase_orders_BusinessUnitId_PurchaseOrderNumber" ON public.supplier_purchase_orders USING btree ("BusinessUnitId", "PurchaseOrderNumber");


--
-- Name: IX_supplier_purchase_orders_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_orders_BusinessUnitId_RfqId" ON public.supplier_purchase_orders USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_supplier_purchase_orders_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_purchase_orders_SupplierId_BusinessUnitId" ON public.supplier_purchase_orders USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_supplier_quote_field_evidence_BusinessUnitId_SupplierQuoteL~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_field_evidence_BusinessUnitId_SupplierQuoteL~" ON public.supplier_quote_field_evidence USING btree ("BusinessUnitId", "SupplierQuoteLineId");


--
-- Name: IX_supplier_quote_field_evidence_BusinessUnitId_SupplierQuoteR~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_field_evidence_BusinessUnitId_SupplierQuoteR~" ON public.supplier_quote_field_evidence USING btree ("BusinessUnitId", "SupplierQuoteRevisionId", "ReviewRequired");


--
-- Name: IX_supplier_quote_lines_BusinessUnitId_CommercialDemandLineId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_lines_BusinessUnitId_CommercialDemandLineId" ON public.supplier_quote_lines USING btree ("BusinessUnitId", "CommercialDemandLineId");


--
-- Name: IX_supplier_quote_lines_BusinessUnitId_SupplierQuoteRevisionId~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_quote_lines_BusinessUnitId_SupplierQuoteRevisionId~" ON public.supplier_quote_lines USING btree ("BusinessUnitId", "SupplierQuoteRevisionId", "LineNumber");


--
-- Name: IX_supplier_quote_review_decisions_BusinessUnitId_SupplierQuot~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_review_decisions_BusinessUnitId_SupplierQuot~" ON public.supplier_quote_review_decisions USING btree ("BusinessUnitId", "SupplierQuoteRevisionId");


--
-- Name: IX_supplier_quote_review_decisions_BusinessUnitId_SupplierQuo~1; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_review_decisions_BusinessUnitId_SupplierQuo~1" ON public.supplier_quote_review_decisions USING btree ("BusinessUnitId", "SupplierQuoteFieldEvidenceId", "ReviewedOn");


--
-- Name: IX_supplier_quote_revisions_BusinessUnitId_CurrencyId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_revisions_BusinessUnitId_CurrencyId" ON public.supplier_quote_revisions USING btree ("BusinessUnitId", "CurrencyId");


--
-- Name: IX_supplier_quote_revisions_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_quote_revisions_BusinessUnitId_IdempotencyKey" ON public.supplier_quote_revisions USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_supplier_quote_revisions_BusinessUnitId_SourceDocumentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quote_revisions_BusinessUnitId_SourceDocumentId" ON public.supplier_quote_revisions USING btree ("BusinessUnitId", "SourceDocumentId");


--
-- Name: IX_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Rev~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Rev~" ON public.supplier_quote_revisions USING btree ("BusinessUnitId", "SupplierQuoteId", "RevisionNumber");


--
-- Name: IX_supplier_quotes_BusinessUnitId_InboxStatus_UpdatedOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quotes_BusinessUnitId_InboxStatus_UpdatedOn" ON public.supplier_quotes USING btree ("BusinessUnitId", "InboxStatus", "UpdatedOn");


--
-- Name: IX_supplier_quotes_BusinessUnitId_NexoraSerial; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quotes_BusinessUnitId_NexoraSerial" ON public.supplier_quotes USING btree ("BusinessUnitId", "NexoraSerial");


--
-- Name: IX_supplier_quotes_BusinessUnitId_RfqId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quotes_BusinessUnitId_RfqId" ON public.supplier_quotes USING btree ("BusinessUnitId", "RfqId");


--
-- Name: IX_supplier_quotes_BusinessUnitId_SourcingCaseId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quotes_BusinessUnitId_SourcingCaseId" ON public.supplier_quotes USING btree ("BusinessUnitId", "SourcingCaseId");


--
-- Name: IX_supplier_quotes_BusinessUnitId_SupplierId_SupplierQuoteRefe~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_quotes_BusinessUnitId_SupplierId_SupplierQuoteRefe~" ON public.supplier_quotes USING btree ("BusinessUnitId", "SupplierId", "SupplierQuoteReference");


--
-- Name: IX_supplier_quotes_BusinessUnitId_SupplierSolicitationId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quotes_BusinessUnitId_SupplierSolicitationId" ON public.supplier_quotes USING btree ("BusinessUnitId", "SupplierSolicitationId");


--
-- Name: IX_supplier_quotes_SupplierId_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_quotes_SupplierId_BusinessUnitId" ON public.supplier_quotes USING btree ("SupplierId", "BusinessUnitId");


--
-- Name: IX_supplier_shipment_lines_BusinessUnitId_ProductId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_shipment_lines_BusinessUnitId_ProductId" ON public.supplier_shipment_lines USING btree ("BusinessUnitId", "ProductId");


--
-- Name: IX_supplier_shipment_lines_BusinessUnitId_SupplierPurchaseOrde~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_shipment_lines_BusinessUnitId_SupplierPurchaseOrde~" ON public.supplier_shipment_lines USING btree ("BusinessUnitId", "SupplierPurchaseOrderLineId");


--
-- Name: IX_supplier_shipment_lines_BusinessUnitId_SupplierShipmentId_S~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_shipment_lines_BusinessUnitId_SupplierShipmentId_S~" ON public.supplier_shipment_lines USING btree ("BusinessUnitId", "SupplierShipmentId", "SupplierPurchaseOrderLineId");


--
-- Name: IX_supplier_shipments_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_shipments_BusinessUnitId_IdempotencyKey" ON public.supplier_shipments USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_supplier_shipments_BusinessUnitId_MaterialAvailableDate; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_shipments_BusinessUnitId_MaterialAvailableDate" ON public.supplier_shipments USING btree ("BusinessUnitId", "MaterialAvailableDate");


--
-- Name: IX_supplier_shipments_BusinessUnitId_Milestone_EtaDate; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_shipments_BusinessUnitId_Milestone_EtaDate" ON public.supplier_shipments USING btree ("BusinessUnitId", "Milestone", "EtaDate");


--
-- Name: IX_supplier_shipments_BusinessUnitId_PortOfEntryId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_shipments_BusinessUnitId_PortOfEntryId" ON public.supplier_shipments USING btree ("BusinessUnitId", "PortOfEntryId");


--
-- Name: IX_supplier_shipments_BusinessUnitId_ShipmentNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_supplier_shipments_BusinessUnitId_ShipmentNumber" ON public.supplier_shipments USING btree ("BusinessUnitId", "ShipmentNumber");


--
-- Name: IX_supplier_shipments_BusinessUnitId_SupplierPurchaseOrderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_supplier_shipments_BusinessUnitId_SupplierPurchaseOrderId" ON public.supplier_shipments USING btree ("BusinessUnitId", "SupplierPurchaseOrderId");


--
-- Name: IX_tenant_governance_audit_events_BusinessUnitId_Area_Occurred~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_tenant_governance_audit_events_BusinessUnitId_Area_Occurred~" ON public.tenant_governance_audit_events USING btree ("BusinessUnitId", "Area", "OccurredOn");


--
-- Name: IX_tenant_governance_audit_events_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_governance_audit_events_BusinessUnitId_IdempotencyKey" ON public.tenant_governance_audit_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_unassigned_work_items_BusinessUnitId_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_unassigned_work_items_BusinessUnitId_IdempotencyKey" ON public.unassigned_work_items USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: IX_unassigned_work_items_BusinessUnitId_LeadId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "IX_unassigned_work_items_BusinessUnitId_LeadId" ON public.unassigned_work_items USING btree ("BusinessUnitId", "LeadId") WHERE ("Status"  = ANY (ARRAY['Open'::character varying, 'Claimed'::character varying]));


--
-- Name: IX_unassigned_work_items_BusinessUnitId_LeadId_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_unassigned_work_items_BusinessUnitId_LeadId_Status" ON public.unassigned_work_items USING btree ("BusinessUnitId", "LeadId", "Status");


--
-- Name: IX_unassigned_work_items_BusinessUnitId_RoutingDecisionId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_unassigned_work_items_BusinessUnitId_RoutingDecisionId" ON public.unassigned_work_items USING btree ("BusinessUnitId", "RoutingDecisionId");


--
-- Name: IX_unassigned_work_items_BusinessUnitId_Status_SlaDueOn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_unassigned_work_items_BusinessUnitId_Status_SlaDueOn" ON public.unassigned_work_items USING btree ("BusinessUnitId", "Status", "SlaDueOn");


--
-- Name: IX_validation_findings_business_unit_id_inquiry_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_validation_findings_business_unit_id_inquiry_id" ON public.validation_findings USING btree (business_unit_id, inquiry_id);


--
-- Name: IX_validation_findings_business_unit_id_line_item_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_validation_findings_business_unit_id_line_item_id" ON public.validation_findings USING btree (business_unit_id, line_item_id);


--
-- Name: IX_validation_findings_business_unit_id_region_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "IX_validation_findings_business_unit_id_region_id" ON public.validation_findings USING btree (business_unit_id, region_id);


--
-- Name: UQ_Contacts_BusinessUnitID_Email; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_Contacts_BusinessUnitID_Email" ON public."Contacts" USING btree ("BusinessUnitID", "Email") WHERE ("Email" IS NOT NULL);


--
-- Name: UQ_Customers_BUID_ContactEmail; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_Customers_BUID_ContactEmail" ON public."Customers" USING btree ("BUID", "ContactEmail") WHERE ("ContactEmail" IS NOT NULL);


--
-- Name: UQ_EmailIngests_EmailConfigurationID_MessageID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_EmailIngests_EmailConfigurationID_MessageID" ON public."EmailIngests" USING btree ("EmailConfigurationID", "MessageID");


--
-- Name: UQ_Products_BUID_PartNo; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_Products_BUID_PartNo" ON public."Products" USING btree ("BUID", "PartNo") WHERE ("BUID" IS NOT NULL);


--
-- Name: UQ_QuoteConfiguration_BusinessUnitId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_QuoteConfiguration_BusinessUnitId" ON public."QuoteConfiguration" USING btree ("BusinessUnitId");


--
-- Name: UQ_Warehouses_Code_BU; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_Warehouses_Code_BU" ON public."Warehouses" USING btree ("WarehouseCode", "BusinessUnitID");


--
-- Name: UQ__Module__EAC9AEC357051E1B; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ__Module__EAC9AEC357051E1B" ON public."Module" USING btree ("ModuleName");


--
-- Name: UQ__Supplier__FFA796CDFB352BC7; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS "UQ__Supplier__FFA796CDFB352BC7" ON public."Suppliers" USING btree ("ContactEmail");


--
-- Name: UQ__Users__A9D10534A3A2A11E; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UQ__Users__A9D10534A3A2A11E" ON public."Users" USING btree ("Email");


--
-- Name: UX_AccountingPeriods_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AccountingPeriods_BU_Idempotency" ON public."AccountingPeriods" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_AccountingPeriods_BU_Year_Period; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AccountingPeriods_BU_Year_Period" ON public."AccountingPeriods" USING btree ("BusinessUnitId", "FiscalYear", "PeriodNumber");


--
-- Name: UX_AgentPolicies_BU; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AgentPolicies_BU" ON public."AgentPolicies" USING btree ("BusinessUnitId");


--
-- Name: UX_AiCallAttempts_ProviderRequestId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AiCallAttempts_ProviderRequestId" ON public."AiCallAttempts" USING btree ("Provider", "ProviderRequestId") WHERE ("ProviderRequestId" IS NOT NULL);


--
-- Name: UX_AiCallAttempts_Request_Attempt; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AiCallAttempts_Request_Attempt" ON public."AiCallAttempts" USING btree ("RequestId", "AttemptNumber");


--
-- Name: UX_AiProviderAuthorizations_BU_Provider_Endpoint_Model; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AiProviderAuthorizations_BU_Provider_Endpoint_Model" ON public."AiProviderAuthorizations" USING btree ("BusinessUnitId", "Provider", "Endpoint", "Model");


--
-- Name: UX_AiRequests_BU_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AiRequests_BU_IdempotencyKey" ON public."AiRequests" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_BankAccounts_BU_Fingerprint; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankAccounts_BU_Fingerprint" ON public."BankAccounts" USING btree ("BusinessUnitId", "AccountFingerprint");


--
-- Name: UX_BankAccounts_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankAccounts_BU_Idempotency" ON public."BankAccounts" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_BankAdjustmentDistributions_Order; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankAdjustmentDistributions_Order" ON public."BankAdjustmentDistributions" USING btree ("BusinessUnitId", "BankAdjustmentId", "Sequence");


--
-- Name: UX_BankAdjustments_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankAdjustments_BU_Idempotency" ON public."BankAdjustments" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_BankAdjustments_BU_Journal; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankAdjustments_BU_Journal" ON public."BankAdjustments" USING btree ("BusinessUnitId", "JournalEntryId") WHERE ("JournalEntryId" IS NOT NULL);


--
-- Name: UX_BankImports_BU_Account_SourceHash; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankImports_BU_Account_SourceHash" ON public."BankStatementImports" USING btree ("BusinessUnitId", "BankAccountId", "SourceHash");


--
-- Name: UX_BankImports_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankImports_BU_Idempotency" ON public."BankStatementImports" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_BankLines_BU_Account_ExternalId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankLines_BU_Account_ExternalId" ON public."BankStatementLines" USING btree ("BusinessUnitId", "BankAccountId", "ExternalTransactionId") WHERE ("ExternalTransactionId" IS NOT NULL);


--
-- Name: UX_BankLines_BU_Account_Fingerprint; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankLines_BU_Account_Fingerprint" ON public."BankStatementLines" USING btree ("BusinessUnitId", "BankAccountId", "LineFingerprint");


--
-- Name: UX_BankLines_BU_Statement_Ordinal; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankLines_BU_Statement_Ordinal" ON public."BankStatementLines" USING btree ("BusinessUnitId", "BankStatementId", "SourceOrdinal");


--
-- Name: UX_BankMatchingRules_BU_ActiveScope; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankMatchingRules_BU_ActiveScope" ON public."BankMatchingRules" USING btree ("BusinessUnitId", "Code", "BankAccountId") WHERE (("Status")::text = 'Active'::text);


--
-- Name: UX_BankMatchingRules_BU_ActiveTenant; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankMatchingRules_BU_ActiveTenant" ON public."BankMatchingRules" USING btree ("BusinessUnitId", "Code") WHERE ((("Status")::text = 'Active'::text) AND ("BankAccountId" IS NULL));


--
-- Name: UX_BankMatchingRules_BU_Code_Version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankMatchingRules_BU_Code_Version" ON public."BankMatchingRules" USING btree ("BusinessUnitId", "Code", "RuleVersion");


--
-- Name: UX_BankMatchingRules_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankMatchingRules_BU_Idempotency" ON public."BankMatchingRules" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_BankStatements_BU_Account_Reference; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankStatements_BU_Account_Reference" ON public."BankStatements" USING btree ("BusinessUnitId", "BankAccountId", "StatementReference");


--
-- Name: UX_BoqAssemblies_BU_Code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_BoqAssemblies_BU_Code" ON public."BoqAssemblies" USING btree ("BusinessUnitId", "Code");


--
-- Name: UX_CollectionControls_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CollectionControls_BU_Idempotency" ON public."CollectionControls" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_CommercialCases_AllocationNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CommercialCases_AllocationNumber" ON public."CommercialCases" USING btree ("AllocationNumber");


--
-- Name: UX_CommercialCases_BU_MasterReference; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CommercialCases_BU_MasterReference" ON public."CommercialCases" USING btree ("BusinessUnitID", "MasterReference");


--
-- Name: UX_Contacts_BU_Customer_Primary; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Contacts_BU_Customer_Primary" ON public."Contacts" USING btree ("BusinessUnitID", "CustomerID") WHERE (("IsPrimary" = true) AND ("IsActive" IS DISTINCT FROM false) AND ("CustomerID" IS NOT NULL));


--
-- Name: UX_Contacts_BU_Supplier_Primary; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Contacts_BU_Supplier_Primary" ON public."Contacts" USING btree ("BusinessUnitID", "SupplierID") WHERE (("IsPrimary" = true) AND ("IsActive" IS DISTINCT FROM false) AND ("SupplierID" IS NOT NULL));


--
-- Name: UX_Currency_BusinessUnitID_ID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Currency_BusinessUnitID_ID" ON public."Currency" USING btree ("BusinessUnitID", "ID");


--
-- Name: UX_CustomerAwardAllocations_Award_POLine_QuoteItem; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerAwardAllocations_Award_POLine_QuoteItem" ON public."CustomerAwardLineAllocations" USING btree ("CustomerAwardId", "CustomerPurchaseOrderLineId", "QuoteItemId");


--
-- Name: UX_CustomerAwards_BU_AwardNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerAwards_BU_AwardNumber" ON public."CustomerAwards" USING btree ("BusinessUnitId", "AwardNumber");


--
-- Name: UX_CustomerCollectionProfiles_BU_Customer_Currency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerCollectionProfiles_BU_Customer_Currency" ON public."CustomerCollectionProfiles" USING btree ("BusinessUnitId", "CustomerId", "CurrencyId") NULLS NOT DISTINCT;


--
-- Name: UX_CustomerPayments_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerPayments_BU_Idempotency" ON public."CustomerPayments" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_CustomerPayments_BU_Journal; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerPayments_BU_Journal" ON public."CustomerPayments" USING btree ("BusinessUnitId", "JournalEntryId") WHERE ("JournalEntryId" IS NOT NULL);


--
-- Name: UX_CustomerPayments_BU_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerPayments_BU_Number" ON public."CustomerPayments" USING btree ("BusinessUnitId", "ReceiptNumber");


--
-- Name: UX_CustomerPurchaseOrderLines_PO_ExternalReference; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerPurchaseOrderLines_PO_ExternalReference" ON public."CustomerPurchaseOrderLines" USING btree ("BusinessUnitId", "CustomerPurchaseOrderId", "ExternalLineReference");


--
-- Name: UX_CustomerPurchaseOrders_BU_Customer_ExternalNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerPurchaseOrders_BU_Customer_ExternalNumber" ON public."CustomerPurchaseOrders" USING btree ("BusinessUnitId", "CustomerId", "NormalizedExternalPoNumber");


--
-- Name: UX_CustomerPurchaseOrders_BU_InternalNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerPurchaseOrders_BU_InternalNumber" ON public."CustomerPurchaseOrders" USING btree ("BusinessUnitId", "InternalNumber");


--
-- Name: UX_CustomerRefunds_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerRefunds_BU_Idempotency" ON public."CustomerRefunds" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_CustomerRefunds_BU_Journal; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerRefunds_BU_Journal" ON public."CustomerRefunds" USING btree ("BusinessUnitId", "JournalEntryId") WHERE ("JournalEntryId" IS NOT NULL);


--
-- Name: UX_CustomerRefunds_BU_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerRefunds_BU_Number" ON public."CustomerRefunds" USING btree ("BusinessUnitId", "RefundNumber") WHERE ("RefundNumber" IS NOT NULL);


--
-- Name: UX_CustomerStatementLines_BU_Statement_Sequence; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerStatementLines_BU_Statement_Sequence" ON public."CustomerStatementLines" USING btree ("BusinessUnitId", "CustomerStatementId", "Sequence");


--
-- Name: UX_CustomerStatements_BU_Customer_Currency_Cutoff_Revision; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerStatements_BU_Customer_Currency_Cutoff_Revision" ON public."CustomerStatements" USING btree ("BusinessUnitId", "CustomerId", "CurrencyId", "CutoffAt", "Revision") NULLS NOT DISTINCT WHERE (("Status")::text <> 'Cancelled'::text);


--
-- Name: UX_CustomerStatements_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerStatements_BU_Idempotency" ON public."CustomerStatements" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_CustomerStatements_BU_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerStatements_BU_Number" ON public."CustomerStatements" USING btree ("BusinessUnitId", "StatementNumber") WHERE ("StatementNumber" IS NOT NULL);


--
-- Name: UX_CustomerStatements_BU_Successor; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerStatements_BU_Successor" ON public."CustomerStatements" USING btree ("BusinessUnitId", "SupersedesStatementId") WHERE (("SupersedesStatementId" IS NOT NULL) AND (("Status")::text <> 'Cancelled'::text));


--
-- Name: UX_Customers_BU_DocId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Customers_BU_DocId" ON public."Customers" USING btree ("BUID", "DocId") WHERE ("DocId" IS NOT NULL);


--
-- Name: UX_DunningCases_BU_ActiveCustomerCurrency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningCases_BU_ActiveCustomerCurrency" ON public."DunningCases" USING btree ("BusinessUnitId", "CustomerId", "CurrencyId") NULLS NOT DISTINCT WHERE ("Status"  = ANY (ARRAY['Open'::character varying, 'Held'::character varying, 'Disputed'::character varying]));


--
-- Name: UX_DunningCases_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningCases_BU_Idempotency" ON public."DunningCases" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_DunningDeliveryAttempts_BU_Notice_Attempt; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningDeliveryAttempts_BU_Notice_Attempt" ON public."DunningDeliveryAttempts" USING btree ("BusinessUnitId", "DunningNoticeId", "AttemptNumber");


--
-- Name: UX_DunningDeliveryAttempts_BU_ProviderEvent; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningDeliveryAttempts_BU_ProviderEvent" ON public."DunningDeliveryAttempts" USING btree ("BusinessUnitId", "ProviderEventId");


--
-- Name: UX_DunningNotices_BU_Case_Stage_Hash; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningNotices_BU_Case_Stage_Hash" ON public."DunningNotices" USING btree ("BusinessUnitId", "DunningCaseId", "Stage", "SnapshotHash");


--
-- Name: UX_DunningNotices_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningNotices_BU_Idempotency" ON public."DunningNotices" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_DunningPolicies_BU_Active; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningPolicies_BU_Active" ON public."DunningPolicies" USING btree ("BusinessUnitId", "Status") WHERE (("Status")::text = 'Active'::text);


--
-- Name: UX_DunningPolicies_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningPolicies_BU_Idempotency" ON public."DunningPolicies" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_DunningPolicies_BU_Version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningPolicies_BU_Version" ON public."DunningPolicies" USING btree ("BusinessUnitId", "PolicyVersion");


--
-- Name: UX_DunningPolicySteps_BU_Policy_Stage; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningPolicySteps_BU_Policy_Stage" ON public."DunningPolicySteps" USING btree ("BusinessUnitId", "DunningPolicyId", "Stage");


--
-- Name: UX_DunningRunDecisions_BU_Run_Profile; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningRunDecisions_BU_Run_Profile" ON public."DunningRunDecisions" USING btree ("BusinessUnitId", "DunningRunId", "CustomerCollectionProfileId") WHERE ("CustomerCollectionProfileId" IS NOT NULL);


--
-- Name: UX_DunningRuns_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_DunningRuns_BU_Idempotency" ON public."DunningRuns" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_ExtractionCorpusEntries_BU_Audit_Field; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ExtractionCorpusEntries_BU_Audit_Field" ON public."ExtractionCorpusEntries" USING btree ("BusinessUnitId", "LeadReviewAuditId", "Scope", "FieldName");


--
-- Name: UX_ExtractionJobs_BU_SourceOccurrence; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ExtractionJobs_BU_SourceOccurrence" ON public."ExtractionJobs" USING btree ("BusinessUnitId", "SourceDocumentOccurrenceId") WHERE ("SourceDocumentOccurrenceId" IS NOT NULL);


--
-- Name: UX_FinanceCommunicationContacts_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FinanceCommunicationContacts_BU_Idempotency" ON public."FinanceCommunicationContacts" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_FinanceCommunicationContacts_BU_Token; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FinanceCommunicationContacts_BU_Token" ON public."FinanceCommunicationContacts" USING btree ("BusinessUnitId", "DestinationToken");


--
-- Name: UX_FinanceCommunicationContacts_BU_VerificationEvent; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FinanceCommunicationContacts_BU_VerificationEvent" ON public."FinanceCommunicationContacts" USING btree ("BusinessUnitId", "VerificationProviderEventId");


--
-- Name: UX_FinanceOutbox_AggregateVersionEvent; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FinanceOutbox_AggregateVersionEvent" ON public."FinanceOutboxMessages" USING btree ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "EventType");


--
-- Name: UX_FinanceOutbox_EventId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FinanceOutbox_EventId" ON public."FinanceOutboxMessages" USING btree ("EventId");


--
-- Name: UX_FolderIngestionRetryStates_BU_Source_File; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FolderIngestionRetryStates_BU_Source_File" ON public."FolderIngestionRetryStates" USING btree ("BusinessUnitId", "SourceLabel", "FileName");


--
-- Name: UX_FxRateSnapshots_BU_Document_Pair; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FxRateSnapshots_BU_Document_Pair" ON public."FxRateSnapshots" USING btree ("BusinessUnitId", "DocumentType", "DocumentId", "FromCurrencyId", "ToCurrencyId");


--
-- Name: UX_FxRates_BU_Pair_EffectiveFrom; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FxRates_BU_Pair_EffectiveFrom" ON public."FxRates" USING btree ("BusinessUnitId", "FromCurrencyId", "ToCurrencyId", "EffectiveFrom");


--
-- Name: UX_Inventory_BU_Product_Warehouse; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Inventory_BU_Product_Warehouse" ON public."Inventory" USING btree ("Buid", "ProductId", "WarehouseId") WHERE (("ProductId" IS NOT NULL) AND ("WarehouseId" IS NOT NULL));


--
-- Name: UX_JournalEntries_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntries_BU_Idempotency" ON public."JournalEntries" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_JournalEntries_BU_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntries_BU_Number" ON public."JournalEntries" USING btree ("BusinessUnitId", "EntryNumber") WHERE ("EntryNumber" IS NOT NULL);


--
-- Name: UX_JournalEntries_BU_Reversal; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntries_BU_Reversal" ON public."JournalEntries" USING btree ("BusinessUnitId", "ReversesJournalEntryId") WHERE ("ReversesJournalEntryId" IS NOT NULL);


--
-- Name: UX_JournalEntries_BU_SourceVersion; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntries_BU_SourceVersion" ON public."JournalEntries" USING btree ("BusinessUnitId", "SourceType", "SourceReference", "SourceVersion") WHERE (("SourceReference" IS NOT NULL) AND ("ReversesJournalEntryId" IS NULL));


--
-- Name: UX_JournalEntryLines_BU_Journal_Sequence; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntryLines_BU_Journal_Sequence" ON public."JournalEntryLines" USING btree ("BusinessUnitId", "JournalEntryId", "Sequence");


--
-- Name: UX_LeadReviewAudits_BU_Lead_Version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_LeadReviewAudits_BU_Lead_Version" ON public."LeadReviewAudits" USING btree ("BusinessUnitId", "LeadId", "ToVersion");


--
-- Name: UX_Leads_BU_CommercialCaseReference; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Leads_BU_CommercialCaseReference" ON public."Leads" USING btree ("BusinessUnitID", "CommercialCaseReference");


--
-- Name: UX_Leads_CommercialCaseID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Leads_CommercialCaseID" ON public."Leads" USING btree ("CommercialCaseId");


--
-- Name: UX_LedgerAccounts_BU_Code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_LedgerAccounts_BU_Code" ON public."LedgerAccounts" USING btree ("BusinessUnitId", "Code");


--
-- Name: UX_LedgerAccounts_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_LedgerAccounts_BU_Idempotency" ON public."LedgerAccounts" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_LedgerBooks_BU; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_LedgerBooks_BU" ON public."LedgerBooks" USING btree ("BusinessUnitId");


--
-- Name: UX_LedgerBooks_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_LedgerBooks_BU_Idempotency" ON public."LedgerBooks" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_LoginAttempts_Plane_AttemptKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_LoginAttempts_Plane_AttemptKey" ON public."LoginAttempts" USING btree ("Plane", "AttemptKey");


--
-- Name: UX_OrderItems_CustomerAwardLineAllocationID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_OrderItems_CustomerAwardLineAllocationID" ON public."OrderItems" USING btree ("CustomerAwardLineAllocationID") WHERE ("CustomerAwardLineAllocationID" IS NOT NULL);


--
-- Name: UX_OrderToCashAudit_BU_Aggregate_Version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_OrderToCashAudit_BU_Aggregate_Version" ON public."OrderToCashAuditEvents" USING btree ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion");


--
-- Name: UX_OrderToCashAudit_BU_Command_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_OrderToCashAudit_BU_Command_Idempotency" ON public."OrderToCashAuditEvents" USING btree ("BusinessUnitId", "CommandType", "IdempotencyKey");


--
-- Name: UX_Orders_BU_CustomerAwardID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Orders_BU_CustomerAwardID" ON public."Orders" USING btree ("BusinessUnitID", "CustomerAwardID") WHERE ("CustomerAwardID" IS NOT NULL);


--
-- Name: UX_Orders_BU_LegacyQuoteID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Orders_BU_LegacyQuoteID" ON public."Orders" USING btree ("BusinessUnitID", "QuoteID") WHERE (("QuoteID" IS NOT NULL) AND ("CustomerAwardID" IS NULL));


--
-- Name: UX_Orders_BU_OrderNo; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Orders_BU_OrderNo" ON public."Orders" USING btree ("BusinessUnitID", "OrderNo") WHERE (("OrderNo" IS NOT NULL) AND (btrim(("OrderNo")::text) <> ''::text));


--
-- Name: UX_PaymentAllocations_BU_Payment_Document; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_PaymentAllocations_BU_Payment_Document" ON public."PaymentAllocations" USING btree ("BusinessUnitId", "CustomerPaymentId", "ReceivableDocumentId");


--
-- Name: UX_PromisesToPay_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_PromisesToPay_BU_Idempotency" ON public."PromisesToPay" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_PromisesToPay_BU_MatchedPayment; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_PromisesToPay_BU_MatchedPayment" ON public."PromisesToPay" USING btree ("BusinessUnitId", "MatchedPaymentId") WHERE ("MatchedPaymentId" IS NOT NULL);


--
-- Name: UX_QuotePriceAttestationLines_Attestation_QuoteItem; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_QuotePriceAttestationLines_Attestation_QuoteItem" ON public."QuotePriceAttestationLines" USING btree ("AttestationId", "QuoteItemId");


--
-- Name: UX_QuoteValidityExtensions_BU_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_QuoteValidityExtensions_BU_IdempotencyKey" ON public."QuoteValidityExtensions" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_Quotes_BU_RevisionOfQuoteId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Quotes_BU_RevisionOfQuoteId" ON public."Quotes" USING btree ("BusinessUnitID", "RevisionOfQuoteId") WHERE ("RevisionOfQuoteId" IS NOT NULL);


--
-- Name: UX_Quotes_BusinessUnitID_QuoteNo; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Quotes_BusinessUnitID_QuoteNo" ON public."Quotes" USING btree ("BusinessUnitID", "QuoteNo");


--
-- Name: UX_Quotes_BusinessUnitID_RFQID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Quotes_BusinessUnitID_RFQID" ON public."Quotes" USING btree ("BusinessUnitID", "RFQID") WHERE ("RFQID" IS NOT NULL);


--
-- Name: UX_RFQ_BusinessUnitID_LeadID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_RFQ_BusinessUnitID_LeadID" ON public."RFQ" USING btree ("BusinessUnitID", "LeadID") WHERE ("LeadID" IS NOT NULL);


--
-- Name: UX_ReceivableDocuments_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReceivableDocuments_BU_Idempotency" ON public."ReceivableDocuments" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_ReceivableDocuments_BU_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReceivableDocuments_BU_Number" ON public."ReceivableDocuments" USING btree ("BusinessUnitId", "DocumentNumber") WHERE ("DocumentNumber" IS NOT NULL);


--
-- Name: UX_ReceivableWriteOffs_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReceivableWriteOffs_BU_Idempotency" ON public."ReceivableWriteOffs" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_ReceivableWriteOffs_BU_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReceivableWriteOffs_BU_Number" ON public."ReceivableWriteOffs" USING btree ("BusinessUnitId", "WriteOffNumber") WHERE ("WriteOffNumber" IS NOT NULL);


--
-- Name: UX_ReconciliationAllocations_Evidence; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReconciliationAllocations_Evidence" ON public."ReconciliationAllocations" USING btree ("BusinessUnitId", "ReconciliationMatchId", "BankStatementLineId", "JournalEntryLineId");


--
-- Name: UX_ReconciliationMatches_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReconciliationMatches_BU_Idempotency" ON public."ReconciliationMatches" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_ReconciliationRunRules_Evidence; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReconciliationRunRules_Evidence" ON public."ReconciliationRunRules" USING btree ("BusinessUnitId", "ReconciliationRunId", "BankMatchingRuleId");


--
-- Name: UX_ReconciliationRunRules_Order; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReconciliationRunRules_Order" ON public."ReconciliationRunRules" USING btree ("BusinessUnitId", "ReconciliationRunId", "EvaluationOrder");


--
-- Name: UX_ReconciliationRuns_BU_ActiveStatement; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReconciliationRuns_BU_ActiveStatement" ON public."ReconciliationRuns" USING btree ("BusinessUnitId", "BankStatementId") WHERE (("Status")::text <> 'Reopened'::text);


--
-- Name: UX_ReconciliationRuns_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReconciliationRuns_BU_Idempotency" ON public."ReconciliationRuns" USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_ReportSubscriptions_BU_Report_Cadence_Format; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReportSubscriptions_BU_Report_Cadence_Format" ON public."ReportSubscriptions" USING btree ("BusinessUnitId", "ReportKey", "Cadence", "Format");


--
-- Name: UX_SlaEvents_BU_DedupKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_SlaEvents_BU_DedupKey" ON public."SlaEvents" USING btree ("BusinessUnitId", "DedupKey") WHERE (("Status")::text <> 'RELEASED'::text);


--
-- Name: UX_SlaPolicies_BU; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_SlaPolicies_BU" ON public."SlaPolicies" USING btree ("BusinessUnitId");


--
-- Name: UX_SupplierQuotedItems_BU_ResponseKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_SupplierQuotedItems_BU_ResponseKey" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "ResponseIdempotencyKey") WHERE ("ResponseIdempotencyKey" IS NOT NULL);


--
-- Name: UX_SupplierQuotedItems_BU_SourceQuoteLine; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_SupplierQuotedItems_BU_SourceQuoteLine" ON public."SupplierQuotedItems" USING btree ("BusinessUnitId", "SourceSupplierQuoteLineId") WHERE ("SourceSupplierQuoteLineId" IS NOT NULL);


--
-- Name: UX_Suppliers_BU_ContactEmail; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Suppliers_BU_ContactEmail" ON public."Suppliers" USING btree ("BUID", "ContactEmail") WHERE (("ContactEmail" IS NOT NULL) AND ("BUID" IS NOT NULL));


--
-- Name: UX_Suppliers_BU_DocId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Suppliers_BU_DocId" ON public."Suppliers" USING btree ("BUID", "DocId") WHERE ("DocId" IS NOT NULL);


--
-- Name: UX_Teams_BusinessUnitID_ID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Teams_BusinessUnitID_ID" ON public."Teams" USING btree ("BusinessUnitID", "ID");


--
-- Name: UX_UserColumnPreferences_BU_User_View; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_UserColumnPreferences_BU_User_View" ON public."UserColumnPreferences" USING btree ("BusinessUnitId", "UserId", "ViewKey");


--
-- Name: UX_Users_BUID_ID; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_BUID_ID" ON public."Users" USING btree ("BUID", "ID");


--
-- Name: UX_WriteOffAllocations_BU_WriteOff_Document; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_WriteOffAllocations_BU_WriteOff_Document" ON public."WriteOffAllocations" USING btree ("BusinessUnitId", "ReceivableWriteOffId", "ReceivableDocumentId");


--
-- Name: UX_customer_identifiers_authoritative; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_customer_identifiers_authoritative" ON public.customer_identifiers USING btree ("BusinessUnitId", "IdentifierType", "NormalizedValue") WHERE (("EffectiveTo" IS NULL) AND ("IdentifierType"  = ANY (ARRAY['ErpAccount'::character varying, 'TaxRegistration'::character varying, 'Email'::character varying, 'Phone'::character varying])));


--
-- Name: UX_customer_ownerships_single_active; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_customer_ownerships_single_active" ON public.customer_ownerships USING btree ("BusinessUnitId", "CustomerId", "Scope", COALESCE("ScopeKey", ''::character varying)) WHERE (("IsActive" = true) AND ("EffectiveTo" IS NULL));


--
-- Name: UX_delivery_proof_lines_BU_Proof_ShipmentItem; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_delivery_proof_lines_BU_Proof_ShipmentItem" ON public.delivery_proof_lines USING btree ("BusinessUnitId", "DeliveryProofId", "ShipmentItemId");


--
-- Name: UX_delivery_proofs_BU_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_delivery_proofs_BU_IdempotencyKey" ON public.delivery_proofs USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_delivery_proofs_BU_Shipment; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_delivery_proofs_BU_Shipment" ON public.delivery_proofs USING btree ("BusinessUnitId", "ShipmentId");


--
-- Name: UX_delivery_shortfall_decisions_BU_ProofLine; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_delivery_shortfall_decisions_BU_ProofLine" ON public.delivery_shortfall_decisions USING btree ("BusinessUnitId", "DeliveryProofLineId");


--
-- Name: UX_evidence_retention_policies_BusinessUnit; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_evidence_retention_policies_BusinessUnit" ON public.evidence_retention_policies USING btree ("BusinessUnitId");


--
-- Name: UX_extraction_dead_letter_events_tenant_job_idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_extraction_dead_letter_events_tenant_job_idempotency" ON public.extraction_dead_letter_events USING btree ("BusinessUnitId", "ExtractionJobId", "IdempotencyKey");


--
-- Name: UX_follow_up_tasks_BusinessUnitId_Id; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_follow_up_tasks_BusinessUnitId_Id" ON public.follow_up_tasks USING btree ("BusinessUnitId", "Id");


--
-- Name: UX_goods_receipt_lines_InventoryMovementId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_goods_receipt_lines_InventoryMovementId" ON public.goods_receipt_lines USING btree ("InventoryMovementId");


--
-- Name: UX_inventory_reorder_alerts_live; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_inventory_reorder_alerts_live" ON public.inventory_reorder_alerts USING btree ("BusinessUnitId", "InventoryId", "Kind") WHERE ("Status"  = ANY (ARRAY['OPEN'::character varying, 'ACKNOWLEDGED'::character varying]));


--
-- Name: UX_lead_assignments_BusinessUnitId_Id; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_lead_assignments_BusinessUnitId_Id" ON public.lead_assignments USING btree ("BusinessUnitId", "Id");


--
-- Name: UX_lead_customer_match_candidates_lead_rank; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_lead_customer_match_candidates_lead_rank" ON public.lead_customer_match_candidates USING btree ("BusinessUnitId", "LeadId", "Rank");


--
-- Name: UX_learning_governance_events_BU_Idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_governance_events_BU_Idempotency" ON public.learning_governance_events USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_learning_governance_events_BU_Signal_Version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_governance_events_BU_Signal_Version" ON public.learning_governance_events USING btree ("BusinessUnitId", "SignalId", "Version");


--
-- Name: UX_material_lot_certificates_BU_Attachment; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_material_lot_certificates_BU_Attachment" ON public.material_lot_certificates USING btree ("BusinessUnitId", "AttachmentId");


--
-- Name: UX_material_lot_certificates_BU_Lot_Type_Number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_material_lot_certificates_BU_Lot_Type_Number" ON public.material_lot_certificates USING btree ("BusinessUnitId", "MaterialLotId", "CertificateType", "CertificateNumber") WHERE ("CertificateNumber" IS NOT NULL);


--
-- Name: UX_material_lot_consumptions_BU_IdempotencyKey; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_material_lot_consumptions_BU_IdempotencyKey" ON public.material_lot_consumptions USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: UX_material_lots_BU_Receipt_Line_LotNumber; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_material_lots_BU_Receipt_Line_LotNumber" ON public.material_lots USING btree ("BusinessUnitId", "GoodsReceiptId", "SupplierPurchaseOrderLineId", "LotNumber");


--
-- Name: UX_product_aliases_tenant_identity; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_product_aliases_tenant_identity" ON public.product_aliases USING btree ("BusinessUnitId", "Kind", "NormalizedValue", COALESCE("AccountId", (0)::bigint));


--
-- Name: UX_stock_reservations_idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS "UX_stock_reservations_idempotency" ON public.stock_reservations USING btree ("BusinessUnitId", "IdempotencyKey");


--
-- Name: ix_canonical_inquiries_lead; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_canonical_inquiries_lead ON public.canonical_inquiries USING btree (lead_id);


--
-- Name: ix_canonical_inquiries_tenant_customer_rfq; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_canonical_inquiries_tenant_customer_rfq ON public.canonical_inquiries USING btree (business_unit_id, customer_rfq_number);


--
-- Name: ix_canonical_inquiries_tenant_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_canonical_inquiries_tenant_status ON public.canonical_inquiries USING btree (business_unit_id, status);


--
-- Name: ix_canonical_line_items_lead_item; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_canonical_line_items_lead_item ON public.canonical_line_items USING btree (lead_item_id);


--
-- Name: ix_canonical_line_items_tenant_inquiry; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_canonical_line_items_tenant_inquiry ON public.canonical_line_items USING btree (business_unit_id, inquiry_id);


--
-- Name: ix_canonical_line_items_tenant_mpn; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_canonical_line_items_tenant_mpn ON public.canonical_line_items USING btree (business_unit_id, manufacturer_part_number);


--
-- Name: ix_commercial_document_classifications_review_queue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_commercial_document_classifications_review_queue ON public.commercial_document_classifications USING btree (business_unit_id, review_status, created_on);


--
-- Name: ix_document_corpora_tenant_created; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_corpora_tenant_created ON public.document_corpora USING btree (business_unit_id, created_on);


--
-- Name: ix_document_corpora_tenant_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_corpora_tenant_status ON public.document_corpora USING btree (business_unit_id, status);


--
-- Name: ix_document_pages_tenant_document; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_pages_tenant_document ON public.document_pages USING btree (business_unit_id, document_id);


--
-- Name: ix_document_pages_tenant_ocr_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_pages_tenant_ocr_status ON public.document_pages USING btree (business_unit_id, ocr_status);


--
-- Name: ix_document_regions_page_address; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_regions_page_address ON public.document_regions USING btree (page_id, source_address);


--
-- Name: ix_document_regions_tenant_page; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_regions_tenant_page ON public.document_regions USING btree (business_unit_id, page_id);


--
-- Name: ix_document_regions_tenant_type; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_document_regions_tenant_type ON public.document_regions USING btree (business_unit_id, region_type);


--
-- Name: ix_extraction_runs_extraction_job; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_extraction_runs_extraction_job ON public.extraction_runs USING btree (extraction_job_id);


--
-- Name: ix_extraction_runs_tenant_status_created; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_extraction_runs_tenant_status_created ON public.extraction_runs USING btree (business_unit_id, status, created_on);


--
-- Name: ix_field_evidence_inquiry_field; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_field_evidence_inquiry_field ON public.field_evidence USING btree (business_unit_id, inquiry_id, field_name);


--
-- Name: ix_field_evidence_line_field; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_field_evidence_line_field ON public.field_evidence USING btree (business_unit_id, line_item_id, field_name);


--
-- Name: ix_field_evidence_region; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_field_evidence_region ON public.field_evidence USING btree (region_id);


--
-- Name: ix_field_evidence_tenant_run; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_field_evidence_tenant_run ON public.field_evidence USING btree (business_unit_id, run_id);


--
-- Name: ix_source_document_occurrences_extraction_job; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_document_occurrences_extraction_job ON public.source_document_occurrences USING btree (extraction_job_id);


--
-- Name: ix_source_document_occurrences_tenant_document; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_document_occurrences_tenant_document ON public.source_document_occurrences USING btree (business_unit_id, source_document_id, received_on);


--
-- Name: ix_source_document_occurrences_tenant_group; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_document_occurrences_tenant_group ON public.source_document_occurrences USING btree (business_unit_id, logical_group_key, received_on);


--
-- Name: ix_source_document_occurrences_tenant_original; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_document_occurrences_tenant_original ON public.source_document_occurrences USING btree (business_unit_id, original_occurrence_id);


--
-- Name: ix_source_document_occurrences_tenant_outcome; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_document_occurrences_tenant_outcome ON public.source_document_occurrences USING btree (business_unit_id, outcome_state, received_on);


--
-- Name: ix_source_documents_extraction_job; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_documents_extraction_job ON public.source_documents USING btree (extraction_job_id);


--
-- Name: ix_source_documents_tenant_corpus; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_documents_tenant_corpus ON public.source_documents USING btree (business_unit_id, corpus_id);


--
-- Name: ix_source_documents_tenant_purge_state; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_documents_tenant_purge_state ON public.source_documents USING btree (business_unit_id, purge_state) WHERE ((purge_state)::text <> 'Present'::text);


--
-- Name: ix_source_documents_tenant_security; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_source_documents_tenant_security ON public.source_documents USING btree (business_unit_id, security_status);


--
-- Name: ix_validation_findings_tenant_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_validation_findings_tenant_code ON public.validation_findings USING btree (business_unit_id, code);


--
-- Name: ix_validation_findings_tenant_run_severity; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX IF NOT EXISTS ix_validation_findings_tenant_run_severity ON public.validation_findings USING btree (business_unit_id, extraction_run_id, severity);


--
-- Name: ux_canonical_inquiries_corpus_number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_canonical_inquiries_corpus_number ON public.canonical_inquiries USING btree (corpus_id, inquiry_number);


--
-- Name: ux_canonical_line_items_inquiry_line; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_canonical_line_items_inquiry_line ON public.canonical_line_items USING btree (inquiry_id, line_number);


--
-- Name: ux_commercial_document_classifications_tenant_document; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_commercial_document_classifications_tenant_document ON public.commercial_document_classifications USING btree (business_unit_id, source_document_id);


--
-- Name: ux_commercial_document_classifications_tenant_idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_commercial_document_classifications_tenant_idempotency ON public.commercial_document_classifications USING btree (business_unit_id, idempotency_key);


--
-- Name: ux_document_corpora_tenant_batch; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_document_corpora_tenant_batch ON public.document_corpora USING btree (business_unit_id, batch_id);


--
-- Name: ux_document_pages_document_number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_document_pages_document_number ON public.document_pages USING btree (document_id, page_number);


--
-- Name: ux_extraction_runs_tenant_job_attempt; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_extraction_runs_tenant_job_attempt ON public.extraction_runs USING btree (business_unit_id, extraction_job_id, attempt_number);


--
-- Name: ux_field_evidence_tenant_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_field_evidence_tenant_key ON public.field_evidence USING btree (business_unit_id, evidence_key);


--
-- Name: ux_source_document_occurrences_tenant_idempotency; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_source_document_occurrences_tenant_idempotency ON public.source_document_occurrences USING btree (business_unit_id, idempotency_key);


--
-- Name: ux_source_documents_object_version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_source_documents_object_version ON public.source_documents USING btree (business_unit_id, object_bucket, object_key, object_version);


--
-- Name: ux_source_documents_tenant_hash; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX IF NOT EXISTS ux_source_documents_tenant_hash ON public.source_documents USING btree (business_unit_id, content_hash);

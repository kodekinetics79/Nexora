-- ==========================================================================
-- Triggers (incl. ENABLE ALWAYS)
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
-- ==========================================================================

--
-- Name: AccountingOutbox accounting_outbox_guard; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER accounting_outbox_guard BEFORE DELETE OR UPDATE ON platform."AccountingOutbox" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_accounting_outbox();

ALTER TABLE platform."AccountingOutbox" ENABLE ALWAYS TRIGGER accounting_outbox_guard;


--
-- Name: BillingStatementLines billing_statement_lines_guard_write; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER billing_statement_lines_guard_write BEFORE INSERT OR DELETE OR UPDATE ON platform."BillingStatementLines" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_line_mutation();

ALTER TABLE platform."BillingStatementLines" ENABLE ALWAYS TRIGGER billing_statement_lines_guard_write;


--
-- Name: BillingStatements billing_statements_guard_delete; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER billing_statements_guard_delete BEFORE DELETE ON platform."BillingStatements" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_mutation();

ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_delete;


--
-- Name: BillingStatements billing_statements_guard_update; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER billing_statements_guard_update BEFORE UPDATE ON platform."BillingStatements" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_mutation();

ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_update;


--
-- Name: PlatformAuditLogs platform_ai_policy_audits_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER platform_ai_policy_audits_immutable BEFORE DELETE OR UPDATE ON platform."PlatformAuditLogs" FOR EACH ROW WHEN (((old."Action")::text = 'tenant.ai-policy.update'::text)) EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();

ALTER TABLE platform."PlatformAuditLogs" ENABLE ALWAYS TRIGGER platform_ai_policy_audits_immutable;


--
-- Name: PlatformAuditLogs platform_audit_logs_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER platform_audit_logs_append_only BEFORE DELETE OR UPDATE ON platform."PlatformAuditLogs" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."PlatformAuditLogs" ENABLE ALWAYS TRIGGER platform_audit_logs_append_only;


--
-- Name: PlatformAuditLogs platform_audit_logs_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER platform_audit_logs_no_truncate BEFORE TRUNCATE ON platform."PlatformAuditLogs" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."PlatformAuditLogs" ENABLE ALWAYS TRIGGER platform_audit_logs_no_truncate;


--
-- Name: ProvisioningExecutions provisioning_executions_lease_transfer_guard; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER provisioning_executions_lease_transfer_guard BEFORE UPDATE ON platform."ProvisioningExecutions" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_provisioning_lease_transfer();

ALTER TABLE platform."ProvisioningExecutions" ENABLE ALWAYS TRIGGER provisioning_executions_lease_transfer_guard;


--
-- Name: SubscriptionRevenueActions subscription_action_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE CONSTRAINT TRIGGER subscription_action_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionRevenueActions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();

ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ALWAYS TRIGGER subscription_action_rollups_reconcile;


--
-- Name: SubscriptionCreditNotes subscription_credit_notes_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER subscription_credit_notes_immutable BEFORE DELETE OR UPDATE ON platform."SubscriptionCreditNotes" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."SubscriptionCreditNotes" ENABLE ALWAYS TRIGGER subscription_credit_notes_immutable;


--
-- Name: SubscriptionCreditNotes subscription_credit_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE CONSTRAINT TRIGGER subscription_credit_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionCreditNotes" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();

ALTER TABLE platform."SubscriptionCreditNotes" ENABLE ALWAYS TRIGGER subscription_credit_rollups_reconcile;


--
-- Name: SubscriptionInvoices subscription_invoice_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE CONSTRAINT TRIGGER subscription_invoice_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionInvoices" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();

ALTER TABLE platform."SubscriptionInvoices" ENABLE ALWAYS TRIGGER subscription_invoice_rollups_reconcile;


--
-- Name: SubscriptionInvoices subscription_invoices_guard; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER subscription_invoices_guard BEFORE DELETE OR UPDATE ON platform."SubscriptionInvoices" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_invoice();

ALTER TABLE platform."SubscriptionInvoices" ENABLE ALWAYS TRIGGER subscription_invoices_guard;


--
-- Name: SubscriptionPayments subscription_payment_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE CONSTRAINT TRIGGER subscription_payment_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionPayments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();

ALTER TABLE platform."SubscriptionPayments" ENABLE ALWAYS TRIGGER subscription_payment_rollups_reconcile;


--
-- Name: SubscriptionPayments subscription_payments_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER subscription_payments_immutable BEFORE DELETE OR UPDATE ON platform."SubscriptionPayments" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."SubscriptionPayments" ENABLE ALWAYS TRIGGER subscription_payments_immutable;


--
-- Name: SubscriptionRevenueActions subscription_revenue_actions_guard; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER subscription_revenue_actions_guard BEFORE DELETE OR UPDATE ON platform."SubscriptionRevenueActions" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_revenue_action();

ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ALWAYS TRIGGER subscription_revenue_actions_guard;


--
-- Name: SubscriptionTaxRules subscription_tax_rules_guard; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER subscription_tax_rules_guard BEFORE DELETE OR UPDATE ON platform."SubscriptionTaxRules" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_tax_rule();

ALTER TABLE platform."SubscriptionTaxRules" ENABLE ALWAYS TRIGGER subscription_tax_rules_guard;


--
-- Name: TenantDataRecoveryEvidence tenant_data_recovery_evidence_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_data_recovery_evidence_immutable BEFORE DELETE OR UPDATE ON platform."TenantDataRecoveryEvidence" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantDataRecoveryEvidence" ENABLE ALWAYS TRIGGER tenant_data_recovery_evidence_immutable;


--
-- Name: TenantDeletionCertificates tenant_deletion_certificates_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_deletion_certificates_immutable BEFORE DELETE OR UPDATE ON platform."TenantDeletionCertificates" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantDeletionCertificates" ENABLE ALWAYS TRIGGER tenant_deletion_certificates_immutable;


--
-- Name: TenantExportReceipts tenant_export_receipts_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_export_receipts_append_only BEFORE DELETE OR UPDATE ON platform."TenantExportReceipts" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantExportReceipts" ENABLE ALWAYS TRIGGER tenant_export_receipts_append_only;


--
-- Name: TenantExportReceipts tenant_export_receipts_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_export_receipts_no_truncate BEFORE TRUNCATE ON platform."TenantExportReceipts" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantExportReceipts" ENABLE ALWAYS TRIGGER tenant_export_receipts_no_truncate;


--
-- Name: TenantLegalHolds tenant_legal_holds_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_legal_holds_immutable BEFORE DELETE OR UPDATE ON platform."TenantLegalHolds" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_tenant_legal_hold();

ALTER TABLE platform."TenantLegalHolds" ENABLE ALWAYS TRIGGER tenant_legal_holds_immutable;


--
-- Name: TenantLegalHolds tenant_legal_holds_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_legal_holds_no_truncate BEFORE TRUNCATE ON platform."TenantLegalHolds" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantLegalHolds" ENABLE ALWAYS TRIGGER tenant_legal_holds_no_truncate;


--
-- Name: TenantLifecycleEvents tenant_lifecycle_events_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_lifecycle_events_append_only BEFORE DELETE OR UPDATE ON platform."TenantLifecycleEvents" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantLifecycleEvents" ENABLE ALWAYS TRIGGER tenant_lifecycle_events_append_only;


--
-- Name: TenantLifecycleEvents tenant_lifecycle_events_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_lifecycle_events_no_truncate BEFORE TRUNCATE ON platform."TenantLifecycleEvents" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantLifecycleEvents" ENABLE ALWAYS TRIGGER tenant_lifecycle_events_no_truncate;


--
-- Name: TenantOffboardings tenant_offboardings_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_offboardings_append_only BEFORE DELETE ON platform."TenantOffboardings" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantOffboardings" ENABLE ALWAYS TRIGGER tenant_offboardings_append_only;


--
-- Name: TenantOffboardings tenant_offboardings_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenant_offboardings_no_truncate BEFORE TRUNCATE ON platform."TenantOffboardings" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."TenantOffboardings" ENABLE ALWAYS TRIGGER tenant_offboardings_no_truncate;


--
-- Name: Tenants tenants_seed_meter_source_policies; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER tenants_seed_meter_source_policies AFTER INSERT ON platform."Tenants" FOR EACH ROW EXECUTE FUNCTION platform.nexora_seed_tenant_meter_source_policies();

ALTER TABLE platform."Tenants" ENABLE ALWAYS TRIGGER tenants_seed_meter_source_policies;


--
-- Name: UsageCoverageSegments usage_coverage_segments_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER usage_coverage_segments_immutable BEFORE DELETE OR UPDATE ON platform."UsageCoverageSegments" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."UsageCoverageSegments" ENABLE ALWAYS TRIGGER usage_coverage_segments_immutable;


--
-- Name: UsageEventRatings usage_event_ratings_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER usage_event_ratings_immutable BEFORE DELETE OR UPDATE ON platform."UsageEventRatings" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."UsageEventRatings" ENABLE ALWAYS TRIGGER usage_event_ratings_immutable;


--
-- Name: UsageEvents usage_events_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER usage_events_immutable BEFORE DELETE OR UPDATE ON platform."UsageEvents" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();

ALTER TABLE platform."UsageEvents" ENABLE ALWAYS TRIGGER usage_events_immutable;


--
-- Name: UsageEvents usage_events_insert_guard; Type: TRIGGER; Schema: platform; Owner: -
--

CREATE TRIGGER usage_events_insert_guard BEFORE INSERT ON platform."UsageEvents" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_usage_event_insert();

ALTER TABLE platform."UsageEvents" ENABLE ALWAYS TRIGGER usage_events_insert_guard;


--
-- Name: CommercialCases TR_CommercialCases_ProtectIdentity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CommercialCases_ProtectIdentity" BEFORE DELETE OR UPDATE OF "AllocationNumber", "MasterReference", "BusinessUnitID" ON public."CommercialCases" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_commercial_identity();


--
-- Name: commercial_lifecycle_events TR_CommercialLifecycleEvents_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CommercialLifecycleEvents_Immutable" BEFORE DELETE OR UPDATE ON public.commercial_lifecycle_events FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_commercial_lifecycle_event();


--
-- Name: commercial_lifecycle_events TR_CommercialLifecycleEvents_NoTruncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CommercialLifecycleEvents_NoTruncate" BEFORE TRUNCATE ON public.commercial_lifecycle_events FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_protect_commercial_lifecycle_event();


--
-- Name: custom_field_definitions TR_CustomFieldDefinitions_NoDelete; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldDefinitions_NoDelete" BEFORE DELETE ON public.custom_field_definitions FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: custom_field_dependencies TR_CustomFieldDependencies_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldDependencies_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_dependencies FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: custom_field_options TR_CustomFieldOptions_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldOptions_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_options FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: custom_field_records TR_CustomFieldRecords_NoDelete; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldRecords_NoDelete" BEFORE DELETE ON public.custom_field_records FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: custom_field_rules TR_CustomFieldRules_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldRules_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_rules FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: custom_field_values TR_CustomFieldValues_NoDelete; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldValues_NoDelete" BEFORE DELETE ON public.custom_field_values FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: custom_field_versions TR_CustomFieldVersions_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_CustomFieldVersions_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_versions FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();


--
-- Name: LeadStatusHistories TR_LeadStatusHistories_AppendOnly; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_LeadStatusHistories_AppendOnly" BEFORE DELETE OR UPDATE ON public."LeadStatusHistories" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_lead_status_history();


--
-- Name: Leads TR_Leads_AssignCommercialCase; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Leads_AssignCommercialCase" BEFORE INSERT ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_assign_commercial_case();


--
-- Name: Leads TR_Leads_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Leads_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CustomerID", "ContactID" ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_lead_commercial_identity();


--
-- Name: Leads TR_Leads_ProtectCommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Leads_ProtectCommercialIdentity" BEFORE UPDATE OF "CommercialCaseId", "CommercialCaseReference", "BusinessUnitID" ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_commercial_identity();


--
-- Name: Leads TR_Leads_RecordStatusHistory; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Leads_RecordStatusHistory" AFTER INSERT OR UPDATE OF "LeadStatusId" ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_record_lead_status_history();


--
-- Name: Leads TR_Leads_RequireLifecycleCommand; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Leads_RequireLifecycleCommand" BEFORE UPDATE OF "LeadStatusId" ON public."Leads" FOR EACH ROW WHEN ((old."LeadStatusId" IS DISTINCT FROM new."LeadStatusId")) EXECUTE FUNCTION public.nexora_require_lifecycle_command();


--
-- Name: Orders TR_Orders_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Orders_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID", "QuoteID" ON public."Orders" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_order_commercial_identity();


--
-- Name: Quotes TR_Quotes_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Quotes_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID" ON public."Quotes" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_downstream_commercial_identity();


--
-- Name: Quotes TR_Quotes_RequireLifecycleCommand; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_Quotes_RequireLifecycleCommand" BEFORE UPDATE OF "StatusID" ON public."Quotes" FOR EACH ROW WHEN ((old."StatusID" IS DISTINCT FROM new."StatusID")) EXECUTE FUNCTION public.nexora_require_lifecycle_command();


--
-- Name: RFQ TR_RFQ_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_RFQ_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID" ON public."RFQ" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_downstream_commercial_identity();


--
-- Name: RFQ TR_RFQ_RequireLifecycleCommand; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER "TR_RFQ_RequireLifecycleCommand" BEFORE UPDATE OF "RFQStatusID" ON public."RFQ" FOR EACH ROW WHEN ((old."RFQStatusID" IS DISTINCT FROM new."RFQStatusID")) EXECUTE FUNCTION public.nexora_require_lifecycle_command();


--
-- Name: AiCallAttempts ai_call_attempts_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER ai_call_attempts_immutable BEFORE DELETE OR UPDATE ON public."AiCallAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();


--
-- Name: AiCallAttempts ai_call_attempts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER ai_call_attempts_reject_truncate BEFORE TRUNCATE ON public."AiCallAttempts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();


--
-- Name: AiRequests ai_requests_guard_update; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER ai_requests_guard_update BEFORE UPDATE ON public."AiRequests" FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_ai_request_update();


--
-- Name: AiRequests ai_requests_reject_delete; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER ai_requests_reject_delete BEFORE DELETE ON public."AiRequests" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();


--
-- Name: AiRequests ai_requests_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER ai_requests_reject_truncate BEFORE TRUNCATE ON public."AiRequests" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();


--
-- Name: BankAccounts bankaccounts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankaccounts_reject_truncate BEFORE TRUNCATE ON public."BankAccounts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankAdjustmentDistributions bankadjustmentdistributions_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankadjustmentdistributions_reject_truncate BEFORE TRUNCATE ON public."BankAdjustmentDistributions" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankAdjustments bankadjustments_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankadjustments_reject_truncate BEFORE TRUNCATE ON public."BankAdjustments" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankMatchingRules bankmatchingrules_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankmatchingrules_reject_truncate BEFORE TRUNCATE ON public."BankMatchingRules" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankStatementImports bankstatementimports_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankstatementimports_reject_truncate BEFORE TRUNCATE ON public."BankStatementImports" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankStatementLines bankstatementlines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankstatementlines_reject_truncate BEFORE TRUNCATE ON public."BankStatementLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankStatements bankstatements_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER bankstatements_reject_truncate BEFORE TRUNCATE ON public."BankStatements" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BusinessUnits business_units_create_ai_policy; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER business_units_create_ai_policy AFTER INSERT ON public."BusinessUnits" FOR EACH ROW EXECUTE FUNCTION public.nexora_create_default_ai_policy();


--
-- Name: commercial_activities commercial_activities_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER commercial_activities_immutable BEFORE DELETE OR UPDATE ON public.commercial_activities FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();


--
-- Name: commercial_demand_lines commercial_demand_lines_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER commercial_demand_lines_immutable BEFORE DELETE OR UPDATE ON public.commercial_demand_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_demand_line_mutation();


--
-- Name: commercial_document_classifications commercial_document_classifications_source_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER commercial_document_classifications_source_immutable BEFORE UPDATE ON public.commercial_document_classifications FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_classification_source_mutation();


--
-- Name: lead_line_commercial_resolutions commercial_line_resolution_delete_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER commercial_line_resolution_delete_guard BEFORE DELETE ON public.lead_line_commercial_resolutions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();


--
-- Name: lead_line_commercial_resolutions commercial_line_resolution_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER commercial_line_resolution_tenant_integrity BEFORE INSERT OR UPDATE ON public.lead_line_commercial_resolutions FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_commercial_line_resolution();


--
-- Name: lead_line_commercial_resolutions commercial_line_resolution_update_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER commercial_line_resolution_update_guard BEFORE UPDATE ON public.lead_line_commercial_resolutions FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_line_resolution_update();


--
-- Name: customer_quote_sourcing_decisions customer_quote_sourcing_decisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER customer_quote_sourcing_decisions_append_only BEFORE DELETE OR UPDATE ON public.customer_quote_sourcing_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();


--
-- Name: extraction_dead_letter_events extraction_dead_letter_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER extraction_dead_letter_events_append_only BEFORE DELETE OR UPDATE ON public.extraction_dead_letter_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_extraction_dead_letter_event_mutation();


--
-- Name: follow_up_transition_events follow_up_transition_events_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER follow_up_transition_events_immutable BEFORE DELETE OR UPDATE ON public.follow_up_transition_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();


--
-- Name: governed_artifact_events governed_artifact_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER governed_artifact_events_append_only BEFORE DELETE OR UPDATE ON public.governed_artifact_events FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();


--
-- Name: human_action_events human_action_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER human_action_events_append_only BEFORE DELETE OR UPDATE ON public.human_action_events FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();


--
-- Name: incoming_inventory incoming_inventory_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER incoming_inventory_tenant_integrity BEFORE INSERT OR UPDATE ON public.incoming_inventory FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();


--
-- Name: inventory_movements inventory_movements_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER inventory_movements_immutable BEFORE DELETE OR UPDATE ON public.inventory_movements FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();


--
-- Name: inventory_movements inventory_movements_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER inventory_movements_tenant_integrity BEFORE INSERT ON public.inventory_movements FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();


--
-- Name: Inventory inventory_procurement_tenant_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER inventory_procurement_tenant_immutable BEFORE UPDATE OF "Buid" ON public."Inventory" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_referenced_inventory_tenant_change();


--
-- Name: Inventory inventory_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER inventory_tenant_integrity BEFORE INSERT OR UPDATE OF "Buid", "ProductId" ON public."Inventory" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();


--
-- Name: Inventory inventory_warehouse_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER inventory_warehouse_tenant_integrity BEFORE INSERT OR UPDATE OF "Buid", "WarehouseId" ON public."Inventory" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_warehouse_tenant();


--
-- Name: LeadReviewAudits lead_review_audits_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER lead_review_audits_immutable BEFORE DELETE OR UPDATE ON public."LeadReviewAudits" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_lead_review_audit_mutation();


--
-- Name: LeadReviewAudits lead_review_audits_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER lead_review_audits_reject_truncate BEFORE TRUNCATE ON public."LeadReviewAudits" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_lead_review_audit_mutation();


--
-- Name: lead_routing_decisions lead_routing_decisions_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER lead_routing_decisions_immutable BEFORE DELETE OR UPDATE ON public.lead_routing_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_routing_decision_mutation();


--
-- Name: learning_governance_events learning_governance_events_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER learning_governance_events_immutable BEFORE DELETE OR UPDATE ON public.learning_governance_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_learning_governance_mutation();


--
-- Name: learning_governance_events learning_governance_events_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER learning_governance_events_reject_truncate BEFORE TRUNCATE ON public.learning_governance_events FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_learning_governance_mutation();


--
-- Name: learning_governance_events learning_governance_events_validate_insert; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER learning_governance_events_validate_insert BEFORE INSERT ON public.learning_governance_events FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_learning_governance_insert();


--
-- Name: procurement_events procurement_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER procurement_events_append_only BEFORE DELETE OR UPDATE ON public.procurement_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_procurement_event_mutation();


--
-- Name: product_aliases product_aliases_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER product_aliases_tenant_integrity BEFORE INSERT OR UPDATE ON public.product_aliases FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();


--
-- Name: Products product_procurement_tenant_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER product_procurement_tenant_immutable BEFORE UPDATE OF "BUID" ON public."Products" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_referenced_product_tenant_change();


--
-- Name: product_supersessions product_supersessions_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER product_supersessions_tenant_integrity BEFORE INSERT OR UPDATE ON public.product_supersessions FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();


--
-- Name: quote_delivery_requests quote_delivery_delete_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER quote_delivery_delete_guard BEFORE DELETE ON public.quote_delivery_requests FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_quote_delivery_mutation();


--
-- Name: quote_delivery_requests quote_delivery_update_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER quote_delivery_update_guard BEFORE UPDATE ON public.quote_delivery_requests FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_quote_delivery_mutation();


--
-- Name: ReconciliationAllocations reconciliationallocations_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER reconciliationallocations_reject_truncate BEFORE TRUNCATE ON public."ReconciliationAllocations" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReconciliationMatches reconciliationmatches_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER reconciliationmatches_reject_truncate BEFORE TRUNCATE ON public."ReconciliationMatches" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReconciliationRunRules reconciliationrunrules_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER reconciliationrunrules_reject_truncate BEFORE TRUNCATE ON public."ReconciliationRunRules" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReconciliationRuns reconciliationruns_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER reconciliationruns_reject_truncate BEFORE TRUNCATE ON public."ReconciliationRuns" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: sales_coaching_acknowledgements sales_coaching_acknowledgements_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER sales_coaching_acknowledgements_append_only BEFORE DELETE OR UPDATE ON public.sales_coaching_acknowledgements FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_coaching_ack_mutation();


--
-- Name: sales_coaching_acknowledgements sales_coaching_acknowledgements_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER sales_coaching_acknowledgements_reject_truncate BEFORE TRUNCATE ON public.sales_coaching_acknowledgements FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_sales_coaching_ack_mutation();


--
-- Name: sales_contributions sales_contributions_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER sales_contributions_immutable BEFORE DELETE OR UPDATE ON public.sales_contributions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();


--
-- Name: sourcing_cases sourcing_cases_lineage_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER sourcing_cases_lineage_immutable BEFORE UPDATE ON public.sourcing_cases FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sourcing_case_lineage_mutation();


--
-- Name: supplier_negotiation_decisions supplier_negotiation_decisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_negotiation_decisions_append_only BEFORE DELETE OR UPDATE ON public.supplier_negotiation_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_negotiation_decision_mutation();


--
-- Name: supplier_negotiation_decisions supplier_negotiation_decisions_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_negotiation_decisions_reject_truncate BEFORE TRUNCATE ON public.supplier_negotiation_decisions FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_supplier_negotiation_decision_mutation();


--
-- Name: supplier_purchase_order_lines supplier_po_line_inventory_tenant; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_po_line_inventory_tenant BEFORE INSERT OR UPDATE OF "InventoryId", "BusinessUnitId" ON public.supplier_purchase_order_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_procurement_inventory_tenant();


--
-- Name: supplier_purchase_order_lines supplier_po_line_product_tenant; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_po_line_product_tenant BEFORE INSERT OR UPDATE OF "ProductId", "BusinessUnitId" ON public.supplier_purchase_order_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_procurement_product_tenant();


--
-- Name: supplier_quote_field_evidence supplier_quote_field_evidence_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quote_field_evidence_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_field_evidence FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();


--
-- Name: supplier_quote_lines supplier_quote_lines_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quote_lines_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();


--
-- Name: SupplierQuotedItems supplier_quote_product_tenant; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quote_product_tenant BEFORE INSERT OR UPDATE OF "ProductId", "BusinessUnitId" ON public."SupplierQuotedItems" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_procurement_product_tenant();


--
-- Name: supplier_quote_review_decisions supplier_quote_review_decisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quote_review_decisions_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_review_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();


--
-- Name: supplier_quote_revisions supplier_quote_revisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quote_revisions_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_revisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();


--
-- Name: SupplierQuotedItems supplier_quoted_items_projected_lineage_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quoted_items_projected_lineage_immutable BEFORE UPDATE ON public."SupplierQuotedItems" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_projected_supplier_quote_lineage();


--
-- Name: supplier_quotes supplier_quotes_lineage_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_quotes_lineage_immutable BEFORE UPDATE ON public.supplier_quotes FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_supplier_quote_lineage();


--
-- Name: SupplierSolicitations supplier_solicitations_commercial_lineage_write_once; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER supplier_solicitations_commercial_lineage_write_once BEFORE UPDATE ON public."SupplierSolicitations" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_supplier_rfq_lineage();


--
-- Name: tenant_governance_audit_events tenant_governance_audit_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER tenant_governance_audit_events_append_only BEFORE DELETE OR UPDATE ON public.tenant_governance_audit_events FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();


--
-- Name: AccountingPeriods trg_accountingperiods_book; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_accountingperiods_book BEFORE INSERT ON public."AccountingPeriods" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();


--
-- Name: AccountingPeriods trg_accountingperiods_certification; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_accountingperiods_certification BEFORE INSERT OR UPDATE ON public."AccountingPeriods" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_certify_period_close();


--
-- Name: AccountingPeriods trg_accountingperiods_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_accountingperiods_evidence AFTER INSERT OR UPDATE ON public."AccountingPeriods" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();


--
-- Name: AccountingPeriods trg_accountingperiods_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_accountingperiods_guard BEFORE INSERT OR DELETE OR UPDATE ON public."AccountingPeriods" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_period();


--
-- Name: AccountingPeriods trg_accountingperiods_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_accountingperiods_reject_truncate BEFORE TRUNCATE ON public."AccountingPeriods" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: BankAccounts trg_bankaccounts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_bankaccounts_evidence AFTER INSERT OR UPDATE ON public."BankAccounts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();


--
-- Name: BankAccounts trg_bankaccounts_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankaccounts_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankAccounts" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_account();


--
-- Name: BankAdjustmentDistributions trg_bankadjustmentdistributions_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankadjustmentdistributions_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankAdjustmentDistributions" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_distribution();


--
-- Name: BankAdjustments trg_bankadjustments_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_bankadjustments_evidence AFTER INSERT OR UPDATE ON public."BankAdjustments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();


--
-- Name: BankAdjustments trg_bankadjustments_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankadjustments_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankAdjustments" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_adjustment();


--
-- Name: BankAdjustments trg_bankadjustments_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_bankadjustments_validate AFTER INSERT OR UPDATE ON public."BankAdjustments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_adjustment();


--
-- Name: BankStatementImports trg_bankimports_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_bankimports_evidence AFTER INSERT ON public."BankStatementImports" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();


--
-- Name: BankStatementImports trg_bankimports_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankimports_immutable BEFORE DELETE OR UPDATE ON public."BankStatementImports" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();


--
-- Name: BankStatementImports trg_bankimports_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankimports_validate BEFORE INSERT ON public."BankStatementImports" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_import();


--
-- Name: BankStatementLines trg_banklines_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_banklines_immutable BEFORE DELETE OR UPDATE ON public."BankStatementLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();


--
-- Name: BankMatchingRules trg_bankmatchingrules_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_bankmatchingrules_evidence AFTER INSERT OR UPDATE ON public."BankMatchingRules" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();


--
-- Name: BankMatchingRules trg_bankmatchingrules_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankmatchingrules_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankMatchingRules" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_rule();


--
-- Name: BankStatements trg_bankstatements_balance; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_bankstatements_balance AFTER INSERT ON public."BankStatements" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_validate_statement();


--
-- Name: BankStatements trg_bankstatements_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bankstatements_immutable BEFORE DELETE OR UPDATE ON public."BankStatements" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();


--
-- Name: canonical_inquiries trg_canonical_inquiries_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_canonical_inquiries_append_only BEFORE DELETE OR UPDATE ON public.canonical_inquiries FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: canonical_line_items trg_canonical_line_items_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_canonical_line_items_append_only BEFORE DELETE OR UPDATE ON public.canonical_line_items FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: CollectionControls trg_collectioncontrols_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_collectioncontrols_evidence AFTER INSERT OR UPDATE ON public."CollectionControls" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: CollectionControls trg_collectioncontrols_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_collectioncontrols_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CollectionControls" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: CollectionControls trg_collectioncontrols_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_collectioncontrols_reject_truncate BEFORE TRUNCATE ON public."CollectionControls" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CollectionControls trg_collectioncontrols_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_collectioncontrols_tenant_reference BEFORE INSERT OR UPDATE ON public."CollectionControls" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: commercial_exception_events trg_commercial_exception_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_commercial_exception_events_append_only BEFORE DELETE OR UPDATE ON public.commercial_exception_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_exception_event_mutation();


--
-- Name: commercial_exception_operations trg_commercial_exception_operations_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_commercial_exception_operations_append_only BEFORE DELETE OR UPDATE ON public.commercial_exception_operations FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_exception_operation_mutation();


--
-- Name: CommercialFinanceAudits trg_commercial_finance_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_commercial_finance_audit_append_only BEFORE DELETE OR UPDATE ON public."CommercialFinanceAudits" FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_audit_append_only();


--
-- Name: CommercialFinanceAudits trg_commercial_finance_audits_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_commercial_finance_audits_reject_truncate BEFORE TRUNCATE ON public."CommercialFinanceAudits" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerPayments trg_customer_payments_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customer_payments_reject_truncate BEFORE TRUNCATE ON public."CustomerPayments" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerRefunds trg_customer_refunds_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customer_refunds_reject_truncate BEFORE TRUNCATE ON public."CustomerRefunds" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_customercollectionprofiles_evidence AFTER INSERT OR UPDATE ON public."CustomerCollectionProfiles" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customercollectionprofiles_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerCollectionProfiles" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customercollectionprofiles_reject_truncate BEFORE TRUNCATE ON public."CustomerCollectionProfiles" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customercollectionprofiles_tenant_reference BEFORE INSERT OR UPDATE ON public."CustomerCollectionProfiles" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: CustomerPayments trg_customerpayments_cash_bridge; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_customerpayments_cash_bridge AFTER INSERT OR UPDATE ON public."CustomerPayments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_cash_bridge();


--
-- Name: CustomerPayments trg_customerpayments_protect_kept_promise; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerpayments_protect_kept_promise BEFORE UPDATE ON public."CustomerPayments" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_reconcile_kept_promise_payment();


--
-- Name: CustomerRefunds trg_customerrefunds_cash_bridge; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_customerrefunds_cash_bridge AFTER INSERT OR UPDATE ON public."CustomerRefunds" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_cash_bridge();


--
-- Name: CustomerRefunds trg_customerrefunds_protect_kept_promise; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerrefunds_protect_kept_promise BEFORE UPDATE ON public."CustomerRefunds" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_reconcile_kept_promise_payment();


--
-- Name: CustomerStatementLines trg_customerstatementlines_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_customerstatementlines_evidence AFTER INSERT OR UPDATE ON public."CustomerStatementLines" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: CustomerStatementLines trg_customerstatementlines_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerstatementlines_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerStatementLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: CustomerStatementLines trg_customerstatementlines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerstatementlines_reject_truncate BEFORE TRUNCATE ON public."CustomerStatementLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerStatementLines trg_customerstatementlines_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerstatementlines_tenant_reference BEFORE INSERT OR UPDATE ON public."CustomerStatementLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: CustomerStatements trg_customerstatements_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_customerstatements_evidence AFTER INSERT OR UPDATE ON public."CustomerStatements" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: CustomerStatements trg_customerstatements_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerstatements_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerStatements" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: CustomerStatements trg_customerstatements_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerstatements_reject_truncate BEFORE TRUNCATE ON public."CustomerStatements" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerStatements trg_customerstatements_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_customerstatements_tenant_reference BEFORE INSERT OR UPDATE ON public."CustomerStatements" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: document_corpora trg_document_corpora_no_delete; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_document_corpora_no_delete BEFORE DELETE ON public.document_corpora FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: document_pages trg_document_pages_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_document_pages_append_only BEFORE DELETE OR UPDATE ON public.document_pages FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: document_regions trg_document_regions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_document_regions_append_only BEFORE DELETE OR UPDATE ON public.document_regions FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: DunningCases trg_dunningcases_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningcases_evidence AFTER INSERT OR UPDATE ON public."DunningCases" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningCases trg_dunningcases_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningcases_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningCases" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningCases trg_dunningcases_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningcases_reject_truncate BEFORE TRUNCATE ON public."DunningCases" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningCases trg_dunningcases_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningcases_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningCases" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningdeliveryattempts_evidence AFTER INSERT OR UPDATE ON public."DunningDeliveryAttempts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningdeliveryattempts_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningDeliveryAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningdeliveryattempts_reject_truncate BEFORE TRUNCATE ON public."DunningDeliveryAttempts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningdeliveryattempts_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningDeliveryAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_verify_provider; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningdeliveryattempts_verify_provider BEFORE INSERT ON public."DunningDeliveryAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_verify_provider_evidence();


--
-- Name: DunningNotices trg_dunningnotices_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningnotices_evidence AFTER INSERT OR UPDATE ON public."DunningNotices" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningNotices trg_dunningnotices_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningnotices_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningNotices" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningNotices trg_dunningnotices_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningnotices_reject_truncate BEFORE TRUNCATE ON public."DunningNotices" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningNotices trg_dunningnotices_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningnotices_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningNotices" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: DunningPolicies trg_dunningpolicies_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningpolicies_evidence AFTER INSERT OR UPDATE ON public."DunningPolicies" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningPolicies trg_dunningpolicies_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningpolicies_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningPolicies" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningPolicies trg_dunningpolicies_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningpolicies_reject_truncate BEFORE TRUNCATE ON public."DunningPolicies" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningPolicies trg_dunningpolicies_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningpolicies_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningPolicies" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: DunningPolicySteps trg_dunningpolicysteps_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningpolicysteps_evidence AFTER INSERT OR UPDATE ON public."DunningPolicySteps" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningPolicySteps trg_dunningpolicysteps_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningpolicysteps_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningPolicySteps" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningPolicySteps trg_dunningpolicysteps_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningpolicysteps_reject_truncate BEFORE TRUNCATE ON public."DunningPolicySteps" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningPolicySteps trg_dunningpolicysteps_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningpolicysteps_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningPolicySteps" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: DunningRunDecisions trg_dunningrundecisions_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningrundecisions_evidence AFTER INSERT OR UPDATE ON public."DunningRunDecisions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningRunDecisions trg_dunningrundecisions_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningrundecisions_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningRunDecisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningRunDecisions trg_dunningrundecisions_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningrundecisions_reject_truncate BEFORE TRUNCATE ON public."DunningRunDecisions" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningRunDecisions trg_dunningrundecisions_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningrundecisions_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningRunDecisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: DunningRunDecisions trg_dunningrundecisions_verify_profile; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningrundecisions_verify_profile BEFORE INSERT OR UPDATE OF "CustomerCollectionProfileId", "DunningRunId", "CustomerId", "CurrencyId" ON public."DunningRunDecisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_verify_run_decision_profile();


--
-- Name: DunningRuns trg_dunningruns_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_dunningruns_evidence AFTER INSERT OR UPDATE ON public."DunningRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: DunningRuns trg_dunningruns_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningruns_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningRuns" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: DunningRuns trg_dunningruns_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningruns_reject_truncate BEFORE TRUNCATE ON public."DunningRuns" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: DunningRuns trg_dunningruns_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_dunningruns_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningRuns" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: extraction_runs trg_extraction_runs_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_extraction_runs_guard BEFORE DELETE OR UPDATE ON public.extraction_runs FOR EACH ROW EXECUTE FUNCTION public.nexora_extraction_run_guard();


--
-- Name: field_evidence trg_field_evidence_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_field_evidence_append_only BEFORE DELETE OR UPDATE ON public.field_evidence FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: FinanceOutboxMessages trg_finance_outbox_core_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_finance_outbox_core_immutable BEFORE DELETE OR UPDATE ON public."FinanceOutboxMessages" FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_outbox_core_immutable();


--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_financecommunicationcontacts_evidence AFTER INSERT OR UPDATE ON public."FinanceCommunicationContacts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_financecommunicationcontacts_governed BEFORE INSERT OR DELETE OR UPDATE ON public."FinanceCommunicationContacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_financecommunicationcontacts_reject_truncate BEFORE TRUNCATE ON public."FinanceCommunicationContacts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_financecommunicationcontacts_tenant_reference BEFORE INSERT OR UPDATE ON public."FinanceCommunicationContacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_verify_provider; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_financecommunicationcontacts_verify_provider BEFORE INSERT ON public."FinanceCommunicationContacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_verify_provider_evidence();


--
-- Name: commercial_exception_cases trg_guard_commercial_exception_case; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_guard_commercial_exception_case BEFORE INSERT OR UPDATE ON public.commercial_exception_cases FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_exception_case();


--
-- Name: commercial_exception_outbox trg_guard_commercial_exception_outbox; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_guard_commercial_exception_outbox BEFORE DELETE OR UPDATE ON public.commercial_exception_outbox FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_exception_outbox();


--
-- Name: commercial_opportunity_outbox trg_guard_opportunity_outbox; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_guard_opportunity_outbox BEFORE DELETE OR UPDATE ON public.commercial_opportunity_outbox FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_opportunity_outbox();


--
-- Name: JournalEntries trg_journalentries_book; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journalentries_book BEFORE INSERT OR UPDATE ON public."JournalEntries" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();


--
-- Name: JournalEntries trg_journalentries_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journalentries_evidence AFTER INSERT OR UPDATE ON public."JournalEntries" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();


--
-- Name: JournalEntries trg_journalentries_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journalentries_guard BEFORE INSERT OR DELETE OR UPDATE ON public."JournalEntries" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_journal();


--
-- Name: JournalEntries trg_journalentries_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journalentries_reject_truncate BEFORE TRUNCATE ON public."JournalEntries" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: JournalEntries trg_journalentries_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journalentries_validate AFTER UPDATE ON public."JournalEntries" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_validate_posting();


--
-- Name: JournalEntryLines trg_journalentrylines_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journalentrylines_guard BEFORE INSERT OR DELETE OR UPDATE ON public."JournalEntryLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_line();


--
-- Name: JournalEntryLines trg_journalentrylines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journalentrylines_reject_truncate BEFORE TRUNCATE ON public."JournalEntryLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: LeadIdentityAuditEvents trg_lead_identity_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_identity_audit_append_only BEFORE DELETE OR UPDATE ON public."LeadIdentityAuditEvents" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();


--
-- Name: LeadItemRevisions trg_lead_item_revisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_item_revisions_append_only BEFORE DELETE OR UPDATE ON public."LeadItemRevisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();


--
-- Name: LeadOccurrenceDocuments trg_lead_occurrence_documents_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_occurrence_documents_append_only BEFORE DELETE OR UPDATE ON public."LeadOccurrenceDocuments" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();


--
-- Name: LeadIngestionOccurrences trg_lead_occurrence_provenance_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_occurrence_provenance_guard BEFORE UPDATE ON public."LeadIngestionOccurrences" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_occurrence_guard();


--
-- Name: LeadRevisionDifferences trg_lead_revision_differences_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_revision_differences_append_only BEFORE DELETE OR UPDATE ON public."LeadRevisionDifferences" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();


--
-- Name: LeadRevisionImpacts trg_lead_revision_impacts_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_revision_impacts_append_only BEFORE DELETE OR UPDATE ON public."LeadRevisionImpacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();


--
-- Name: LeadRevisions trg_lead_revisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lead_revisions_append_only BEFORE DELETE OR UPDATE ON public."LeadRevisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();


--
-- Name: LedgerAccounts trg_ledgeraccounts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_ledgeraccounts_evidence AFTER INSERT OR UPDATE ON public."LedgerAccounts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();


--
-- Name: LedgerAccounts trg_ledgeraccounts_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_ledgeraccounts_guard BEFORE INSERT OR DELETE OR UPDATE ON public."LedgerAccounts" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_account();


--
-- Name: LedgerAccounts trg_ledgeraccounts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_ledgeraccounts_reject_truncate BEFORE TRUNCATE ON public."LedgerAccounts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: LedgerBooks trg_ledgerbooks_currency; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_ledgerbooks_currency BEFORE INSERT ON public."LedgerBooks" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();


--
-- Name: LedgerBooks trg_ledgerbooks_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_ledgerbooks_evidence AFTER INSERT ON public."LedgerBooks" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();


--
-- Name: LedgerBooks trg_ledgerbooks_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_ledgerbooks_guard BEFORE INSERT OR DELETE OR UPDATE ON public."LedgerBooks" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_book();


--
-- Name: LedgerBooks trg_ledgerbooks_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_ledgerbooks_reject_truncate BEFORE TRUNCATE ON public."LedgerBooks" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: LegalDocumentCounters trg_legal_document_counters_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_legal_document_counters_reject_truncate BEFORE TRUNCATE ON public."LegalDocumentCounters" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: MasterDataChangeEvents trg_master_data_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_master_data_audit_append_only BEFORE DELETE OR UPDATE ON public."MasterDataChangeEvents" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_master_data_audit_mutation();


--
-- Name: MasterDataFieldChanges trg_master_data_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_master_data_audit_append_only BEFORE DELETE OR UPDATE ON public."MasterDataFieldChanges" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_master_data_audit_mutation();


--
-- Name: commercial_opportunity_events trg_opportunity_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_opportunity_events_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();


--
-- Name: commercial_opportunity_feedback trg_opportunity_feedback_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_opportunity_feedback_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_feedback FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();


--
-- Name: commercial_opportunity_operations trg_opportunity_operations_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_opportunity_operations_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_operations FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();


--
-- Name: commercial_opportunity_outcomes trg_opportunity_outcomes_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_opportunity_outcomes_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_outcomes FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();


--
-- Name: commercial_opportunity_recommendations trg_opportunity_recommendations_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_opportunity_recommendations_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_recommendations FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();


--
-- Name: CustomerAwardLineAllocations trg_otc_allocation_delete_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_allocation_delete_guard BEFORE DELETE ON public."CustomerAwardLineAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_allocation_delete_guard();


--
-- Name: CustomerAwardLineAllocations trg_otc_allocation_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_allocation_validate BEFORE INSERT OR UPDATE ON public."CustomerAwardLineAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_allocation();


--
-- Name: OrderToCashAuditEvents trg_otc_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_audit_append_only BEFORE DELETE OR UPDATE ON public."OrderToCashAuditEvents" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_audit_append_only();


--
-- Name: CustomerAwards trg_otc_award_outbox; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_award_outbox AFTER INSERT OR UPDATE ON public."CustomerAwards" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();


--
-- Name: CustomerAwards trg_otc_award_transition_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_award_transition_guard BEFORE DELETE OR UPDATE ON public."CustomerAwards" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_award_transition_guard();


--
-- Name: CustomerAwards trg_otc_award_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_award_validate BEFORE INSERT OR UPDATE ON public."CustomerAwards" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_award();


--
-- Name: OrderItems trg_otc_order_item_source_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_order_item_source_guard BEFORE INSERT OR DELETE OR UPDATE ON public."OrderItems" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_order_item_source_guard();


--
-- Name: Orders trg_otc_order_outbox; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_order_outbox AFTER INSERT ON public."Orders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();


--
-- Name: Orders trg_otc_order_source_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_order_source_guard BEFORE INSERT OR DELETE OR UPDATE ON public."Orders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_order_source_guard();


--
-- Name: CustomerPurchaseOrderLines trg_otc_purchase_order_line_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_purchase_order_line_validate BEFORE INSERT OR UPDATE ON public."CustomerPurchaseOrderLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_purchase_order_line();


--
-- Name: CustomerPurchaseOrders trg_otc_purchase_order_outbox; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_purchase_order_outbox AFTER INSERT OR UPDATE ON public."CustomerPurchaseOrders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();


--
-- Name: CustomerPurchaseOrders trg_otc_purchase_order_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_otc_purchase_order_validate BEFORE INSERT OR UPDATE ON public."CustomerPurchaseOrders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_purchase_order();


--
-- Name: PaymentAllocations trg_payment_allocation_amount; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_payment_allocation_amount BEFORE INSERT OR UPDATE ON public."PaymentAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_allocation_valid();


--
-- Name: PaymentAllocations trg_payment_allocation_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_payment_allocation_append_only BEFORE DELETE OR UPDATE ON public."PaymentAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_audit_append_only();


--
-- Name: PaymentAllocations trg_payment_allocations_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_payment_allocations_reject_truncate BEFORE TRUNCATE ON public."PaymentAllocations" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: CustomerPayments trg_payment_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_payment_outbox_event AFTER INSERT OR UPDATE ON public."CustomerPayments" FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_outbox_event();


--
-- Name: CustomerPayments trg_payment_posted_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_payment_posted_immutable BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerPayments" FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();


--
-- Name: procurement_callback_receipts trg_procurement_callback_receipts_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_procurement_callback_receipts_append_only BEFORE DELETE OR UPDATE ON public.procurement_callback_receipts FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_procurement_callback_receipt();


--
-- Name: procurement_handoffs trg_procurement_handoffs_protect_lineage; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_procurement_handoffs_protect_lineage BEFORE DELETE OR UPDATE ON public.procurement_handoffs FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_procurement_handoff_lineage();


--
-- Name: PromisesToPay trg_promisestopay_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_promisestopay_evidence AFTER INSERT OR UPDATE ON public."PromisesToPay" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();


--
-- Name: PromisesToPay trg_promisestopay_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_promisestopay_governed BEFORE INSERT OR DELETE OR UPDATE ON public."PromisesToPay" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();


--
-- Name: PromisesToPay trg_promisestopay_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_promisestopay_reject_truncate BEFORE TRUNCATE ON public."PromisesToPay" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: PromisesToPay trg_promisestopay_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_promisestopay_tenant_reference BEFORE INSERT OR UPDATE ON public."PromisesToPay" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();


--
-- Name: source_documents trg_protect_source_document_identity; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_protect_source_document_identity BEFORE UPDATE ON public.source_documents FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_source_document_identity();


--
-- Name: source_document_occurrences trg_protect_source_occurrence_metadata; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_protect_source_occurrence_metadata BEFORE UPDATE ON public.source_document_occurrences FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_source_occurrence_metadata();


--
-- Name: ReceivableDocuments trg_receivable_document_issued_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_receivable_document_issued_immutable BEFORE INSERT OR DELETE OR UPDATE ON public."ReceivableDocuments" FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_issued_immutable();


--
-- Name: ReceivableDocuments trg_receivable_documents_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_receivable_documents_reject_truncate BEFORE TRUNCATE ON public."ReceivableDocuments" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReceivableDocumentLines trg_receivable_line_issued_immutable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_receivable_line_issued_immutable BEFORE INSERT OR DELETE OR UPDATE ON public."ReceivableDocumentLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_line_issued_immutable();


--
-- Name: ReceivableDocumentLines trg_receivable_lines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_receivable_lines_reject_truncate BEFORE TRUNCATE ON public."ReceivableDocumentLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReceivableDocumentLines trg_receivable_order_item_ownership; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_receivable_order_item_ownership BEFORE INSERT OR UPDATE ON public."ReceivableDocumentLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_order_item_valid();


--
-- Name: ReceivableDocuments trg_receivable_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_receivable_outbox_event AFTER INSERT OR UPDATE ON public."ReceivableDocuments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_outbox_event();


--
-- Name: ReceivableWriteOffs trg_receivable_write_offs_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_receivable_write_offs_reject_truncate BEFORE TRUNCATE ON public."ReceivableWriteOffs" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReconciliationAllocations trg_reconciliationallocations_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_reconciliationallocations_guard BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_allocation();


--
-- Name: ReconciliationAllocations trg_reconciliationallocations_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_reconciliationallocations_validate AFTER INSERT ON public."ReconciliationAllocations" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_check_match_trigger();


--
-- Name: ReconciliationMatches trg_reconciliationmatches_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_evidence AFTER INSERT OR UPDATE ON public."ReconciliationMatches" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();


--
-- Name: ReconciliationMatches trg_reconciliationmatches_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_reconciliationmatches_guard BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationMatches" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_match();


--
-- Name: ReconciliationMatches trg_reconciliationmatches_rule; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_reconciliationmatches_rule BEFORE INSERT OR UPDATE ON public."ReconciliationMatches" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_match_rule();


--
-- Name: ReconciliationMatches trg_reconciliationmatches_validate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_validate AFTER INSERT OR UPDATE ON public."ReconciliationMatches" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_check_match_trigger();


--
-- Name: ReconciliationRunRules trg_reconciliationrunrules_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_reconciliationrunrules_guard BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationRunRules" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_snapshot();


--
-- Name: ReconciliationRuns trg_reconciliationruns_certify; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_reconciliationruns_certify BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationRuns" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_certify_run();


--
-- Name: ReconciliationRuns trg_reconciliationruns_evidence; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_reconciliationruns_evidence AFTER INSERT OR UPDATE ON public."ReconciliationRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();


--
-- Name: ReconciliationRuns trg_reconciliationruns_rules; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_reconciliationruns_rules AFTER INSERT OR UPDATE ON public."ReconciliationRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_run_rules();


--
-- Name: CustomerRefunds trg_refund_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_refund_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerRefunds" FOR EACH ROW EXECUTE FUNCTION public.nexora_refund_governed();


--
-- Name: CustomerRefunds trg_refund_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_refund_outbox_event AFTER INSERT OR UPDATE ON public."CustomerRefunds" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_refund_outbox_event();


--
-- Name: Contacts trg_release01b_contact_tenant_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_release01b_contact_tenant_guard BEFORE INSERT OR UPDATE OF "BusinessUnitID", "CustomerID", "SupplierID" ON public."Contacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01b_contact_tenant_guard();


--
-- Name: ExtractionJobs trg_release01b_intake_before_claim_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_release01b_intake_before_claim_guard BEFORE UPDATE OF "Status" ON public."ExtractionJobs" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01b_intake_before_claim_guard();


--
-- Name: LeadIngestionOccurrences trg_release01b_lead_occurrence_source_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_release01b_lead_occurrence_source_guard BEFORE UPDATE ON public."LeadIngestionOccurrences" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01b_lead_occurrence_source_guard();


--
-- Name: ExtractionJobs trg_release01c_sync_intake_from_job; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_release01c_sync_intake_from_job AFTER UPDATE OF "Status" ON public."ExtractionJobs" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01c_sync_intake_from_job();


--
-- Name: commercial_exception_cases trg_require_commercial_exception_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_require_commercial_exception_event AFTER INSERT OR UPDATE ON public.commercial_exception_cases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_commercial_exception_event();


--
-- Name: commercial_opportunity_feedback trg_require_opportunity_feedback_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_require_opportunity_feedback_event AFTER INSERT ON public.commercial_opportunity_feedback DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();


--
-- Name: commercial_opportunity_events trg_require_opportunity_outbox; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_require_opportunity_outbox AFTER INSERT ON public.commercial_opportunity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_outbox();


--
-- Name: commercial_opportunity_outcomes trg_require_opportunity_outcome_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_require_opportunity_outcome_event AFTER INSERT ON public.commercial_opportunity_outcomes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();


--
-- Name: commercial_opportunity_recommendations trg_require_opportunity_recommendation_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_require_opportunity_recommendation_event AFTER INSERT ON public.commercial_opportunity_recommendations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();


--
-- Name: source_document_occurrences trg_source_document_occurrences_guard; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_source_document_occurrences_guard BEFORE DELETE OR UPDATE ON public.source_document_occurrences FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_occurrence_guard();


--
-- Name: source_documents trg_source_documents_no_delete; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_source_documents_no_delete BEFORE DELETE ON public.source_documents FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: source_documents trg_source_documents_purge_forward_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_source_documents_purge_forward_only BEFORE UPDATE ON public.source_documents FOR EACH ROW EXECUTE FUNCTION public.nexora_source_document_purge_forward_only();


--
-- Name: commercial_opportunity_feedback trg_validate_opportunity_feedback; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_validate_opportunity_feedback BEFORE INSERT ON public.commercial_opportunity_feedback FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_feedback();


--
-- Name: commercial_opportunity_outcomes trg_validate_opportunity_outcome; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_validate_opportunity_outcome BEFORE INSERT ON public.commercial_opportunity_outcomes FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_outcome();


--
-- Name: commercial_opportunity_recommendations trg_validate_opportunity_recommendation_lineage; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_validate_opportunity_recommendation_lineage BEFORE INSERT ON public.commercial_opportunity_recommendations FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_recommendation_lineage();


--
-- Name: validation_findings trg_validation_findings_append_only; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_validation_findings_append_only BEFORE DELETE OR UPDATE ON public.validation_findings FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();


--
-- Name: WriteOffAllocations trg_write_off_allocation_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_write_off_allocation_governed BEFORE INSERT OR DELETE OR UPDATE ON public."WriteOffAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_allocation_governed();


--
-- Name: WriteOffAllocations trg_write_off_allocations_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_write_off_allocations_reject_truncate BEFORE TRUNCATE ON public."WriteOffAllocations" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();


--
-- Name: ReceivableWriteOffs trg_write_off_governed; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_write_off_governed BEFORE INSERT OR DELETE OR UPDATE ON public."ReceivableWriteOffs" FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_governed();


--
-- Name: ReceivableWriteOffs trg_write_off_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_write_off_outbox_event AFTER INSERT OR UPDATE ON public."ReceivableWriteOffs" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_outbox_event();

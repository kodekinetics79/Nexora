-- ==========================================================================
-- Triggers (incl. ENABLE ALWAYS)
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
-- Name: AccountingOutbox accounting_outbox_guard; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'accounting_outbox_guard'
      AND tgrelid = to_regclass('platform."AccountingOutbox"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER accounting_outbox_guard BEFORE DELETE OR UPDATE ON platform."AccountingOutbox" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_accounting_outbox();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."AccountingOutbox" ENABLE ALWAYS TRIGGER accounting_outbox_guard;


--
-- Name: BillingStatementLines billing_statement_lines_guard_write; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'billing_statement_lines_guard_write'
      AND tgrelid = to_regclass('platform."BillingStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER billing_statement_lines_guard_write BEFORE INSERT OR DELETE OR UPDATE ON platform."BillingStatementLines" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_line_mutation();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."BillingStatementLines" ENABLE ALWAYS TRIGGER billing_statement_lines_guard_write;


--
-- Name: BillingStatements billing_statements_guard_delete; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'billing_statements_guard_delete'
      AND tgrelid = to_regclass('platform."BillingStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER billing_statements_guard_delete BEFORE DELETE ON platform."BillingStatements" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_mutation();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_delete;


--
-- Name: BillingStatements billing_statements_guard_update; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'billing_statements_guard_update'
      AND tgrelid = to_regclass('platform."BillingStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER billing_statements_guard_update BEFORE UPDATE ON platform."BillingStatements" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_mutation();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_update;


--
-- Name: PlatformAuditLogs platform_ai_policy_audits_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'platform_ai_policy_audits_immutable'
      AND tgrelid = to_regclass('platform."PlatformAuditLogs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER platform_ai_policy_audits_immutable BEFORE DELETE OR UPDATE ON platform."PlatformAuditLogs" FOR EACH ROW WHEN (((old."Action")::text = 'tenant.ai-policy.update'::text)) EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."PlatformAuditLogs" ENABLE ALWAYS TRIGGER platform_ai_policy_audits_immutable;


--
-- Name: PlatformAuditLogs platform_audit_logs_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'platform_audit_logs_append_only'
      AND tgrelid = to_regclass('platform."PlatformAuditLogs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER platform_audit_logs_append_only BEFORE DELETE OR UPDATE ON platform."PlatformAuditLogs" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."PlatformAuditLogs" ENABLE ALWAYS TRIGGER platform_audit_logs_append_only;


--
-- Name: PlatformAuditLogs platform_audit_logs_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'platform_audit_logs_no_truncate'
      AND tgrelid = to_regclass('platform."PlatformAuditLogs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER platform_audit_logs_no_truncate BEFORE TRUNCATE ON platform."PlatformAuditLogs" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."PlatformAuditLogs" ENABLE ALWAYS TRIGGER platform_audit_logs_no_truncate;


--
-- Name: ProvisioningExecutions provisioning_executions_lease_transfer_guard; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'provisioning_executions_lease_transfer_guard'
      AND tgrelid = to_regclass('platform."ProvisioningExecutions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER provisioning_executions_lease_transfer_guard BEFORE UPDATE ON platform."ProvisioningExecutions" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_provisioning_lease_transfer();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."ProvisioningExecutions" ENABLE ALWAYS TRIGGER provisioning_executions_lease_transfer_guard;


--
-- Name: SubscriptionRevenueActions subscription_action_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_action_rollups_reconcile'
      AND tgrelid = to_regclass('platform."SubscriptionRevenueActions"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER subscription_action_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionRevenueActions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ALWAYS TRIGGER subscription_action_rollups_reconcile;


--
-- Name: SubscriptionCreditNotes subscription_credit_notes_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_credit_notes_immutable'
      AND tgrelid = to_regclass('platform."SubscriptionCreditNotes"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER subscription_credit_notes_immutable BEFORE DELETE OR UPDATE ON platform."SubscriptionCreditNotes" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionCreditNotes" ENABLE ALWAYS TRIGGER subscription_credit_notes_immutable;


--
-- Name: SubscriptionCreditNotes subscription_credit_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_credit_rollups_reconcile'
      AND tgrelid = to_regclass('platform."SubscriptionCreditNotes"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER subscription_credit_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionCreditNotes" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionCreditNotes" ENABLE ALWAYS TRIGGER subscription_credit_rollups_reconcile;


--
-- Name: SubscriptionInvoices subscription_invoice_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_invoice_rollups_reconcile'
      AND tgrelid = to_regclass('platform."SubscriptionInvoices"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER subscription_invoice_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionInvoices" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionInvoices" ENABLE ALWAYS TRIGGER subscription_invoice_rollups_reconcile;


--
-- Name: SubscriptionInvoices subscription_invoices_guard; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_invoices_guard'
      AND tgrelid = to_regclass('platform."SubscriptionInvoices"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER subscription_invoices_guard BEFORE DELETE OR UPDATE ON platform."SubscriptionInvoices" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_invoice();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionInvoices" ENABLE ALWAYS TRIGGER subscription_invoices_guard;


--
-- Name: SubscriptionPayments subscription_payment_rollups_reconcile; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_payment_rollups_reconcile'
      AND tgrelid = to_regclass('platform."SubscriptionPayments"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER subscription_payment_rollups_reconcile AFTER INSERT OR UPDATE ON platform."SubscriptionPayments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionPayments" ENABLE ALWAYS TRIGGER subscription_payment_rollups_reconcile;


--
-- Name: SubscriptionPayments subscription_payments_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_payments_immutable'
      AND tgrelid = to_regclass('platform."SubscriptionPayments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER subscription_payments_immutable BEFORE DELETE OR UPDATE ON platform."SubscriptionPayments" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionPayments" ENABLE ALWAYS TRIGGER subscription_payments_immutable;


--
-- Name: SubscriptionRevenueActions subscription_revenue_actions_guard; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_revenue_actions_guard'
      AND tgrelid = to_regclass('platform."SubscriptionRevenueActions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER subscription_revenue_actions_guard BEFORE DELETE OR UPDATE ON platform."SubscriptionRevenueActions" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_revenue_action();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ALWAYS TRIGGER subscription_revenue_actions_guard;


--
-- Name: SubscriptionTaxRules subscription_tax_rules_guard; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'subscription_tax_rules_guard'
      AND tgrelid = to_regclass('platform."SubscriptionTaxRules"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER subscription_tax_rules_guard BEFORE DELETE OR UPDATE ON platform."SubscriptionTaxRules" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_tax_rule();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."SubscriptionTaxRules" ENABLE ALWAYS TRIGGER subscription_tax_rules_guard;


--
-- Name: TenantDataRecoveryEvidence tenant_data_recovery_evidence_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_data_recovery_evidence_immutable'
      AND tgrelid = to_regclass('platform."TenantDataRecoveryEvidence"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_data_recovery_evidence_immutable BEFORE DELETE OR UPDATE ON platform."TenantDataRecoveryEvidence" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantDataRecoveryEvidence" ENABLE ALWAYS TRIGGER tenant_data_recovery_evidence_immutable;


--
-- Name: TenantDeletionCertificates tenant_deletion_certificates_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_deletion_certificates_immutable'
      AND tgrelid = to_regclass('platform."TenantDeletionCertificates"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_deletion_certificates_immutable BEFORE DELETE OR UPDATE ON platform."TenantDeletionCertificates" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantDeletionCertificates" ENABLE ALWAYS TRIGGER tenant_deletion_certificates_immutable;


--
-- Name: TenantExportReceipts tenant_export_receipts_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_export_receipts_append_only'
      AND tgrelid = to_regclass('platform."TenantExportReceipts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_export_receipts_append_only BEFORE DELETE OR UPDATE ON platform."TenantExportReceipts" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantExportReceipts" ENABLE ALWAYS TRIGGER tenant_export_receipts_append_only;


--
-- Name: TenantExportReceipts tenant_export_receipts_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_export_receipts_no_truncate'
      AND tgrelid = to_regclass('platform."TenantExportReceipts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_export_receipts_no_truncate BEFORE TRUNCATE ON platform."TenantExportReceipts" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantExportReceipts" ENABLE ALWAYS TRIGGER tenant_export_receipts_no_truncate;


--
-- Name: TenantLegalHolds tenant_legal_holds_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_legal_holds_immutable'
      AND tgrelid = to_regclass('platform."TenantLegalHolds"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_legal_holds_immutable BEFORE DELETE OR UPDATE ON platform."TenantLegalHolds" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_tenant_legal_hold();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantLegalHolds" ENABLE ALWAYS TRIGGER tenant_legal_holds_immutable;


--
-- Name: TenantLegalHolds tenant_legal_holds_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_legal_holds_no_truncate'
      AND tgrelid = to_regclass('platform."TenantLegalHolds"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_legal_holds_no_truncate BEFORE TRUNCATE ON platform."TenantLegalHolds" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantLegalHolds" ENABLE ALWAYS TRIGGER tenant_legal_holds_no_truncate;


--
-- Name: TenantLifecycleEvents tenant_lifecycle_events_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_lifecycle_events_append_only'
      AND tgrelid = to_regclass('platform."TenantLifecycleEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_lifecycle_events_append_only BEFORE DELETE OR UPDATE ON platform."TenantLifecycleEvents" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantLifecycleEvents" ENABLE ALWAYS TRIGGER tenant_lifecycle_events_append_only;


--
-- Name: TenantLifecycleEvents tenant_lifecycle_events_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_lifecycle_events_no_truncate'
      AND tgrelid = to_regclass('platform."TenantLifecycleEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_lifecycle_events_no_truncate BEFORE TRUNCATE ON platform."TenantLifecycleEvents" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantLifecycleEvents" ENABLE ALWAYS TRIGGER tenant_lifecycle_events_no_truncate;


--
-- Name: TenantOffboardings tenant_offboardings_append_only; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_offboardings_append_only'
      AND tgrelid = to_regclass('platform."TenantOffboardings"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_offboardings_append_only BEFORE DELETE ON platform."TenantOffboardings" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantOffboardings" ENABLE ALWAYS TRIGGER tenant_offboardings_append_only;


--
-- Name: TenantOffboardings tenant_offboardings_no_truncate; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_offboardings_no_truncate'
      AND tgrelid = to_regclass('platform."TenantOffboardings"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_offboardings_no_truncate BEFORE TRUNCATE ON platform."TenantOffboardings" FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."TenantOffboardings" ENABLE ALWAYS TRIGGER tenant_offboardings_no_truncate;


--
-- Name: Tenants tenants_seed_meter_source_policies; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenants_seed_meter_source_policies'
      AND tgrelid = to_regclass('platform."Tenants"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenants_seed_meter_source_policies AFTER INSERT ON platform."Tenants" FOR EACH ROW EXECUTE FUNCTION platform.nexora_seed_tenant_meter_source_policies();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."Tenants" ENABLE ALWAYS TRIGGER tenants_seed_meter_source_policies;


--
-- Name: UsageCoverageSegments usage_coverage_segments_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'usage_coverage_segments_immutable'
      AND tgrelid = to_regclass('platform."UsageCoverageSegments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER usage_coverage_segments_immutable BEFORE DELETE OR UPDATE ON platform."UsageCoverageSegments" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."UsageCoverageSegments" ENABLE ALWAYS TRIGGER usage_coverage_segments_immutable;


--
-- Name: UsageEventRatings usage_event_ratings_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'usage_event_ratings_immutable'
      AND tgrelid = to_regclass('platform."UsageEventRatings"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER usage_event_ratings_immutable BEFORE DELETE OR UPDATE ON platform."UsageEventRatings" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."UsageEventRatings" ENABLE ALWAYS TRIGGER usage_event_ratings_immutable;


--
-- Name: UsageEvents usage_events_immutable; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'usage_events_immutable'
      AND tgrelid = to_regclass('platform."UsageEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER usage_events_immutable BEFORE DELETE OR UPDATE ON platform."UsageEvents" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."UsageEvents" ENABLE ALWAYS TRIGGER usage_events_immutable;


--
-- Name: UsageEvents usage_events_insert_guard; Type: TRIGGER; Schema: platform; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'usage_events_insert_guard'
      AND tgrelid = to_regclass('platform."UsageEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER usage_events_insert_guard BEFORE INSERT ON platform."UsageEvents" FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_usage_event_insert();
END IF;
END
$nexora_idem$;


ALTER TABLE platform."UsageEvents" ENABLE ALWAYS TRIGGER usage_events_insert_guard;


--
-- Name: CommercialCases TR_CommercialCases_ProtectIdentity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CommercialCases_ProtectIdentity'
      AND tgrelid = to_regclass('public."CommercialCases"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CommercialCases_ProtectIdentity" BEFORE DELETE OR UPDATE OF "AllocationNumber", "MasterReference", "BusinessUnitID" ON public."CommercialCases" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_commercial_identity();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_lifecycle_events TR_CommercialLifecycleEvents_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CommercialLifecycleEvents_Immutable'
      AND tgrelid = to_regclass('public.commercial_lifecycle_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CommercialLifecycleEvents_Immutable" BEFORE DELETE OR UPDATE ON public.commercial_lifecycle_events FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_commercial_lifecycle_event();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_lifecycle_events TR_CommercialLifecycleEvents_NoTruncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CommercialLifecycleEvents_NoTruncate'
      AND tgrelid = to_regclass('public.commercial_lifecycle_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CommercialLifecycleEvents_NoTruncate" BEFORE TRUNCATE ON public.commercial_lifecycle_events FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_protect_commercial_lifecycle_event();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_definitions TR_CustomFieldDefinitions_NoDelete; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldDefinitions_NoDelete'
      AND tgrelid = to_regclass('public.custom_field_definitions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldDefinitions_NoDelete" BEFORE DELETE ON public.custom_field_definitions FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_dependencies TR_CustomFieldDependencies_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldDependencies_Immutable'
      AND tgrelid = to_regclass('public.custom_field_dependencies')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldDependencies_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_dependencies FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_options TR_CustomFieldOptions_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldOptions_Immutable'
      AND tgrelid = to_regclass('public.custom_field_options')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldOptions_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_options FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_records TR_CustomFieldRecords_NoDelete; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldRecords_NoDelete'
      AND tgrelid = to_regclass('public.custom_field_records')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldRecords_NoDelete" BEFORE DELETE ON public.custom_field_records FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_rules TR_CustomFieldRules_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldRules_Immutable'
      AND tgrelid = to_regclass('public.custom_field_rules')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldRules_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_rules FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_values TR_CustomFieldValues_NoDelete; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldValues_NoDelete'
      AND tgrelid = to_regclass('public.custom_field_values')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldValues_NoDelete" BEFORE DELETE ON public.custom_field_values FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: custom_field_versions TR_CustomFieldVersions_Immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_CustomFieldVersions_Immutable'
      AND tgrelid = to_regclass('public.custom_field_versions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_CustomFieldVersions_Immutable" BEFORE DELETE OR UPDATE ON public.custom_field_versions FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_custom_field_governance();
END IF;
END
$nexora_idem$;



--
-- Name: LeadStatusHistories TR_LeadStatusHistories_AppendOnly; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_LeadStatusHistories_AppendOnly'
      AND tgrelid = to_regclass('public."LeadStatusHistories"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_LeadStatusHistories_AppendOnly" BEFORE DELETE OR UPDATE ON public."LeadStatusHistories" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_lead_status_history();
END IF;
END
$nexora_idem$;



--
-- Name: Leads TR_Leads_AssignCommercialCase; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Leads_AssignCommercialCase'
      AND tgrelid = to_regclass('public."Leads"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Leads_AssignCommercialCase" BEFORE INSERT ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_assign_commercial_case();
END IF;
END
$nexora_idem$;



--
-- Name: Leads TR_Leads_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Leads_CommercialIdentity'
      AND tgrelid = to_regclass('public."Leads"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Leads_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CustomerID", "ContactID" ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_lead_commercial_identity();
END IF;
END
$nexora_idem$;



--
-- Name: Leads TR_Leads_ProtectCommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Leads_ProtectCommercialIdentity'
      AND tgrelid = to_regclass('public."Leads"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Leads_ProtectCommercialIdentity" BEFORE UPDATE OF "CommercialCaseId", "CommercialCaseReference", "BusinessUnitID" ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_commercial_identity();
END IF;
END
$nexora_idem$;



--
-- Name: Leads TR_Leads_RecordStatusHistory; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Leads_RecordStatusHistory'
      AND tgrelid = to_regclass('public."Leads"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Leads_RecordStatusHistory" AFTER INSERT OR UPDATE OF "LeadStatusId" ON public."Leads" FOR EACH ROW EXECUTE FUNCTION public.nexora_record_lead_status_history();
END IF;
END
$nexora_idem$;



--
-- Name: Leads TR_Leads_RequireLifecycleCommand; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Leads_RequireLifecycleCommand'
      AND tgrelid = to_regclass('public."Leads"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Leads_RequireLifecycleCommand" BEFORE UPDATE OF "LeadStatusId" ON public."Leads" FOR EACH ROW WHEN ((old."LeadStatusId" IS DISTINCT FROM new."LeadStatusId")) EXECUTE FUNCTION public.nexora_require_lifecycle_command();
END IF;
END
$nexora_idem$;



--
-- Name: Orders TR_Orders_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Orders_CommercialIdentity'
      AND tgrelid = to_regclass('public."Orders"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Orders_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID", "QuoteID" ON public."Orders" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_order_commercial_identity();
END IF;
END
$nexora_idem$;



--
-- Name: Quotes TR_Quotes_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Quotes_CommercialIdentity'
      AND tgrelid = to_regclass('public."Quotes"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Quotes_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID" ON public."Quotes" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_downstream_commercial_identity();
END IF;
END
$nexora_idem$;



--
-- Name: Quotes TR_Quotes_RequireLifecycleCommand; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_Quotes_RequireLifecycleCommand'
      AND tgrelid = to_regclass('public."Quotes"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_Quotes_RequireLifecycleCommand" BEFORE UPDATE OF "StatusID" ON public."Quotes" FOR EACH ROW WHEN ((old."StatusID" IS DISTINCT FROM new."StatusID")) EXECUTE FUNCTION public.nexora_require_lifecycle_command();
END IF;
END
$nexora_idem$;



--
-- Name: RFQ TR_RFQ_CommercialIdentity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_RFQ_CommercialIdentity'
      AND tgrelid = to_regclass('public."RFQ"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_RFQ_CommercialIdentity" BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID" ON public."RFQ" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_downstream_commercial_identity();
END IF;
END
$nexora_idem$;



--
-- Name: RFQ TR_RFQ_RequireLifecycleCommand; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'TR_RFQ_RequireLifecycleCommand'
      AND tgrelid = to_regclass('public."RFQ"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER "TR_RFQ_RequireLifecycleCommand" BEFORE UPDATE OF "RFQStatusID" ON public."RFQ" FOR EACH ROW WHEN ((old."RFQStatusID" IS DISTINCT FROM new."RFQStatusID")) EXECUTE FUNCTION public.nexora_require_lifecycle_command();
END IF;
END
$nexora_idem$;



--
-- Name: AiCallAttempts ai_call_attempts_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'ai_call_attempts_immutable'
      AND tgrelid = to_regclass('public."AiCallAttempts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER ai_call_attempts_immutable BEFORE DELETE OR UPDATE ON public."AiCallAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: AiCallAttempts ai_call_attempts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'ai_call_attempts_reject_truncate'
      AND tgrelid = to_regclass('public."AiCallAttempts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER ai_call_attempts_reject_truncate BEFORE TRUNCATE ON public."AiCallAttempts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: AiRequests ai_requests_guard_update; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'ai_requests_guard_update'
      AND tgrelid = to_regclass('public."AiRequests"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER ai_requests_guard_update BEFORE UPDATE ON public."AiRequests" FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_ai_request_update();
END IF;
END
$nexora_idem$;



--
-- Name: AiRequests ai_requests_reject_delete; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'ai_requests_reject_delete'
      AND tgrelid = to_regclass('public."AiRequests"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER ai_requests_reject_delete BEFORE DELETE ON public."AiRequests" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: AiRequests ai_requests_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'ai_requests_reject_truncate'
      AND tgrelid = to_regclass('public."AiRequests"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER ai_requests_reject_truncate BEFORE TRUNCATE ON public."AiRequests" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts bankaccounts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankaccounts_reject_truncate'
      AND tgrelid = to_regclass('public."BankAccounts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankaccounts_reject_truncate BEFORE TRUNCATE ON public."BankAccounts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustmentDistributions bankadjustmentdistributions_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankadjustmentdistributions_reject_truncate'
      AND tgrelid = to_regclass('public."BankAdjustmentDistributions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankadjustmentdistributions_reject_truncate BEFORE TRUNCATE ON public."BankAdjustmentDistributions" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments bankadjustments_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankadjustments_reject_truncate'
      AND tgrelid = to_regclass('public."BankAdjustments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankadjustments_reject_truncate BEFORE TRUNCATE ON public."BankAdjustments" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankMatchingRules bankmatchingrules_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankmatchingrules_reject_truncate'
      AND tgrelid = to_regclass('public."BankMatchingRules"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankmatchingrules_reject_truncate BEFORE TRUNCATE ON public."BankMatchingRules" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports bankstatementimports_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankstatementimports_reject_truncate'
      AND tgrelid = to_regclass('public."BankStatementImports"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankstatementimports_reject_truncate BEFORE TRUNCATE ON public."BankStatementImports" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementLines bankstatementlines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankstatementlines_reject_truncate'
      AND tgrelid = to_regclass('public."BankStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankstatementlines_reject_truncate BEFORE TRUNCATE ON public."BankStatementLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements bankstatements_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'bankstatements_reject_truncate'
      AND tgrelid = to_regclass('public."BankStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER bankstatements_reject_truncate BEFORE TRUNCATE ON public."BankStatements" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BusinessUnits business_units_create_ai_policy; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'business_units_create_ai_policy'
      AND tgrelid = to_regclass('public."BusinessUnits"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER business_units_create_ai_policy AFTER INSERT ON public."BusinessUnits" FOR EACH ROW EXECUTE FUNCTION public.nexora_create_default_ai_policy();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_activities commercial_activities_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'commercial_activities_immutable'
      AND tgrelid = to_regclass('public.commercial_activities')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER commercial_activities_immutable BEFORE DELETE OR UPDATE ON public.commercial_activities FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_demand_lines commercial_demand_lines_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'commercial_demand_lines_immutable'
      AND tgrelid = to_regclass('public.commercial_demand_lines')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER commercial_demand_lines_immutable BEFORE DELETE OR UPDATE ON public.commercial_demand_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_demand_line_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_document_classifications commercial_document_classifications_source_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'commercial_document_classifications_source_immutable'
      AND tgrelid = to_regclass('public.commercial_document_classifications')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER commercial_document_classifications_source_immutable BEFORE UPDATE ON public.commercial_document_classifications FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_classification_source_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: lead_line_commercial_resolutions commercial_line_resolution_delete_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'commercial_line_resolution_delete_guard'
      AND tgrelid = to_regclass('public.lead_line_commercial_resolutions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER commercial_line_resolution_delete_guard BEFORE DELETE ON public.lead_line_commercial_resolutions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: lead_line_commercial_resolutions commercial_line_resolution_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'commercial_line_resolution_tenant_integrity'
      AND tgrelid = to_regclass('public.lead_line_commercial_resolutions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER commercial_line_resolution_tenant_integrity BEFORE INSERT OR UPDATE ON public.lead_line_commercial_resolutions FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_commercial_line_resolution();
END IF;
END
$nexora_idem$;



--
-- Name: lead_line_commercial_resolutions commercial_line_resolution_update_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'commercial_line_resolution_update_guard'
      AND tgrelid = to_regclass('public.lead_line_commercial_resolutions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER commercial_line_resolution_update_guard BEFORE UPDATE ON public.lead_line_commercial_resolutions FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_line_resolution_update();
END IF;
END
$nexora_idem$;



--
-- Name: customer_quote_sourcing_decisions customer_quote_sourcing_decisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'customer_quote_sourcing_decisions_append_only'
      AND tgrelid = to_regclass('public.customer_quote_sourcing_decisions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER customer_quote_sourcing_decisions_append_only BEFORE DELETE OR UPDATE ON public.customer_quote_sourcing_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: extraction_dead_letter_events extraction_dead_letter_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'extraction_dead_letter_events_append_only'
      AND tgrelid = to_regclass('public.extraction_dead_letter_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER extraction_dead_letter_events_append_only BEFORE DELETE OR UPDATE ON public.extraction_dead_letter_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_extraction_dead_letter_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: follow_up_transition_events follow_up_transition_events_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'follow_up_transition_events_immutable'
      AND tgrelid = to_regclass('public.follow_up_transition_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER follow_up_transition_events_immutable BEFORE DELETE OR UPDATE ON public.follow_up_transition_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: governed_artifact_events governed_artifact_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'governed_artifact_events_append_only'
      AND tgrelid = to_regclass('public.governed_artifact_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER governed_artifact_events_append_only BEFORE DELETE OR UPDATE ON public.governed_artifact_events FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: human_action_events human_action_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'human_action_events_append_only'
      AND tgrelid = to_regclass('public.human_action_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER human_action_events_append_only BEFORE DELETE OR UPDATE ON public.human_action_events FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: incoming_inventory incoming_inventory_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'incoming_inventory_tenant_integrity'
      AND tgrelid = to_regclass('public.incoming_inventory')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER incoming_inventory_tenant_integrity BEFORE INSERT OR UPDATE ON public.incoming_inventory FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: inventory_movements inventory_movements_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'inventory_movements_immutable'
      AND tgrelid = to_regclass('public.inventory_movements')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER inventory_movements_immutable BEFORE DELETE OR UPDATE ON public.inventory_movements FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: inventory_movements inventory_movements_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'inventory_movements_tenant_integrity'
      AND tgrelid = to_regclass('public.inventory_movements')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER inventory_movements_tenant_integrity BEFORE INSERT ON public.inventory_movements FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: Inventory inventory_procurement_tenant_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'inventory_procurement_tenant_immutable'
      AND tgrelid = to_regclass('public."Inventory"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER inventory_procurement_tenant_immutable BEFORE UPDATE OF "Buid" ON public."Inventory" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_referenced_inventory_tenant_change();
END IF;
END
$nexora_idem$;



--
-- Name: Inventory inventory_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'inventory_tenant_integrity'
      AND tgrelid = to_regclass('public."Inventory"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER inventory_tenant_integrity BEFORE INSERT OR UPDATE OF "Buid", "ProductId" ON public."Inventory" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: Inventory inventory_warehouse_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'inventory_warehouse_tenant_integrity'
      AND tgrelid = to_regclass('public."Inventory"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER inventory_warehouse_tenant_integrity BEFORE INSERT OR UPDATE OF "Buid", "WarehouseId" ON public."Inventory" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_warehouse_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: LeadReviewAudits lead_review_audits_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'lead_review_audits_immutable'
      AND tgrelid = to_regclass('public."LeadReviewAudits"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER lead_review_audits_immutable BEFORE DELETE OR UPDATE ON public."LeadReviewAudits" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_lead_review_audit_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LeadReviewAudits lead_review_audits_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'lead_review_audits_reject_truncate'
      AND tgrelid = to_regclass('public."LeadReviewAudits"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER lead_review_audits_reject_truncate BEFORE TRUNCATE ON public."LeadReviewAudits" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_lead_review_audit_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: lead_routing_decisions lead_routing_decisions_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'lead_routing_decisions_immutable'
      AND tgrelid = to_regclass('public.lead_routing_decisions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER lead_routing_decisions_immutable BEFORE DELETE OR UPDATE ON public.lead_routing_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_routing_decision_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: learning_governance_events learning_governance_events_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'learning_governance_events_immutable'
      AND tgrelid = to_regclass('public.learning_governance_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER learning_governance_events_immutable BEFORE DELETE OR UPDATE ON public.learning_governance_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_learning_governance_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: learning_governance_events learning_governance_events_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'learning_governance_events_reject_truncate'
      AND tgrelid = to_regclass('public.learning_governance_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER learning_governance_events_reject_truncate BEFORE TRUNCATE ON public.learning_governance_events FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_learning_governance_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: learning_governance_events learning_governance_events_validate_insert; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'learning_governance_events_validate_insert'
      AND tgrelid = to_regclass('public.learning_governance_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER learning_governance_events_validate_insert BEFORE INSERT ON public.learning_governance_events FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_learning_governance_insert();
END IF;
END
$nexora_idem$;



--
-- Name: procurement_events procurement_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'procurement_events_append_only'
      AND tgrelid = to_regclass('public.procurement_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER procurement_events_append_only BEFORE DELETE OR UPDATE ON public.procurement_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_procurement_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: product_aliases product_aliases_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'product_aliases_tenant_integrity'
      AND tgrelid = to_regclass('public.product_aliases')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER product_aliases_tenant_integrity BEFORE INSERT OR UPDATE ON public.product_aliases FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: Products product_procurement_tenant_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'product_procurement_tenant_immutable'
      AND tgrelid = to_regclass('public."Products"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER product_procurement_tenant_immutable BEFORE UPDATE OF "BUID" ON public."Products" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_referenced_product_tenant_change();
END IF;
END
$nexora_idem$;



--
-- Name: product_supersessions product_supersessions_tenant_integrity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'product_supersessions_tenant_integrity'
      AND tgrelid = to_regclass('public.product_supersessions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER product_supersessions_tenant_integrity BEFORE INSERT OR UPDATE ON public.product_supersessions FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: quote_delivery_requests quote_delivery_delete_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'quote_delivery_delete_guard'
      AND tgrelid = to_regclass('public.quote_delivery_requests')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER quote_delivery_delete_guard BEFORE DELETE ON public.quote_delivery_requests FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_quote_delivery_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: quote_delivery_requests quote_delivery_update_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'quote_delivery_update_guard'
      AND tgrelid = to_regclass('public.quote_delivery_requests')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER quote_delivery_update_guard BEFORE UPDATE ON public.quote_delivery_requests FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_quote_delivery_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationAllocations reconciliationallocations_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'reconciliationallocations_reject_truncate'
      AND tgrelid = to_regclass('public."ReconciliationAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER reconciliationallocations_reject_truncate BEFORE TRUNCATE ON public."ReconciliationAllocations" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches reconciliationmatches_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'reconciliationmatches_reject_truncate'
      AND tgrelid = to_regclass('public."ReconciliationMatches"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER reconciliationmatches_reject_truncate BEFORE TRUNCATE ON public."ReconciliationMatches" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRunRules reconciliationrunrules_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'reconciliationrunrules_reject_truncate'
      AND tgrelid = to_regclass('public."ReconciliationRunRules"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER reconciliationrunrules_reject_truncate BEFORE TRUNCATE ON public."ReconciliationRunRules" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns reconciliationruns_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'reconciliationruns_reject_truncate'
      AND tgrelid = to_regclass('public."ReconciliationRuns"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER reconciliationruns_reject_truncate BEFORE TRUNCATE ON public."ReconciliationRuns" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: sales_coaching_acknowledgements sales_coaching_acknowledgements_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'sales_coaching_acknowledgements_append_only'
      AND tgrelid = to_regclass('public.sales_coaching_acknowledgements')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER sales_coaching_acknowledgements_append_only BEFORE DELETE OR UPDATE ON public.sales_coaching_acknowledgements FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_coaching_ack_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: sales_coaching_acknowledgements sales_coaching_acknowledgements_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'sales_coaching_acknowledgements_reject_truncate'
      AND tgrelid = to_regclass('public.sales_coaching_acknowledgements')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER sales_coaching_acknowledgements_reject_truncate BEFORE TRUNCATE ON public.sales_coaching_acknowledgements FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_sales_coaching_ack_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: sales_contributions sales_contributions_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'sales_contributions_immutable'
      AND tgrelid = to_regclass('public.sales_contributions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER sales_contributions_immutable BEFORE DELETE OR UPDATE ON public.sales_contributions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: sourcing_cases sourcing_cases_lineage_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'sourcing_cases_lineage_immutable'
      AND tgrelid = to_regclass('public.sourcing_cases')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER sourcing_cases_lineage_immutable BEFORE UPDATE ON public.sourcing_cases FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sourcing_case_lineage_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_negotiation_decisions supplier_negotiation_decisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_negotiation_decisions_append_only'
      AND tgrelid = to_regclass('public.supplier_negotiation_decisions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_negotiation_decisions_append_only BEFORE DELETE OR UPDATE ON public.supplier_negotiation_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_negotiation_decision_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_negotiation_decisions supplier_negotiation_decisions_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_negotiation_decisions_reject_truncate'
      AND tgrelid = to_regclass('public.supplier_negotiation_decisions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_negotiation_decisions_reject_truncate BEFORE TRUNCATE ON public.supplier_negotiation_decisions FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_supplier_negotiation_decision_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_order_lines supplier_po_line_inventory_tenant; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_po_line_inventory_tenant'
      AND tgrelid = to_regclass('public.supplier_purchase_order_lines')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_po_line_inventory_tenant BEFORE INSERT OR UPDATE OF "InventoryId", "BusinessUnitId" ON public.supplier_purchase_order_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_procurement_inventory_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_purchase_order_lines supplier_po_line_product_tenant; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_po_line_product_tenant'
      AND tgrelid = to_regclass('public.supplier_purchase_order_lines')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_po_line_product_tenant BEFORE INSERT OR UPDATE OF "ProductId", "BusinessUnitId" ON public.supplier_purchase_order_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_procurement_product_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_field_evidence supplier_quote_field_evidence_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quote_field_evidence_append_only'
      AND tgrelid = to_regclass('public.supplier_quote_field_evidence')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quote_field_evidence_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_field_evidence FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_lines supplier_quote_lines_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quote_lines_append_only'
      AND tgrelid = to_regclass('public.supplier_quote_lines')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quote_lines_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_lines FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: SupplierQuotedItems supplier_quote_product_tenant; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quote_product_tenant'
      AND tgrelid = to_regclass('public."SupplierQuotedItems"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quote_product_tenant BEFORE INSERT OR UPDATE OF "ProductId", "BusinessUnitId" ON public."SupplierQuotedItems" FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_procurement_product_tenant();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_review_decisions supplier_quote_review_decisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quote_review_decisions_append_only'
      AND tgrelid = to_regclass('public.supplier_quote_review_decisions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quote_review_decisions_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_review_decisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quote_revisions supplier_quote_revisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quote_revisions_append_only'
      AND tgrelid = to_regclass('public.supplier_quote_revisions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quote_revisions_append_only BEFORE DELETE OR UPDATE ON public.supplier_quote_revisions FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: SupplierQuotedItems supplier_quoted_items_projected_lineage_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quoted_items_projected_lineage_immutable'
      AND tgrelid = to_regclass('public."SupplierQuotedItems"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quoted_items_projected_lineage_immutable BEFORE UPDATE ON public."SupplierQuotedItems" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_projected_supplier_quote_lineage();
END IF;
END
$nexora_idem$;



--
-- Name: supplier_quotes supplier_quotes_lineage_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_quotes_lineage_immutable'
      AND tgrelid = to_regclass('public.supplier_quotes')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_quotes_lineage_immutable BEFORE UPDATE ON public.supplier_quotes FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_supplier_quote_lineage();
END IF;
END
$nexora_idem$;



--
-- Name: SupplierSolicitations supplier_solicitations_commercial_lineage_write_once; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'supplier_solicitations_commercial_lineage_write_once'
      AND tgrelid = to_regclass('public."SupplierSolicitations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER supplier_solicitations_commercial_lineage_write_once BEFORE UPDATE ON public."SupplierSolicitations" FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_supplier_rfq_lineage();
END IF;
END
$nexora_idem$;



--
-- Name: tenant_governance_audit_events tenant_governance_audit_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'tenant_governance_audit_events_append_only'
      AND tgrelid = to_regclass('public.tenant_governance_audit_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER tenant_governance_audit_events_append_only BEFORE DELETE OR UPDATE ON public.tenant_governance_audit_events FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods trg_accountingperiods_book; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_accountingperiods_book'
      AND tgrelid = to_regclass('public."AccountingPeriods"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_accountingperiods_book BEFORE INSERT ON public."AccountingPeriods" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods trg_accountingperiods_certification; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_accountingperiods_certification'
      AND tgrelid = to_regclass('public."AccountingPeriods"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_accountingperiods_certification BEFORE INSERT OR UPDATE ON public."AccountingPeriods" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_certify_period_close();
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods trg_accountingperiods_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_accountingperiods_evidence'
      AND tgrelid = to_regclass('public."AccountingPeriods"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_accountingperiods_evidence AFTER INSERT OR UPDATE ON public."AccountingPeriods" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods trg_accountingperiods_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_accountingperiods_guard'
      AND tgrelid = to_regclass('public."AccountingPeriods"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_accountingperiods_guard BEFORE INSERT OR DELETE OR UPDATE ON public."AccountingPeriods" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_period();
END IF;
END
$nexora_idem$;



--
-- Name: AccountingPeriods trg_accountingperiods_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_accountingperiods_reject_truncate'
      AND tgrelid = to_regclass('public."AccountingPeriods"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_accountingperiods_reject_truncate BEFORE TRUNCATE ON public."AccountingPeriods" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts trg_bankaccounts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankaccounts_evidence'
      AND tgrelid = to_regclass('public."BankAccounts"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_bankaccounts_evidence AFTER INSERT OR UPDATE ON public."BankAccounts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: BankAccounts trg_bankaccounts_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankaccounts_guard'
      AND tgrelid = to_regclass('public."BankAccounts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankaccounts_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankAccounts" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_account();
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustmentDistributions trg_bankadjustmentdistributions_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankadjustmentdistributions_guard'
      AND tgrelid = to_regclass('public."BankAdjustmentDistributions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankadjustmentdistributions_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankAdjustmentDistributions" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_distribution();
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments trg_bankadjustments_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankadjustments_evidence'
      AND tgrelid = to_regclass('public."BankAdjustments"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_bankadjustments_evidence AFTER INSERT OR UPDATE ON public."BankAdjustments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments trg_bankadjustments_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankadjustments_guard'
      AND tgrelid = to_regclass('public."BankAdjustments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankadjustments_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankAdjustments" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_adjustment();
END IF;
END
$nexora_idem$;



--
-- Name: BankAdjustments trg_bankadjustments_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankadjustments_validate'
      AND tgrelid = to_regclass('public."BankAdjustments"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_bankadjustments_validate AFTER INSERT OR UPDATE ON public."BankAdjustments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_adjustment();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports trg_bankimports_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankimports_evidence'
      AND tgrelid = to_regclass('public."BankStatementImports"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_bankimports_evidence AFTER INSERT ON public."BankStatementImports" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports trg_bankimports_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankimports_immutable'
      AND tgrelid = to_regclass('public."BankStatementImports"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankimports_immutable BEFORE DELETE OR UPDATE ON public."BankStatementImports" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementImports trg_bankimports_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankimports_validate'
      AND tgrelid = to_regclass('public."BankStatementImports"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankimports_validate BEFORE INSERT ON public."BankStatementImports" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_import();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatementLines trg_banklines_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_banklines_immutable'
      AND tgrelid = to_regclass('public."BankStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_banklines_immutable BEFORE DELETE OR UPDATE ON public."BankStatementLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();
END IF;
END
$nexora_idem$;



--
-- Name: BankMatchingRules trg_bankmatchingrules_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankmatchingrules_evidence'
      AND tgrelid = to_regclass('public."BankMatchingRules"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_bankmatchingrules_evidence AFTER INSERT OR UPDATE ON public."BankMatchingRules" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: BankMatchingRules trg_bankmatchingrules_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankmatchingrules_guard'
      AND tgrelid = to_regclass('public."BankMatchingRules"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankmatchingrules_guard BEFORE INSERT OR DELETE OR UPDATE ON public."BankMatchingRules" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_rule();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements trg_bankstatements_balance; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankstatements_balance'
      AND tgrelid = to_regclass('public."BankStatements"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_bankstatements_balance AFTER INSERT ON public."BankStatements" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_validate_statement();
END IF;
END
$nexora_idem$;



--
-- Name: BankStatements trg_bankstatements_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_bankstatements_immutable'
      AND tgrelid = to_regclass('public."BankStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_bankstatements_immutable BEFORE DELETE OR UPDATE ON public."BankStatements" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();
END IF;
END
$nexora_idem$;



--
-- Name: canonical_inquiries trg_canonical_inquiries_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_canonical_inquiries_append_only'
      AND tgrelid = to_regclass('public.canonical_inquiries')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_canonical_inquiries_append_only BEFORE DELETE OR UPDATE ON public.canonical_inquiries FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: canonical_line_items trg_canonical_line_items_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_canonical_line_items_append_only'
      AND tgrelid = to_regclass('public.canonical_line_items')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_canonical_line_items_append_only BEFORE DELETE OR UPDATE ON public.canonical_line_items FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls trg_collectioncontrols_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_collectioncontrols_evidence'
      AND tgrelid = to_regclass('public."CollectionControls"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_collectioncontrols_evidence AFTER INSERT OR UPDATE ON public."CollectionControls" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls trg_collectioncontrols_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_collectioncontrols_governed'
      AND tgrelid = to_regclass('public."CollectionControls"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_collectioncontrols_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CollectionControls" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls trg_collectioncontrols_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_collectioncontrols_reject_truncate'
      AND tgrelid = to_regclass('public."CollectionControls"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_collectioncontrols_reject_truncate BEFORE TRUNCATE ON public."CollectionControls" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CollectionControls trg_collectioncontrols_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_collectioncontrols_tenant_reference'
      AND tgrelid = to_regclass('public."CollectionControls"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_collectioncontrols_tenant_reference BEFORE INSERT OR UPDATE ON public."CollectionControls" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_events trg_commercial_exception_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_commercial_exception_events_append_only'
      AND tgrelid = to_regclass('public.commercial_exception_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_commercial_exception_events_append_only BEFORE DELETE OR UPDATE ON public.commercial_exception_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_exception_event_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_operations trg_commercial_exception_operations_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_commercial_exception_operations_append_only'
      AND tgrelid = to_regclass('public.commercial_exception_operations')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_commercial_exception_operations_append_only BEFORE DELETE OR UPDATE ON public.commercial_exception_operations FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_exception_operation_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: CommercialFinanceAudits trg_commercial_finance_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_commercial_finance_audit_append_only'
      AND tgrelid = to_regclass('public."CommercialFinanceAudits"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_commercial_finance_audit_append_only BEFORE DELETE OR UPDATE ON public."CommercialFinanceAudits" FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_audit_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: CommercialFinanceAudits trg_commercial_finance_audits_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_commercial_finance_audits_reject_truncate'
      AND tgrelid = to_regclass('public."CommercialFinanceAudits"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_commercial_finance_audits_reject_truncate BEFORE TRUNCATE ON public."CommercialFinanceAudits" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments trg_customer_payments_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customer_payments_reject_truncate'
      AND tgrelid = to_regclass('public."CustomerPayments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customer_payments_reject_truncate BEFORE TRUNCATE ON public."CustomerPayments" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds trg_customer_refunds_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customer_refunds_reject_truncate'
      AND tgrelid = to_regclass('public."CustomerRefunds"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customer_refunds_reject_truncate BEFORE TRUNCATE ON public."CustomerRefunds" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customercollectionprofiles_evidence'
      AND tgrelid = to_regclass('public."CustomerCollectionProfiles"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_customercollectionprofiles_evidence AFTER INSERT OR UPDATE ON public."CustomerCollectionProfiles" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customercollectionprofiles_governed'
      AND tgrelid = to_regclass('public."CustomerCollectionProfiles"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customercollectionprofiles_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerCollectionProfiles" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customercollectionprofiles_reject_truncate'
      AND tgrelid = to_regclass('public."CustomerCollectionProfiles"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customercollectionprofiles_reject_truncate BEFORE TRUNCATE ON public."CustomerCollectionProfiles" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerCollectionProfiles trg_customercollectionprofiles_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customercollectionprofiles_tenant_reference'
      AND tgrelid = to_regclass('public."CustomerCollectionProfiles"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customercollectionprofiles_tenant_reference BEFORE INSERT OR UPDATE ON public."CustomerCollectionProfiles" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments trg_customerpayments_cash_bridge; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerpayments_cash_bridge'
      AND tgrelid = to_regclass('public."CustomerPayments"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_customerpayments_cash_bridge AFTER INSERT OR UPDATE ON public."CustomerPayments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_cash_bridge();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments trg_customerpayments_protect_kept_promise; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerpayments_protect_kept_promise'
      AND tgrelid = to_regclass('public."CustomerPayments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerpayments_protect_kept_promise BEFORE UPDATE ON public."CustomerPayments" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_reconcile_kept_promise_payment();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds trg_customerrefunds_cash_bridge; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerrefunds_cash_bridge'
      AND tgrelid = to_regclass('public."CustomerRefunds"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_customerrefunds_cash_bridge AFTER INSERT OR UPDATE ON public."CustomerRefunds" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_cash_bridge();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds trg_customerrefunds_protect_kept_promise; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerrefunds_protect_kept_promise'
      AND tgrelid = to_regclass('public."CustomerRefunds"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerrefunds_protect_kept_promise BEFORE UPDATE ON public."CustomerRefunds" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_reconcile_kept_promise_payment();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatementLines trg_customerstatementlines_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatementlines_evidence'
      AND tgrelid = to_regclass('public."CustomerStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_customerstatementlines_evidence AFTER INSERT OR UPDATE ON public."CustomerStatementLines" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatementLines trg_customerstatementlines_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatementlines_governed'
      AND tgrelid = to_regclass('public."CustomerStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerstatementlines_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerStatementLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatementLines trg_customerstatementlines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatementlines_reject_truncate'
      AND tgrelid = to_regclass('public."CustomerStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerstatementlines_reject_truncate BEFORE TRUNCATE ON public."CustomerStatementLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatementLines trg_customerstatementlines_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatementlines_tenant_reference'
      AND tgrelid = to_regclass('public."CustomerStatementLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerstatementlines_tenant_reference BEFORE INSERT OR UPDATE ON public."CustomerStatementLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements trg_customerstatements_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatements_evidence'
      AND tgrelid = to_regclass('public."CustomerStatements"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_customerstatements_evidence AFTER INSERT OR UPDATE ON public."CustomerStatements" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements trg_customerstatements_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatements_governed'
      AND tgrelid = to_regclass('public."CustomerStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerstatements_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerStatements" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements trg_customerstatements_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatements_reject_truncate'
      AND tgrelid = to_regclass('public."CustomerStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerstatements_reject_truncate BEFORE TRUNCATE ON public."CustomerStatements" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerStatements trg_customerstatements_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_customerstatements_tenant_reference'
      AND tgrelid = to_regclass('public."CustomerStatements"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_customerstatements_tenant_reference BEFORE INSERT OR UPDATE ON public."CustomerStatements" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: document_corpora trg_document_corpora_no_delete; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_document_corpora_no_delete'
      AND tgrelid = to_regclass('public.document_corpora')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_document_corpora_no_delete BEFORE DELETE ON public.document_corpora FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: document_pages trg_document_pages_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_document_pages_append_only'
      AND tgrelid = to_regclass('public.document_pages')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_document_pages_append_only BEFORE DELETE OR UPDATE ON public.document_pages FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: document_regions trg_document_regions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_document_regions_append_only'
      AND tgrelid = to_regclass('public.document_regions')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_document_regions_append_only BEFORE DELETE OR UPDATE ON public.document_regions FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases trg_dunningcases_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningcases_evidence'
      AND tgrelid = to_regclass('public."DunningCases"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningcases_evidence AFTER INSERT OR UPDATE ON public."DunningCases" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases trg_dunningcases_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningcases_governed'
      AND tgrelid = to_regclass('public."DunningCases"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningcases_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningCases" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases trg_dunningcases_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningcases_reject_truncate'
      AND tgrelid = to_regclass('public."DunningCases"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningcases_reject_truncate BEFORE TRUNCATE ON public."DunningCases" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningCases trg_dunningcases_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningcases_tenant_reference'
      AND tgrelid = to_regclass('public."DunningCases"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningcases_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningCases" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningdeliveryattempts_evidence'
      AND tgrelid = to_regclass('public."DunningDeliveryAttempts"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningdeliveryattempts_evidence AFTER INSERT OR UPDATE ON public."DunningDeliveryAttempts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningdeliveryattempts_governed'
      AND tgrelid = to_regclass('public."DunningDeliveryAttempts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningdeliveryattempts_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningDeliveryAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningdeliveryattempts_reject_truncate'
      AND tgrelid = to_regclass('public."DunningDeliveryAttempts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningdeliveryattempts_reject_truncate BEFORE TRUNCATE ON public."DunningDeliveryAttempts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningdeliveryattempts_tenant_reference'
      AND tgrelid = to_regclass('public."DunningDeliveryAttempts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningdeliveryattempts_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningDeliveryAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: DunningDeliveryAttempts trg_dunningdeliveryattempts_verify_provider; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningdeliveryattempts_verify_provider'
      AND tgrelid = to_regclass('public."DunningDeliveryAttempts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningdeliveryattempts_verify_provider BEFORE INSERT ON public."DunningDeliveryAttempts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_verify_provider_evidence();
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices trg_dunningnotices_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningnotices_evidence'
      AND tgrelid = to_regclass('public."DunningNotices"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningnotices_evidence AFTER INSERT OR UPDATE ON public."DunningNotices" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices trg_dunningnotices_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningnotices_governed'
      AND tgrelid = to_regclass('public."DunningNotices"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningnotices_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningNotices" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices trg_dunningnotices_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningnotices_reject_truncate'
      AND tgrelid = to_regclass('public."DunningNotices"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningnotices_reject_truncate BEFORE TRUNCATE ON public."DunningNotices" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningNotices trg_dunningnotices_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningnotices_tenant_reference'
      AND tgrelid = to_regclass('public."DunningNotices"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningnotices_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningNotices" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies trg_dunningpolicies_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicies_evidence'
      AND tgrelid = to_regclass('public."DunningPolicies"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningpolicies_evidence AFTER INSERT OR UPDATE ON public."DunningPolicies" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies trg_dunningpolicies_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicies_governed'
      AND tgrelid = to_regclass('public."DunningPolicies"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningpolicies_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningPolicies" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies trg_dunningpolicies_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicies_reject_truncate'
      AND tgrelid = to_regclass('public."DunningPolicies"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningpolicies_reject_truncate BEFORE TRUNCATE ON public."DunningPolicies" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicies trg_dunningpolicies_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicies_tenant_reference'
      AND tgrelid = to_regclass('public."DunningPolicies"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningpolicies_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningPolicies" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicySteps trg_dunningpolicysteps_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicysteps_evidence'
      AND tgrelid = to_regclass('public."DunningPolicySteps"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningpolicysteps_evidence AFTER INSERT OR UPDATE ON public."DunningPolicySteps" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicySteps trg_dunningpolicysteps_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicysteps_governed'
      AND tgrelid = to_regclass('public."DunningPolicySteps"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningpolicysteps_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningPolicySteps" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicySteps trg_dunningpolicysteps_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicysteps_reject_truncate'
      AND tgrelid = to_regclass('public."DunningPolicySteps"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningpolicysteps_reject_truncate BEFORE TRUNCATE ON public."DunningPolicySteps" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningPolicySteps trg_dunningpolicysteps_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningpolicysteps_tenant_reference'
      AND tgrelid = to_regclass('public."DunningPolicySteps"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningpolicysteps_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningPolicySteps" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions trg_dunningrundecisions_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningrundecisions_evidence'
      AND tgrelid = to_regclass('public."DunningRunDecisions"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningrundecisions_evidence AFTER INSERT OR UPDATE ON public."DunningRunDecisions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions trg_dunningrundecisions_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningrundecisions_governed'
      AND tgrelid = to_regclass('public."DunningRunDecisions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningrundecisions_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningRunDecisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions trg_dunningrundecisions_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningrundecisions_reject_truncate'
      AND tgrelid = to_regclass('public."DunningRunDecisions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningrundecisions_reject_truncate BEFORE TRUNCATE ON public."DunningRunDecisions" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions trg_dunningrundecisions_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningrundecisions_tenant_reference'
      AND tgrelid = to_regclass('public."DunningRunDecisions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningrundecisions_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningRunDecisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRunDecisions trg_dunningrundecisions_verify_profile; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningrundecisions_verify_profile'
      AND tgrelid = to_regclass('public."DunningRunDecisions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningrundecisions_verify_profile BEFORE INSERT OR UPDATE OF "CustomerCollectionProfileId", "DunningRunId", "CustomerId", "CurrencyId" ON public."DunningRunDecisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_verify_run_decision_profile();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns trg_dunningruns_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningruns_evidence'
      AND tgrelid = to_regclass('public."DunningRuns"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_dunningruns_evidence AFTER INSERT OR UPDATE ON public."DunningRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns trg_dunningruns_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningruns_governed'
      AND tgrelid = to_regclass('public."DunningRuns"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningruns_governed BEFORE INSERT OR DELETE OR UPDATE ON public."DunningRuns" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns trg_dunningruns_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningruns_reject_truncate'
      AND tgrelid = to_regclass('public."DunningRuns"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningruns_reject_truncate BEFORE TRUNCATE ON public."DunningRuns" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: DunningRuns trg_dunningruns_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_dunningruns_tenant_reference'
      AND tgrelid = to_regclass('public."DunningRuns"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_dunningruns_tenant_reference BEFORE INSERT OR UPDATE ON public."DunningRuns" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: extraction_runs trg_extraction_runs_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_extraction_runs_guard'
      AND tgrelid = to_regclass('public.extraction_runs')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_extraction_runs_guard BEFORE DELETE OR UPDATE ON public.extraction_runs FOR EACH ROW EXECUTE FUNCTION public.nexora_extraction_run_guard();
END IF;
END
$nexora_idem$;



--
-- Name: field_evidence trg_field_evidence_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_field_evidence_append_only'
      AND tgrelid = to_regclass('public.field_evidence')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_field_evidence_append_only BEFORE DELETE OR UPDATE ON public.field_evidence FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: FinanceOutboxMessages trg_finance_outbox_core_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_finance_outbox_core_immutable'
      AND tgrelid = to_regclass('public."FinanceOutboxMessages"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_finance_outbox_core_immutable BEFORE DELETE OR UPDATE ON public."FinanceOutboxMessages" FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_outbox_core_immutable();
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_financecommunicationcontacts_evidence'
      AND tgrelid = to_regclass('public."FinanceCommunicationContacts"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_financecommunicationcontacts_evidence AFTER INSERT OR UPDATE ON public."FinanceCommunicationContacts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_financecommunicationcontacts_governed'
      AND tgrelid = to_regclass('public."FinanceCommunicationContacts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_financecommunicationcontacts_governed BEFORE INSERT OR DELETE OR UPDATE ON public."FinanceCommunicationContacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_financecommunicationcontacts_reject_truncate'
      AND tgrelid = to_regclass('public."FinanceCommunicationContacts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_financecommunicationcontacts_reject_truncate BEFORE TRUNCATE ON public."FinanceCommunicationContacts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_financecommunicationcontacts_tenant_reference'
      AND tgrelid = to_regclass('public."FinanceCommunicationContacts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_financecommunicationcontacts_tenant_reference BEFORE INSERT OR UPDATE ON public."FinanceCommunicationContacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: FinanceCommunicationContacts trg_financecommunicationcontacts_verify_provider; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_financecommunicationcontacts_verify_provider'
      AND tgrelid = to_regclass('public."FinanceCommunicationContacts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_financecommunicationcontacts_verify_provider BEFORE INSERT ON public."FinanceCommunicationContacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_verify_provider_evidence();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_cases trg_guard_commercial_exception_case; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_guard_commercial_exception_case'
      AND tgrelid = to_regclass('public.commercial_exception_cases')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_guard_commercial_exception_case BEFORE INSERT OR UPDATE ON public.commercial_exception_cases FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_exception_case();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_outbox trg_guard_commercial_exception_outbox; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_guard_commercial_exception_outbox'
      AND tgrelid = to_regclass('public.commercial_exception_outbox')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_guard_commercial_exception_outbox BEFORE DELETE OR UPDATE ON public.commercial_exception_outbox FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_exception_outbox();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outbox trg_guard_opportunity_outbox; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_guard_opportunity_outbox'
      AND tgrelid = to_regclass('public.commercial_opportunity_outbox')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_guard_opportunity_outbox BEFORE DELETE OR UPDATE ON public.commercial_opportunity_outbox FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_opportunity_outbox();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries trg_journalentries_book; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentries_book'
      AND tgrelid = to_regclass('public."JournalEntries"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_journalentries_book BEFORE INSERT OR UPDATE ON public."JournalEntries" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries trg_journalentries_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentries_evidence'
      AND tgrelid = to_regclass('public."JournalEntries"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_journalentries_evidence AFTER INSERT OR UPDATE ON public."JournalEntries" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries trg_journalentries_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentries_guard'
      AND tgrelid = to_regclass('public."JournalEntries"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_journalentries_guard BEFORE INSERT OR DELETE OR UPDATE ON public."JournalEntries" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_journal();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries trg_journalentries_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentries_reject_truncate'
      AND tgrelid = to_regclass('public."JournalEntries"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_journalentries_reject_truncate BEFORE TRUNCATE ON public."JournalEntries" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntries trg_journalentries_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentries_validate'
      AND tgrelid = to_regclass('public."JournalEntries"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_journalentries_validate AFTER UPDATE ON public."JournalEntries" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_validate_posting();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntryLines trg_journalentrylines_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentrylines_guard'
      AND tgrelid = to_regclass('public."JournalEntryLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_journalentrylines_guard BEFORE INSERT OR DELETE OR UPDATE ON public."JournalEntryLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_line();
END IF;
END
$nexora_idem$;



--
-- Name: JournalEntryLines trg_journalentrylines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_journalentrylines_reject_truncate'
      AND tgrelid = to_regclass('public."JournalEntryLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_journalentrylines_reject_truncate BEFORE TRUNCATE ON public."JournalEntryLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: LeadIdentityAuditEvents trg_lead_identity_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_identity_audit_append_only'
      AND tgrelid = to_regclass('public."LeadIdentityAuditEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_identity_audit_append_only BEFORE DELETE OR UPDATE ON public."LeadIdentityAuditEvents" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LeadItemRevisions trg_lead_item_revisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_item_revisions_append_only'
      AND tgrelid = to_regclass('public."LeadItemRevisions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_item_revisions_append_only BEFORE DELETE OR UPDATE ON public."LeadItemRevisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LeadOccurrenceDocuments trg_lead_occurrence_documents_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_occurrence_documents_append_only'
      AND tgrelid = to_regclass('public."LeadOccurrenceDocuments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_occurrence_documents_append_only BEFORE DELETE OR UPDATE ON public."LeadOccurrenceDocuments" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionOccurrences trg_lead_occurrence_provenance_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_occurrence_provenance_guard'
      AND tgrelid = to_regclass('public."LeadIngestionOccurrences"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_occurrence_provenance_guard BEFORE UPDATE ON public."LeadIngestionOccurrences" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_occurrence_guard();
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisionDifferences trg_lead_revision_differences_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_revision_differences_append_only'
      AND tgrelid = to_regclass('public."LeadRevisionDifferences"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_revision_differences_append_only BEFORE DELETE OR UPDATE ON public."LeadRevisionDifferences" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisionImpacts trg_lead_revision_impacts_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_revision_impacts_append_only'
      AND tgrelid = to_regclass('public."LeadRevisionImpacts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_revision_impacts_append_only BEFORE DELETE OR UPDATE ON public."LeadRevisionImpacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LeadRevisions trg_lead_revisions_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_lead_revisions_append_only'
      AND tgrelid = to_regclass('public."LeadRevisions"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_lead_revisions_append_only BEFORE DELETE OR UPDATE ON public."LeadRevisions" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01a_forbid_history_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerAccounts trg_ledgeraccounts_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgeraccounts_evidence'
      AND tgrelid = to_regclass('public."LedgerAccounts"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_ledgeraccounts_evidence AFTER INSERT OR UPDATE ON public."LedgerAccounts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerAccounts trg_ledgeraccounts_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgeraccounts_guard'
      AND tgrelid = to_regclass('public."LedgerAccounts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_ledgeraccounts_guard BEFORE INSERT OR DELETE OR UPDATE ON public."LedgerAccounts" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_account();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerAccounts trg_ledgeraccounts_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgeraccounts_reject_truncate'
      AND tgrelid = to_regclass('public."LedgerAccounts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_ledgeraccounts_reject_truncate BEFORE TRUNCATE ON public."LedgerAccounts" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks trg_ledgerbooks_currency; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgerbooks_currency'
      AND tgrelid = to_regclass('public."LedgerBooks"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_ledgerbooks_currency BEFORE INSERT ON public."LedgerBooks" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks trg_ledgerbooks_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgerbooks_evidence'
      AND tgrelid = to_regclass('public."LedgerBooks"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_ledgerbooks_evidence AFTER INSERT ON public."LedgerBooks" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks trg_ledgerbooks_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgerbooks_guard'
      AND tgrelid = to_regclass('public."LedgerBooks"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_ledgerbooks_guard BEFORE INSERT OR DELETE OR UPDATE ON public."LedgerBooks" FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_book();
END IF;
END
$nexora_idem$;



--
-- Name: LedgerBooks trg_ledgerbooks_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_ledgerbooks_reject_truncate'
      AND tgrelid = to_regclass('public."LedgerBooks"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_ledgerbooks_reject_truncate BEFORE TRUNCATE ON public."LedgerBooks" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: LegalDocumentCounters trg_legal_document_counters_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_legal_document_counters_reject_truncate'
      AND tgrelid = to_regclass('public."LegalDocumentCounters"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_legal_document_counters_reject_truncate BEFORE TRUNCATE ON public."LegalDocumentCounters" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataChangeEvents trg_master_data_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_master_data_audit_append_only'
      AND tgrelid = to_regclass('public."MasterDataChangeEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_master_data_audit_append_only BEFORE DELETE OR UPDATE ON public."MasterDataChangeEvents" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_master_data_audit_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: MasterDataFieldChanges trg_master_data_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_master_data_audit_append_only'
      AND tgrelid = to_regclass('public."MasterDataFieldChanges"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_master_data_audit_append_only BEFORE DELETE OR UPDATE ON public."MasterDataFieldChanges" FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_master_data_audit_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_events trg_opportunity_events_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_opportunity_events_append_only'
      AND tgrelid = to_regclass('public.commercial_opportunity_events')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_opportunity_events_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_events FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_feedback trg_opportunity_feedback_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_opportunity_feedback_append_only'
      AND tgrelid = to_regclass('public.commercial_opportunity_feedback')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_opportunity_feedback_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_feedback FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_operations trg_opportunity_operations_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_opportunity_operations_append_only'
      AND tgrelid = to_regclass('public.commercial_opportunity_operations')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_opportunity_operations_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_operations FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outcomes trg_opportunity_outcomes_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_opportunity_outcomes_append_only'
      AND tgrelid = to_regclass('public.commercial_opportunity_outcomes')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_opportunity_outcomes_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_outcomes FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_recommendations trg_opportunity_recommendations_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_opportunity_recommendations_append_only'
      AND tgrelid = to_regclass('public.commercial_opportunity_recommendations')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_opportunity_recommendations_append_only BEFORE DELETE OR UPDATE ON public.commercial_opportunity_recommendations FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwardLineAllocations trg_otc_allocation_delete_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_allocation_delete_guard'
      AND tgrelid = to_regclass('public."CustomerAwardLineAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_allocation_delete_guard BEFORE DELETE ON public."CustomerAwardLineAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_allocation_delete_guard();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwardLineAllocations trg_otc_allocation_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_allocation_validate'
      AND tgrelid = to_regclass('public."CustomerAwardLineAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_allocation_validate BEFORE INSERT OR UPDATE ON public."CustomerAwardLineAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_allocation();
END IF;
END
$nexora_idem$;



--
-- Name: OrderToCashAuditEvents trg_otc_audit_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_audit_append_only'
      AND tgrelid = to_regclass('public."OrderToCashAuditEvents"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_audit_append_only BEFORE DELETE OR UPDATE ON public."OrderToCashAuditEvents" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_audit_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwards trg_otc_award_outbox; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_award_outbox'
      AND tgrelid = to_regclass('public."CustomerAwards"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_award_outbox AFTER INSERT OR UPDATE ON public."CustomerAwards" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwards trg_otc_award_transition_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_award_transition_guard'
      AND tgrelid = to_regclass('public."CustomerAwards"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_award_transition_guard BEFORE DELETE OR UPDATE ON public."CustomerAwards" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_award_transition_guard();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerAwards trg_otc_award_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_award_validate'
      AND tgrelid = to_regclass('public."CustomerAwards"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_award_validate BEFORE INSERT OR UPDATE ON public."CustomerAwards" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_award();
END IF;
END
$nexora_idem$;



--
-- Name: OrderItems trg_otc_order_item_source_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_order_item_source_guard'
      AND tgrelid = to_regclass('public."OrderItems"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_order_item_source_guard BEFORE INSERT OR DELETE OR UPDATE ON public."OrderItems" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_order_item_source_guard();
END IF;
END
$nexora_idem$;



--
-- Name: Orders trg_otc_order_outbox; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_order_outbox'
      AND tgrelid = to_regclass('public."Orders"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_order_outbox AFTER INSERT ON public."Orders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();
END IF;
END
$nexora_idem$;



--
-- Name: Orders trg_otc_order_source_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_order_source_guard'
      AND tgrelid = to_regclass('public."Orders"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_order_source_guard BEFORE INSERT OR DELETE OR UPDATE ON public."Orders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_order_source_guard();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrderLines trg_otc_purchase_order_line_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_purchase_order_line_validate'
      AND tgrelid = to_regclass('public."CustomerPurchaseOrderLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_purchase_order_line_validate BEFORE INSERT OR UPDATE ON public."CustomerPurchaseOrderLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_purchase_order_line();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrders trg_otc_purchase_order_outbox; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_purchase_order_outbox'
      AND tgrelid = to_regclass('public."CustomerPurchaseOrders"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_purchase_order_outbox AFTER INSERT OR UPDATE ON public."CustomerPurchaseOrders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPurchaseOrders trg_otc_purchase_order_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_otc_purchase_order_validate'
      AND tgrelid = to_regclass('public."CustomerPurchaseOrders"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_otc_purchase_order_validate BEFORE INSERT OR UPDATE ON public."CustomerPurchaseOrders" FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_purchase_order();
END IF;
END
$nexora_idem$;



--
-- Name: PaymentAllocations trg_payment_allocation_amount; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_payment_allocation_amount'
      AND tgrelid = to_regclass('public."PaymentAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_payment_allocation_amount BEFORE INSERT OR UPDATE ON public."PaymentAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_allocation_valid();
END IF;
END
$nexora_idem$;



--
-- Name: PaymentAllocations trg_payment_allocation_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_payment_allocation_append_only'
      AND tgrelid = to_regclass('public."PaymentAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_payment_allocation_append_only BEFORE DELETE OR UPDATE ON public."PaymentAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_audit_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: PaymentAllocations trg_payment_allocations_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_payment_allocations_reject_truncate'
      AND tgrelid = to_regclass('public."PaymentAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_payment_allocations_reject_truncate BEFORE TRUNCATE ON public."PaymentAllocations" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments trg_payment_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_payment_outbox_event'
      AND tgrelid = to_regclass('public."CustomerPayments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_payment_outbox_event AFTER INSERT OR UPDATE ON public."CustomerPayments" FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_outbox_event();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerPayments trg_payment_posted_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_payment_posted_immutable'
      AND tgrelid = to_regclass('public."CustomerPayments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_payment_posted_immutable BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerPayments" FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();
END IF;
END
$nexora_idem$;



--
-- Name: procurement_callback_receipts trg_procurement_callback_receipts_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_procurement_callback_receipts_append_only'
      AND tgrelid = to_regclass('public.procurement_callback_receipts')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_procurement_callback_receipts_append_only BEFORE DELETE OR UPDATE ON public.procurement_callback_receipts FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_procurement_callback_receipt();
END IF;
END
$nexora_idem$;



--
-- Name: procurement_handoffs trg_procurement_handoffs_protect_lineage; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_procurement_handoffs_protect_lineage'
      AND tgrelid = to_regclass('public.procurement_handoffs')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_procurement_handoffs_protect_lineage BEFORE DELETE OR UPDATE ON public.procurement_handoffs FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_procurement_handoff_lineage();
END IF;
END
$nexora_idem$;



--
-- Name: PromisesToPay trg_promisestopay_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_promisestopay_evidence'
      AND tgrelid = to_regclass('public."PromisesToPay"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_promisestopay_evidence AFTER INSERT OR UPDATE ON public."PromisesToPay" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: PromisesToPay trg_promisestopay_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_promisestopay_governed'
      AND tgrelid = to_regclass('public."PromisesToPay"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_promisestopay_governed BEFORE INSERT OR DELETE OR UPDATE ON public."PromisesToPay" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation();
END IF;
END
$nexora_idem$;



--
-- Name: PromisesToPay trg_promisestopay_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_promisestopay_reject_truncate'
      AND tgrelid = to_regclass('public."PromisesToPay"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_promisestopay_reject_truncate BEFORE TRUNCATE ON public."PromisesToPay" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: PromisesToPay trg_promisestopay_tenant_reference; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_promisestopay_tenant_reference'
      AND tgrelid = to_regclass('public."PromisesToPay"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_promisestopay_tenant_reference BEFORE INSERT OR UPDATE ON public."PromisesToPay" FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference();
END IF;
END
$nexora_idem$;



--
-- Name: source_documents trg_protect_source_document_identity; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_protect_source_document_identity'
      AND tgrelid = to_regclass('public.source_documents')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_protect_source_document_identity BEFORE UPDATE ON public.source_documents FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_source_document_identity();
END IF;
END
$nexora_idem$;



--
-- Name: source_document_occurrences trg_protect_source_occurrence_metadata; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_protect_source_occurrence_metadata'
      AND tgrelid = to_regclass('public.source_document_occurrences')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_protect_source_occurrence_metadata BEFORE UPDATE ON public.source_document_occurrences FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_source_occurrence_metadata();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocuments trg_receivable_document_issued_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_document_issued_immutable'
      AND tgrelid = to_regclass('public."ReceivableDocuments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_receivable_document_issued_immutable BEFORE INSERT OR DELETE OR UPDATE ON public."ReceivableDocuments" FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_issued_immutable();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocuments trg_receivable_documents_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_documents_reject_truncate'
      AND tgrelid = to_regclass('public."ReceivableDocuments"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_receivable_documents_reject_truncate BEFORE TRUNCATE ON public."ReceivableDocuments" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocumentLines trg_receivable_line_issued_immutable; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_line_issued_immutable'
      AND tgrelid = to_regclass('public."ReceivableDocumentLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_receivable_line_issued_immutable BEFORE INSERT OR DELETE OR UPDATE ON public."ReceivableDocumentLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_line_issued_immutable();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocumentLines trg_receivable_lines_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_lines_reject_truncate'
      AND tgrelid = to_regclass('public."ReceivableDocumentLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_receivable_lines_reject_truncate BEFORE TRUNCATE ON public."ReceivableDocumentLines" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocumentLines trg_receivable_order_item_ownership; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_order_item_ownership'
      AND tgrelid = to_regclass('public."ReceivableDocumentLines"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_receivable_order_item_ownership BEFORE INSERT OR UPDATE ON public."ReceivableDocumentLines" FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_order_item_valid();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableDocuments trg_receivable_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_outbox_event'
      AND tgrelid = to_regclass('public."ReceivableDocuments"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_receivable_outbox_event AFTER INSERT OR UPDATE ON public."ReceivableDocuments" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_outbox_event();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableWriteOffs trg_receivable_write_offs_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_receivable_write_offs_reject_truncate'
      AND tgrelid = to_regclass('public."ReceivableWriteOffs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_receivable_write_offs_reject_truncate BEFORE TRUNCATE ON public."ReceivableWriteOffs" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationAllocations trg_reconciliationallocations_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationallocations_guard'
      AND tgrelid = to_regclass('public."ReconciliationAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_reconciliationallocations_guard BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_allocation();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationAllocations trg_reconciliationallocations_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationallocations_validate'
      AND tgrelid = to_regclass('public."ReconciliationAllocations"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_reconciliationallocations_validate AFTER INSERT ON public."ReconciliationAllocations" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_check_match_trigger();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches trg_reconciliationmatches_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationmatches_evidence'
      AND tgrelid = to_regclass('public."ReconciliationMatches"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_evidence AFTER INSERT OR UPDATE ON public."ReconciliationMatches" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches trg_reconciliationmatches_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationmatches_guard'
      AND tgrelid = to_regclass('public."ReconciliationMatches"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_reconciliationmatches_guard BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationMatches" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_match();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches trg_reconciliationmatches_rule; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationmatches_rule'
      AND tgrelid = to_regclass('public."ReconciliationMatches"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_reconciliationmatches_rule BEFORE INSERT OR UPDATE ON public."ReconciliationMatches" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_match_rule();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationMatches trg_reconciliationmatches_validate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationmatches_validate'
      AND tgrelid = to_regclass('public."ReconciliationMatches"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_validate AFTER INSERT OR UPDATE ON public."ReconciliationMatches" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_check_match_trigger();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRunRules trg_reconciliationrunrules_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationrunrules_guard'
      AND tgrelid = to_regclass('public."ReconciliationRunRules"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_reconciliationrunrules_guard BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationRunRules" FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_snapshot();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns trg_reconciliationruns_certify; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationruns_certify'
      AND tgrelid = to_regclass('public."ReconciliationRuns"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_reconciliationruns_certify BEFORE INSERT OR DELETE OR UPDATE ON public."ReconciliationRuns" FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_certify_run();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns trg_reconciliationruns_evidence; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationruns_evidence'
      AND tgrelid = to_regclass('public."ReconciliationRuns"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_reconciliationruns_evidence AFTER INSERT OR UPDATE ON public."ReconciliationRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
END IF;
END
$nexora_idem$;



--
-- Name: ReconciliationRuns trg_reconciliationruns_rules; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_reconciliationruns_rules'
      AND tgrelid = to_regclass('public."ReconciliationRuns"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_reconciliationruns_rules AFTER INSERT OR UPDATE ON public."ReconciliationRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_run_rules();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds trg_refund_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_refund_governed'
      AND tgrelid = to_regclass('public."CustomerRefunds"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_refund_governed BEFORE INSERT OR DELETE OR UPDATE ON public."CustomerRefunds" FOR EACH ROW EXECUTE FUNCTION public.nexora_refund_governed();
END IF;
END
$nexora_idem$;



--
-- Name: CustomerRefunds trg_refund_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_refund_outbox_event'
      AND tgrelid = to_regclass('public."CustomerRefunds"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_refund_outbox_event AFTER INSERT OR UPDATE ON public."CustomerRefunds" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_refund_outbox_event();
END IF;
END
$nexora_idem$;



--
-- Name: Contacts trg_release01b_contact_tenant_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_release01b_contact_tenant_guard'
      AND tgrelid = to_regclass('public."Contacts"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_release01b_contact_tenant_guard BEFORE INSERT OR UPDATE OF "BusinessUnitID", "CustomerID", "SupplierID" ON public."Contacts" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01b_contact_tenant_guard();
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionJobs trg_release01b_intake_before_claim_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_release01b_intake_before_claim_guard'
      AND tgrelid = to_regclass('public."ExtractionJobs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_release01b_intake_before_claim_guard BEFORE UPDATE OF "Status" ON public."ExtractionJobs" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01b_intake_before_claim_guard();
END IF;
END
$nexora_idem$;



--
-- Name: LeadIngestionOccurrences trg_release01b_lead_occurrence_source_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_release01b_lead_occurrence_source_guard'
      AND tgrelid = to_regclass('public."LeadIngestionOccurrences"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_release01b_lead_occurrence_source_guard BEFORE UPDATE ON public."LeadIngestionOccurrences" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01b_lead_occurrence_source_guard();
END IF;
END
$nexora_idem$;



--
-- Name: ExtractionJobs trg_release01c_sync_intake_from_job; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_release01c_sync_intake_from_job'
      AND tgrelid = to_regclass('public."ExtractionJobs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_release01c_sync_intake_from_job AFTER UPDATE OF "Status" ON public."ExtractionJobs" FOR EACH ROW EXECUTE FUNCTION public.nexora_release01c_sync_intake_from_job();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_exception_cases trg_require_commercial_exception_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_require_commercial_exception_event'
      AND tgrelid = to_regclass('public.commercial_exception_cases')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_require_commercial_exception_event AFTER INSERT OR UPDATE ON public.commercial_exception_cases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_commercial_exception_event();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_feedback trg_require_opportunity_feedback_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_require_opportunity_feedback_event'
      AND tgrelid = to_regclass('public.commercial_opportunity_feedback')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_require_opportunity_feedback_event AFTER INSERT ON public.commercial_opportunity_feedback DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_events trg_require_opportunity_outbox; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_require_opportunity_outbox'
      AND tgrelid = to_regclass('public.commercial_opportunity_events')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_require_opportunity_outbox AFTER INSERT ON public.commercial_opportunity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_outbox();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outcomes trg_require_opportunity_outcome_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_require_opportunity_outcome_event'
      AND tgrelid = to_regclass('public.commercial_opportunity_outcomes')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_require_opportunity_outcome_event AFTER INSERT ON public.commercial_opportunity_outcomes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_recommendations trg_require_opportunity_recommendation_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_require_opportunity_recommendation_event'
      AND tgrelid = to_regclass('public.commercial_opportunity_recommendations')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_require_opportunity_recommendation_event AFTER INSERT ON public.commercial_opportunity_recommendations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();
END IF;
END
$nexora_idem$;



--
-- Name: source_document_occurrences trg_source_document_occurrences_guard; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_source_document_occurrences_guard'
      AND tgrelid = to_regclass('public.source_document_occurrences')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_source_document_occurrences_guard BEFORE DELETE OR UPDATE ON public.source_document_occurrences FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_occurrence_guard();
END IF;
END
$nexora_idem$;



--
-- Name: source_documents trg_source_documents_no_delete; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_source_documents_no_delete'
      AND tgrelid = to_regclass('public.source_documents')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_source_documents_no_delete BEFORE DELETE ON public.source_documents FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: source_documents trg_source_documents_purge_forward_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_source_documents_purge_forward_only'
      AND tgrelid = to_regclass('public.source_documents')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_source_documents_purge_forward_only BEFORE UPDATE ON public.source_documents FOR EACH ROW EXECUTE FUNCTION public.nexora_source_document_purge_forward_only();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_feedback trg_validate_opportunity_feedback; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_validate_opportunity_feedback'
      AND tgrelid = to_regclass('public.commercial_opportunity_feedback')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_validate_opportunity_feedback BEFORE INSERT ON public.commercial_opportunity_feedback FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_feedback();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_outcomes trg_validate_opportunity_outcome; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_validate_opportunity_outcome'
      AND tgrelid = to_regclass('public.commercial_opportunity_outcomes')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_validate_opportunity_outcome BEFORE INSERT ON public.commercial_opportunity_outcomes FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_outcome();
END IF;
END
$nexora_idem$;



--
-- Name: commercial_opportunity_recommendations trg_validate_opportunity_recommendation_lineage; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_validate_opportunity_recommendation_lineage'
      AND tgrelid = to_regclass('public.commercial_opportunity_recommendations')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_validate_opportunity_recommendation_lineage BEFORE INSERT ON public.commercial_opportunity_recommendations FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_recommendation_lineage();
END IF;
END
$nexora_idem$;



--
-- Name: validation_findings trg_validation_findings_append_only; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_validation_findings_append_only'
      AND tgrelid = to_regclass('public.validation_findings')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_validation_findings_append_only BEFORE DELETE OR UPDATE ON public.validation_findings FOR EACH ROW EXECUTE FUNCTION public.nexora_evidence_append_only();
END IF;
END
$nexora_idem$;



--
-- Name: WriteOffAllocations trg_write_off_allocation_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_write_off_allocation_governed'
      AND tgrelid = to_regclass('public."WriteOffAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_write_off_allocation_governed BEFORE INSERT OR DELETE OR UPDATE ON public."WriteOffAllocations" FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_allocation_governed();
END IF;
END
$nexora_idem$;



--
-- Name: WriteOffAllocations trg_write_off_allocations_reject_truncate; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_write_off_allocations_reject_truncate'
      AND tgrelid = to_regclass('public."WriteOffAllocations"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_write_off_allocations_reject_truncate BEFORE TRUNCATE ON public."WriteOffAllocations" FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableWriteOffs trg_write_off_governed; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_write_off_governed'
      AND tgrelid = to_regclass('public."ReceivableWriteOffs"')
      AND NOT tgisinternal
) THEN
CREATE TRIGGER trg_write_off_governed BEFORE INSERT OR DELETE OR UPDATE ON public."ReceivableWriteOffs" FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_governed();
END IF;
END
$nexora_idem$;



--
-- Name: ReceivableWriteOffs trg_write_off_outbox_event; Type: TRIGGER; Schema: public; Owner: -
--

DO $nexora_idem$
BEGIN
-- No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.
IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_write_off_outbox_event'
      AND tgrelid = to_regclass('public."ReceivableWriteOffs"')
      AND NOT tgisinternal
) THEN
CREATE CONSTRAINT TRIGGER trg_write_off_outbox_event AFTER INSERT OR UPDATE ON public."ReceivableWriteOffs" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_outbox_event();
END IF;
END
$nexora_idem$;

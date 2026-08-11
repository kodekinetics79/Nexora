-- ==========================================================================
-- Schema / table / column / sequence / function privileges
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
-- ==========================================================================

--
-- Name: SCHEMA platform; Type: ACL; Schema: -; Owner: -
--

GRANT USAGE ON SCHEMA platform TO nexora_tenant_app;
GRANT USAGE ON SCHEMA platform TO nexora_pipeline_app;
GRANT USAGE ON SCHEMA platform TO nexora_identity_app;


--
-- Name: SCHEMA public; Type: ACL; Schema: -; Owner: -
--

GRANT USAGE ON SCHEMA public TO nexora_tenant_app;
GRANT USAGE ON SCHEMA public TO nexora_identity_app;
GRANT USAGE ON SCHEMA public TO nexora_pipeline_app;


--
-- Name: FUNCTION nexora_guard_accounting_outbox(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_accounting_outbox() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_append_only_record(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_append_only_record() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_billing_statement_line_mutation(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_billing_statement_line_mutation() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_billing_statement_mutation(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_billing_statement_mutation() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_provisioning_lease_transfer(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_provisioning_lease_transfer() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_subscription_revenue_action(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_subscription_revenue_action() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_subscription_tax_rule(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_subscription_tax_rule() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_tenant_legal_hold(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_tenant_legal_hold() FROM PUBLIC;


--
-- Name: FUNCTION nexora_guard_usage_event_insert(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_guard_usage_event_insert() FROM PUBLIC;


--
-- Name: FUNCTION nexora_reconcile_subscription_invoice_rollups(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_reconcile_subscription_invoice_rollups() FROM PUBLIC;


--
-- Name: FUNCTION nexora_seed_tenant_meter_source_policies(); Type: ACL; Schema: platform; Owner: -
--

REVOKE ALL ON FUNCTION platform.nexora_seed_tenant_meter_source_policies() FROM PUBLIC;


--
-- Name: FUNCTION nexora_ai_policy_audit_allowed(tenant_id bigint, action_name text, target_type text, target_id text); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ai_policy_audit_allowed(tenant_id bigint, action_name text, target_type text, target_id text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ai_policy_audit_allowed(tenant_id bigint, action_name text, target_type text, target_id text) TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_ar_evidence_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ar_evidence_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ar_evidence_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_ar_governed_mutation(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ar_governed_mutation() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ar_governed_mutation() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_ar_reconcile_kept_promise_payment(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ar_reconcile_kept_promise_payment() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ar_reconcile_kept_promise_payment() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_ar_validate_tenant_reference(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ar_validate_tenant_reference() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ar_validate_tenant_reference() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_ar_verify_provider_evidence(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ar_verify_provider_evidence() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ar_verify_provider_evidence() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_ar_verify_run_decision_profile(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_ar_verify_run_decision_profile() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_ar_verify_run_decision_profile() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_create_default_ai_policy(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_create_default_ai_policy() FROM PUBLIC;


--
-- Name: FUNCTION nexora_finance_reject_truncate(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_finance_reject_truncate() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_finance_reject_truncate() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_authenticated_actor(business_unit_id bigint); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_authenticated_actor(business_unit_id bigint) FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_authenticated_actor(business_unit_id bigint) TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_certify_period_close(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_certify_period_close() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_certify_period_close() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_enforce_book_currency(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_enforce_book_currency() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_enforce_book_currency() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_evidence_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_evidence_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_evidence_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_guard_account(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_guard_account() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_guard_account() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_guard_book(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_guard_book() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_guard_book() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_guard_journal(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_guard_journal() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_guard_journal() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_guard_line(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_guard_line() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_guard_line() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_guard_period(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_guard_period() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_guard_period() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_gl_validate_posting(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_gl_validate_posting() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_gl_validate_posting() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_guard_ai_request_update(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_guard_ai_request_update() FROM PUBLIC;


--
-- Name: FUNCTION nexora_otc_outbox_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_otc_outbox_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_otc_outbox_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_payment_allocation_valid(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_payment_allocation_valid() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_payment_allocation_valid() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_payment_outbox_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_payment_outbox_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_payment_outbox_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_payment_posted_immutable(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_payment_posted_immutable() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_payment_posted_immutable() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_receivable_issued_immutable(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_receivable_issued_immutable() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_receivable_issued_immutable() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_receivable_line_issued_immutable(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_receivable_line_issued_immutable() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_receivable_line_issued_immutable() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_receivable_live_outstanding(business_unit_id bigint, document_id bigint); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_receivable_live_outstanding(business_unit_id bigint, document_id bigint) FROM PUBLIC;


--
-- Name: FUNCTION nexora_receivable_outbox_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_receivable_outbox_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_receivable_outbox_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_refund_governed(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_refund_governed() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_refund_governed() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_refund_outbox_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_refund_outbox_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_refund_outbox_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_reject_ai_ledger_mutation(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_reject_ai_ledger_mutation() FROM PUBLIC;


--
-- Name: FUNCTION nexora_reject_lead_review_audit_mutation(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_reject_lead_review_audit_mutation() FROM PUBLIC;


--
-- Name: FUNCTION nexora_reject_learning_governance_mutation(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_reject_learning_governance_mutation() FROM PUBLIC;


--
-- Name: FUNCTION nexora_validate_learning_governance_insert(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_validate_learning_governance_insert() FROM PUBLIC;


--
-- Name: FUNCTION nexora_write_finance_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, audit_action text, audit_actor text, audit_detail jsonb, occurred_on timestamp without time zone); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, audit_action text, audit_actor text, audit_detail jsonb, occurred_on timestamp without time zone) FROM PUBLIC;


--
-- Name: FUNCTION nexora_write_finance_outbox(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, event_type text, event_payload jsonb, event_time timestamp without time zone); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_write_finance_outbox(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, event_type text, event_payload jsonb, event_time timestamp without time zone) FROM PUBLIC;


--
-- Name: FUNCTION nexora_write_off_allocation_governed(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_write_off_allocation_governed() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_write_off_allocation_governed() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_write_off_governed(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_write_off_governed() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_write_off_governed() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_write_off_outbox_event(); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_write_off_outbox_event() FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_write_off_outbox_event() TO nexora_tenant_app;


--
-- Name: FUNCTION nexora_write_otc_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, command_type text, previous_state text, new_state text, actor text, reason text, request_hash text, idempotency_key text, result_json jsonb, correlation_id text, occurred_on timestamp without time zone); Type: ACL; Schema: public; Owner: -
--

REVOKE ALL ON FUNCTION public.nexora_write_otc_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, command_type text, previous_state text, new_state text, actor text, reason text, request_hash text, idempotency_key text, result_json jsonb, correlation_id text, occurred_on timestamp without time zone) FROM PUBLIC;
GRANT ALL ON FUNCTION public.nexora_write_otc_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, command_type text, previous_state text, new_state text, actor text, reason text, request_hash text, idempotency_key text, result_json jsonb, correlation_id text, occurred_on timestamp without time zone) TO nexora_tenant_app;


--
-- Name: TABLE "AccountingOutbox"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."AccountingOutbox" TO nexora_pipeline_app;


--
-- Name: TABLE "BillingStatementLines"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."BillingStatementLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BillingStatementLines_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."BillingStatementLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BillingStatements"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."BillingStatements" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BillingStatements_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."BillingStatements_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ImpersonationSessions"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."ImpersonationSessions" TO nexora_pipeline_app;


--
-- Name: COLUMN "ImpersonationSessions"."Id"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Id") ON TABLE platform."ImpersonationSessions" TO nexora_tenant_app;
GRANT SELECT("Id") ON TABLE platform."ImpersonationSessions" TO nexora_identity_app;


--
-- Name: COLUMN "ImpersonationSessions"."Jti"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Jti") ON TABLE platform."ImpersonationSessions" TO nexora_tenant_app;
GRANT SELECT("Jti") ON TABLE platform."ImpersonationSessions" TO nexora_identity_app;


--
-- Name: COLUMN "ImpersonationSessions"."ExpiresAtUtc"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("ExpiresAtUtc") ON TABLE platform."ImpersonationSessions" TO nexora_tenant_app;
GRANT SELECT("ExpiresAtUtc") ON TABLE platform."ImpersonationSessions" TO nexora_identity_app;


--
-- Name: COLUMN "ImpersonationSessions"."RevokedAtUtc"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("RevokedAtUtc") ON TABLE platform."ImpersonationSessions" TO nexora_tenant_app;
GRANT SELECT("RevokedAtUtc") ON TABLE platform."ImpersonationSessions" TO nexora_identity_app;


--
-- Name: SEQUENCE "ImpersonationSessions_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."ImpersonationSessions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Plans"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."Plans" TO nexora_pipeline_app;


--
-- Name: COLUMN "Plans"."Id"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Id") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("Id") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."Code"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Code") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("Code") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."Weight"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Weight") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("Weight") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."MaxConcurrentExtractionJobs"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("MaxConcurrentExtractionJobs") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("MaxConcurrentExtractionJobs") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."MaxDocsPerMonth"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("MaxDocsPerMonth") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("MaxDocsPerMonth") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."MaxSeats"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("MaxSeats") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("MaxSeats") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."Features"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Features") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("Features") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: COLUMN "Plans"."IsActive"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("IsActive") ON TABLE platform."Plans" TO nexora_tenant_app;
GRANT SELECT("IsActive") ON TABLE platform."Plans" TO nexora_identity_app;


--
-- Name: SEQUENCE "Plans_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."Plans_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformAuditLogs"; Type: ACL; Schema: platform; Owner: -
--

GRANT INSERT ON TABLE platform."PlatformAuditLogs" TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE platform."PlatformAuditLogs" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "PlatformAuditLogs_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT USAGE ON SEQUENCE platform."PlatformAuditLogs_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE platform."PlatformAuditLogs_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformBrowserTrusts"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformBrowserTrusts" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformEmailSettings"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformEmailSettings" TO nexora_pipeline_app;


--
-- Name: COLUMN "PlatformEmailSettings"."Id"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Id") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("Id") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."Provider"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Provider") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("Provider") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."FromAddress"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("FromAddress") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("FromAddress") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."FromName"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("FromName") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("FromName") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."ReplyToAddress"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("ReplyToAddress") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("ReplyToAddress") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."AppBaseUrl"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("AppBaseUrl") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("AppBaseUrl") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SmtpHost"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SmtpHost") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SmtpHost") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SmtpPort"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SmtpPort") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SmtpPort") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SmtpUsername"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SmtpUsername") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SmtpUsername") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SmtpPassword"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SmtpPassword") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SmtpPassword") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SmtpEnableSsl"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SmtpEnableSsl") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SmtpEnableSsl") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SmtpTimeoutMs"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SmtpTimeoutMs") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SmtpTimeoutMs") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SendGridApiKey"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SendGridApiKey") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SendGridApiKey") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."SendGridApiBaseUrl"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("SendGridApiBaseUrl") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("SendGridApiBaseUrl") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."OutboundGuardMode"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("OutboundGuardMode") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("OutboundGuardMode") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."OutboundGuardRedirectTo"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("OutboundGuardRedirectTo") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("OutboundGuardRedirectTo") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."OutboundGuardAllowedRecipients"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("OutboundGuardAllowedRecipients") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("OutboundGuardAllowedRecipients") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."OutboundGuardAllowedDomains"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("OutboundGuardAllowedDomains") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("OutboundGuardAllowedDomains") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."OutboundGuardSubjectTag"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("OutboundGuardSubjectTag") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("OutboundGuardSubjectTag") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."Version"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Version") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("Version") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: COLUMN "PlatformEmailSettings"."UpdatedAtUtc"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("UpdatedAtUtc") ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app;
GRANT SELECT("UpdatedAtUtc") ON TABLE platform."PlatformEmailSettings" TO nexora_identity_app;


--
-- Name: TABLE "PlatformMfaChallenges"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformMfaChallenges" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformMfaCredentials"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformMfaCredentials" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformMfaPolicies"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformMfaPolicies" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformMfaRecoveryCodes"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformMfaRecoveryCodes" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "PlatformMfaRecoveryCodes_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."PlatformMfaRecoveryCodes_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformSessions"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."PlatformSessions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "PlatformSessions_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."PlatformSessions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "PlatformUsers"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."PlatformUsers" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "PlatformUsers_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."PlatformUsers_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ProvisioningDrafts"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."ProvisioningDrafts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ProvisioningDrafts_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."ProvisioningDrafts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ProvisioningExecutions"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."ProvisioningExecutions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ProvisioningExecutions_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."ProvisioningExecutions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ProvisioningSteps"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."ProvisioningSteps" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ProvisioningSteps_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."ProvisioningSteps_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "RateCardLines"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."RateCardLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "RateCardLines_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."RateCardLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "RateCards"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."RateCards" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "RateCards_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."RateCards_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SubscriptionCreditNotes"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."SubscriptionCreditNotes" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SubscriptionCreditNotes_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."SubscriptionCreditNotes_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SubscriptionInvoices"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."SubscriptionInvoices" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SubscriptionInvoices_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."SubscriptionInvoices_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SubscriptionPayments"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."SubscriptionPayments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SubscriptionPayments_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."SubscriptionPayments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SubscriptionRevenueActions"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."SubscriptionRevenueActions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SubscriptionRevenueActions_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."SubscriptionRevenueActions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SubscriptionTaxRules"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."SubscriptionTaxRules" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SubscriptionTaxRules_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."SubscriptionTaxRules_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SupportTicketLinks"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."SupportTicketLinks" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SupportTicketLinks_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."SupportTicketLinks_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SupportTicketNotes"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE ON TABLE platform."SupportTicketNotes" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SupportTicketNotes_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."SupportTicketNotes_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SupportTickets"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."SupportTickets" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SupportTickets_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."SupportTickets_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantAdminInvitations"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."TenantAdminInvitations" TO nexora_pipeline_app;
GRANT SELECT,UPDATE ON TABLE platform."TenantAdminInvitations" TO nexora_identity_app;


--
-- Name: SEQUENCE "TenantAdminInvitations_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."TenantAdminInvitations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantDataAssets"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."TenantDataAssets" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantDataAssets_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."TenantDataAssets_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantDataRecoveryEvidence"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT ON TABLE platform."TenantDataRecoveryEvidence" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantDataRecoveryEvidence_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."TenantDataRecoveryEvidence_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantDeletionCertificates"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT ON TABLE platform."TenantDeletionCertificates" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantDeletionCertificates_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."TenantDeletionCertificates_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantExportReceipts"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT ON TABLE platform."TenantExportReceipts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantExportReceipts_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."TenantExportReceipts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantLegalHolds"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."TenantLegalHolds" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantLegalHolds_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."TenantLegalHolds_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantLifecycleEvents"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT ON TABLE platform."TenantLifecycleEvents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantLifecycleEvents_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."TenantLifecycleEvents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantMeterSourcePolicies"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."TenantMeterSourcePolicies" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantOffboardings"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."TenantOffboardings" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "TenantOffboardings_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."TenantOffboardings_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Tenants"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE platform."Tenants" TO nexora_pipeline_app;


--
-- Name: COLUMN "Tenants"."Id"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Id") ON TABLE platform."Tenants" TO nexora_tenant_app;
GRANT SELECT("Id") ON TABLE platform."Tenants" TO nexora_identity_app;


--
-- Name: COLUMN "Tenants"."Status"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("Status") ON TABLE platform."Tenants" TO nexora_tenant_app;
GRANT SELECT("Status") ON TABLE platform."Tenants" TO nexora_identity_app;


--
-- Name: COLUMN "Tenants"."PlanId"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("PlanId") ON TABLE platform."Tenants" TO nexora_tenant_app;
GRANT SELECT("PlanId") ON TABLE platform."Tenants" TO nexora_identity_app;


--
-- Name: COLUMN "Tenants"."PrimaryBusinessUnitId"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("PrimaryBusinessUnitId") ON TABLE platform."Tenants" TO nexora_tenant_app;
GRANT SELECT("PrimaryBusinessUnitId") ON TABLE platform."Tenants" TO nexora_identity_app;


--
-- Name: COLUMN "Tenants"."CreatedOn"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("CreatedOn") ON TABLE platform."Tenants" TO nexora_tenant_app;
GRANT SELECT("CreatedOn") ON TABLE platform."Tenants" TO nexora_identity_app;


--
-- Name: COLUMN "Tenants"."BillingMode"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("BillingMode") ON TABLE platform."Tenants" TO nexora_tenant_app;
GRANT SELECT("BillingMode") ON TABLE platform."Tenants" TO nexora_identity_app;


--
-- Name: COLUMN "Tenants"."DeploymentProfile"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("DeploymentProfile"),UPDATE("DeploymentProfile") ON TABLE platform."Tenants" TO nexora_pipeline_app;


--
-- Name: COLUMN "Tenants"."DeploymentProfileApprovedBy"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("DeploymentProfileApprovedBy"),UPDATE("DeploymentProfileApprovedBy") ON TABLE platform."Tenants" TO nexora_pipeline_app;


--
-- Name: COLUMN "Tenants"."DeploymentProfileApprovedOn"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("DeploymentProfileApprovedOn"),UPDATE("DeploymentProfileApprovedOn") ON TABLE platform."Tenants" TO nexora_pipeline_app;


--
-- Name: COLUMN "Tenants"."DeploymentProfileReason"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT("DeploymentProfileReason"),UPDATE("DeploymentProfileReason") ON TABLE platform."Tenants" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Tenants_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT ALL ON SEQUENCE platform."Tenants_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "UsageCoverageSegments"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT ON TABLE platform."UsageCoverageSegments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "UsageCoverageSegments_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."UsageCoverageSegments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "UsageEventRatings"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT ON TABLE platform."UsageEventRatings" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "UsageEventRatings_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."UsageEventRatings_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "UsageEvents"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."UsageEvents" TO nexora_pipeline_app;


--
-- Name: TABLE "UsageMinuteAggregates"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE platform."UsageMinuteAggregates" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "UsageMinuteAggregates_Id_seq"; Type: ACL; Schema: platform; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE platform."UsageMinuteAggregates_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AccountingPeriods"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."AccountingPeriods" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AccountingPeriods" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "AccountingPeriods_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."AccountingPeriods_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."AccountingPeriods_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AgentApprovals"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentApprovals" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentApprovals" TO nexora_pipeline_app;


--
-- Name: TABLE "AgentAuditLogs"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentAuditLogs" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentAuditLogs" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "AgentAuditLogs_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."AgentAuditLogs_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."AgentAuditLogs_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AgentMessages"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentMessages" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentMessages" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "AgentMessages_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."AgentMessages_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."AgentMessages_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AgentPolicies"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentPolicies" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentPolicies" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "AgentPolicies_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."AgentPolicies_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."AgentPolicies_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AgentSessions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentSessions" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AgentSessions" TO nexora_pipeline_app;


--
-- Name: TABLE "AiBudgetPeriods"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."AiBudgetPeriods" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AiBudgetPeriods" TO nexora_pipeline_app;


--
-- Name: TABLE "AiCallAttempts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."AiCallAttempts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AiCallAttempts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "AiCallAttempts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."AiCallAttempts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."AiCallAttempts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AiProcessingPolicies"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,UPDATE ON TABLE public."AiProcessingPolicies" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AiProcessingPolicies" TO nexora_pipeline_app;


--
-- Name: TABLE "AiProviderAuthorizations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."AiProviderAuthorizations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AiProviderAuthorizations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "AiProviderAuthorizations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."AiProviderAuthorizations_Id_seq" TO nexora_tenant_app;
GRANT SELECT,USAGE ON SEQUENCE public."AiProviderAuthorizations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "AiRequests"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."AiRequests" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AiRequests" TO nexora_pipeline_app;


--
-- Name: TABLE "Attachments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Attachments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Attachments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Attachments_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Attachments_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Attachments_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankAccounts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."BankAccounts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankAccounts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankAccounts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankAccounts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankAccounts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankAdjustmentDistributions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."BankAdjustmentDistributions" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankAdjustmentDistributions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankAdjustmentDistributions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankAdjustmentDistributions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankAdjustmentDistributions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankAdjustments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."BankAdjustments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankAdjustments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankAdjustments_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankAdjustments_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankAdjustments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankMatchingRules"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."BankMatchingRules" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankMatchingRules" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankMatchingRules_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankMatchingRules_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankMatchingRules_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankStatementImports"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."BankStatementImports" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankStatementImports" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankStatementImports_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankStatementImports_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankStatementImports_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankStatementLines"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."BankStatementLines" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankStatementLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankStatementLines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankStatementLines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankStatementLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BankStatements"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."BankStatements" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BankStatements" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BankStatements_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BankStatements_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BankStatements_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BoqAssemblies"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqAssemblies" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqAssemblies" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BoqAssemblies_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BoqAssemblies_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BoqAssemblies_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BoqAssemblyComponents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqAssemblyComponents" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqAssemblyComponents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BoqAssemblyComponents_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BoqAssemblyComponents_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BoqAssemblyComponents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BoqDocuments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqDocuments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqDocuments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BoqDocuments_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BoqDocuments_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BoqDocuments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BoqItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BoqItems_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BoqItems_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BoqItems_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BoqSections"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqSections" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BoqSections" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BoqSections_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BoqSections_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BoqSections_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "BusinessUnits"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BusinessUnits" TO nexora_tenant_app;
GRANT SELECT ON TABLE public."BusinessUnits" TO nexora_identity_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."BusinessUnits" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "BusinessUnits_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."BusinessUnits_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."BusinessUnits_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CollectionControls"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."CollectionControls" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CollectionControls" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CollectionControls_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CollectionControls_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CollectionControls_Id_seq" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CommercialCaseReferenceSequence"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CommercialCaseReferenceSequence" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CommercialCaseReferenceSequence" TO nexora_pipeline_app;


--
-- Name: TABLE "CommercialCases"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CommercialCases" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CommercialCases" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CommercialCases_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CommercialCases_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CommercialCases_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CommercialFinanceAudits"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT ON TABLE public."CommercialFinanceAudits" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CommercialFinanceAudits" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CommercialFinanceAudits_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT ALL ON SEQUENCE public."CommercialFinanceAudits_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CommercialMatchingPolicies"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CommercialMatchingPolicies" TO nexora_tenant_app;


--
-- Name: TABLE "Contacts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Contacts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Contacts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Contacts_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Contacts_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Contacts_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Currency"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Currency" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Currency" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Currency_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Currency_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Currency_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerAwardLineAllocations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerAwardLineAllocations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerAwardLineAllocations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerAwardLineAllocations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerAwardLineAllocations_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerAwardLineAllocations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerAwards"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerAwards" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerAwards" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerAwards_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerAwards_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerAwards_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerCollectionProfiles"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."CustomerCollectionProfiles" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerCollectionProfiles" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerCollectionProfiles_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerCollectionProfiles_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerCollectionProfiles_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerPayments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."CustomerPayments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerPayments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerPayments_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerPayments_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerPayments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerPurchaseOrderLines"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerPurchaseOrderLines" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerPurchaseOrderLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerPurchaseOrderLines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerPurchaseOrderLines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerPurchaseOrderLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerPurchaseOrders"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerPurchaseOrders" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerPurchaseOrders" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerPurchaseOrders_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerPurchaseOrders_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerPurchaseOrders_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerRefunds"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."CustomerRefunds" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerRefunds" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerRefunds_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerRefunds_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerRefunds_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerStatementLines"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."CustomerStatementLines" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerStatementLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerStatementLines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerStatementLines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerStatementLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "CustomerStatements"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."CustomerStatements" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CustomerStatements" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "CustomerStatements_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."CustomerStatements_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."CustomerStatements_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Customers"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Customers" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Customers" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Customers_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Customers_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Customers_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningCases"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."DunningCases" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningCases" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningCases_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningCases_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningCases_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningDeliveryAttempts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."DunningDeliveryAttempts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningDeliveryAttempts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningDeliveryAttempts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningDeliveryAttempts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningDeliveryAttempts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningNotices"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."DunningNotices" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningNotices" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningNotices_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningNotices_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningNotices_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningPolicies"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."DunningPolicies" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningPolicies" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningPolicies_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningPolicies_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningPolicies_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningPolicySteps"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."DunningPolicySteps" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningPolicySteps" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningPolicySteps_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningPolicySteps_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningPolicySteps_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningRunDecisions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."DunningRunDecisions" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningRunDecisions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningRunDecisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningRunDecisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningRunDecisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "DunningRuns"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."DunningRuns" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DunningRuns" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "DunningRuns_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."DunningRuns_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."DunningRuns_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "EmailIngests"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EmailIngests" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EmailIngests" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "EmailIngests_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."EmailIngests_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."EmailIngests_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Email_Configurations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Email_Configurations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Email_Configurations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Email_Configurations_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Email_Configurations_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Email_Configurations_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ExtractionCorpusEntries"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."ExtractionCorpusEntries" TO nexora_tenant_app;


--
-- Name: SEQUENCE "ExtractionCorpusEntries_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ExtractionCorpusEntries_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE "ExtractionJobs"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ExtractionJobs" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ExtractionJobs" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ExtractionJobs_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ExtractionJobs_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ExtractionJobs_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "FinanceCommunicationContacts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."FinanceCommunicationContacts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FinanceCommunicationContacts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "FinanceCommunicationContacts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."FinanceCommunicationContacts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."FinanceCommunicationContacts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "FinanceOutboxMessages"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT ON TABLE public."FinanceOutboxMessages" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FinanceOutboxMessages" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "FinanceOutboxMessages_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT ALL ON SEQUENCE public."FinanceOutboxMessages_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "FolderIngestionRetryStates"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FolderIngestionRetryStates" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FolderIngestionRetryStates" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "FolderIngestionRetryStates_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."FolderIngestionRetryStates_Id_seq" TO nexora_tenant_app;
GRANT SELECT,USAGE ON SEQUENCE public."FolderIngestionRetryStates_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "FxRateSnapshots"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FxRateSnapshots" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FxRateSnapshots" TO nexora_pipeline_app;


--
-- Name: TABLE "FxRates"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FxRates" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FxRates" TO nexora_pipeline_app;


--
-- Name: TABLE "IamAuditEvents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."IamAuditEvents" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."IamAuditEvents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "IamAuditEvents_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."IamAuditEvents_Id_seq" TO nexora_tenant_app;
GRANT SELECT,USAGE ON SEQUENCE public."IamAuditEvents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Images"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Images" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Images_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT ALL ON SEQUENCE public."Images_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Inventory"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Inventory" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Inventory" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Inventory_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Inventory_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Inventory_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "JournalEntries"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."JournalEntries" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."JournalEntries" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "JournalEntries_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."JournalEntries_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."JournalEntries_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "JournalEntryLines"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."JournalEntryLines" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."JournalEntryLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "JournalEntryLines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."JournalEntryLines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."JournalEntryLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadIdentityAuditEvents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadIdentityAuditEvents" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadIdentityAuditEvents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadIdentityAuditEvents_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadIdentityAuditEvents_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadIdentityAuditEvents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadIngestionBatches"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."LeadIngestionBatches" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadIngestionBatches" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadIngestionOccurrences"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."LeadIngestionOccurrences" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadIngestionOccurrences" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadIngestionOccurrences_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadIngestionOccurrences_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadIngestionOccurrences_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadItemRevisions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadItemRevisions" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadItemRevisions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadItemRevisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadItemRevisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadItemRevisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadItems_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadItems_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadItems_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadMatchCandidates"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."LeadMatchCandidates" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadMatchCandidates" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadMatchCandidates_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadMatchCandidates_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadMatchCandidates_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadOccurrenceDocuments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadOccurrenceDocuments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadOccurrenceDocuments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadOccurrenceDocuments_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadOccurrenceDocuments_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadOccurrenceDocuments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadReferenceConfigurations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadReferenceConfigurations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadReferenceConfigurations" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadReviewAudits"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadReviewAudits" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadReviewAudits" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadReviewAudits_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadReviewAudits_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadReviewAudits_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadRevisionDifferences"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadRevisionDifferences" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadRevisionDifferences" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadRevisionDifferences_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadRevisionDifferences_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadRevisionDifferences_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadRevisionImpacts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadRevisionImpacts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadRevisionImpacts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadRevisionImpacts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadRevisionImpacts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadRevisionImpacts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadRevisions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."LeadRevisions" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadRevisions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadRevisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadRevisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadRevisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LeadStatusHistories"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadStatusHistories" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LeadStatusHistories" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LeadStatusHistories_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LeadStatusHistories_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LeadStatusHistories_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Leads"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Leads" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Leads" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Leads_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Leads_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Leads_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LedgerAccounts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."LedgerAccounts" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LedgerAccounts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LedgerAccounts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LedgerAccounts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LedgerAccounts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LedgerActorNonces"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LedgerActorNonces" TO nexora_pipeline_app;


--
-- Name: TABLE "LedgerBooks"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."LedgerBooks" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LedgerBooks" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LedgerBooks_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."LedgerBooks_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."LedgerBooks_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "LegalDocumentCounters"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT ON TABLE public."LegalDocumentCounters" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LegalDocumentCounters" TO nexora_pipeline_app;


--
-- Name: TABLE "LoginAttempts"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LoginAttempts" TO nexora_identity_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LoginAttempts" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "LoginAttempts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,USAGE ON SEQUENCE public."LoginAttempts_Id_seq" TO nexora_identity_app;
GRANT SELECT,USAGE ON SEQUENCE public."LoginAttempts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "MasterDataChangeEvents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."MasterDataChangeEvents" TO nexora_tenant_app;


--
-- Name: TABLE "MasterDataFieldChanges"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."MasterDataFieldChanges" TO nexora_tenant_app;


--
-- Name: TABLE "MetricEvents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."MetricEvents" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."MetricEvents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "MetricEvents_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."MetricEvents_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."MetricEvents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Module"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT ON TABLE public."Module" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Module" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Module_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT ALL ON SEQUENCE public."Module_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "OrderItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OrderItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OrderItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "OrderItems_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."OrderItems_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."OrderItems_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "OrderToCashAuditEvents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT ON TABLE public."OrderToCashAuditEvents" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OrderToCashAuditEvents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "OrderToCashAuditEvents_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."OrderToCashAuditEvents_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."OrderToCashAuditEvents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "OrderToCashDocumentCounters"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."OrderToCashDocumentCounters" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OrderToCashDocumentCounters" TO nexora_pipeline_app;


--
-- Name: TABLE "Orders"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Orders" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Orders" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Orders_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Orders_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Orders_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "PaymentAllocations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."PaymentAllocations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PaymentAllocations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "PaymentAllocations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."PaymentAllocations_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."PaymentAllocations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ProductAttachments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProductAttachments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProductAttachments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ProductAttachments_AttachmentID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ProductAttachments_AttachmentID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ProductAttachments_AttachmentID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ProductCategories"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProductCategories" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProductCategories" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ProductCategories_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ProductCategories_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ProductCategories_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ProductSubCategories"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProductSubCategories" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProductSubCategories" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ProductSubCategories_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ProductSubCategories_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ProductSubCategories_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Products"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Products" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Products" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Products_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Products_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Products_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "PromisesToPay"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."PromisesToPay" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PromisesToPay" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "PromisesToPay_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."PromisesToPay_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."PromisesToPay_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "QuoteConfiguration"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuoteConfiguration" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuoteConfiguration" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "QuoteConfiguration_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."QuoteConfiguration_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."QuoteConfiguration_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "QuoteItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuoteItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuoteItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "QuoteItems_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."QuoteItems_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."QuoteItems_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "QuotePriceAttestationLines"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuotePriceAttestationLines" TO nexora_tenant_app;


--
-- Name: TABLE "QuotePriceAttestations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuotePriceAttestations" TO nexora_tenant_app;


--
-- Name: TABLE "QuoteRemovalRecords"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."QuoteRemovalRecords" TO nexora_tenant_app;


--
-- Name: TABLE "QuoteValidityExtensions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."QuoteValidityExtensions" TO nexora_tenant_app;


--
-- Name: TABLE "Quotes"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Quotes" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Quotes" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Quotes_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Quotes_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Quotes_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "RFQ"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RFQ" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RFQ" TO nexora_pipeline_app;


--
-- Name: TABLE "RFQItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RFQItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RFQItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "RFQItems_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."RFQItems_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."RFQItems_ID_seq" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "RFQ_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."RFQ_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."RFQ_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReceivableDocumentLines"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."ReceivableDocumentLines" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReceivableDocumentLines" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReceivableDocumentLines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReceivableDocumentLines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReceivableDocumentLines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReceivableDocuments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."ReceivableDocuments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReceivableDocuments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReceivableDocuments_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReceivableDocuments_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReceivableDocuments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReceivableWriteOffs"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."ReceivableWriteOffs" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReceivableWriteOffs" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReceivableWriteOffs_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReceivableWriteOffs_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReceivableWriteOffs_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReconciliationAllocations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."ReconciliationAllocations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReconciliationAllocations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReconciliationAllocations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReconciliationAllocations_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReconciliationAllocations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReconciliationMatches"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."ReconciliationMatches" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReconciliationMatches" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReconciliationMatches_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReconciliationMatches_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReconciliationMatches_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReconciliationRunRules"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."ReconciliationRunRules" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReconciliationRunRules" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReconciliationRunRules_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReconciliationRunRules_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReconciliationRunRules_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReconciliationRuns"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."ReconciliationRuns" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReconciliationRuns" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ReconciliationRuns_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ReconciliationRuns_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ReconciliationRuns_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ReportSubscriptions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReportSubscriptions" TO nexora_tenant_app;
GRANT SELECT,UPDATE ON TABLE public."ReportSubscriptions" TO nexora_pipeline_app;


--
-- Name: TABLE "RolePermissions"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RolePermissions" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RolePermissions" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "RolePermissions_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."RolePermissions_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."RolePermissions_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SetCity"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SetCity" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SetCity" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SetCity_CityID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SetCity_CityID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SetCity_CityID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SetCountry"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SetCountry" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SetCountry" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SetCountry_CountryID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SetCountry_CountryID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SetCountry_CountryID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SetState"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SetState" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SetState" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SetState_StateID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SetState_StateID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SetState_StateID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Setup_Master"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Setup_Master" TO nexora_tenant_app;
GRANT SELECT ON TABLE public."Setup_Master" TO nexora_identity_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Setup_Master" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Setup_Master_SetupID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Setup_Master_SetupID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Setup_Master_SetupID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ShipmentItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ShipmentItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ShipmentItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ShipmentItems_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ShipmentItems_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ShipmentItems_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "ShipmentStatusHistory"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ShipmentStatusHistory" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ShipmentStatusHistory" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "ShipmentStatusHistory_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."ShipmentStatusHistory_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."ShipmentStatusHistory_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Shipments"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Shipments" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Shipments" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Shipments_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Shipments_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Shipments_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SlaEvents"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SlaEvents" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SlaEvents" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SlaEvents_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SlaEvents_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SlaEvents_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SlaPolicies"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SlaPolicies" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SlaPolicies" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SlaPolicies_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SlaPolicies_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SlaPolicies_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SourcingAwards"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."SourcingAwards" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SourcingAwards" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SourcingAwards_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SourcingAwards_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SourcingAwards_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SupplierPurchaseHistory"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SupplierPurchaseHistory" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SupplierPurchaseHistory" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SupplierPurchaseHistory_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SupplierPurchaseHistory_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SupplierPurchaseHistory_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SupplierQuotedItems"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."SupplierQuotedItems" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SupplierQuotedItems" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SupplierQuotedItems_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SupplierQuotedItems_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SupplierQuotedItems_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "SupplierSolicitations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."SupplierSolicitations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SupplierSolicitations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "SupplierSolicitations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."SupplierSolicitations_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."SupplierSolicitations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Suppliers"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public."Suppliers" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Suppliers" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Suppliers_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Suppliers_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Suppliers_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Taxes"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Taxes" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Taxes" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Taxes_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Taxes_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Taxes_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Teams"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Teams" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Teams" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Teams_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Teams_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Teams_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "TenantQueueStates"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TenantQueueStates" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TenantQueueStates" TO nexora_pipeline_app;


--
-- Name: TABLE "UserColumnPreferences"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."UserColumnPreferences" TO nexora_tenant_app;


--
-- Name: TABLE "UserGroups"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."UserGroups" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."UserGroups" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "UserGroups_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."UserGroups_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."UserGroups_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Users"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Users" TO nexora_tenant_app;
GRANT SELECT ON TABLE public."Users" TO nexora_identity_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Users" TO nexora_pipeline_app;


--
-- Name: COLUMN "Users"."Password_Hash"; Type: ACL; Schema: public; Owner: -
--

GRANT UPDATE("Password_Hash") ON TABLE public."Users" TO nexora_identity_app;


--
-- Name: COLUMN "Users"."LastLogin"; Type: ACL; Schema: public; Owner: -
--

GRANT UPDATE("LastLogin") ON TABLE public."Users" TO nexora_identity_app;


--
-- Name: COLUMN "Users"."IsActive"; Type: ACL; Schema: public; Owner: -
--

GRANT UPDATE("IsActive") ON TABLE public."Users" TO nexora_identity_app;


--
-- Name: COLUMN "Users"."ModifiedBy"; Type: ACL; Schema: public; Owner: -
--

GRANT UPDATE("ModifiedBy") ON TABLE public."Users" TO nexora_identity_app;


--
-- Name: COLUMN "Users"."ModifiedOn"; Type: ACL; Schema: public; Owner: -
--

GRANT UPDATE("ModifiedOn") ON TABLE public."Users" TO nexora_identity_app;


--
-- Name: COLUMN "Users"."DeactivatedAtUtc"; Type: ACL; Schema: public; Owner: -
--

GRANT UPDATE("DeactivatedAtUtc") ON TABLE public."Users" TO nexora_identity_app;


--
-- Name: SEQUENCE "Users_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Users_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Users_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "Warehouses"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Warehouses" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Warehouses" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "Warehouses_ID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."Warehouses_ID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."Warehouses_ID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "WriteOffAllocations"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public."WriteOffAllocations" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."WriteOffAllocations" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "WriteOffAllocations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."WriteOffAllocations_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."WriteOffAllocations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE canonical_inquiries; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.canonical_inquiries TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.canonical_inquiries TO nexora_pipeline_app;


--
-- Name: SEQUENCE canonical_inquiries_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.canonical_inquiries_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.canonical_inquiries_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE canonical_line_items; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.canonical_line_items TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.canonical_line_items TO nexora_pipeline_app;


--
-- Name: SEQUENCE canonical_line_items_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.canonical_line_items_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.canonical_line_items_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE commercial_activities; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_activities TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.commercial_activities TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_activities_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_activities_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."commercial_activities_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_demand_lines; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_demand_lines TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.commercial_demand_lines TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_demand_lines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_demand_lines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."commercial_demand_lines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_document_classifications; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_document_classifications TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.commercial_document_classifications TO nexora_pipeline_app;


--
-- Name: TABLE commercial_exception_cases; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_exception_cases TO nexora_tenant_app;
GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_exception_cases TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_exception_cases_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_exception_cases_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_exception_cases_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_exception_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_exception_events TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_exception_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_exception_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_exception_events_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_exception_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_exception_operations; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_exception_operations TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_exception_operations TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_exception_operations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_exception_operations_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_exception_operations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_exception_outbox; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_exception_outbox TO nexora_tenant_app;
GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_exception_outbox TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_exception_outbox_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_exception_outbox_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_exception_outbox_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_lifecycle_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.commercial_lifecycle_events TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.commercial_lifecycle_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_lifecycle_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_lifecycle_events_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."commercial_lifecycle_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_opportunity_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_events TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_opportunity_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_opportunity_events_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_opportunity_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_opportunity_feedback; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_feedback TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_feedback TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_opportunity_feedback_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_opportunity_feedback_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_opportunity_feedback_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_opportunity_operations; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_operations TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_operations TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_opportunity_operations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_opportunity_operations_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_opportunity_operations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_opportunity_outbox; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_opportunity_outbox TO nexora_tenant_app;
GRANT SELECT,INSERT,UPDATE ON TABLE public.commercial_opportunity_outbox TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_opportunity_outbox_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_opportunity_outbox_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_opportunity_outbox_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_opportunity_outcomes; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_outcomes TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_outcomes TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_opportunity_outcomes_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_opportunity_outcomes_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_opportunity_outcomes_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE commercial_opportunity_recommendations; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_recommendations TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.commercial_opportunity_recommendations TO nexora_pipeline_app;


--
-- Name: SEQUENCE "commercial_opportunity_recommendations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."commercial_opportunity_recommendations_Id_seq" TO nexora_tenant_app;
GRANT USAGE ON SEQUENCE public."commercial_opportunity_recommendations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_definitions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_definitions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_definitions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_definitions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_definitions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_definitions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_dependencies; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_dependencies TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_dependencies TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_dependencies_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_dependencies_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_dependencies_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_options; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_options TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_options TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_options_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_options_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_options_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_records; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_records TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_records TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_records_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_records_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_records_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_rules; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_rules TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_rules TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_rules_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_rules_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_rules_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_values; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_values TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_values TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_values_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_values_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_values_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE custom_field_versions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_versions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.custom_field_versions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "custom_field_versions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."custom_field_versions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."custom_field_versions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE customer_identifiers; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.customer_identifiers TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.customer_identifiers TO nexora_pipeline_app;


--
-- Name: SEQUENCE "customer_identifiers_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."customer_identifiers_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."customer_identifiers_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE customer_ownerships; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.customer_ownerships TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.customer_ownerships TO nexora_pipeline_app;


--
-- Name: SEQUENCE "customer_ownerships_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."customer_ownerships_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."customer_ownerships_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE customer_quote_sourcing_decisions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.customer_quote_sourcing_decisions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.customer_quote_sourcing_decisions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "customer_quote_sourcing_decisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."customer_quote_sourcing_decisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."customer_quote_sourcing_decisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE delivery_proof_lines; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.delivery_proof_lines TO nexora_tenant_app;


--
-- Name: TABLE delivery_proofs; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.delivery_proofs TO nexora_tenant_app;


--
-- Name: TABLE delivery_shortfall_decisions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.delivery_shortfall_decisions TO nexora_tenant_app;


--
-- Name: TABLE document_corpora; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.document_corpora TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.document_corpora TO nexora_pipeline_app;


--
-- Name: SEQUENCE document_corpora_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.document_corpora_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.document_corpora_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE document_pages; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.document_pages TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.document_pages TO nexora_pipeline_app;


--
-- Name: SEQUENCE document_pages_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.document_pages_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.document_pages_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE document_regions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.document_regions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.document_regions TO nexora_pipeline_app;


--
-- Name: SEQUENCE document_regions_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.document_regions_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.document_regions_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE evidence_retention_policies; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.evidence_retention_policies TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.evidence_retention_policies TO nexora_pipeline_app;


--
-- Name: SEQUENCE "evidence_retention_policies_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."evidence_retention_policies_Id_seq" TO nexora_tenant_app;
GRANT SELECT,USAGE ON SEQUENCE public."evidence_retention_policies_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE extraction_dead_letter_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.extraction_dead_letter_events TO nexora_tenant_app;


--
-- Name: SEQUENCE "extraction_dead_letter_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."extraction_dead_letter_events_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE extraction_runs; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.extraction_runs TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.extraction_runs TO nexora_pipeline_app;


--
-- Name: SEQUENCE extraction_runs_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.extraction_runs_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.extraction_runs_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE field_evidence; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.field_evidence TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.field_evidence TO nexora_pipeline_app;


--
-- Name: SEQUENCE field_evidence_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.field_evidence_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.field_evidence_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE follow_up_tasks; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.follow_up_tasks TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.follow_up_tasks TO nexora_pipeline_app;


--
-- Name: SEQUENCE "follow_up_tasks_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."follow_up_tasks_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."follow_up_tasks_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE follow_up_transition_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.follow_up_transition_events TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.follow_up_transition_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "follow_up_transition_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."follow_up_transition_events_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."follow_up_transition_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE goods_receipt_lines; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.goods_receipt_lines TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.goods_receipt_lines TO nexora_pipeline_app;


--
-- Name: SEQUENCE "goods_receipt_lines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."goods_receipt_lines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."goods_receipt_lines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE goods_receipts; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.goods_receipts TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.goods_receipts TO nexora_pipeline_app;


--
-- Name: SEQUENCE "goods_receipts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."goods_receipts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."goods_receipts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE governed_artifact_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.governed_artifact_events TO nexora_tenant_app;


--
-- Name: SEQUENCE "governed_artifact_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."governed_artifact_events_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE governed_artifact_versions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.governed_artifact_versions TO nexora_tenant_app;


--
-- Name: SEQUENCE "governed_artifact_versions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."governed_artifact_versions_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE governed_artifacts; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.governed_artifacts TO nexora_tenant_app;


--
-- Name: SEQUENCE "governed_artifacts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."governed_artifacts_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE human_action_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.human_action_events TO nexora_tenant_app;


--
-- Name: SEQUENCE "human_action_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."human_action_events_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE human_action_items; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.human_action_items TO nexora_tenant_app;


--
-- Name: SEQUENCE "human_action_items_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."human_action_items_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE inbound_logistics_policies; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.inbound_logistics_policies TO nexora_tenant_app;


--
-- Name: TABLE incoming_inventory; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.incoming_inventory TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.incoming_inventory TO nexora_pipeline_app;


--
-- Name: SEQUENCE "incoming_inventory_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."incoming_inventory_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."incoming_inventory_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE inventory_movements; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.inventory_movements TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.inventory_movements TO nexora_pipeline_app;


--
-- Name: SEQUENCE "inventory_movements_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."inventory_movements_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."inventory_movements_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE inventory_reorder_alerts; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.inventory_reorder_alerts TO nexora_tenant_app;
GRANT SELECT,UPDATE ON TABLE public.inventory_reorder_alerts TO nexora_pipeline_app;


--
-- Name: TABLE lead_assignments; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lead_assignments TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lead_assignments TO nexora_pipeline_app;


--
-- Name: SEQUENCE "lead_assignments_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."lead_assignments_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."lead_assignments_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE lead_customer_match_candidates; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lead_customer_match_candidates TO nexora_tenant_app;


--
-- Name: SEQUENCE "lead_customer_match_candidates_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."lead_customer_match_candidates_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE lead_line_commercial_resolutions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.lead_line_commercial_resolutions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lead_line_commercial_resolutions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "lead_line_commercial_resolutions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."lead_line_commercial_resolutions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."lead_line_commercial_resolutions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE lead_routing_decisions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lead_routing_decisions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lead_routing_decisions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "lead_routing_decisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."lead_routing_decisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."lead_routing_decisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE learning_governance_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.learning_governance_events TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.learning_governance_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "learning_governance_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."learning_governance_events_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."learning_governance_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE lifecycle_outbox_messages; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lifecycle_outbox_messages TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.lifecycle_outbox_messages TO nexora_pipeline_app;


--
-- Name: SEQUENCE "lifecycle_outbox_messages_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."lifecycle_outbox_messages_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."lifecycle_outbox_messages_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE material_lot_certificates; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.material_lot_certificates TO nexora_tenant_app;


--
-- Name: TABLE material_lot_consumptions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.material_lot_consumptions TO nexora_tenant_app;


--
-- Name: TABLE material_lots; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.material_lots TO nexora_tenant_app;


--
-- Name: SEQUENCE nexora_rfq_number_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.nexora_rfq_number_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.nexora_rfq_number_seq TO nexora_pipeline_app;


--
-- Name: SEQUENCE nexora_supplier_po_doc_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.nexora_supplier_po_doc_seq TO nexora_tenant_app;


--
-- Name: TABLE ports_of_entry; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.ports_of_entry TO nexora_tenant_app;


--
-- Name: TABLE procurement_callback_receipts; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.procurement_callback_receipts TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.procurement_callback_receipts TO nexora_pipeline_app;


--
-- Name: SEQUENCE "procurement_callback_receipts_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."procurement_callback_receipts_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."procurement_callback_receipts_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE procurement_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.procurement_events TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.procurement_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "procurement_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."procurement_events_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."procurement_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE procurement_handoffs; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.procurement_handoffs TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.procurement_handoffs TO nexora_pipeline_app;


--
-- Name: SEQUENCE "procurement_handoffs_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."procurement_handoffs_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."procurement_handoffs_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE procurement_outbox; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.procurement_outbox TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.procurement_outbox TO nexora_pipeline_app;


--
-- Name: SEQUENCE "procurement_outbox_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."procurement_outbox_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."procurement_outbox_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE product_aliases; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.product_aliases TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.product_aliases TO nexora_pipeline_app;


--
-- Name: SEQUENCE "product_aliases_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."product_aliases_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."product_aliases_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE product_supersessions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.product_supersessions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.product_supersessions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "product_supersessions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."product_supersessions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."product_supersessions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE quote_delivery_requests; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.quote_delivery_requests TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.quote_delivery_requests TO nexora_pipeline_app;


--
-- Name: SEQUENCE "quote_delivery_requests_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."quote_delivery_requests_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."quote_delivery_requests_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE sales_coaching_acknowledgements; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.sales_coaching_acknowledgements TO nexora_tenant_app;


--
-- Name: SEQUENCE "sales_coaching_acknowledgements_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."sales_coaching_acknowledgements_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE sales_contributions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.sales_contributions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sales_contributions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "sales_contributions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."sales_contributions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."sales_contributions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE sales_rep_profiles; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.sales_rep_profiles TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sales_rep_profiles TO nexora_pipeline_app;


--
-- Name: SEQUENCE "sales_rep_profiles_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."sales_rep_profiles_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."sales_rep_profiles_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE sales_team_memberships; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.sales_team_memberships TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sales_team_memberships TO nexora_pipeline_app;


--
-- Name: SEQUENCE "sales_team_memberships_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."sales_team_memberships_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."sales_team_memberships_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE "setUOM"; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."setUOM" TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."setUOM" TO nexora_pipeline_app;


--
-- Name: SEQUENCE "setUOM_UomID_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."setUOM_UomID_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."setUOM_UomID_seq" TO nexora_pipeline_app;


--
-- Name: TABLE source_document_occurrences; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.source_document_occurrences TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.source_document_occurrences TO nexora_pipeline_app;


--
-- Name: SEQUENCE source_document_occurrences_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.source_document_occurrences_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.source_document_occurrences_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE source_documents; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.source_documents TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.source_documents TO nexora_pipeline_app;


--
-- Name: SEQUENCE source_documents_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.source_documents_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.source_documents_id_seq TO nexora_pipeline_app;


--
-- Name: TABLE sourcing_case_candidates; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sourcing_case_candidates TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sourcing_case_candidates TO nexora_pipeline_app;


--
-- Name: SEQUENCE "sourcing_case_candidates_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."sourcing_case_candidates_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."sourcing_case_candidates_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE sourcing_cases; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.sourcing_cases TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sourcing_cases TO nexora_pipeline_app;


--
-- Name: SEQUENCE "sourcing_cases_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."sourcing_cases_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."sourcing_cases_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE stock_reservations; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.stock_reservations TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.stock_reservations TO nexora_pipeline_app;


--
-- Name: SEQUENCE "stock_reservations_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."stock_reservations_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."stock_reservations_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_negotiation_decisions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.supplier_negotiation_decisions TO nexora_tenant_app;


--
-- Name: SEQUENCE "supplier_negotiation_decisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_negotiation_decisions_Id_seq" TO nexora_tenant_app;


--
-- Name: TABLE supplier_purchase_order_lines; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.supplier_purchase_order_lines TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_purchase_order_lines TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_purchase_order_lines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_purchase_order_lines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_purchase_order_lines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_purchase_orders; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.supplier_purchase_orders TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_purchase_orders TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_purchase_orders_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_purchase_orders_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_purchase_orders_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_quote_field_evidence; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.supplier_quote_field_evidence TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_quote_field_evidence TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_quote_field_evidence_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_quote_field_evidence_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_quote_field_evidence_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_quote_lines; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.supplier_quote_lines TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_quote_lines TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_quote_lines_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_quote_lines_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_quote_lines_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_quote_review_decisions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.supplier_quote_review_decisions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_quote_review_decisions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_quote_review_decisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_quote_review_decisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_quote_review_decisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_quote_revisions; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.supplier_quote_revisions TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_quote_revisions TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_quote_revisions_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_quote_revisions_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_quote_revisions_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_quotes; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.supplier_quotes TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_quotes TO nexora_pipeline_app;


--
-- Name: SEQUENCE "supplier_quotes_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."supplier_quotes_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."supplier_quotes_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE supplier_shipment_lines; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_shipment_lines TO nexora_tenant_app;
GRANT SELECT ON TABLE public.supplier_shipment_lines TO nexora_pipeline_app;


--
-- Name: TABLE supplier_shipments; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.supplier_shipments TO nexora_tenant_app;
GRANT SELECT ON TABLE public.supplier_shipments TO nexora_pipeline_app;


--
-- Name: TABLE tenant_governance_audit_events; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.tenant_governance_audit_events TO nexora_tenant_app;
GRANT SELECT,INSERT ON TABLE public.tenant_governance_audit_events TO nexora_pipeline_app;


--
-- Name: SEQUENCE "tenant_governance_audit_events_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."tenant_governance_audit_events_Id_seq" TO nexora_tenant_app;
GRANT SELECT,USAGE ON SEQUENCE public."tenant_governance_audit_events_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE unassigned_work_items; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.unassigned_work_items TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.unassigned_work_items TO nexora_pipeline_app;


--
-- Name: SEQUENCE "unassigned_work_items_Id_seq"; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public."unassigned_work_items_Id_seq" TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public."unassigned_work_items_Id_seq" TO nexora_pipeline_app;


--
-- Name: TABLE validation_findings; Type: ACL; Schema: public; Owner: -
--

GRANT SELECT,INSERT ON TABLE public.validation_findings TO nexora_tenant_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.validation_findings TO nexora_pipeline_app;


--
-- Name: SEQUENCE validation_findings_id_seq; Type: ACL; Schema: public; Owner: -
--

GRANT USAGE ON SEQUENCE public.validation_findings_id_seq TO nexora_tenant_app;
GRANT ALL ON SEQUENCE public.validation_findings_id_seq TO nexora_pipeline_app;


--
-- PostgreSQL database dump complete
--

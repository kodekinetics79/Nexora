-- ---------------------------------------------------------------------------
-- Explicit REVOKEs transcribed verbatim from the migrations that issued them.
--
-- pg_dump cannot emit these: revoking a privilege a role never held leaves the
-- table with no non-owner ACL entry, so the dump has nothing to print. The
-- statements are still replayed because they are the recorded intent that the
-- migration history holds, and because issuing them materialises the same
-- pg_class.relacl state DB_A has (owner-default entries present rather than
-- relacl = NULL), which is what a catalogue-level parity check compares.
--
--   20260723120000_CompleteTenantRlsCoverage
--   20260723230000_GovernStatementsAndDunning
--   20260728134117_ConfigureDatabaseExecutionRoles
-- ---------------------------------------------------------------------------
REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory" FROM nexora_tenant_app;
REVOKE ALL ON public."FinanceProviderSecrets" FROM PUBLIC;
REVOKE ALL ON public."FinanceProviderSecrets" FROM nexora_tenant_app;
REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory"
    FROM nexora_identity_app;
REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory", public."FinanceProviderSecrets"
    FROM nexora_pipeline_app;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformAuditLogs"
    FROM nexora_pipeline_app;

-- Put search_path back before control returns to EF Core. The replay above ran with
-- pg_dump's empty search_path; EF's own `INSERT INTO "__EFMigrationsHistory"` runs in
-- this same transaction and is not schema-qualified, so it would fail without this.
SELECT pg_catalog.set_config(
    'search_path',
    current_setting('nexora.squashed_baseline_saved_search_path'),
    true);

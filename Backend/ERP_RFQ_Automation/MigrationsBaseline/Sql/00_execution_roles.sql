-- ---------------------------------------------------------------------------
-- Execution roles.
--
-- Transcribed verbatim from the migrations that created them, because pg_dump
-- never emits roles (they are cluster-scoped, not database-scoped) and every
-- GRANT and every `TO nexora_tenant_app` policy below depends on them existing.
--
--   nexora_tenant_app    20260723031900_AddTenantRowLevelSecurity
--   nexora_identity_app  20260728134117_ConfigureDatabaseExecutionRoles
--   nexora_pipeline_app  20260728134117_ConfigureDatabaseExecutionRoles
--
-- NOINHERIT on all three, and on the migrating role itself, is the control that
-- forces an explicit SET ROLE instead of silent privilege inheritance
-- (20260723140000_AddAiGovernanceLedger). BYPASSRLS is deliberately asymmetric:
-- the tenant role is NOBYPASSRLS (it is the role RLS is written against), the
-- identity and pipeline roles are BYPASSRLS.
-- ---------------------------------------------------------------------------
DO $roles$
DECLARE runtime_role name;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
        CREATE ROLE nexora_tenant_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
        CREATE ROLE nexora_identity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
        CREATE ROLE nexora_pipeline_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
    END IF;

    EXECUTE format('GRANT nexora_tenant_app TO %I', current_user);
    EXECUTE format('GRANT nexora_identity_app, nexora_pipeline_app TO %I', current_user);
    FOR runtime_role IN
        SELECT rolname
        FROM pg_roles
        WHERE rolcanlogin
          AND NOT rolinherit
          AND NOT rolsuper
          AND NOT rolbypassrls
          AND pg_has_role(oid, 'nexora_tenant_app', 'MEMBER')
    LOOP
        EXECUTE format(
            'GRANT nexora_identity_app, nexora_pipeline_app TO %I', runtime_role);
    END LOOP;

    EXECUTE format('ALTER ROLE %I NOINHERIT', current_user);
END
$roles$;

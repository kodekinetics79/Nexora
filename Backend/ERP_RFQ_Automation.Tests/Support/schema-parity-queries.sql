-- =====================================================================================
-- schema-parity-queries.sql
--
-- PURPOSE
--   Dump every NON-TABLE security and behavioural control that lives in the database
--   into a deterministic, sorted, line-oriented form so that two databases can be
--   diffed against each other with plain `diff`.
--
--   Intended use: the migration squash. Build database A by applying the 134 existing
--   migrations; build database B by applying the single hand-authored baseline. Run
--   this file against both and diff. Any line that appears in A but not in B is a
--   control the squash dropped.
--
-- WHAT IT COVERS
--   1  extensions                    9  triggers (incl. tgenabled / ENABLE ALWAYS)
--   2  roles + role attributes      10  functions (incl. body hash, SECURITY DEFINER)
--   3  role memberships             11  CHECK constraints
--   4  schema privileges            12  FOREIGN KEY constraints
--   5  table privileges             13  UNIQUE / EXCLUDE / PRIMARY KEY constraints
--   6  COLUMN-level privileges      14  indexes (incl. partial predicates)
--   7  sequence + function privs    15  column defaults
--   8  RLS enabled/forced + policies 16 sequences
--                                   17 rollup counts (fast smoke test)
--
-- HOW TO RUN
--   psql "$CONNSTRING" -X -q -A -F '|' -t -v ON_ERROR_STOP=1 \
--        -f schema-parity-queries.sql > baseline.txt
--   diff -u legacy.txt baseline.txt
--
--   -A -F '|' -t gives unaligned, pipe-separated, header-less output, which is the
--   only psql format that is byte-stable across servers. Every query below ends with
--   an explicit ORDER BY over its full projection so row order never depends on the
--   planner. Whitespace inside function bodies and policy expressions is normalised
--   with regexp_replace so that a reformatted-but-identical definition does not show
--   up as a diff; if you want to catch reformatting too, drop the normalisation.
--
-- SCOPE NOTE
--   Only the 'public' and 'platform' schemas are considered, and only the three
--   application roles plus PUBLIC. Adjust the nexora_scope CTE if that changes.
-- =====================================================================================

\pset pager off

-- Schemas and roles under test. Everything below joins against these.
CREATE TEMP VIEW nexora_schemas(nspname) AS
    VALUES ('public'), ('platform');

CREATE TEMP VIEW nexora_roles(rolname) AS
    VALUES ('nexora_tenant_app'), ('nexora_identity_app'), ('nexora_pipeline_app'), ('PUBLIC');


-- =====================================================================================
-- 1. EXTENSIONS
--    Expect: citext (model-backed), pgcrypto, btree_gist.
--    pgcrypto and btree_gist are NOT in the EF model snapshot and are installed only by
--    raw SQL. btree_gist is a hard dependency of the two EXCLUDE constraints in §13.
-- =====================================================================================
SELECT '01_extension'                         AS section,
       e.extname                              AS extension,
       n.nspname                              AS schema
FROM pg_extension e
JOIN pg_namespace n ON n.oid = e.extnamespace
WHERE e.extname <> 'plpgsql'
ORDER BY 1, 2, 3;


-- =====================================================================================
-- 2. ROLES AND THEIR ATTRIBUTES
--    BYPASSRLS is the load-bearing attribute here:
--      nexora_tenant_app    -> NOBYPASSRLS  (must be false; true silently disables
--                                            every tenant-isolation policy)
--      nexora_identity_app  -> BYPASSRLS    (true)
--      nexora_pipeline_app  -> BYPASSRLS    (true)
--    All three are NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT.
--    NOINHERIT matters: the runtime role SETs to these, it does not inherit them.
-- =====================================================================================
SELECT '02_role'                              AS section,
       r.rolname                              AS role,
       r.rolsuper                             AS is_superuser,
       r.rolinherit                           AS inherits,
       r.rolcreaterole                        AS can_create_role,
       r.rolcreatedb                          AS can_create_db,
       r.rolcanlogin                          AS can_login,
       r.rolbypassrls                         AS bypasses_rls,
       r.rolreplication                       AS is_replication,
       r.rolconnlimit                         AS conn_limit
FROM pg_roles r
WHERE r.rolname LIKE 'nexora\_%'
ORDER BY 1, 2;


-- =====================================================================================
-- 3. ROLE MEMBERSHIPS
--    Each migration grants the app roles TO current_user so the owner connection can
--    SET ROLE into them. Losing this makes TenantRlsCommandInterceptor fail at runtime
--    with 42501 on the very first request, not at migration time.
-- =====================================================================================
SELECT '03_role_member'                       AS section,
       g.rolname                              AS granted_role,
       m.rolname                              AS member,
       a.admin_option                         AS with_admin
FROM pg_auth_members a
JOIN pg_roles g ON g.oid = a.roleid
JOIN pg_roles m ON m.oid = a.member
WHERE g.rolname LIKE 'nexora\_%'
   OR m.rolname LIKE 'nexora\_%'
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 4. SCHEMA-LEVEL PRIVILEGES
--    USAGE ON SCHEMA platform is granted narrowly. Without it every column-level
--    grant in §6 is unreachable regardless of how correct it looks.
-- =====================================================================================
SELECT '04_schema_priv'                       AS section,
       n.nspname                              AS schema,
       grantee.rolname                        AS grantee,
       priv.privilege_type                    AS privilege
FROM pg_namespace n
CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) AS priv
LEFT JOIN pg_roles grantee ON grantee.oid = priv.grantee
WHERE n.nspname IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3 NULLS FIRST, 4;


-- =====================================================================================
-- 5. TABLE-LEVEL PRIVILEGES
--    The append-only contract is expressed here as much as in triggers: a ledger that
--    still carries DELETE for nexora_tenant_app is a ledger the squash got wrong.
--    Reported as one row per (table, grantee, privilege) so a single missing verb
--    shows up as a single missing diff line.
-- =====================================================================================
SELECT '05_table_priv'                        AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       COALESCE(grantee.rolname, 'PUBLIC')    AS grantee,
       priv.privilege_type                    AS privilege,
       priv.is_grantable                      AS grantable
FROM pg_class c
CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('r', c.relowner))) AS priv
LEFT JOIN pg_roles grantee ON grantee.oid = priv.grantee
WHERE c.relkind IN ('r', 'p', 'v', 'm')
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND COALESCE(grantee.rolname, 'PUBLIC') IN (SELECT rolname FROM nexora_roles)
ORDER BY 1, 2, 3, 4, 5, 6;


-- =====================================================================================
-- 6. COLUMN-LEVEL PRIVILEGES   *** highest-risk section ***
--    Twelve statements across four migrations narrow the tenant/identity planes to
--    named columns on platform."Tenants", platform."Plans",
--    platform."ImpersonationSessions", platform."PlatformEmailSettings" and
--    public."Users". A regenerated baseline that issues table-level grants instead
--    parses, deploys, passes every test, and silently re-opens the columns that were
--    deliberately hidden (tenant names, slugs, status reasons, operator emails,
--    BillingModeReason, DeploymentProfileReason).
--
--    NOTE the direction of the check: the danger is EXTRA privilege, not missing. Diff
--    both ways and treat a column present in B but not A as a failure too.
-- =====================================================================================
SELECT '06_column_priv'                       AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       a.attname                              AS column_name,
       COALESCE(grantee.rolname, 'PUBLIC')    AS grantee,
       priv.privilege_type                    AS privilege
FROM pg_class c
JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
CROSS JOIN LATERAL aclexplode(a.attacl) AS priv
LEFT JOIN pg_roles grantee ON grantee.oid = priv.grantee
WHERE c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4, 5, 6;

-- 6b. Tables that carry ANY column-level ACL at all. A table appearing here in A but
--     not in B means the narrowing was replaced wholesale by a table-level grant.
SELECT DISTINCT
       '06b_column_acl_table'                 AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name
FROM pg_class c
JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
WHERE a.attacl IS NOT NULL
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3;


-- =====================================================================================
-- 7. SEQUENCE AND FUNCTION PRIVILEGES
--    Sequences: the pattern throughout is REVOKE ALL then GRANT USAGE only. USAGE
--    permits nextval(); SELECT/UPDATE would permit currval()/setval() and let a tenant
--    read or rewind another tenant's document-number counter.
--    Functions: every guard is REVOKE ALL ... FROM PUBLIC, then EXECUTE granted only
--    to the roles that legitimately call it.
-- =====================================================================================
SELECT '07a_sequence_priv'                    AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS sequence_name,
       COALESCE(grantee.rolname, 'PUBLIC')    AS grantee,
       priv.privilege_type                    AS privilege
FROM pg_class c
-- acldefault object-type code for a SEQUENCE is lowercase 's'
-- (uppercase 'S' is FOREIGN SERVER and yields the wrong default ACL).
CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('s', c.relowner))) AS priv
LEFT JOIN pg_roles grantee ON grantee.oid = priv.grantee
WHERE c.relkind = 'S'
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND COALESCE(grantee.rolname, 'PUBLIC') IN (SELECT rolname FROM nexora_roles)
ORDER BY 1, 2, 3, 4, 5;

SELECT '07b_function_priv'                    AS section,
       p.pronamespace::regnamespace::text     AS schema,
       p.proname                              AS function_name,
       pg_get_function_identity_arguments(p.oid) AS args,
       COALESCE(grantee.rolname, 'PUBLIC')    AS grantee,
       priv.privilege_type                    AS privilege
FROM pg_proc p
CROSS JOIN LATERAL aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) AS priv
LEFT JOIN pg_roles grantee ON grantee.oid = priv.grantee
WHERE p.pronamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND p.proname LIKE 'nexora\_%'
ORDER BY 1, 2, 3, 4, 5, 6;

-- 7c. DEFAULT PRIVILEGES. Two migrations set and then unset ALTER DEFAULT PRIVILEGES
--     in schema public. The net expected state is EMPTY; a non-empty result means a
--     future table is silently granted to a role on creation.
SELECT '07c_default_priv'                     AS section,
       COALESCE(d.defaclnamespace::regnamespace::text, '<all>') AS schema,
       owner.rolname                          AS owner,
       d.defaclobjtype                        AS object_type,
       COALESCE(grantee.rolname, 'PUBLIC')    AS grantee,
       priv.privilege_type                    AS privilege
FROM pg_default_acl d
JOIN pg_roles owner ON owner.oid = d.defaclrole
CROSS JOIN LATERAL aclexplode(d.defaclacl) AS priv
LEFT JOIN pg_roles grantee ON grantee.oid = priv.grantee
ORDER BY 1, 2, 3, 4, 5, 6;


-- =====================================================================================
-- 8. ROW LEVEL SECURITY
--
--    8a. Which tables have RLS enabled, and which additionally FORCE it.
--        ENABLE alone does not bind the table OWNER; FORCE does. Roughly 110 tables
--        carry FORCE today. A table that is ENABLE-only in B but FORCE in A is a
--        tenant-isolation regression that no functional test will catch, because the
--        application connects as a member role, not as the owner — the gap only opens
--        for migrations, data-fix scripts and the purge path.
--
--    8b. Every policy verbatim. `qual` is USING, `with_check` is WITH CHECK.
--        A policy whose `roles` is {public} rather than {nexora_tenant_app} applies to
--        the owner too and is a different control; both spellings exist in the history
--        (the 16 policies from 20260723031900 have no TO clause) so the column is
--        reported rather than filtered.
--
--        *** THE SPELLING TRAP ***
--        The tenant column is "BusinessUnitID" on the legacy tables (Leads, RFQ,
--        Quotes, Orders, Shipments, CommercialCases, LeadStatusHistories, Contacts),
--        "BusinessUnitId" on everything added since, "business_unit_id" on the
--        snake_case evidence tables, and "BUID"/"Buid" on Customers, Suppliers,
--        Products, Users, Teams and Inventory. PostgreSQL folds nothing here: these
--        are quoted identifiers and they are four DIFFERENT columns. A policy written
--        against the wrong one does not fail — CREATE POLICY resolves the identifier
--        at creation time, so a genuinely wrong name raises 42703 at deploy, but a
--        name that happens to exist on the table with different semantics (or a
--        correlated reference inside an EXISTS that resolves to the OUTER table) is
--        accepted and matches nothing. Diff §8b as literal text; do not normalise case.
-- =====================================================================================
SELECT '08a_rls'                              AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       c.relrowsecurity                       AS rls_enabled,
       c.relforcerowsecurity                  AS rls_forced
FROM pg_class c
WHERE c.relkind IN ('r', 'p')
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND (c.relrowsecurity OR c.relforcerowsecurity)
ORDER BY 1, 2, 3;

-- 8a-inverse. Tables with a tenant-discriminator column that do NOT have RLS.
-- This is the query that catches the 43 tables whose policy exists only because of the
-- dynamic information_schema sweep in 20260723120000_CompleteTenantRlsCoverage — among
-- them "Users", "Setup_Master", "RolePermissions", "Products", "Inventory",
-- "Email_Configurations" and "SlaPolicies". A hand-authored baseline that enumerates
-- only the statically-named tables leaves every one of these readable across tenants.
SELECT '08a_inv_tenant_table_without_rls'     AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       string_agg(a.attname, ',' ORDER BY a.attname) AS tenant_columns
FROM pg_class c
JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
WHERE c.relkind IN ('r', 'p')
  AND c.relnamespace::regnamespace::text = 'public'
  AND a.attname IN ('BusinessUnitID', 'BusinessUnitId', 'business_unit_id',
                    'BUID', 'Buid', 'buid')
  AND NOT c.relrowsecurity
GROUP BY 1, 2, 3
ORDER BY 1, 2, 3;

SELECT '08b_policy'                           AS section,
       p.schemaname                           AS schema,
       p.tablename                            AS table_name,
       p.policyname                           AS policy_name,
       p.permissive                           AS permissive,
       array_to_string(p.roles, ',')          AS roles,
       p.cmd                                  AS command,
       regexp_replace(COALESCE(p.qual, '<none>'), '\s+', ' ', 'g')       AS using_expr,
       regexp_replace(COALESCE(p.with_check, '<none>'), '\s+', ' ', 'g') AS with_check_expr
FROM pg_policies p
WHERE p.schemaname IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;

-- 8c. Per-table policy count, so a table that lost one of several policies is obvious
--     even if the expression text also changed.
SELECT '08c_policy_count'                     AS section,
       p.schemaname                           AS schema,
       p.tablename                            AS table_name,
       count(*)::text                         AS policy_count
FROM pg_policies p
WHERE p.schemaname IN (SELECT nspname FROM nexora_schemas)
GROUP BY 1, 2, 3
ORDER BY 1, 2, 3;


-- =====================================================================================
-- 9. TRIGGERS
--
--    tgenabled is the column that matters and the one a naive dump omits:
--       'O' = ENABLE ORIGIN  (the default; does NOT fire under
--                             session_replication_role = 'replica')
--       'A' = ENABLE ALWAYS  (fires in replica mode)
--       'R' = ENABLE REPLICA
--       'D' = DISABLED
--
--    21 triggers are ENABLE ALWAYS on purpose. They are the append-only guards on
--    platform."TenantLifecycleEvents", "TenantExportReceipts", "PlatformAuditLogs",
--    "TenantOffboardings", "TenantLegalHolds", the subscription/usage revenue guards,
--    and platform."ProvisioningExecutions".provisioning_executions_lease_transfer_guard.
--    They exist precisely because the tenant purge path runs under replica mode, where
--    an ordinary trigger silently does not run. A baseline that creates the trigger but
--    omits the following ALTER TABLE ... ENABLE ALWAYS produces a database that looks
--    identical in every catalogue except this one byte, and the guard is off exactly
--    when it is needed.
--
--    tgtype is reported raw AND decoded; it encodes BEFORE/AFTER/INSTEAD OF,
--    ROW/STATEMENT and the INSERT/DELETE/UPDATE/TRUNCATE mask.
-- =====================================================================================
SELECT '09_trigger'                           AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       t.tgname                               AS trigger_name,
       t.tgenabled                            AS tgenabled,
       CASE t.tgenabled WHEN 'O' THEN 'ENABLE ORIGIN'
                        WHEN 'A' THEN 'ENABLE ALWAYS'
                        WHEN 'R' THEN 'ENABLE REPLICA'
                        WHEN 'D' THEN 'DISABLED' END AS enabled_mode,
       t.tgtype::int                          AS tgtype_raw,
       CASE WHEN (t.tgtype::int & 1) = 1 THEN 'ROW' ELSE 'STATEMENT' END AS level,
       CASE WHEN (t.tgtype::int & 2) = 2 THEN 'BEFORE'
            WHEN (t.tgtype::int & 64) = 64 THEN 'INSTEAD OF'
            ELSE 'AFTER' END                  AS timing,
       concat_ws('|',
           CASE WHEN (t.tgtype::int & 4)  = 4  THEN 'INSERT'   END,
           CASE WHEN (t.tgtype::int & 8)  = 8  THEN 'DELETE'   END,
           CASE WHEN (t.tgtype::int & 16) = 16 THEN 'UPDATE'   END,
           CASE WHEN (t.tgtype::int & 32) = 32 THEN 'TRUNCATE' END) AS events,
       t.tgdeferrable                         AS deferrable,
       t.tginitdeferred                       AS initially_deferred,
       p.pronamespace::regnamespace::text || '.' || p.proname AS function_name,
       -- pg_get_triggerdef is the authoritative rendering: it carries UPDATE OF
       -- <columns>, the WHEN clause, CONSTRAINT/DEFERRABLE and the function call
       -- arguments in one string, and it is the field to diff first.
       -- It does NOT carry tgenabled, which is why the column above exists.
       regexp_replace(pg_get_triggerdef(t.oid), '\s+', ' ', 'g') AS trigger_def
FROM pg_trigger t
JOIN pg_class c ON c.oid = t.tgrelid
JOIN pg_proc p  ON p.oid = t.tgfoid
WHERE NOT t.tgisinternal
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;

-- 9b. The ENABLE ALWAYS set on its own. Short enough to eyeball; it should contain
--     exactly the 21 triggers listed in the inventory.
SELECT '09b_enable_always'                    AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       t.tgname                               AS trigger_name
FROM pg_trigger t
JOIN pg_class c ON c.oid = t.tgrelid
WHERE NOT t.tgisinternal
  AND t.tgenabled = 'A'
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;

-- 9c. Any trigger that is DISABLED. Expected to be empty. Two migrations temporarily
--     DISABLE a trigger around a backfill and re-ENABLE it; a row here means one of
--     those re-enables was lost.
SELECT '09c_disabled_trigger'                 AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       t.tgname                               AS trigger_name,
       t.tgenabled                            AS tgenabled
FROM pg_trigger t
JOIN pg_class c ON c.oid = t.tgrelid
WHERE NOT t.tgisinternal
  AND t.tgenabled = 'D'
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 10. FUNCTIONS
--
--     142 distinct guard functions. Several were redefined by later migrations with
--     CREATE OR REPLACE, so only the LAST definition is authoritative — which is
--     exactly the thing a squash gets wrong by copying an early version.
--
--     prosecdef (SECURITY DEFINER) and proconfig (SET search_path) are reported
--     separately because losing either turns a working guard into a privilege
--     escalation or a search_path hijack. Every SECURITY DEFINER function in this
--     schema carries `SET search_path = pg_catalog, public` (some also `, platform`).
--
--     10a gives an md5 of the whitespace-normalised body: a compact, diffable identity.
--     10b gives the full body for the functions whose exact text is load-bearing.
-- =====================================================================================
SELECT '10a_function'                         AS section,
       p.pronamespace::regnamespace::text     AS schema,
       p.proname                              AS function_name,
       pg_get_function_identity_arguments(p.oid) AS args,
       pg_get_function_result(p.oid)          AS returns,
       l.lanname                              AS language,
       p.prosecdef                            AS security_definer,
       p.provolatile                          AS volatility,
       COALESCE(array_to_string(p.proconfig, ','), '<none>') AS config,
       md5(regexp_replace(p.prosrc, '\s+', ' ', 'g')) AS body_md5,
       length(regexp_replace(p.prosrc, '\s+', ' ', 'g'))::text AS body_len
FROM pg_proc p
JOIN pg_language l ON l.oid = p.prolang
WHERE p.pronamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND p.prokind = 'f'
  AND (p.proname LIKE 'nexora\_%' OR p.proname LIKE 'wave1\_%')
ORDER BY 1, 2, 3, 4;

-- 10b. Full normalised bodies. Verbose, but this is the only way to see WHICH branch of
--      a rewritten guard survived. Notable members: nexora_write_otc_audit (its
--      (command_type, previous_state, new_state) whitelist gained CANCEL_PURCHASE_ORDER
--      and ACCEPT_PO_DIFFERENCES in 20260810145424 — an older copy raises 23514 on a
--      cancellation the application has already committed to), and
--      platform.nexora_guard_provisioning_lease_transfer (ERRCODE 55006).
SELECT '10b_function_body'                    AS section,
       p.pronamespace::regnamespace::text     AS schema,
       p.proname                              AS function_name,
       pg_get_function_identity_arguments(p.oid) AS args,
       regexp_replace(p.prosrc, '\s+', ' ', 'g') AS body
FROM pg_proc p
WHERE p.pronamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND p.prokind = 'f'
  AND (p.proname LIKE 'nexora\_%' OR p.proname LIKE 'wave1\_%')
ORDER BY 1, 2, 3, 4;

-- 10c. Every distinct ERRCODE raised by a guard. These are a contract with the
--      application's exception handling: 55000 (object not in prerequisite state,
--      used for every append-only refusal), 55006 (object in use — the provisioning
--      lease fence), 23514 (check violation), 23503 (FK violation), 42501 (insufficient
--      privilege), 40001 (serialization failure), 23P01 (exclusion violation).
--      A baseline that drops or renames one turns a handled refusal into a 500.
SELECT DISTINCT
       '10c_errcode'                          AS section,
       m[1]                                   AS errcode
FROM pg_proc p
CROSS JOIN LATERAL regexp_matches(p.prosrc, 'ERRCODE\s*=\s*''([0-9A-Za-z]+)''', 'g') AS m
WHERE p.pronamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND (p.proname LIKE 'nexora\_%' OR p.proname LIKE 'wave1\_%')
ORDER BY 1, 2;


-- =====================================================================================
-- 11. CHECK CONSTRAINTS
--     ~120 of these are added by raw SQL and are NOT present in the EF model snapshot,
--     so EF regenerating from the model emits none of them. They carry real invariants:
--     hash shape regexes, status enumerations, non-negative amounts, evidence tuples,
--     and the AI_NOT_AUTHORIZED outcome-state list.
--     conislocal/coninhcount are reported so an inherited-vs-local difference shows up.
-- =====================================================================================
SELECT '11_check'                             AS section,
       c.connamespace::regnamespace::text     AS schema,
       rel.relname                            AS table_name,
       c.conname                              AS constraint_name,
       regexp_replace(pg_get_constraintdef(c.oid), '\s+', ' ', 'g') AS definition,
       c.convalidated                         AS validated
FROM pg_constraint c
JOIN pg_class rel ON rel.oid = c.conrelid
WHERE c.contype = 'c'
  AND c.connamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 12. FOREIGN KEY CONSTRAINTS
--     The composite tenant-scoped FKs (FK_sales_activity_tenant_user,
--     FK_customer_owner_tenant_customer, FK_LedgerAccounts_Currency_Tenant, the eight
--     FK_lead_* / FK_unassigned_* in 20260730234426, and the two
--     FK_sales_coaching_ack_* ) are raw SQL and absent from the model. Each depends on
--     a composite UNIQUE index from §14 that is ALSO absent from the model — so losing
--     the index silently makes the FK uncreatable, and losing the FK removes the only
--     thing stopping a row referencing another tenant's user.
--     Two FKs are NOT VALID by design (FK__RolePermi__RoleI__*, FK__Users__RoleID__*);
--     convalidated preserves that.
-- =====================================================================================
SELECT '12_foreign_key'                       AS section,
       c.connamespace::regnamespace::text     AS schema,
       rel.relname                            AS table_name,
       c.conname                              AS constraint_name,
       regexp_replace(pg_get_constraintdef(c.oid), '\s+', ' ', 'g') AS definition,
       c.convalidated                         AS validated,
       c.condeferrable                        AS deferrable,
       c.condeferred                          AS initially_deferred
FROM pg_constraint c
JOIN pg_class rel ON rel.oid = c.conrelid
WHERE c.contype = 'f'
  AND c.connamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 13. UNIQUE / PRIMARY KEY / EXCLUDE CONSTRAINTS
--     Two EXCLUDE constraints exist and both need btree_gist (§1):
--       EX_UsageCoverageSegments_NoAuthoritativeOverlap
--       EX_SubscriptionTaxRules_ApprovedInterval
--     They are the only thing preventing two overlapping authoritative usage-coverage
--     windows and two overlapping approved tax rates — i.e. double-billing.
-- =====================================================================================
SELECT '13_uniq_pk_excl'                      AS section,
       c.connamespace::regnamespace::text     AS schema,
       rel.relname                            AS table_name,
       c.conname                              AS constraint_name,
       c.contype                              AS constraint_type,
       regexp_replace(pg_get_constraintdef(c.oid), '\s+', ' ', 'g') AS definition
FROM pg_constraint c
JOIN pg_class rel ON rel.oid = c.conrelid
WHERE c.contype IN ('u', 'p', 'x')
  AND c.connamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 14. INDEXES
--     pg_get_indexdef renders the full definition including the partial WHERE clause
--     and any expression columns, which is where the business rules hide:
--       UX_ProvisioningExecutions_LiveSlug  UNIQUE (Slug) WHERE State IN
--                                           ('Pending','Running','Failed')   [model-backed]
--       UX_RFQ_BusinessUnitID_LeadID        UNIQUE WHERE LeadID IS NOT NULL   [raw SQL]
--       UX_Quotes_BusinessUnitID_RFQID      UNIQUE WHERE RFQID IS NOT NULL    [raw SQL]
--       UX_customer_ownerships_single_active UNIQUE on an expression
--                                           COALESCE(ScopeKey,'') WHERE IsActive
--       UX_product_aliases_tenant_identity  UNIQUE on COALESCE(AccountId, 0)
--       UX_AiCallAttempts_ProviderRequestId UNIQUE WHERE ProviderRequestId IS NOT NULL
--       UX_BankMatchingRules_BU_ActiveTenant UNIQUE WHERE Status='Active' AND
--                                            BankAccountId IS NULL
--     plus the composite UNIQUE indexes the raw-SQL FKs in §12 depend on:
--       UX_Users_BUID_ID, UX_Teams_BusinessUnitID_ID, UX_Currency_BusinessUnitID_ID,
--       UX_lead_assignments_BusinessUnitId_Id, UX_follow_up_tasks_BusinessUnitId_Id.
--     Eleven of the sixteen raw-SQL indexes are absent from the model snapshot.
-- =====================================================================================
SELECT '14_index'                             AS section,
       i.schemaname                           AS schema,
       i.tablename                            AS table_name,
       i.indexname                            AS index_name,
       regexp_replace(i.indexdef, '\s+', ' ', 'g') AS definition
FROM pg_indexes i
WHERE i.schemaname IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;

-- 14b. Partial and expression indexes only — the subset where a silently different
--      predicate changes what is enforceable rather than only what is fast.
SELECT '14b_partial_or_expression_index'      AS section,
       n.nspname                              AS schema,
       t.relname                              AS table_name,
       ic.relname                             AS index_name,
       ix.indisunique                         AS is_unique,
       COALESCE(regexp_replace(pg_get_expr(ix.indpred, ix.indrelid), '\s+', ' ', 'g'), '<not partial>') AS predicate,
       COALESCE(regexp_replace(pg_get_expr(ix.indexprs, ix.indrelid), '\s+', ' ', 'g'), '<no expressions>') AS expressions
FROM pg_index ix
JOIN pg_class ic ON ic.oid = ix.indexrelid
JOIN pg_class t  ON t.oid  = ix.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE (ix.indpred IS NOT NULL OR ix.indexprs IS NOT NULL)
  AND n.nspname IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 15. COLUMN DEFAULTS AND IDENTITY
--     Two defaults were corrected by data-carrying migrations and must persist:
--       public."SlaPolicies"."QuoteNoResponseExpiryDays"  DEFAULT 90  (was 0; 0 expires
--            every open quote on the first sweep tick — 20260810110923)
--       public."Quotes"."FinancialCalculationVersion"     DEFAULT 2   (20260723150000)
--     Reported for all columns because a default is cheap to lose and invisible when it
--     is wrong in the safe direction until the one row that omits the value arrives.
-- =====================================================================================
SELECT '15_column_default'                    AS section,
       c.relnamespace::regnamespace::text     AS schema,
       c.relname                              AS table_name,
       a.attname                              AS column_name,
       a.attnotnull                           AS not_null,
       a.attidentity                          AS identity,
       a.attgenerated                         AS generated,
       COALESCE(regexp_replace(pg_get_expr(ad.adbin, ad.adrelid), '\s+', ' ', 'g'), '<none>') AS default_expr
FROM pg_attribute a
JOIN pg_class c ON c.oid = a.attrelid
LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
WHERE c.relkind IN ('r', 'p')
  AND a.attnum > 0
  AND NOT a.attisdropped
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND (ad.adbin IS NOT NULL OR a.attidentity <> '' OR a.attgenerated <> '')
ORDER BY 1, 2, 3, 4;


-- =====================================================================================
-- 16. SEQUENCES
--     Three sequences are created by raw SQL and are the server-side authority for
--     document numbering:
--       public."CommercialCaseReferenceSequence"
--       public.nexora_rfq_number_seq
--       public.nexora_supplier_po_doc_seq
--     Their EXISTENCE is what matters for parity; last_value is data and is deliberately
--     not compared.
-- =====================================================================================
SELECT '16_sequence'                          AS section,
       s.schemaname                           AS schema,
       s.sequencename                         AS sequence_name,
       s.data_type::text                      AS data_type,
       s.start_value::text                    AS start_value,
       s.increment_by::text                   AS increment_by,
       s.cycle                                AS cycles
FROM pg_sequences s
WHERE s.schemaname IN (SELECT nspname FROM nexora_schemas)
ORDER BY 1, 2, 3;


-- =====================================================================================
-- 17. ROLLUP COUNTS
--     Run this section alone for a 30-second smoke test before diffing everything.
--     A count that differs tells you which section to look at.
-- =====================================================================================
SELECT '17_count' AS section, 'extensions' AS metric, count(*)::text AS value
FROM pg_extension WHERE extname <> 'plpgsql'
UNION ALL
SELECT '17_count', 'nexora_roles', count(*)::text
FROM pg_roles WHERE rolname LIKE 'nexora\_%'
UNION ALL
SELECT '17_count', 'rls_enabled_tables', count(*)::text
FROM pg_class WHERE relkind IN ('r','p') AND relrowsecurity
  AND relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'rls_forced_tables', count(*)::text
FROM pg_class WHERE relkind IN ('r','p') AND relforcerowsecurity
  AND relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'policies', count(*)::text
FROM pg_policies WHERE schemaname IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'triggers', count(*)::text
FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
WHERE NOT t.tgisinternal AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'triggers_enable_always', count(*)::text
FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
WHERE NOT t.tgisinternal AND t.tgenabled = 'A'
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'nexora_functions', count(*)::text
FROM pg_proc WHERE prokind = 'f'
  AND pronamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
  AND (proname LIKE 'nexora\_%' OR proname LIKE 'wave1\_%')
UNION ALL
SELECT '17_count', 'security_definer_functions', count(*)::text
FROM pg_proc WHERE prokind = 'f' AND prosecdef
  AND pronamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'check_constraints', count(*)::text
FROM pg_constraint WHERE contype = 'c'
  AND connamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'foreign_keys', count(*)::text
FROM pg_constraint WHERE contype = 'f'
  AND connamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'exclusion_constraints', count(*)::text
FROM pg_constraint WHERE contype = 'x'
  AND connamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'indexes', count(*)::text
FROM pg_indexes WHERE schemaname IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'partial_indexes', count(*)::text
FROM pg_index ix JOIN pg_class t ON t.oid = ix.indrelid
WHERE ix.indpred IS NOT NULL
  AND t.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'column_level_acls', count(*)::text
FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
WHERE a.attacl IS NOT NULL AND a.attnum > 0 AND NOT a.attisdropped
  AND c.relnamespace::regnamespace::text IN (SELECT nspname FROM nexora_schemas)
UNION ALL
SELECT '17_count', 'tenant_tables_without_rls', count(*)::text
FROM (
    SELECT DISTINCT c.oid
    FROM pg_class c
    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
    WHERE c.relkind IN ('r','p')
      AND c.relnamespace::regnamespace::text = 'public'
      AND a.attname IN ('BusinessUnitID','BusinessUnitId','business_unit_id','BUID','Buid','buid')
      AND NOT c.relrowsecurity
) gap
ORDER BY 1, 2;

-- =====================================================================================
-- END
-- =====================================================================================

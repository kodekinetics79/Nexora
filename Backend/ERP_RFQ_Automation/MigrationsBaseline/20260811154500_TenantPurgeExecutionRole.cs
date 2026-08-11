using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Gives the tenant purge an identity the row-level-security policies actually match, so that
    /// a DELETE issued by an offboarding reaches the rows it names.
    ///
    /// <para><b>THE DEFECT.</b> <c>TenantPurgeExecutor</c> deletes on the OWNER connection — it has
    /// to, because <c>session_replication_role = 'replica'</c> is a superuser-restricted setting
    /// and is what suspends the ~30 append-only guards and the foreign-key triggers. It issues no
    /// <c>SET ROLE</c>. Migrations reuse the runtime username
    /// (<c>Program.cs ResolveDirectMigrationConnection</c>), so the runtime login role also OWNS
    /// every table, and <c>ValidateRuntimeDatabaseRoleAsync</c> requires that role to be
    /// <c>NOINHERIT</c>. On a table declared <c>FORCE ROW LEVEL SECURITY</c> the owner IS bound by
    /// RLS, and PostgreSQL matches a policy's role list with <c>has_privs_of_role()</c>, which for
    /// a <c>NOINHERIT</c> role does not include roles it is merely a member of. Every tenant policy
    /// in this schema is written <c>TO nexora_tenant_app</c>. None of them match the owner.</para>
    ///
    /// <para>The DELETE therefore affects zero rows and raises nothing. Reproduced against
    /// postgres:16 with this exact role topology: on a <c>FORCE</c> table the owner's
    /// <c>DELETE</c> returned <c>DELETE 0</c> while the identical statement on an
    /// <c>ENABLE</c>-only table returned <c>DELETE 3</c>, in the same transaction, and the
    /// transaction committed. 100 of the 195 tenant-plane tables a purge sweeps carry
    /// <c>FORCE</c>. <c>public."BusinessUnits"</c> does not, so the tenant's anchor row IS
    /// destroyed and the customer disappears from every screen while their data stays.</para>
    ///
    /// <para><b>Reproduced a second way, which is why the fix is not just a role switch.</b> With an
    /// <c>INHERIT</c> owner — same privileges otherwise — the policy role list DOES match, and the
    /// DELETE still returned <c>DELETE 0</c>: the executor never sets
    /// <c>nexora.business_unit_id</c>, so <c>nexora_tenant_isolation</c>'s predicate evaluates
    /// against NULL. Any fix has to satisfy BOTH the role list and a predicate.</para>
    ///
    /// <para><b>WHAT THIS MIGRATION DOES.</b> Creates <c>nexora_purge_app</c> and gives it one
    /// named policy per table a purge is allowed to clear, scoped to the single tenant named by
    /// <c>nexora.purge_business_unit_id</c>. The executor sets that GUC and
    /// <c>SET LOCAL ROLE</c>s into this role for the deletes only.</para>
    ///
    /// <para><b>Why a role and a policy rather than the alternatives.</b></para>
    ///
    /// <para><i>Not BYPASSRLS.</i> <c>nexora_pipeline_app</c> already carries it, so a one-line
    /// <c>SET LOCAL ROLE nexora_pipeline_app</c> looks like the whole fix. It is not, on two counts.
    /// It would not even work: BYPASSRLS bypasses row-level security, not table privileges, and
    /// that role holds no DELETE on 41 of the 195 purge targets — including
    /// <c>public."CommercialMatchingPolicies"</c>, the FORCE table the regression test uses — so the
    /// sweep would have died on 42501 rather than silently deleting nothing. (Measured during
    /// certification; an earlier draft of this comment claimed it would have worked, which was
    /// wrong.) And it routes tenant destruction through the widest role in the system — the same role
    /// <c>TenantRlsCommandInterceptor</c> selects for platform request paths — and it makes the
    /// purge invisible to RLS, so nothing in the database constrains which tenant a purge reaches.
    /// This role is deliberately <c>NOBYPASSRLS</c>: with no GUC set it can see nothing at all, and
    /// with one set it can see exactly one tenant. That is a control the previous owner path did
    /// not have.</para>
    ///
    /// <para><i>Not <c>ALTER TABLE ... NO FORCE</c> for the life of the transaction.</i> It is
    /// genuinely transactional — the flag rolls back with the transaction and no concurrent session
    /// can observe it off, because the <c>ALTER</c> holds <c>ACCESS EXCLUSIVE</c> until commit. It
    /// was rejected on two grounds. First that lock: <c>ACCESS EXCLUSIVE</c> on 100 tables blocks
    /// every read from every other tenant for the whole purge, and queues new readers behind it.
    /// Second, and decisive, it can only fix the DELETE. <c>PreviewAsync</c> counts the same rows
    /// on the same connection to build the operator's confirmation screen, and it must not take a
    /// DDL lock or do DDL at all — so preview and execute would disagree, with preview reporting
    /// zero for the same 100 tables. A role-based fix makes both truthful with no DDL.</para>
    ///
    /// <para><i>Not <c>SET LOCAL ROLE nexora_tenant_app</c>.</i> The policies already match it and
    /// the GUC already exists, so this looked like the cheap answer. It is not available:
    /// <c>nexora_tenant_app</c> holds no DELETE privilege on 84 of the 100 <c>FORCE</c> targets —
    /// the finance, ledger, lead-revision and governance tables where DELETE was deliberately
    /// revoked — so the sweep would fail with 42501 partway through.</para>
    ///
    /// <para><i>Not baking the owner's name into the policies.</i> <c>TO &lt;current_user&gt;</c>
    /// needs no new role at all, but it pins ~200 policy definitions to a role name that differs
    /// per deployment (<c>neondb_owner</c> on the managed target, the runtime username on the
    /// direct one) and would travel into the next <c>pg_dump</c> baseline.</para>
    ///
    /// <para><b>The platform plane is deliberately absent from the policy loop.</b> All four tables
    /// <c>PlatformTenantDataMap</c> classifies as the customer's record —
    /// <c>TenantAdminInvitations</c>, <c>ProvisioningExecutions</c>, <c>ProvisioningSteps</c>,
    /// <c>ProvisioningDrafts</c> — have no row-level security at all, so no policy is needed and
    /// inventing a <c>USING (true)</c> one would be writing a bypass by another name. They get the
    /// privileges and nothing else. If a later migration enables RLS on one of them,
    /// <c>TenantPurgeExecutor</c>'s reach assertion refuses to run rather than quietly skipping it
    /// — which is the whole point of the assertion.</para>
    /// </summary>
    public partial class TenantPurgeExecutionRole : Migration
    {
        /// <summary>
        /// The four platform tables a purge destroys. Transcribed from
        /// <c>PlatformTenantDataMap.Destroyed</c> rather than discovered, for the reason that file
        /// gives at length: a column named <c>TenantId</c> cannot answer whether a platform row is
        /// the customer's record or the operator's record of them, and both wrong answers are
        /// damaging. <c>TenantLifecyclePlatformTableClassificationTests</c> is what keeps the two
        /// lists in step.
        /// </summary>
        private const string DestroyedPlatformTables =
            "'TenantAdminInvitations','ProvisioningExecutions','ProvisioningSteps','ProvisioningDrafts'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql($"""
                DO $tenant_purge_role$
                DECLARE
                    target       record;
                    unit_key     name;
                BEGIN
                    -- Same guard every role-touching migration in this repo carries: a database
                    -- provisioned without the execution roles (the portable lane, a partial
                    -- restore) has nothing for this to authorise, and must not fail here.
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RETURN;
                    END IF;

                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        CREATE ROLE nexora_purge_app
                            NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                    END IF;

                    -- Granted to the MIGRATION role only, and deliberately NOT to the runtime
                    -- login roles the way 20260728134117 grants the identity and pipeline roles.
                    -- Those two are selected on request paths; this one never is. The purge opens
                    -- ConnectionStrings:MigrationConnection, which is the role running this
                    -- statement, so this is the whole set that needs it. Where the runtime role
                    -- and the migration role are the same login (ResolveDirectMigrationConnection)
                    -- this grants nothing new: that role already owns every table.
                    EXECUTE format('GRANT nexora_purge_app TO %I', current_user);

                    GRANT USAGE ON SCHEMA public TO nexora_purge_app;
                    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'platform') THEN
                        GRANT USAGE ON SCHEMA platform TO nexora_purge_app;
                    END IF;

                    -- ---- tenant plane -------------------------------------------------------
                    -- Discovered from the live catalogue, exactly as TenantPurgeExecutor
                    -- discovers its targets, and for the same reason: a hand-maintained list
                    -- stops covering new tables the moment somebody adds one, and the failure is
                    -- invisible. Any divergence between this loop and that sweep is caught by the
                    -- executor's reach assertion before a single row is deleted.
                    FOR target IN
                        SELECT n.nspname AS schema_name,
                               c.relname  AS table_name,
                               a.attname  AS tenant_column,
                               c.relrowsecurity AS rls_enabled,
                               c.oid      AS relation
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                        WHERE c.relkind = 'r'
                          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                          AND lower(a.attname) IN ('businessunitid', 'buid')
                        ORDER BY n.nspname, c.relname
                    LOOP
                        EXECUTE format(
                            'GRANT SELECT, DELETE ON %I.%I TO nexora_purge_app',
                            target.schema_name, target.table_name);

                        IF target.rls_enabled AND NOT EXISTS (
                            SELECT 1 FROM pg_policy p
                            WHERE p.polrelid = target.relation
                              AND p.polname = 'nexora_tenant_purge') THEN
                            EXECUTE format(
                                'CREATE POLICY nexora_tenant_purge ON %I.%I '
                                'AS PERMISSIVE FOR ALL TO nexora_purge_app '
                                'USING (%I = NULLIF(current_setting(''nexora.purge_business_unit_id'', true), '''')::bigint)',
                                target.schema_name, target.table_name, target.tenant_column);
                        END IF;
                    END LOOP;

                    -- ---- the tenant's own workspace row -------------------------------------
                    -- public."BusinessUnits" is the anchor every sweep above resolves against and
                    -- the one tenant table whose tenant column is its own primary key, so the
                    -- loop cannot find it. The key column is READ rather than written out: the
                    -- entity property is Id and the mapped column is "ID", and hardcoding the
                    -- wrong one answers 42703 against the real schema while every portable test
                    -- stays green. TenantPurgeExecutor.BusinessUnitKeyColumnAsync reads it the
                    -- same way, from the same catalogue, so the policy and the DELETE cannot
                    -- disagree about which column that is.
                    IF to_regclass('public."BusinessUnits"') IS NOT NULL THEN
                        SELECT a.attname INTO unit_key
                        FROM pg_index i
                        JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
                        WHERE i.indrelid = 'public."BusinessUnits"'::regclass
                          AND i.indisprimary
                        LIMIT 1;

                        IF unit_key IS NOT NULL THEN
                            GRANT SELECT, DELETE ON public."BusinessUnits" TO nexora_purge_app;

                            IF NOT EXISTS (
                                SELECT 1 FROM pg_policy p
                                WHERE p.polrelid = 'public."BusinessUnits"'::regclass
                                  AND p.polname = 'nexora_tenant_purge') THEN
                                EXECUTE format(
                                    'CREATE POLICY nexora_tenant_purge ON public."BusinessUnits" '
                                    'AS PERMISSIVE FOR ALL TO nexora_purge_app '
                                    'USING (%I = NULLIF(current_setting(''nexora.purge_business_unit_id'', true), '''')::bigint)',
                                    unit_key);
                            END IF;
                        END IF;
                    END IF;

                    -- ---- platform plane ------------------------------------------------------
                    -- Privileges only. None of these four carries row-level security today; see
                    -- the class comment for why a USING (true) policy is not written in advance.
                    FOR target IN
                        SELECT c.relname AS table_name
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'platform'
                          AND c.relkind = 'r'
                          AND c.relname IN ({DestroyedPlatformTables})
                        ORDER BY c.relname
                    LOOP
                        EXECUTE format(
                            'GRANT SELECT, DELETE ON platform.%I TO nexora_purge_app',
                            target.table_name);
                    END LOOP;

                    -- ProvisioningSteps and ProvisioningDrafts reach a tenant only through
                    -- ProvisioningExecutions, so their DELETE predicates are subqueries over it.
                    -- The SELECT grant above already covers that; stated here because removing
                    -- ProvisioningExecutions from the destroyed list later would silently break
                    -- the two children's predicates rather than their own grants.
                END
                $tenant_purge_role$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql("""
                DO $tenant_purge_role_down$
                DECLARE
                    target record;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        RETURN;
                    END IF;

                    -- Dropped by name from the catalogue rather than by replaying the Up loop:
                    -- the set of tables carrying a business unit column can have changed since,
                    -- and a Down that leaves one policy standing leaves a role that cannot be
                    -- dropped.
                    FOR target IN
                        SELECT n.nspname AS schema_name, c.relname AS table_name
                        FROM pg_policy p
                        JOIN pg_class c ON c.oid = p.polrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE p.polname = 'nexora_tenant_purge'
                    LOOP
                        EXECUTE format(
                            'DROP POLICY IF EXISTS nexora_tenant_purge ON %I.%I',
                            target.schema_name, target.table_name);
                    END LOOP;

                    -- Removes every remaining GRANT held by the role in THIS database. Roles are
                    -- cluster-scoped and DROP ROLE fails while any database still records a
                    -- privilege for them.
                    DROP OWNED BY nexora_purge_app;
                    DROP ROLE nexora_purge_app;
                END
                $tenant_purge_role_down$;
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Gives the schema owner the tenant-isolation predicate it is already supposed to be bound by,
    /// so that the SECURITY DEFINER functions the finance plane is built out of can read and write
    /// the tenant they were invoked for — on a database whose owner is bound by its own row-level
    /// security.
    ///
    /// <para><b>THE DEFECT.</b> Every governed finance journey fails on a
    /// <c>NOSUPERUSER NOBYPASSRLS NOINHERIT</c> schema owner — the topology
    /// <c>Program.cs ValidateRuntimeDatabaseRoleAsync</c> demands and the shape every managed
    /// provider hands out. Reproduced against postgres:16-alpine with the role topology
    /// <c>MigrationsBaseline/Sql/00_execution_roles.sql</c> builds, the baseline applied BY that
    /// owner, and every statement issued through the request-path preamble
    /// <c>TenantRlsCommandInterceptor</c> writes (<c>SET LOCAL ROLE nexora_tenant_app</c> plus
    /// <c>nexora.business_unit_id</c>). Eight real journeys, eight failures:</para>
    ///
    /// <code>
    /// create a ledger book   -> nexora_gl_evidence_event      -> 42501 CommercialFinanceAudits
    /// create a dunning policy-> nexora_ar_evidence_event      -> 42501 CommercialFinanceAudits
    /// open a bank account    -> nexora_bank_guard_account     -> 23514 "bank account currency must equal ..."
    /// post a journal entry   -> nexora_gl_enforce_book_currency-> 23514 "a tenant accounting book is required"
    /// issue an invoice       -> nexora_receivable_issued_immutable -> 23514 "lines and header do not reconcile"
    /// reverse a receipt      -> nexora_write_finance_audit    -> 23514 "audit action is inconsistent ..."
    /// release a refund       -> nexora_refund_governed        -> 23514 "refund source receipt identity ... invalid"
    /// post a write-off       -> nexora_write_off_governed     -> 23514 "allocations do not reconcile"
    /// </code>
    ///
    /// <para><b>One mechanism, two symptoms, and the second is the dangerous one.</b> SECURITY
    /// DEFINER switches the effective role to the FUNCTION OWNER. 110 tables in this schema are
    /// <c>FORCE ROW LEVEL SECURITY</c>, which makes the owner subject to its own policies, and every
    /// tenant policy is written <c>TO nexora_tenant_app</c> — a role list PostgreSQL matches with
    /// <c>has_privs_of_role()</c>, which for a NOINHERIT owner does not include roles it is merely a
    /// member of. So inside these functions the owner matches NO policy on ANY forced table.</para>
    ///
    /// <para>The WRITES fail loudly: that is the <c>42501</c> in the first two lines above, and it
    /// is the twelve (SECURITY DEFINER function -> FORCE table) write pairs across ten functions
    /// that 20260811210000 catalogued and left. The READS fail SILENTLY: a guard that asks
    /// <c>SELECT count(*) FROM "ReceivableDocumentLines" WHERE ...</c> gets zero rows rather than an
    /// error. Measured directly, with a probe function owned by that role:
    /// <c>current_user=nexora_schema_owner lines=0 periods=0</c> against a fixture holding both.
    /// Six of the eight journeys above never reach their write at all — they are refused by their
    /// own guard, which read nothing and concluded the evidence was missing. Fixing only the twelve
    /// write pairs would therefore have fixed nothing that can be exercised, which is why this is
    /// one migration over the forced tables rather than four policies over four tables.</para>
    ///
    /// <para>The same blindness is a governance risk in the other direction wherever a guard is
    /// written as "raise IF EXISTS (...)" rather than "raise IF NOT EXISTS (...)": an empty read
    /// makes the check PASS. Nothing here relies on having enumerated those; the fix removes the
    /// blindness itself.</para>
    ///
    /// <para><b>WHAT THIS MIGRATION DOES.</b> Three policies per forced tenant table — SELECT,
    /// INSERT, UPDATE — each granted <c>TO</c> the TABLE'S OWNER and admitting it only for the
    /// tenant named by <c>nexora.business_unit_id</c>, which is the same GUC and the same predicate
    /// <c>nexora_tenant_isolation</c> already uses for <c>nexora_tenant_app</c>. The owner gains
    /// exactly the access the design already intended for tenant-scoped work and nothing else. No
    /// role gains a privilege, no role gains BYPASSRLS, FORCE stays on all 110 tables, and
    /// <c>nexora_tenant_isolation</c> is not touched.</para>
    ///
    /// <para><b>DELETE is deliberately absent.</b> <c>FOR ALL</c> would have been one policy per
    /// table instead of three and was rejected. Several of the functions this migration exists to
    /// unblock are immutability guards — <c>nexora_receivable_issued_immutable</c>,
    /// <c>nexora_gl_guard_journal</c>, <c>nexora_write_off_governed</c> all raise on
    /// <c>TG_OP = 'DELETE'</c> — and handing the identity they run as a latent tenant-wide DELETE is
    /// the opposite of what they are for. Tenant deletion already has its own identity:
    /// 20260811154500 created <c>nexora_purge_app</c> with one policy per table gated on a separate
    /// GUC, and that separation is the thing that would be dissolved.</para>
    ///
    /// <para><b>Why the role list names the owner, and why that is not the thing 20260811210000
    /// rejected.</b> A SECURITY DEFINER function cannot change role — both <c>SET LOCAL ROLE</c> in
    /// the body and <c>SET role TO</c> in the function's SET clause answer <c>ERROR: cannot set
    /// parameter "role" within security-definer function</c> — so the policy that admits it has to
    /// match the role it already has, and the only two spellings are <c>TO &lt;the owner&gt;</c> and
    /// <c>TO PUBLIC</c> plus a predicate. 20260811210000 rejected the first because the owner is
    /// <c>neondb_owner</c> on the managed target and the runtime username on the direct one, so a
    /// HAND-WRITTEN role name would be wrong on one of them. That objection is about writing the
    /// name down; this migration does not write it down. The loop below reads the name from
    /// <c>pg_class.relowner</c> on the database it is running against and emits it, so each
    /// deployment gets its own correct owner and no name is transcribed.</para>
    ///
    /// <para>Naming the owner rather than PUBLIC is what keeps these policies out of the
    /// request-reachability invariant <c>AiProcessingPolicyTenantIsolationPostgreSqlTests</c> and
    /// <c>PostgreSqlProductionDialectTests</c> enforce — "exactly one permissive policy on
    /// <c>AiProcessingPolicies</c> that any request identity can match". That guard tests
    /// reachability with <c>pg_has_role(request_role, admitted, 'USAGE')</c>, which is what
    /// PostgreSQL itself uses to match a policy's role list, and none of
    /// <c>nexora_tenant_app</c> / <c>nexora_identity_app</c> / <c>nexora_pipeline_app</c> is a
    /// member of the schema owner — the GRANT in <c>Sql/00_execution_roles.sql</c> runs the other
    /// way, making the OWNER a member of them. So these three fall out of that guard on the merits
    /// rather than by an exemption, and the guard is left byte-for-byte alone. Measured on the
    /// reproduction, both halves: with these policies installed the guard still returns exactly
    /// <c>nexora_tenant_isolation</c>; with a hostile <c>TO PUBLIC ... WITH CHECK (true)</c>
    /// installed, and again with a hostile policy hiding its fence in a dead disjunct
    /// (<c>"UpdatedBy" = 'x' OR current_user = &lt;owner&gt;</c>), it names both of them.</para>
    ///
    /// <para><b>The predicate is kept anyway, and it is not redundant.</b> The role list is resolved
    /// once, at migration time; the predicate is evaluated every statement and reads
    /// <c>pg_class.relowner</c> live. Two things follow. An <c>ALTER TABLE ... OWNER TO</c> or a
    /// restore under a different owner leaves the role list naming the OLD owner, and the predicate
    /// then refuses rather than admitting the new one — fail-closed, and the loop below repairs the
    /// role list on its next run. And if anyone ever granted the owner role to a request role, the
    /// role list would start matching on a request path while the predicate still would not, because
    /// <c>current_user</c> there is the request role. Verified on the reproduction after the fix:
    /// <c>nexora_tenant_app</c> scoped to a neighbouring tenant reads 0 rows of the first tenant's
    /// evidence, and still cannot INSERT into <c>public."CommercialFinanceAudits"</c> at all (it
    /// holds SELECT and nothing else — a table privilege, checked before RLS, and untouched here).
    /// The owner itself with no GUC set reads 0 from every one of these tables, and with the GUC set
    /// reads exactly one tenant.</para>
    ///
    /// <para><b>Why the ambient GUC rather than a new one.</b> 20260811210000 introduced a
    /// dedicated <c>nexora.provisioning_business_unit_id</c> because its two seeders fire on the row
    /// that CREATES the tenant scope, before any ambient scope exists, and because a trigger
    /// overwriting the tenant GUC could leave the rest of the transaction running under a hijacked
    /// tenant. Neither applies here. These functions fire on ESTABLISHED tenants during ordinary
    /// tenant DML, so the scope they need is the one the request already declared; and because the
    /// base tables they fire from are themselves FORCE with <c>nexora_tenant_isolation</c>, a
    /// tenant-role writer cannot have reached them without that GUC being set AND equal to the row's
    /// own <c>BusinessUnitId</c>. Reusing it also means no function body changes at all: ten bodies,
    /// one of them 278 lines, stay byte-for-byte what the baseline created, and the whole change is
    /// visible in <c>\d</c>.</para>
    ///
    /// <para><b>What is deliberately still refused.</b> A caller that reaches these triggers with NO
    /// tenant scope declared — <c>nexora_pipeline_app</c> is BYPASSRLS and would pass the base-table
    /// write, but the trigger still runs as the owner — is refused, exactly as before. That is
    /// fail-closed and correct: an evidence row whose tenant nobody declared should not be written,
    /// and the caller's fix is to declare the scope the interceptor already declares on every
    /// tenant-scoped path.</para>
    ///
    /// <para><b>Scope.</b> Catalogue-driven, exactly as 20260811154500 discovers the purge targets
    /// and for the same reason: a hand-maintained list stops covering new tables the moment somebody
    /// adds one, and the failure is invisible. 100 of the 110 forced tables carry
    /// <c>BusinessUnitId</c> or <c>BUID</c>. The other ten are platform-plane tables keyed on
    /// <c>TenantId</c>; they are not tenant-plane tables, the one of them with this defect
    /// (<c>platform."TenantMeterSourcePolicies"</c>) was fixed by 20260811210000, and inventing a
    /// second tenant key here would put a predicate on tables no reproduction covers.</para>
    ///
    /// <para><b>On the pg_roles guard the sibling migrations carry.</b> Deliberately absent, for the
    /// reason 20260811210000 records: these policies name no role, so there is no 42704 to avoid,
    /// and guarding on one could only SKIP a fix that is still required.</para>
    /// </summary>
    public partial class DefinerTenantIsolationUnderForcedRowSecurity : Migration
    {
        /// <summary>
        /// The tenant fence, built once per table and substituted into all three policies (and into
        /// both halves of the UPDATE one) as an already-formatted string. It is built by its own
        /// <c>format()</c> rather than inlined into theirs so that the quote doubling below is
        /// written once instead of four times: this text passes through TWO levels of SQL string
        /// literal — the C# raw string is the DO block's source, and the DO block builds a
        /// <c>CREATE POLICY</c> statement inside it — and the level at which each quote has to be
        /// doubled is exactly the kind of thing that is right once and wrong on the copy.
        ///
        /// <para>The owner half reads <c>pg_class.relowner</c> rather than naming a role, so it
        /// follows an ownership change; it is a scalar subquery on a constant OID, so the planner
        /// makes it an InitPlan evaluated once per statement rather than a per-row catalogue
        /// lookup. The tenant half is character-for-character the predicate
        /// <c>nexora_tenant_isolation</c> carries, which is the point: this is not a new isolation
        /// rule, it is the existing one reaching the identity SECURITY DEFINER code actually runs
        /// as.</para>
        /// </summary>
        private const string OwnerTenantFence =
            "current_user = (SELECT pg_catalog.pg_get_userbyid(t.relowner) " +
            "FROM pg_catalog.pg_class t WHERE t.oid = %L::regclass) " +
            "AND %I = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql($"""
                DO $definer_tenant_isolation$
                DECLARE
                    target     record;
                    owner_role name;
                    qualified  text;
                    fence      text;
                BEGIN
                    FOR target IN
                        SELECT n.nspname AS schema_name,
                               c.relname  AS table_name,
                               a.attname  AS tenant_column,
                               c.oid      AS relation,
                               c.relowner AS owner_oid
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                        WHERE c.relkind = 'r'
                          AND c.relrowsecurity
                          AND c.relforcerowsecurity
                          AND n.nspname IN ('public', 'platform')
                          AND lower(a.attname) IN ('businessunitid', 'buid')
                        ORDER BY n.nspname, c.relname
                    LOOP
                        -- Read, never transcribed. This is the whole of what makes TO <owner>
                        -- portable across the managed target (neondb_owner) and the direct one
                        -- (the runtime username), which is the objection 20260811210000 raised
                        -- against writing a role name down.
                        owner_role := pg_catalog.pg_get_userbyid(target.owner_oid);

                        -- Built once and passed as a literal so the ::regclass cast inside the
                        -- predicate resolves the table by name at policy-creation time and records
                        -- a dependency on it, rather than embedding an OID that a restore renumbers.
                        qualified  := format('%I.%I', target.schema_name, target.table_name);
                        fence      := format('{OwnerTenantFence}', qualified, target.tenant_column);

                        -- Repair before create, and this is the branch that keeps the role list
                        -- honest. CREATE POLICY resolves TO <role> once; an ALTER TABLE ... OWNER TO
                        -- or a restore under a different owner leaves the stored list naming a role
                        -- that no longer owns the table. The predicate fails closed in that state,
                        -- so nothing leaks -- but the fix has silently stopped working, and an
                        -- IF NOT EXISTS guard on its own would never notice, because the policy
                        -- does exist. Dropping the stale one lets the create below re-emit it
                        -- against the current owner on the next run.
                        IF EXISTS (SELECT 1 FROM pg_policy p
                                   WHERE p.polrelid = target.relation
                                     AND p.polname IN ('nexora_definer_tenant_read',
                                                       'nexora_definer_tenant_insert',
                                                       'nexora_definer_tenant_update')
                                     AND p.polroles IS DISTINCT FROM ARRAY[target.owner_oid]) THEN
                            EXECUTE format('DROP POLICY IF EXISTS nexora_definer_tenant_read ON %s', qualified);
                            EXECUTE format('DROP POLICY IF EXISTS nexora_definer_tenant_insert ON %s', qualified);
                            EXECUTE format('DROP POLICY IF EXISTS nexora_definer_tenant_update ON %s', qualified);
                        END IF;

                        -- FOR SELECT, and it is the half that matters most. The twelve write pairs
                        -- are what a catalogue sweep finds; the reads are what actually stops the
                        -- journeys, because a guard that reads nothing raises on its own evidence
                        -- check long before its write is reached. It is also what ON CONFLICT needs:
                        -- the arbiter check READS the target, and three of these functions upsert a
                        -- LegalDocumentCounters row.
                        IF NOT EXISTS (SELECT 1 FROM pg_policy p
                                       WHERE p.polrelid = target.relation
                                         AND p.polname = 'nexora_definer_tenant_read') THEN
                            EXECUTE format(
                                'CREATE POLICY nexora_definer_tenant_read ON %s '
                                'AS PERMISSIVE FOR SELECT TO %I USING (%s)',
                                qualified, owner_role, fence);
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM pg_policy p
                                       WHERE p.polrelid = target.relation
                                         AND p.polname = 'nexora_definer_tenant_insert') THEN
                            EXECUTE format(
                                'CREATE POLICY nexora_definer_tenant_insert ON %s '
                                'AS PERMISSIVE FOR INSERT TO %I WITH CHECK (%s)',
                                qualified, owner_role, fence);
                        END IF;

                        -- USING and WITH CHECK both, and neither is redundant. USING is what lets an
                        -- UPDATE see the row it is changing -- and what PostgreSQL also applies to
                        -- SELECT ... FOR UPDATE, which several of these guards use to lock the
                        -- aggregate they are validating. WITH CHECK is what stops the updated row
                        -- from landing outside the tenant the statement was scoped to.
                        IF NOT EXISTS (SELECT 1 FROM pg_policy p
                                       WHERE p.polrelid = target.relation
                                         AND p.polname = 'nexora_definer_tenant_update') THEN
                            EXECUTE format(
                                'CREATE POLICY nexora_definer_tenant_update ON %s '
                                'AS PERMISSIVE FOR UPDATE TO %I USING (%s) WITH CHECK (%s)',
                                qualified, owner_role, fence, fence);
                        END IF;
                    END LOOP;
                END
                $definer_tenant_isolation$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            // Discovered from pg_policy rather than by replaying the Up loop's own catalogue query.
            // The two would agree today, and they would stop agreeing the moment a table's FORCE
            // flag or tenant column changed between the Up and the Down -- and a Down that leaves
            // behind the policies it created has not returned the database to the state the previous
            // migration left, which is the only thing a Down is for.
            migrationBuilder.Sql("""
                DO $definer_tenant_isolation_down$
                DECLARE existing record;
                BEGIN
                    FOR existing IN
                        SELECT n.nspname AS schema_name, c.relname AS table_name, p.polname
                        FROM pg_policy p
                        JOIN pg_class c ON c.oid = p.polrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE p.polname IN ('nexora_definer_tenant_read',
                                            'nexora_definer_tenant_insert',
                                            'nexora_definer_tenant_update')
                        ORDER BY n.nspname, c.relname, p.polname
                    LOOP
                        EXECUTE format('DROP POLICY IF EXISTS %I ON %I.%I',
                            existing.polname, existing.schema_name, existing.table_name);
                    END LOOP;
                END
                $definer_tenant_isolation_down$;
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}

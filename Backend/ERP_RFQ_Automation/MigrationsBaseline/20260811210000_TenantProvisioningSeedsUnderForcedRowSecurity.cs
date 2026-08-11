using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Lets the two SECURITY DEFINER provisioning seeders write the rows they exist to write, on a
    /// database whose schema owner is bound by its own row-level security.
    ///
    /// <para><b>THE DEFECT.</b> Creating a tenant fails outright — no workaround, no partial
    /// success — the moment the schema owner is <c>NOSUPERUSER NOBYPASSRLS</c>, which is the
    /// topology <c>Program.cs ValidateRuntimeDatabaseRoleAsync</c> demands and the shape every
    /// managed provider hands out:</para>
    ///
    /// <code>
    /// INSERT INTO public."BusinessUnits" ...
    ///   -&gt; trigger business_units_create_ai_policy
    ///   -&gt; nexora_create_default_ai_policy()          [SECURITY DEFINER]
    ///   -&gt; INSERT INTO public."AiProcessingPolicies"  [FORCE ROW LEVEL SECURITY]
    ///   -&gt; ERROR 42501: new row violates row-level security policy
    ///
    /// INSERT INTO platform."Tenants" ...
    ///   -&gt; trigger tenants_seed_meter_source_policies
    ///   -&gt; nexora_seed_tenant_meter_source_policies()        [SECURITY DEFINER]
    ///   -&gt; INSERT INTO platform."TenantMeterSourcePolicies"  [FORCE ROW LEVEL SECURITY]
    ///   -&gt; ERROR 42501: new row violates row-level security policy
    /// </code>
    ///
    /// <para>Reproduced against postgres:16-alpine with the role topology
    /// <c>MigrationsBaseline/Sql/00_execution_roles.sql</c> builds and the baseline applied by a
    /// <c>NOSUPERUSER NOBYPASSRLS NOINHERIT CREATEROLE</c> owner: 110 FORCE tables, 36
    /// SECURITY DEFINER functions, both INSERTs refused, zero business units created.</para>
    ///
    /// <para><b>Why the caller's identity is irrelevant.</b> SECURITY DEFINER switches the
    /// effective role to the FUNCTION OWNER, so <c>nexora_pipeline_app</c>'s BYPASSRLS never
    /// applies. Measured: the refusal is byte-identical whether the caller is the owner directly,
    /// has <c>SET ROLE nexora_pipeline_app</c>, has <c>SET ROLE nexora_tenant_app</c> with the
    /// tenant GUC set, or is a SUPERUSER. It is equally unrelated to the policy
    /// 20260811110019_DropAiDefaultProvisioningPolicy removed: with that policy restored the
    /// refusal is identical, because the trigger's statement is
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c> and the arbiter check READS the target, which a
    /// <c>FOR INSERT</c> policy can never permit.</para>
    ///
    /// <para>Today this is masked only because the deployed schema owner happens to bypass RLS.
    /// That is a property of one database, not of the design.</para>
    ///
    /// <para><b>WHAT THIS MIGRATION DOES.</b> Two policies per affected table — one
    /// <c>FOR SELECT</c> for the ON CONFLICT arbiter, one <c>FOR INSERT</c> for the row — each
    /// admitting the function owner and ONLY for the single tenant the seeder is currently
    /// creating, named by a GUC the seeder sets and restores around its own INSERT. No role gains
    /// a privilege, no role gains BYPASSRLS, FORCE stays on, and with the GUC unset the owner
    /// still sees nothing at all on either table.</para>
    ///
    /// <para><b>Why the fence is a predicate and not a role list, which was not a preference.</b>
    /// The obvious shape is a dedicated role — a <c>nexora_provisioning_app</c> the seeder
    /// <c>SET LOCAL ROLE</c>s into, exactly as 20260811154500_TenantPurgeExecutionRole does for the
    /// purge. PostgreSQL forbids it outright. Measured, both spellings, on postgres:16:
    /// <c>SET LOCAL ROLE</c> in the function body and <c>SET role TO ...</c> in the function's own
    /// SET clause both answer <c>ERROR: cannot set parameter "role" within security-definer
    /// function</c>. A SECURITY DEFINER function cannot change role, so the policy that admits it
    /// has to match the role it already has, and the only two ways to write that are
    /// <c>TO &lt;owner name&gt;</c> and <c>TO PUBLIC</c> plus a predicate.
    /// <c>TO &lt;owner name&gt;</c> was rejected on the grounds 20260811154500 records: the owner is
    /// <c>neondb_owner</c> on the managed target and the runtime username on the direct one, and a
    /// per-deployment role name would travel into the next <c>pg_dump</c> baseline.</para>
    ///
    /// <para><b>Why a TO PUBLIC policy does not widen anything.</b> The predicate leads with
    /// <c>current_user = &lt;the function's owner&gt;</c>, read from <c>pg_proc</c> rather than
    /// written out, so it follows an <c>ALTER FUNCTION ... OWNER TO</c> and a restore under a
    /// different owner without edit. Every request path issues
    /// <c>SET LOCAL ROLE nexora_tenant_app | nexora_identity_app | nexora_pipeline_app</c>
    /// (<c>TenantRlsCommandInterceptor</c>), so <c>current_user</c> on a request is never the
    /// owner and this policy can never OR around <c>nexora_tenant_isolation</c> for one — which is
    /// the property <c>AiProcessingPolicyTenantIsolationPostgreSqlTests</c> and
    /// <c>PostgreSqlProductionDialectTests</c> now assert directly rather than by proxy. Verified:
    /// <c>nexora_tenant_app</c> holding the provisioning GUC pointed at another tenant reads and
    /// writes nothing of that tenant's.</para>
    ///
    /// <para><b>Why the GUC is new rather than <c>nexora.business_unit_id</c>.</b> Reusing the
    /// tenant GUC would mean a trigger overwriting the value the whole request is scoped by; if
    /// the restore ever failed the rest of the transaction would run under a hijacked tenant. A
    /// GUC nothing else reads fails safe in that scenario, and it also keeps the seeders out of
    /// any ambient path that already has the tenant GUC set.</para>
    ///
    /// <para><b>Why the restore is written by hand and not declared in the function's SET
    /// clause.</b> The SET clause is the documented mechanism and would be shorter. It is not
    /// available: a custom placeholder in a function's SET clause is validated by
    /// <c>validate_option_array_item</c>, which requires SUPERUSER, and the owner this migration
    /// exists for is not one — measured, it answers
    /// <c>ERROR: permission denied to set parameter</c> at CREATE FUNCTION time. The seeders
    /// therefore capture the previous value and put it back. On the error path nothing is needed:
    /// <c>set_config(..., is_local =&gt; true)</c> is transactional and unwinds with the
    /// (sub)transaction.</para>
    ///
    /// <para><b>Why only these two of the fourteen (function, FORCE table) write pairs.</b>
    /// A catalogue sweep of the live schema — 36 SECURITY DEFINER functions against 110 FORCE
    /// tables — found 14 write pairs across 12 functions, and not one of the target tables carries
    /// a policy the owner matches, so all 14 are latent breaks of this same mechanism. Only these
    /// two are the same SHAPE: an AFTER INSERT trigger on the row that CREATES the scope, seeding
    /// a fixed default for exactly that new scope, with the scope id in hand. The remaining twelve
    /// pairs, across ten functions, are finance-plane evidence, guard and
    /// legal-counter writes (<c>nexora_ar_evidence_event</c>,
    /// <c>nexora_ar_reconcile_kept_promise_payment</c>, <c>nexora_bank_evidence_event</c>,
    /// <c>nexora_gl_evidence_event</c>, <c>nexora_gl_guard_journal</c>,
    /// <c>nexora_receivable_issued_immutable</c>, <c>nexora_refund_governed</c>,
    /// <c>nexora_write_finance_audit</c>, <c>nexora_write_finance_outbox</c>,
    /// <c>nexora_write_off_governed</c> over <c>public."CommercialFinanceAudits"</c>,
    /// <c>public."FinanceOutboxMessages"</c>, <c>public."LegalDocumentCounters"</c> and
    /// <c>public."PromisesToPay"</c>) fired on established tenants by ordinary tenant DML. Their
    /// scope is the ambient request tenant rather than a row being created, and their table
    /// privileges do not line up the way this pair's do: <c>nexora_tenant_app</c> holds SELECT and
    /// nothing else on <c>CommercialFinanceAudits</c>, <c>FinanceOutboxMessages</c> and
    /// <c>LegalDocumentCounters</c>, but SELECT, INSERT and UPDATE on <c>PromisesToPay</c>
    /// (Sql/09_privileges.sql). So the fix for them is not one shape but at least two, and each
    /// needs its own journey reproduced before anything is written. Reported, not fixed here.</para>
    ///
    /// <para><b>One thing the FOR SELECT policy does give away, considered and accepted.</b> The
    /// owner previously read NOTHING from either table — FORCE with no matching policy — and now
    /// reads one tenant's row whenever the provisioning GUC names it. Custom GUCs are settable by
    /// anyone, so that read is gated on a value an attacker could choose. It is not a route to
    /// anything: reaching it requires already executing as the schema owner, and a caller with
    /// arbitrary SQL as the owner can <c>ALTER TABLE ... NO FORCE</c>, <c>DROP POLICY</c>, or
    /// <c>ALTER ROLE ... BYPASSRLS</c> and read the whole table. The exposure is strictly smaller
    /// than what that identity already has, it is one row rather than the table, and it is the
    /// minimum the ON CONFLICT arbiter needs. Written down because "the fix opened a read that did
    /// not exist" is true and should not have to be rediscovered.</para>
    ///
    /// <para><b>On the pg_roles guard the sibling migrations carry.</b> Deliberately absent.
    /// 20260811154500 and 20260811110019 guard on <c>nexora_tenant_app</c> because they write
    /// <c>CREATE POLICY ... TO &lt;role&gt;</c> and a missing role is a hard 42704. These policies
    /// name no role. Guarding on one here could only SKIP a fix that is still required — a
    /// database restored without the execution roles still has FORCE on 110 tables and may still
    /// have an owner that does not bypass it. What is guarded instead is the thing that can
    /// actually be absent: the tables and the functions.</para>
    /// </summary>
    public partial class TenantProvisioningSeedsUnderForcedRowSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            // ---- public."AiProcessingPolicies" -------------------------------------------------
            // The seeder keeps its body byte-for-byte apart from the two set_config calls that
            // bracket the INSERT. That is the whole change: it already knew the BusinessUnitId, it
            // just had no way to say so to a policy.
            //
            // The tenant fence is written out per policy rather than shared through a constant,
            // because a policy predicate is not code that is called — it is the text pg_dump will
            // print and the next reviewer will read in `\d public."AiProcessingPolicies"`, and it
            // has to be legible there. The owner half reads the owner out of pg_proc instead of
            // naming it, so it follows the function through a restore or an ownership change; the
            // regprocedure OID constant makes it an InitPlan evaluated once per statement rather
            // than a per-row catalogue lookup, and it records a dependency that stops the function
            // being dropped while the policy that exists to admit it survives.
            // The function text sits flush against the left margin inside EXECUTE rather than
            // indented with the block that guards it. prosrc stores whitespace verbatim, so
            // indenting it here would make Down() restore a body that differs from the baseline's
            // in every line — semantically identical, and still a lie in the catalogue. Flush
            // text keeps Up and Down each byte-exact, and keeps the diff between them down to the
            // two set_config calls, which is the whole of the change.
            migrationBuilder.Sql("""
                DO $ai_seed$
                BEGIN
                    IF to_regclass('public."AiProcessingPolicies"') IS NULL
                       OR to_regprocedure('public.nexora_create_default_ai_policy()') IS NULL THEN
                        RETURN;
                    END IF;

                    EXECUTE $ddl$
                CREATE OR REPLACE FUNCTION public.nexora_create_default_ai_policy() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'public'
                    AS $fn$
                DECLARE
                    previous_scope text := current_setting('nexora.provisioning_business_unit_id', true);
                BEGIN
                    PERFORM set_config('nexora.provisioning_business_unit_id', NEW."ID"::text, true);

                    INSERT INTO public."AiProcessingPolicies"
                        ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                         "Version", "UpdatedOn", "UpdatedBy")
                    VALUES (NEW."ID", TRUE, FALSE, 'RfqExtraction,BoqDraft', 1, now(), 'tenant-provisioning')
                    ON CONFLICT ("BusinessUnitId") DO NOTHING;

                    PERFORM set_config('nexora.provisioning_business_unit_id', coalesce(previous_scope, ''), true);
                    RETURN NEW;
                END
                $fn$;
                $ddl$;

                    -- FOR SELECT, and it is not optional. The seeder's statement is
                    -- INSERT ... ON CONFLICT ("BusinessUnitId") DO NOTHING and the arbiter check
                    -- reads the target. Measured with only the FOR INSERT policy below in place:
                    -- the plain INSERT passes its WITH CHECK and reaches the foreign key, while
                    -- the identical INSERT with ON CONFLICT is still refused with "new row
                    -- violates row-level security policy".
                    IF NOT EXISTS (SELECT 1 FROM pg_policy
                                   WHERE polrelid = 'public."AiProcessingPolicies"'::regclass
                                     AND polname = 'nexora_ai_default_policy_seed_read') THEN
                        CREATE POLICY nexora_ai_default_policy_seed_read
                            ON public."AiProcessingPolicies"
                            AS PERMISSIVE FOR SELECT TO PUBLIC
                            USING (
                                current_user = (SELECT pg_catalog.pg_get_userbyid(seeder.proowner)
                                                FROM pg_catalog.pg_proc seeder
                                                WHERE seeder.oid = 'public.nexora_create_default_ai_policy()'::pg_catalog.regprocedure)
                                AND "BusinessUnitId" = NULLIF(current_setting('nexora.provisioning_business_unit_id', true), '')::bigint);
                    END IF;

                    -- UpdatedBy is pinned as well as the tenant. It costs nothing — the seeder
                    -- writes that literal and this migration owns both halves — and it keeps the
                    -- policy honest about the single row it exists for, so the owner path cannot
                    -- use it to plant a governance row attributed to anything else.
                    IF NOT EXISTS (SELECT 1 FROM pg_policy
                                   WHERE polrelid = 'public."AiProcessingPolicies"'::regclass
                                     AND polname = 'nexora_ai_default_policy_seed_write') THEN
                        CREATE POLICY nexora_ai_default_policy_seed_write
                            ON public."AiProcessingPolicies"
                            AS PERMISSIVE FOR INSERT TO PUBLIC
                            WITH CHECK (
                                current_user = (SELECT pg_catalog.pg_get_userbyid(seeder.proowner)
                                                FROM pg_catalog.pg_proc seeder
                                                WHERE seeder.oid = 'public.nexora_create_default_ai_policy()'::pg_catalog.regprocedure)
                                AND "BusinessUnitId" = NULLIF(current_setting('nexora.provisioning_business_unit_id', true), '')::bigint
                                AND "UpdatedBy" = 'tenant-provisioning');
                    END IF;
                END
                $ai_seed$;
                """);

            // ---- platform."TenantMeterSourcePolicies" ------------------------------------------
            // The same defect on the other half of the same journey. platform."Tenants" is the
            // platform-plane anchor a tenant is created from, and its AFTER INSERT trigger seeds
            // the sixteen meter source policies that decide what the tenant may be billed for.
            // Reproduced identically: INSERT INTO platform."Tenants" answers 42501 from inside
            // nexora_seed_tenant_meter_source_policies(). Note the one policy on that table is
            // TO nexora_pipeline_app rather than TO nexora_tenant_app, which changes nothing —
            // the effective role inside the function is the owner either way.
            migrationBuilder.Sql("""
                DO $meter_seed$
                BEGIN
                    IF to_regclass('platform."TenantMeterSourcePolicies"') IS NULL
                       OR to_regprocedure('platform.nexora_seed_tenant_meter_source_policies()') IS NULL THEN
                        RETURN;
                    END IF;

                    EXECUTE $ddl$
                CREATE OR REPLACE FUNCTION platform.nexora_seed_tenant_meter_source_policies() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'platform'
                    AS $fn$
                DECLARE
                    previous_scope text := current_setting('nexora.provisioning_tenant_id', true);
                BEGIN
                    PERFORM set_config('nexora.provisioning_tenant_id', NEW."Id"::text, true);

                    INSERT INTO platform."TenantMeterSourcePolicies" ("TenantId","MeterKey","Mode","Version")
                    SELECT NEW."Id",m.meter_key,m.mode,1
                      FROM (VALUES
                        ('base.subscription','LegacyAuthoritative'),('documents','LegacyAuthoritative'),
                        ('ai.tokens.external','LegacyAuthoritative'),('seats','LegacyAuthoritative'),
                        ('processing.minutes','BillingBlocked'),('pages.processed','BillingBlocked'),
                        ('rfqs','BillingBlocked'),('quotes','BillingBlocked'),('orders','BillingBlocked'),
                        ('emails','BillingBlocked'),('pages.ocr','BillingBlocked'),('api.calls','BillingBlocked'),
                        ('storage.gb','BillingBlocked'),('supplier.searches','BillingBlocked'),
                        ('automation.runs','BillingBlocked'),('dedicated.infrastructure','BillingBlocked')) AS m(meter_key,mode)
                    ON CONFLICT ("TenantId","MeterKey") DO NOTHING;

                    PERFORM set_config('nexora.provisioning_tenant_id', coalesce(previous_scope, ''), true);
                    RETURN NEW;
                END
                $fn$;
                $ddl$;

                    IF NOT EXISTS (SELECT 1 FROM pg_policy
                                   WHERE polrelid = 'platform."TenantMeterSourcePolicies"'::regclass
                                     AND polname = 'nexora_meter_source_policy_seed_read') THEN
                        CREATE POLICY nexora_meter_source_policy_seed_read
                            ON platform."TenantMeterSourcePolicies"
                            AS PERMISSIVE FOR SELECT TO PUBLIC
                            USING (
                                current_user = (SELECT pg_catalog.pg_get_userbyid(seeder.proowner)
                                                FROM pg_catalog.pg_proc seeder
                                                WHERE seeder.oid = 'platform.nexora_seed_tenant_meter_source_policies()'::pg_catalog.regprocedure)
                                AND "TenantId" = NULLIF(current_setting('nexora.provisioning_tenant_id', true), '')::bigint);
                    END IF;

                    -- Version = 1 plays the part UpdatedBy plays above: the seeder writes the
                    -- first version of a meter policy and nothing else, so a later revision of one
                    -- of these rows cannot travel in through this policy.
                    IF NOT EXISTS (SELECT 1 FROM pg_policy
                                   WHERE polrelid = 'platform."TenantMeterSourcePolicies"'::regclass
                                     AND polname = 'nexora_meter_source_policy_seed_write') THEN
                        CREATE POLICY nexora_meter_source_policy_seed_write
                            ON platform."TenantMeterSourcePolicies"
                            AS PERMISSIVE FOR INSERT TO PUBLIC
                            WITH CHECK (
                                current_user = (SELECT pg_catalog.pg_get_userbyid(seeder.proowner)
                                                FROM pg_catalog.pg_proc seeder
                                                WHERE seeder.oid = 'platform.nexora_seed_tenant_meter_source_policies()'::pg_catalog.regprocedure)
                                AND "TenantId" = NULLIF(current_setting('nexora.provisioning_tenant_id', true), '')::bigint
                                AND "Version" = 1);
                    END IF;
                END
                $meter_seed$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            // Both seeders go back to the body 20260811033109_SquashedSchemaBaseline created, byte
            // for byte, INCLUDING the fact that they cannot write on an RLS-bound owner. A Down
            // that keeps the half of the change it likes has not returned the database to the
            // state the previous migration left, which is the only thing a Down is for.
            //
            // The policies are dropped BEFORE the functions are replaced. Their predicates carry a
            // regprocedure dependency on those functions; CREATE OR REPLACE keeps the OID so the
            // order is not load-bearing today, but it is the order that stays correct if a later
            // change ever drops and recreates one of them.
            migrationBuilder.Sql("""
                DO $ai_seed_down$
                BEGIN
                    IF to_regclass('public."AiProcessingPolicies"') IS NOT NULL THEN
                        DROP POLICY IF EXISTS nexora_ai_default_policy_seed_read
                            ON public."AiProcessingPolicies";
                        DROP POLICY IF EXISTS nexora_ai_default_policy_seed_write
                            ON public."AiProcessingPolicies";
                    END IF;

                    IF to_regclass('public."AiProcessingPolicies"') IS NULL
                       OR to_regprocedure('public.nexora_create_default_ai_policy()') IS NULL THEN
                        RETURN;
                    END IF;

                    EXECUTE $ddl$
                CREATE OR REPLACE FUNCTION public.nexora_create_default_ai_policy() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'public'
                    AS $fn$
                BEGIN
                    INSERT INTO public."AiProcessingPolicies"
                        ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                         "Version", "UpdatedOn", "UpdatedBy")
                    VALUES (NEW."ID", TRUE, FALSE, 'RfqExtraction,BoqDraft', 1, now(), 'tenant-provisioning')
                    ON CONFLICT ("BusinessUnitId") DO NOTHING;
                    RETURN NEW;
                END
                $fn$;
                $ddl$;
                END
                $ai_seed_down$;
                """);

            migrationBuilder.Sql("""
                DO $meter_seed_down$
                BEGIN
                    IF to_regclass('platform."TenantMeterSourcePolicies"') IS NOT NULL THEN
                        DROP POLICY IF EXISTS nexora_meter_source_policy_seed_read
                            ON platform."TenantMeterSourcePolicies";
                        DROP POLICY IF EXISTS nexora_meter_source_policy_seed_write
                            ON platform."TenantMeterSourcePolicies";
                    END IF;

                    IF to_regclass('platform."TenantMeterSourcePolicies"') IS NULL
                       OR to_regprocedure('platform.nexora_seed_tenant_meter_source_policies()') IS NULL THEN
                        RETURN;
                    END IF;

                    EXECUTE $ddl$
                CREATE OR REPLACE FUNCTION platform.nexora_seed_tenant_meter_source_policies() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'platform'
                    AS $fn$
                BEGIN
                    INSERT INTO platform."TenantMeterSourcePolicies" ("TenantId","MeterKey","Mode","Version")
                    SELECT NEW."Id",m.meter_key,m.mode,1
                      FROM (VALUES
                        ('base.subscription','LegacyAuthoritative'),('documents','LegacyAuthoritative'),
                        ('ai.tokens.external','LegacyAuthoritative'),('seats','LegacyAuthoritative'),
                        ('processing.minutes','BillingBlocked'),('pages.processed','BillingBlocked'),
                        ('rfqs','BillingBlocked'),('quotes','BillingBlocked'),('orders','BillingBlocked'),
                        ('emails','BillingBlocked'),('pages.ocr','BillingBlocked'),('api.calls','BillingBlocked'),
                        ('storage.gb','BillingBlocked'),('supplier.searches','BillingBlocked'),
                        ('automation.runs','BillingBlocked'),('dedicated.infrastructure','BillingBlocked')) AS m(meter_key,mode)
                    ON CONFLICT ("TenantId","MeterKey") DO NOTHING;
                    RETURN NEW;
                END $fn$;
                $ddl$;
                END
                $meter_seed_down$;
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}

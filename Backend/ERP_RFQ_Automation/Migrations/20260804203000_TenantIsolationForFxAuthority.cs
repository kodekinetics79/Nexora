using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Row-level security for the FX authority tables.
    ///
    /// <c>FxRates</c> and <c>FxRateSnapshots</c> are created by
    /// 20260804181520_Module05MultiCurrencyFxAuthority with tenant DML grants but no RLS
    /// policy, and <c>Models/ErpRfqAutomationContext.Fx.cs</c> documents that they also
    /// carry NO EF global query filter — the module could not add a splice point to
    /// ErpRfqAutomationContext.Tenancy.cs. Both isolation layers are therefore absent and
    /// tenant safety rests entirely on FxConversionService remembering an explicit
    /// BusinessUnitId predicate in every query. These two tables decide what rate a
    /// customer's quote is converted at, so that is the wrong place to depend on
    /// hand-written predicates.
    ///
    /// This migration closes the database layer, which is what constrains every
    /// authenticated HTTP request (they execute as nexora_tenant_app). It is deliberately
    /// idempotent so it cannot conflict with the FX module adding the same policy to its
    /// own migration concurrently, and it is guarded on table existence so it degrades to
    /// a no-op rather than failing the chain if that migration is revised.
    ///
    /// ENABLE, not FORCE: this matches the idiom every other tenant table uses
    /// (20260723120000_CompleteTenantRlsCoverage). FORCE is reserved in this codebase for
    /// the four AI-governance tables. Applying it here would also subject the database
    /// OWNER role to the policy, and TenantRlsCommandInterceptor.ResolveDatabaseRole
    /// returns null — i.e. leaves the connection on the owner/login role with no
    /// nexora.business_unit_id GUC set — for anonymous and non-login routes, which would
    /// then read zero rows instead of being correctly scoped.
    ///
    /// STILL OPEN after this migration (owner of Models/ErpRfqAutomationContext.Tenancy.cs):
    /// the EF global query filters for FxRate and FxRateSnapshot. RLS does not constrain
    /// nexora_pipeline_app, which is created BYPASSRLS and is the role every background
    /// worker runs under, so the query filter remains the layer that protects worker-side
    /// reads of FX data.
    /// </summary>
    public partial class TenantIsolationForFxAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $fx_rls$
                DECLARE
                    fx_table text;
                    tenant_setting constant text :=
                        'NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint';
                BEGIN
                    FOREACH fx_table IN ARRAY ARRAY['FxRates', 'FxRateSnapshots']
                    LOOP
                        IF NOT EXISTS (
                            SELECT 1
                            FROM pg_class table_definition
                            JOIN pg_namespace schema_definition
                                ON schema_definition.oid = table_definition.relnamespace
                            WHERE schema_definition.nspname = 'public'
                              AND table_definition.relname = fx_table)
                        THEN
                            CONTINUE;
                        END IF;

                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', fx_table);
                        EXECUTE format(
                            'DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', fx_table);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = %s) WITH CHECK ("BusinessUnitId" = %s)',
                            fx_table, tenant_setting, tenant_setting);

                        -- Blanket privileges are revoked by default in this database, so a table
                        -- added after the role migration is unreadable until granted explicitly.
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                            EXECUTE format(
                                'GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO nexora_tenant_app',
                                fx_table);
                        END IF;
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                            EXECUTE format(
                                'GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO nexora_pipeline_app',
                                fx_table);
                        END IF;
                    END LOOP;
                END
                $fx_rls$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $fx_rls_down$
                DECLARE fx_table text;
                BEGIN
                    FOREACH fx_table IN ARRAY ARRAY['FxRates', 'FxRateSnapshots']
                    LOOP
                        IF EXISTS (
                            SELECT 1
                            FROM pg_class table_definition
                            JOIN pg_namespace schema_definition
                                ON schema_definition.oid = table_definition.relnamespace
                            WHERE schema_definition.nspname = 'public'
                              AND table_definition.relname = fx_table)
                        THEN
                            EXECUTE format(
                                'DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', fx_table);
                            EXECUTE format(
                                'ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY', fx_table);
                        END IF;
                    END LOOP;
                END
                $fx_rls_down$;
                """);
        }
    }
}

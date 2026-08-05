using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Independent-review hardening for the platform control plane
    /// (20260805034514_PlatformAdmin360ControlPlane). Two defects were raised against
    /// that migration and are corrected here rather than by rewriting it, because it
    /// already has successors:
    ///
    /// 1. It granted table-level SELECT on platform."Tenants"/"Plans"/"ImpersonationSessions"
    ///    to the tenant and identity execution roles so suspension/entitlement checks and
    ///    impersonation revocation could run. Table-level is wider than those checks need:
    ///    a filter bug or injection in the tenant plane could enumerate the whole customer
    ///    directory, internal status reasons, support-impersonation reason text, and
    ///    revoking-operator emails. Narrowed to column-level grants.
    ///
    /// 2. Finalized billing statements were immutable only in service code while
    ///    nexora_pipeline_app retained UPDATE/DELETE. Revenue records now carry the same
    ///    database-level protection as the AI ledger and the platform audit log.
    /// </summary>
    public partial class HardenPlatformGrantsAndBillingImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql("""
                DO $narrow_platform_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RETURN;
                    END IF;

                    -- Replace the table-wide SELECT with exactly the columns the tenant-plane
                    -- checks read. Tenant names, slugs, status reasons, impersonation reasons
                    -- and revoking-operator emails become unreadable to these roles.
                    REVOKE SELECT ON TABLE platform."Tenants", platform."Plans",
                        platform."ImpersonationSessions"
                        FROM nexora_tenant_app, nexora_identity_app;

                    -- TenantAccessService: resolve tenant status + plan entitlements by BU.
                    GRANT SELECT ("Id", "PrimaryBusinessUnitId", "Status", "PlanId")
                        ON TABLE platform."Tenants"
                        TO nexora_tenant_app, nexora_identity_app;
                    GRANT SELECT ("Id", "Code", "Weight", "MaxConcurrentExtractionJobs",
                                  "MaxDocsPerMonth", "MaxSeats", "IsActive")
                        ON TABLE platform."Plans"
                        TO nexora_tenant_app, nexora_identity_app;
                    -- ReadOnlyImpersonationMiddleware: is this jti still live?
                    GRANT SELECT ("Id", "Jti", "RevokedAtUtc", "ExpiresAtUtc")
                        ON TABLE platform."ImpersonationSessions"
                        TO nexora_tenant_app, nexora_identity_app;
                END
                $narrow_platform_grants$;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform.nexora_guard_billing_statement_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."Status" = 'Final' THEN
                            RAISE EXCEPTION 'Finalized billing statements are immutable'
                                USING ERRCODE = '55000';
                        END IF;
                        RETURN OLD;
                    END IF;

                    -- A Final row is closed for every further write. Draft -> Final is the
                    -- last permitted transition and is applied while the row is still Draft.
                    IF OLD."Status" = 'Final' THEN
                        RAISE EXCEPTION 'Finalized billing statements are immutable'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_billing_statement_mutation() FROM PUBLIC;

                CREATE OR REPLACE FUNCTION platform.nexora_guard_billing_statement_line_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE parent_status text;
                BEGIN
                    SELECT statement."Status" INTO parent_status
                    FROM platform."BillingStatements" statement
                    WHERE statement."Id" = COALESCE(NEW."BillingStatementId", OLD."BillingStatementId");

                    IF parent_status = 'Final' THEN
                        RAISE EXCEPTION 'Lines of a finalized billing statement are immutable'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN COALESCE(NEW, OLD);
                END
                $function$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_billing_statement_line_mutation() FROM PUBLIC;

                DROP TRIGGER IF EXISTS billing_statements_guard_update ON platform."BillingStatements";
                CREATE TRIGGER billing_statements_guard_update
                    BEFORE UPDATE ON platform."BillingStatements"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_mutation();

                DROP TRIGGER IF EXISTS billing_statements_guard_delete ON platform."BillingStatements";
                CREATE TRIGGER billing_statements_guard_delete
                    BEFORE DELETE ON platform."BillingStatements"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_mutation();

                DROP TRIGGER IF EXISTS billing_statement_lines_guard_write ON platform."BillingStatementLines";
                CREATE TRIGGER billing_statement_lines_guard_write
                    BEFORE INSERT OR UPDATE OR DELETE ON platform."BillingStatementLines"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_billing_statement_line_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS billing_statement_lines_guard_write ON platform."BillingStatementLines";
                DROP TRIGGER IF EXISTS billing_statements_guard_delete ON platform."BillingStatements";
                DROP TRIGGER IF EXISTS billing_statements_guard_update ON platform."BillingStatements";
                DROP FUNCTION IF EXISTS platform.nexora_guard_billing_statement_line_mutation();
                DROP FUNCTION IF EXISTS platform.nexora_guard_billing_statement_mutation();
                """);

            // Restore the table-level grants this migration narrowed, so rolling back
            // returns the previous migration's exact privilege surface.
            migrationBuilder.Sql("""
                DO $restore_platform_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RETURN;
                    END IF;

                    REVOKE SELECT ON TABLE platform."Tenants", platform."Plans",
                        platform."ImpersonationSessions"
                        FROM nexora_tenant_app, nexora_identity_app;
                    GRANT SELECT ON TABLE platform."Tenants", platform."Plans",
                        platform."ImpersonationSessions"
                        TO nexora_tenant_app, nexora_identity_app;
                END
                $restore_platform_grants$;
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}

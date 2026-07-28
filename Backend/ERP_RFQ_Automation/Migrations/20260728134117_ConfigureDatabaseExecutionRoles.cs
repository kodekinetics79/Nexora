using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class ConfigureDatabaseExecutionRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $roles$
                DECLARE runtime_role name;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                        CREATE ROLE nexora_identity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        CREATE ROLE nexora_pipeline_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
                    END IF;

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
                END
                $roles$;

                GRANT USAGE ON SCHEMA public TO nexora_identity_app;
                GRANT SELECT ON TABLE public."Users", public."Setup_Master", public."BusinessUnits"
                    TO nexora_identity_app;
                REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory"
                    FROM nexora_identity_app;

                GRANT USAGE ON SCHEMA public, platform TO nexora_pipeline_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public, platform
                    TO nexora_pipeline_app;
                GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public, platform
                    TO nexora_pipeline_app;

                REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory", public."FinanceProviderSecrets"
                    FROM nexora_pipeline_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformAuditLogs"
                    FROM nexora_pipeline_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP OWNED BY nexora_identity_app;
                DROP OWNED BY nexora_pipeline_app;
                """);
        }
    }
}

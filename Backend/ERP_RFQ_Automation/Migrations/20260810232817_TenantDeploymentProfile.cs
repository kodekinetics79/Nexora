using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The server-authoritative deployment profile: PRODUCTION, LOCAL_TEST or DEMO.
    ///
    /// <para><b>Why a column and not an inference.</b> The obvious alternative was to work out
    /// "this is a test tenant" from <c>ASPNETCORE_ENVIRONMENT</c>, the hostname, or a slug
    /// convention. Every one of those is a property of the PROCESS rather than of the customer
    /// record, so a production tenant read by an API started with the wrong environment variable
    /// would inherit a relaxed activation gate — and nothing in the audit trail would say it had.
    /// A column is read the same way by every process, travels with a backup, and appears in the
    /// activation decision it governs.</para>
    ///
    /// <para><b>The default is the strict one, at the database level.</b> <c>DEFAULT 'Production'</c>
    /// means a row inserted by anything that does not know about this column — an older code path,
    /// a hand-written INSERT, a restored backup, a fixture — lands on the profile that defers
    /// nothing. There is deliberately no value of this column that relaxes anything on its own:
    /// <c>DeploymentProfilePolicy</c> requires an approver, an approval instant and a reason before
    /// a DEMO tenant defers a single control, and all three are written only by the Owner-gated,
    /// audited <c>PUT /api/platform/tenants/{id}/deployment-profile</c>.</para>
    /// </summary>
    public partial class TenantDeploymentProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeploymentProfile",
                schema: "platform",
                table: "Tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Production");

            migrationBuilder.AddColumn<string>(
                name: "DeploymentProfileApprovedBy",
                schema: "platform",
                table: "Tenants",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeploymentProfileApprovedOn",
                schema: "platform",
                table: "Tenants",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeploymentProfileReason",
                schema: "platform",
                table: "Tenants",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            GrantDeploymentProfilePrivileges(migrationBuilder);
        }

        /// <summary>
        /// Who may read and write a tenant's deployment profile.
        ///
        /// <para><c>nexora_pipeline_app</c> is the platform plane. Its grant on
        /// <c>platform."Tenants"</c> is TABLE-level (20260805034514), so the four columns above are
        /// already covered — this block re-issues it explicitly anyway, following the same
        /// point-in-time reasoning that migration gave: a blanket grant issued before a column
        /// existed is not evidence that the column is readable, and the cheapest way to be sure is
        /// to say so.</para>
        ///
        /// <para><c>nexora_tenant_app</c> and <c>nexora_identity_app</c> get NOTHING here, and the
        /// REVOKE below is the point of this block rather than a formality. 20260805105320
        /// deliberately narrowed those two roles from table-level SELECT to exactly the four
        /// columns suspension and entitlement checks read, and that narrowing is asserted column by
        /// column by <c>PlatformControlPlaneHardeningPostgreSqlTests</c>. The profile, the reason it
        /// was set, and the operator who approved it are internal operator reasoning about how
        /// strictly a customer is being governed — precisely the class of text that narrowing
        /// removed from the tenant plane. An explicit REVOKE is a no-op today and stays correct if
        /// somebody later re-issues a table-wide grant to those roles.</para>
        /// </summary>
        private static void GrantDeploymentProfilePrivileges(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $deployment_profile_grants$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT (
                            "DeploymentProfile", "DeploymentProfileReason",
                            "DeploymentProfileApprovedBy", "DeploymentProfileApprovedOn"
                        ) ON TABLE platform."Tenants" TO nexora_pipeline_app;
                        GRANT UPDATE (
                            "DeploymentProfile", "DeploymentProfileReason",
                            "DeploymentProfileApprovedBy", "DeploymentProfileApprovedOn"
                        ) ON TABLE platform."Tenants" TO nexora_pipeline_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        REVOKE SELECT (
                            "DeploymentProfile", "DeploymentProfileReason",
                            "DeploymentProfileApprovedBy", "DeploymentProfileApprovedOn"
                        ) ON TABLE platform."Tenants" FROM nexora_tenant_app;
                        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE platform."Tenants"
                            FROM nexora_tenant_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                        REVOKE SELECT (
                            "DeploymentProfile", "DeploymentProfileReason",
                            "DeploymentProfileApprovedBy", "DeploymentProfileApprovedOn"
                        ) ON TABLE platform."Tenants" FROM nexora_identity_app;
                        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE platform."Tenants"
                            FROM nexora_identity_app;
                    END IF;
                END
                $deployment_profile_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No grant rollback: dropping the columns drops every privilege that names them, and
            // the two REVOKEs above only removed privileges the tenant plane was never granted.
            migrationBuilder.DropColumn(
                name: "DeploymentProfile",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DeploymentProfileApprovedBy",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DeploymentProfileApprovedOn",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DeploymentProfileReason",
                schema: "platform",
                table: "Tenants");
        }
    }
}

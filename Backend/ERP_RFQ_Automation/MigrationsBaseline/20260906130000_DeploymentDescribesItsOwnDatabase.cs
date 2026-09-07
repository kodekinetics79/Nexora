using System;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// One row saying what database this deployment keeps its customers' data in.
    ///
    /// <para><b>Why the table exists.</b> <c>data.residency-isolation</c> needs a provider
    /// reference, a region and a versioned backup policy for the database every tenant lives in.
    /// Those facts had two homes and neither belonged to the person who meets them: four opaque
    /// fields in a dialog, retyped per tenant, or four environment variables on the API service.
    /// An operator onboarding a customer cannot answer either — the Neon endpoint id is not in
    /// their world, and an environment variable needs a deploy and a dashboard. So the values move
    /// to a row an Owner records once in the console, prefilled with what the process reads off its
    /// own live connection, and audited like every other governed statement. Configuration still
    /// works and is still right for infrastructure-as-code deployments; it is no longer the only
    /// door. The precedent is <c>PlatformEmailSettings</c>, which left configuration for the same
    /// reason.</para>
    ///
    /// <para><b>Single row, enforced here rather than in application code.</b> Two rows would be
    /// two answers to where a customer's data lives, and the failure mode is not a duplicate — it
    /// is the auditor reading one row while the probe measures against the other.</para>
    ///
    /// <para>Additive: one new table in the platform schema, no column touched, no data migrated.
    /// A deployment that never opens the screen behaves exactly as it does today.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260906130000_DeploymentDescribesItsOwnDatabase")]
    public partial class DeploymentDescribesItsOwnDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformDataBoundarySettings",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    OpaqueProviderReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BackupPolicyReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BackupPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Basis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ObservedHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RecordedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformDataBoundarySettings", x => x.Id);
                    table.CheckConstraint("CK_PlatformDataBoundarySettings_Singleton", "\"Id\" = 1");
                });

            // A new table in this database is readable by NOBODY until it is said so: every table
            // in the platform schema is granted per table in 09_privileges.sql, and the runtime
            // roles are least-privilege. The control plane serves under nexora_pipeline_app, so
            // that role — and only that role — gets to read and write this row. No DELETE: the
            // deployment's database is corrected, never withdrawn, and a TRUNCATE on a singleton
            // is indistinguishable from "this deployment has never said where its data lives".
            //
            // The tenant and identity roles are deliberately absent. Unlike PlatformEmailSettings,
            // which they read column-by-column because outbound mail is composed on their paths,
            // nothing on a tenant or identity path has any business knowing which database the
            // platform runs on.
            // Guarded rather than returned on, unlike the pure-SQL migrations: the CreateTable
            // above has to run on every provider, and roles and GRANTs exist only on PostgreSQL.
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql("""
                DO $security$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT, UPDATE
                            ON TABLE platform."PlatformDataBoundarySettings"
                            TO nexora_pipeline_app;

                        REVOKE TRUNCATE, DELETE
                            ON TABLE platform."PlatformDataBoundarySettings"
                            FROM nexora_pipeline_app;
                    END IF;
                END
                $security$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The grants go with the table; dropping it drops them.
            migrationBuilder.DropTable(
                name: "PlatformDataBoundarySettings",
                schema: "platform");
        }
    }
}

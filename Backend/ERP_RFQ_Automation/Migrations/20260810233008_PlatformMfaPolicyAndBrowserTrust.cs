using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Server-authoritative platform MFA enforcement.
    ///
    /// <para>Three things land together because they are one control: the singleton policy row that
    /// says whether a second factor is enforced, the browser-trust ledger that keeps "remember this
    /// browser" from being a plaintext secret in a cookie, and two columns on
    /// <c>PlatformSessions</c> that bind a session to the trust it came from and record the last
    /// password re-authentication performed on it.</para>
    ///
    /// <para>No data migration and no backfill: the ABSENCE of a policy row means REQUIRED, which is
    /// the behaviour every existing deployment already has. An upgrade therefore changes nothing
    /// about enforcement until an Owner deliberately changes it, which is the only safe default for
    /// a migration that touches authentication.</para>
    /// </summary>
    public partial class PlatformMfaPolicyAndBrowserTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BrowserTrustId",
                schema: "platform",
                table: "PlatformSessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPasswordReauthAtUtc",
                schema: "platform",
                table: "PlatformSessions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlatformBrowserTrusts",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformBrowserTrusts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformBrowserTrusts_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlatformMfaPolicies",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "REQUIRED"),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    EnvironmentClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Production"),
                    ChangedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ChangedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiryRecordedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMfaPolicies", x => x.Id);
                    table.CheckConstraint("CK_PlatformMfaPolicies_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformBrowserTrusts_PlatformUserId_RevokedAtUtc_ExpiresAt~",
                schema: "platform",
                table: "PlatformBrowserTrusts",
                columns: new[] { "PlatformUserId", "RevokedAtUtc", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformBrowserTrusts_TokenHash",
                schema: "platform",
                table: "PlatformBrowserTrusts",
                column: "TokenHash",
                unique: true);

            GrantMfaPolicyPrivileges(migrationBuilder);
        }

        /// <summary>
        /// Who may read and write platform MFA enforcement.
        ///
        /// <para><b>Why this block has to exist at all.</b> The blanket
        /// <c>GRANT … ON ALL TABLES IN SCHEMA platform</c> from 20260728134117 was point-in-time and
        /// grants nothing on a table created afterwards. Without this, the very first attempt to read
        /// the policy fails with 42501 — and because the read path fails CLOSED, the platform would
        /// enforce MFA correctly while logging an error nobody connects to a missing GRANT. The
        /// SQLite suite would stay entirely green throughout, because SQLite has neither roles nor
        /// privileges.</para>
        ///
        /// <para><b>Why only the pipeline role.</b> <c>/api/platform</c> with no tenant scope resolves
        /// to <c>nexora_pipeline_app</c>, and that is the only plane that has any business knowing —
        /// let alone changing — whether operator MFA is enforced. The tenant and identity roles are
        /// explicitly REVOKED rather than merely left ungranted, so a future blanket grant cannot
        /// quietly hand a customer's plane the ability to read a table that says "the operators are
        /// not using a second factor right now, until 18:00".</para>
        ///
        /// <para><b>No DELETE on either table.</b> Deleting the policy row would revert enforcement to
        /// REQUIRED — fail-safe — but it would also erase the record of what was in force and who set
        /// it, which is the half of this control that survives the incident. Deleting a browser-trust
        /// row would orphan the <c>PlatformSessions.BrowserTrustId</c> that points at it and break the
        /// revocation check that reads it. Revocation is an UPDATE, deliberately.</para>
        ///
        /// <para>No sequence grant on <c>PlatformMfaPolicies</c>: it has a fixed key
        /// (<c>ValueGeneratedNever</c>), so no identity sequence exists and asking for one raises
        /// 42P01 and takes the migration down with it — the same trap 20260807191755 documented.
        /// <c>PlatformBrowserTrusts</c> uses an IDENTITY column, whose sequence is owned by the
        /// column and carries no separate privilege requirement.</para>
        /// </summary>
        private static void GrantMfaPolicyPrivileges(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $platform_mfa_policy_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    GRANT SELECT, INSERT, UPDATE ON TABLE
                        platform."PlatformMfaPolicies", platform."PlatformBrowserTrusts"
                        TO nexora_pipeline_app;

                    REVOKE DELETE, TRUNCATE ON TABLE
                        platform."PlatformMfaPolicies", platform."PlatformBrowserTrusts"
                        FROM nexora_pipeline_app;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        REVOKE ALL PRIVILEGES ON TABLE
                            platform."PlatformMfaPolicies", platform."PlatformBrowserTrusts"
                            FROM nexora_tenant_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                        REVOKE ALL PRIVILEGES ON TABLE
                            platform."PlatformMfaPolicies", platform."PlatformBrowserTrusts"
                            FROM nexora_identity_app;
                    END IF;
                END
                $platform_mfa_policy_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformBrowserTrusts",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "PlatformMfaPolicies",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "BrowserTrustId",
                schema: "platform",
                table: "PlatformSessions");

            migrationBuilder.DropColumn(
                name: "LastPasswordReauthAtUtc",
                schema: "platform",
                table: "PlatformSessions");
        }
    }
}

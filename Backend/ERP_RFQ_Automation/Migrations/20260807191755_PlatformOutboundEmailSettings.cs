using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlatformOutboundEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformEmailSettings",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "console"),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReplyToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    AppBaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SmtpPassword = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SmtpEnableSsl = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpTimeoutMs = table.Column<int>(type: "integer", nullable: false),
                    SendGridApiKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SendGridApiBaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    OutboundGuardMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Live"),
                    OutboundGuardRedirectTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    OutboundGuardAllowedRecipients = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OutboundGuardAllowedDomains = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OutboundGuardSubjectTag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    UpdateReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastVerifiedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    LastVerifiedRecipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    LastFailureAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastFailureKind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    LastFailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformEmailSettings", x => x.Id);
                    table.CheckConstraint("CK_PlatformEmailSettings_Singleton", "\"Id\" = 1");
                });

            GrantOutboundEmailPrivileges(migrationBuilder);
        }

        /// <summary>
        /// Who may read and write the platform's outbound email identity.
        ///
        /// <para><c>nexora_pipeline_app</c> is the platform plane and the background workers: it
        /// reads everything and writes the configuration. Deliberately no DELETE — a single-row
        /// configuration that can be deleted is a way to silently revert the whole platform to the
        /// console provider, which logs mail instead of sending it, and that failure is invisible
        /// until a customer says they never received something.</para>
        ///
        /// <para><c>nexora_tenant_app</c> and <c>nexora_identity_app</c> get SELECT on the
        /// TRANSPORT columns only, because a tenant-scoped request that delivers a quote resolves
        /// the transport on its own connection. They cannot read who configured it, why, or when it
        /// was last verified — that is operator correspondence about the platform, not tenant data
        /// — and they cannot write anything at all. The credential columns ARE included, and that
        /// is safe for the same reason 20260806194714 gave for mailbox passwords: they hold
        /// AES-256-GCM envelopes whose key lives in application configuration and never in the
        /// database, so a role that can read the row still cannot read the password.</para>
        /// </summary>
        private static void GrantOutboundEmailPrivileges(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $outbound_email_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    GRANT SELECT, INSERT, UPDATE ON TABLE platform."PlatformEmailSettings"
                        TO nexora_pipeline_app;
                    -- No sequence grant: this is a single-row configuration with a fixed key
                    -- (ValueGeneratedNever), so no identity sequence exists to grant on. Asking
                    -- for one raises 42P01 and takes the whole migration down with it.
                    REVOKE DELETE, TRUNCATE ON TABLE platform."PlatformEmailSettings"
                        FROM nexora_pipeline_app;

                    GRANT SELECT (
                        "Id", "Version", "UpdatedAtUtc",
                        "Provider", "FromAddress", "FromName", "ReplyToAddress", "AppBaseUrl",
                        "SmtpHost", "SmtpPort", "SmtpUsername", "SmtpPassword", "SmtpEnableSsl",
                        "SmtpTimeoutMs", "SendGridApiKey", "SendGridApiBaseUrl",
                        "OutboundGuardMode", "OutboundGuardRedirectTo",
                        "OutboundGuardAllowedRecipients", "OutboundGuardAllowedDomains",
                        "OutboundGuardSubjectTag"
                    ) ON TABLE platform."PlatformEmailSettings"
                        TO nexora_tenant_app, nexora_identity_app;

                    REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformEmailSettings"
                        FROM nexora_tenant_app, nexora_identity_app;
                END
                $outbound_email_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformEmailSettings",
                schema: "platform");
        }
    }
}

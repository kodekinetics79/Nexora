using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlatformSessionLegalHoldAndPurgeFencing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComputedBy",
                schema: "platform",
                table: "BillingStatements",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "system:legacy");

            migrationBuilder.AddColumn<Guid>(
                name: "PurgeAttemptId",
                schema: "platform",
                table: "TenantOffboardings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PurgeExecutedOn",
                schema: "platform",
                table: "TenantOffboardings",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PurgeExecutedRowCount",
                schema: "platform",
                table: "TenantOffboardings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurgeExecutionDetail",
                schema: "platform",
                table: "TenantOffboardings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SessionGeneration",
                schema: "platform",
                table: "PlatformUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "PlatformSessions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    SessionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformSessions_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantLegalHolds",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Authority = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PlacedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PlacedByPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    PlacedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ReleasedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReleasedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReleasedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLegalHolds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSessions_Jti",
                schema: "platform",
                table: "PlatformSessions",
                column: "Jti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSessions_PlatformUserId_RevokedAtUtc_ExpiresAtUtc",
                schema: "platform",
                table: "PlatformSessions",
                columns: new[] { "PlatformUserId", "RevokedAtUtc", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLegalHolds_TenantId_ReleasedOn",
                schema: "platform",
                table: "TenantLegalHolds",
                columns: new[] { "TenantId", "ReleasedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantLegalHolds_ActiveScope",
                schema: "platform",
                table: "TenantLegalHolds",
                columns: new[] { "TenantId", "Scope" },
                unique: true,
                filter: "\"ReleasedOn\" IS NULL");

            InstallPlatformSecurity(migrationBuilder);
        }

        private static void InstallPlatformSecurity(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                ALTER TABLE platform."TenantLegalHolds"
                    ADD CONSTRAINT "CK_TenantLegalHolds_ReleaseComplete" CHECK (
                        ("ReleasedOn" IS NULL AND "ReleasedByPlatformUserId" IS NULL
                         AND "ReleasedBy" IS NULL AND "ReleaseReason" IS NULL)
                        OR
                        ("ReleasedOn" IS NOT NULL AND "ReleasedByPlatformUserId" > 0
                         AND length(btrim("ReleasedBy")) > 0
                         AND length(btrim("ReleaseReason")) >= 15));

                ALTER TABLE platform."TenantLegalHolds"
                    ADD CONSTRAINT "CK_TenantLegalHolds_PlacementEvidence" CHECK (
                        "PlacedByPlatformUserId" > 0
                        AND length(btrim("Scope")) >= 3
                        AND length(btrim("Authority")) >= 3
                        AND length(btrim("Reason")) >= 15
                        AND length(btrim("EvidenceReference")) >= 3);

                ALTER TABLE platform."TenantOffboardings"
                    ADD CONSTRAINT "CK_TenantOffboardings_PurgeExecutionFence" CHECK (
                        ("PurgeExecutedOn" IS NULL AND "PurgeExecutedRowCount" IS NULL
                         AND "PurgeExecutionDetail" IS NULL)
                        OR
                        ("PurgeExecutedOn" IS NOT NULL AND "PurgeAttemptId" IS NOT NULL
                         AND "PurgeStartedOn" IS NOT NULL AND "PurgeExecutedRowCount" >= 0
                         AND "PurgeExecutionDetail" IS NOT NULL));

                CREATE OR REPLACE FUNCTION platform.nexora_guard_tenant_legal_hold()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Tenant legal holds are immutable and cannot be deleted.'
                            USING ERRCODE = '55000';
                    END IF;
                    IF OLD."TenantId" IS DISTINCT FROM NEW."TenantId"
                       OR OLD."Scope" IS DISTINCT FROM NEW."Scope"
                       OR OLD."Authority" IS DISTINCT FROM NEW."Authority"
                       OR OLD."Reason" IS DISTINCT FROM NEW."Reason"
                       OR OLD."EvidenceReference" IS DISTINCT FROM NEW."EvidenceReference"
                       OR OLD."PlacedOn" IS DISTINCT FROM NEW."PlacedOn"
                       OR OLD."PlacedByPlatformUserId" IS DISTINCT FROM NEW."PlacedByPlatformUserId"
                       OR OLD."PlacedBy" IS DISTINCT FROM NEW."PlacedBy" THEN
                        RAISE EXCEPTION 'Tenant legal-hold placement evidence is immutable.'
                            USING ERRCODE = '55000';
                    END IF;
                    IF OLD."ReleasedOn" IS NOT NULL AND (
                       OLD."ReleasedOn" IS DISTINCT FROM NEW."ReleasedOn"
                       OR OLD."ReleasedByPlatformUserId" IS DISTINCT FROM NEW."ReleasedByPlatformUserId"
                       OR OLD."ReleasedBy" IS DISTINCT FROM NEW."ReleasedBy"
                       OR OLD."ReleaseReason" IS DISTINCT FROM NEW."ReleaseReason") THEN
                        RAISE EXCEPTION 'A released tenant legal hold cannot be rewritten.'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_tenant_legal_hold() FROM PUBLIC;

                CREATE TRIGGER tenant_legal_holds_immutable
                    BEFORE UPDATE OR DELETE ON platform."TenantLegalHolds"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_tenant_legal_hold();
                ALTER TABLE platform."TenantLegalHolds"
                    ENABLE ALWAYS TRIGGER tenant_legal_holds_immutable;

                CREATE TRIGGER tenant_legal_holds_no_truncate
                    BEFORE TRUNCATE ON platform."TenantLegalHolds"
                    FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                ALTER TABLE platform."TenantLegalHolds"
                    ENABLE ALWAYS TRIGGER tenant_legal_holds_no_truncate;
                """);

            migrationBuilder.Sql("""
                DO $platform_security_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;
                    GRANT SELECT, INSERT, UPDATE ON TABLE
                        platform."PlatformSessions", platform."TenantLegalHolds"
                        TO nexora_pipeline_app;
                    -- Platform Owner AI provider approvals use the same transaction for
                    -- the effective allow-list row, tenant governance occurrence and
                    -- PlatformAudit row. The pipeline role needs append-only access to
                    -- the tenant occurrence ledger; UPDATE/DELETE/TRUNCATE stay revoked.
                    GRANT SELECT, INSERT ON TABLE public.tenant_governance_audit_events
                        TO nexora_pipeline_app;
                    GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                        platform."PlatformSessions_Id_seq", platform."TenantLegalHolds_Id_seq"
                        TO nexora_pipeline_app;
                    GRANT USAGE, SELECT ON SEQUENCE public."tenant_governance_audit_events_Id_seq"
                        TO nexora_pipeline_app;
                    -- Tenant authentication runs under the identity role and stamps only this
                    -- non-authoritative activity field after a successful login. Without this
                    -- column grant every login succeeds but emits a PostgreSQL 42501 error.
                    GRANT UPDATE ("LastLogin") ON TABLE public."Users" TO nexora_identity_app;
                    REVOKE DELETE, TRUNCATE ON TABLE
                        platform."PlatformSessions", platform."TenantLegalHolds"
                        FROM nexora_pipeline_app;
                    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE public.tenant_governance_audit_events
                        FROM nexora_pipeline_app;
                    REVOKE ALL PRIVILEGES ON TABLE
                        platform."PlatformSessions", platform."TenantLegalHolds"
                        FROM nexora_tenant_app, nexora_identity_app;
                    REVOKE ALL PRIVILEGES ON SEQUENCE
                        platform."PlatformSessions_Id_seq", platform."TenantLegalHolds_Id_seq"
                        FROM nexora_tenant_app, nexora_identity_app;
                END
                $platform_security_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql(
                    "DROP TRIGGER IF EXISTS tenant_legal_holds_no_truncate ON platform.\"TenantLegalHolds\";");
                migrationBuilder.Sql(
                    "DROP TRIGGER IF EXISTS tenant_legal_holds_immutable ON platform.\"TenantLegalHolds\";");
                migrationBuilder.Sql(
                    "DROP FUNCTION IF EXISTS platform.nexora_guard_tenant_legal_hold();");
            }

            migrationBuilder.DropTable(
                name: "PlatformSessions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantLegalHolds",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "ComputedBy",
                schema: "platform",
                table: "BillingStatements");

            migrationBuilder.DropColumn(
                name: "PurgeAttemptId",
                schema: "platform",
                table: "TenantOffboardings");

            migrationBuilder.DropColumn(
                name: "PurgeExecutedOn",
                schema: "platform",
                table: "TenantOffboardings");

            migrationBuilder.DropColumn(
                name: "PurgeExecutedRowCount",
                schema: "platform",
                table: "TenantOffboardings");

            migrationBuilder.DropColumn(
                name: "PurgeExecutionDetail",
                schema: "platform",
                table: "TenantOffboardings");

            migrationBuilder.DropColumn(
                name: "SessionGeneration",
                schema: "platform",
                table: "PlatformUsers");
        }
    }
}

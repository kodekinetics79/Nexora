using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAdmin360ControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Result",
                schema: "platform",
                table: "PlatformAuditLogs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "success");

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyPriceUsd",
                schema: "platform",
                table: "Plans",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImpersonationSessions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    ActorPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImpersonationSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RateCards",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateCards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingStatements",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RateCardId = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FinalizedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FinalizedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingStatements_RateCards_RateCardId",
                        column: x => x.RateCardId,
                        principalSchema: "platform",
                        principalTable: "RateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingStatements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RateCardLines",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RateCardId = table.Column<long>(type: "bigint", nullable: false),
                    MeterKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IncludedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TierNote = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateCardLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RateCardLines_RateCards_RateCardId",
                        column: x => x.RateCardId,
                        principalSchema: "platform",
                        principalTable: "RateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingStatementLines",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BillingStatementId = table.Column<long>(type: "bigint", nullable: false),
                    MeterKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MeteredQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    IncludedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    BillableQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    SourceNote = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingStatementLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingStatementLines_BillingStatements_BillingStatementId",
                        column: x => x.BillingStatementId,
                        principalSchema: "platform",
                        principalTable: "BillingStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingStatementLines_Statement",
                schema: "platform",
                table: "BillingStatementLines",
                column: "BillingStatementId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingStatements_RateCard",
                schema: "platform",
                table: "BillingStatements",
                column: "RateCardId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingStatements_Tenant_Status",
                schema: "platform",
                table: "BillingStatements",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_BillingStatements_Tenant_PeriodStart",
                schema: "platform",
                table: "BillingStatements",
                columns: new[] { "TenantId", "PeriodStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationSessions_ExpiresAtUtc",
                schema: "platform",
                table: "ImpersonationSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationSessions_Jti",
                schema: "platform",
                table: "ImpersonationSessions",
                column: "Jti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationSessions_TenantId_IssuedAtUtc",
                schema: "platform",
                table: "ImpersonationSessions",
                columns: new[] { "TenantId", "IssuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_RateCardLines_RateCard_MeterKey",
                schema: "platform",
                table: "RateCardLines",
                columns: new[] { "RateCardId", "MeterKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RateCards_Active_EffectiveFrom",
                schema: "platform",
                table: "RateCards",
                columns: new[] { "IsActive", "EffectiveFromUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_RateCards_Code",
                schema: "platform",
                table: "RateCards",
                column: "Code",
                unique: true);

            // Role grants for the platform-plane additions. Migrations execute only on
            // the PostgreSQL lane (portable tests use EnsureCreated); the DO-block guard
            // makes the SQL a no-op when the execution roles from
            // 20260728134117_ConfigureDatabaseExecutionRoles are absent (bare dev DBs).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $platform_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    -- The blanket grant in 20260728134117 was point-in-time: re-issue for
                    -- the tables added by this migration.
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform
                        TO nexora_pipeline_app;
                    GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA platform
                        TO nexora_pipeline_app;
                    -- Preserve the append-only audit contract.
                    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformAuditLogs"
                        FROM nexora_pipeline_app;

                    -- Tenant-plane enforcement (suspension + plan limits) and login checks
                    -- run under the tenant/identity roles, which previously had no access
                    -- to the platform schema. Grant the minimum read surface:
                    --   Tenants/Plans        -> tenant status + entitlement resolution
                    --   ImpersonationSessions -> revocation check on impersonated tokens
                    GRANT USAGE ON SCHEMA platform TO nexora_tenant_app, nexora_identity_app;
                    GRANT SELECT ON TABLE platform."Tenants", platform."Plans",
                        platform."ImpersonationSessions"
                        TO nexora_tenant_app, nexora_identity_app;
                END
                $platform_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingStatementLines",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ImpersonationSessions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "RateCardLines",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "BillingStatements",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "RateCards",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "Result",
                schema: "platform",
                table: "PlatformAuditLogs");

            migrationBuilder.DropColumn(
                name: "MonthlyPriceUsd",
                schema: "platform",
                table: "Plans");
        }
    }
}

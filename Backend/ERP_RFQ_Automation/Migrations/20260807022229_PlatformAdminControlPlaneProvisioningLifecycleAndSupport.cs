using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAdminControlPlaneProvisioningLifecycleAndSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProvisioningDrafts",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    OwnerPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SubmittedExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningExecutions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestPayload = table.Column<string>(type: "jsonb", nullable: false),
                    AdminPasswordHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AdminEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    AdminActivation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentStep = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailedStep = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailureIsTerminal = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    ProvisionedBusinessUnitId = table.Column<long>(type: "bigint", nullable: true),
                    FoundingRoleId = table.Column<long>(type: "bigint", nullable: true),
                    FoundingUserId = table.Column<long>(type: "bigint", nullable: true),
                    InvitationId = table.Column<long>(type: "bigint", nullable: true),
                    ResultPayload = table.Column<string>(type: "jsonb", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RequestedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    StartedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportTickets",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    AssignedToPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    RequesterEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    RequesterTenantUserId = table.Column<long>(type: "bigint", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FirstRespondedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RedactedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RedactedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportTickets_PlatformUsers_AssignedToPlatformUserId",
                        column: x => x.AssignedToPlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupportTickets_PlatformUsers_OpenedByPlatformUserId",
                        column: x => x.OpenedByPlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupportTickets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantExportReceipts",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    TenantSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    Sections = table.Column<string>(type: "jsonb", nullable: false),
                    TotalRows = table.Column<long>(type: "bigint", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantExportReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantLifecycleEvents",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    TenantSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FromStage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TenantStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Detail = table.Column<string>(type: "jsonb", nullable: true),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantOffboardings",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: true),
                    DeletionScheduledOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PurgeEligibleOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeletionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DeletionScheduledBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PurgeStartedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PurgedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PurgedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PurgeReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PurgedRowCount = table.Column<long>(type: "bigint", nullable: true),
                    PersonalDataErasedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PersonalDataErasedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PersonalDataErasureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ErasedIdentityCount = table.Column<long>(type: "bigint", nullable: true),
                    LastExportedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastExportedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOffboardings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningSteps",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: false),
                    StepCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    StartedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisioningSteps_ProvisioningExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "platform",
                        principalTable: "ProvisioningExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupportTicketLinks",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupportTicketId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TargetKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LinkedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    LinkedByLabel = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    LinkedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportTicketLinks_SupportTickets_SupportTicketId",
                        column: x => x.SupportTicketId,
                        principalSchema: "platform",
                        principalTable: "SupportTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupportTicketNotes",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupportTicketId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    AuthorKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AuthorLabel = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    IsInternal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportTicketNotes_SupportTickets_SupportTicketId",
                        column: x => x.SupportTicketId,
                        principalSchema: "platform",
                        principalTable: "SupportTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningDrafts_Owner_UpdatedOn",
                schema: "platform",
                table: "ProvisioningDrafts",
                columns: new[] { "OwnerEmail", "UpdatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningExecutions_Claim",
                schema: "platform",
                table: "ProvisioningExecutions",
                columns: new[] { "State", "LeaseUntil", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningExecutions_CreatedOn",
                schema: "platform",
                table: "ProvisioningExecutions",
                column: "CreatedOn");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningExecutions_TenantId",
                schema: "platform",
                table: "ProvisioningExecutions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_ProvisioningExecutions_IdempotencyKey",
                schema: "platform",
                table: "ProvisioningExecutions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ProvisioningExecutions_LiveSlug",
                schema: "platform",
                table: "ProvisioningExecutions",
                column: "Slug",
                unique: true,
                filter: "\"State\" IN ('Pending', 'Running', 'Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningSteps_Execution_Ordinal",
                schema: "platform",
                table: "ProvisioningSteps",
                columns: new[] { "ExecutionId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "UX_ProvisioningSteps_Execution_StepCode",
                schema: "platform",
                table: "ProvisioningSteps",
                columns: new[] { "ExecutionId", "StepCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketLinks_Kind_Target",
                schema: "platform",
                table: "SupportTicketLinks",
                columns: new[] { "Kind", "TargetKey" });

            migrationBuilder.CreateIndex(
                name: "UX_SupportTicketLinks_Ticket_Kind_Target",
                schema: "platform",
                table: "SupportTicketLinks",
                columns: new[] { "SupportTicketId", "Kind", "TargetKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketNotes_Ticket_Created",
                schema: "platform",
                table: "SupportTicketNotes",
                columns: new[] { "SupportTicketId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_Assignee_Status_Updated",
                schema: "platform",
                table: "SupportTickets",
                columns: new[] { "AssignedToPlatformUserId", "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_OpenedByPlatformUserId",
                schema: "platform",
                table: "SupportTickets",
                column: "OpenedByPlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_Status_Severity_Created",
                schema: "platform",
                table: "SupportTickets",
                columns: new[] { "Status", "Severity", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_Tenant_Status_Updated",
                schema: "platform",
                table: "SupportTickets",
                columns: new[] { "TenantId", "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantExportReceipts_TenantId_CompletedOn",
                schema: "platform",
                table: "TenantExportReceipts",
                columns: new[] { "TenantId", "CompletedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLifecycleEvents_Action_OccurredOn",
                schema: "platform",
                table: "TenantLifecycleEvents",
                columns: new[] { "Action", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLifecycleEvents_TenantId_OccurredOn",
                schema: "platform",
                table: "TenantLifecycleEvents",
                columns: new[] { "TenantId", "OccurredOn", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOffboardings_Stage_PurgeEligibleOn",
                schema: "platform",
                table: "TenantOffboardings",
                columns: new[] { "Stage", "PurgeEligibleOn" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantOffboardings_TenantId",
                schema: "platform",
                table: "TenantOffboardings",
                column: "TenantId",
                unique: true);

            GrantControlPlanePrivileges(migrationBuilder);
            InstallAppendOnlyGuards(migrationBuilder);
            PromoteBillingImmutabilityGuards(migrationBuilder);
        }

        /// <summary>
        /// Brings the finalized-statement guards up to the same standard as the evidence tables.
        ///
        /// <para>20260805105320 made a finalized <c>BillingStatement</c> immutable with ordinary
        /// triggers. Ordinary triggers do not fire under <c>session_replication_role = 'replica'</c>,
        /// which is the mode the tenant purge runs in on the owner connection — so a finalized
        /// statement, the record of what a customer was actually charged, could be rewritten or
        /// deleted from inside a replica-mode transaction.</para>
        ///
        /// <para>Nothing reaches that today: the purge preserves the billing tables by naming them,
        /// and the tenant data reset misses them only because it sweeps for
        /// <c>businessunitid</c>/<c>buid</c> while billing is keyed by <c>TenantId</c>. Both of
        /// those are lists and coincidences. This migration's own argument for
        /// <c>ENABLE ALWAYS</c> — that a protection resting on a list is not a protection — applies
        /// to these triggers exactly as it does to the ones above, so they are promoted rather than
        /// left as the one guard that a future replica-mode caller could walk through.</para>
        /// </summary>
        private static void PromoteBillingImmutabilityGuards(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $promote_billing_guards$
                BEGIN
                    IF to_regclass('platform."BillingStatements"') IS NOT NULL THEN
                        ALTER TABLE platform."BillingStatements"
                            ENABLE ALWAYS TRIGGER billing_statements_guard_update;
                        ALTER TABLE platform."BillingStatements"
                            ENABLE ALWAYS TRIGGER billing_statements_guard_delete;
                    END IF;
                    IF to_regclass('platform."BillingStatementLines"') IS NOT NULL THEN
                        ALTER TABLE platform."BillingStatementLines"
                            ENABLE ALWAYS TRIGGER billing_statement_lines_guard_write;
                    END IF;
                    -- The record of who changed a tenant's AI processing policy, and when. Exactly
                    -- the class of evidence the guards above protect: our account of what we did to
                    -- a customer, which must outlive the customer.
                    --
                    -- The trigger's NAME says PlatformAiPolicyAudits and it sits on
                    -- platform."PlatformAuditLogs" — 20260723140000 installs it there with a WHEN
                    -- clause narrowing it to Action = 'tenant.ai-policy.update', because the audit
                    -- log is where those rows live. Guarding on the name-implied table meant
                    -- to_regclass returned NULL, the IF was skipped, and the ALTER silently never
                    -- ran while the migration source read as though it had.
                    IF to_regclass('platform."PlatformAuditLogs"') IS NOT NULL THEN
                        ALTER TABLE platform."PlatformAuditLogs"
                            ENABLE ALWAYS TRIGGER platform_ai_policy_audits_immutable;
                    END IF;
                END
                $promote_billing_guards$;
                """);
        }

        /// <summary>
        /// Privileges for the nine control-plane tables this migration creates.
        ///
        /// <para><b>Why this is not optional.</b> The blanket
        /// <c>GRANT … ON ALL TABLES IN SCHEMA platform</c> in 20260728134117 was point-in-time: it
        /// grants nothing on a table created afterwards. Without this block every one of these
        /// tables is unreadable and unwritable by the runtime roles, and the first provisioning
        /// submit, offboarding action or support ticket fails with 42501 — while the SQLite suite
        /// stays entirely green, because SQLite has neither roles nor privileges.</para>
        ///
        /// <para><b>Why only the pipeline role.</b> Every one of these is operator-plane data:
        /// provisioning attempts, offboarding reasons, export receipts, support correspondence.
        /// <c>/api/platform</c> with no tenant scope resolves to <c>nexora_pipeline_app</c>, and so
        /// do the background workers. The tenant and identity roles are explicitly REVOKED rather
        /// than merely left ungranted, so a future blanket grant cannot silently hand a customer's
        /// plane the record of what we said about them internally.</para>
        /// </summary>
        private static void GrantControlPlanePrivileges(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $control_plane_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                        platform."ProvisioningExecutions", platform."ProvisioningSteps",
                        platform."ProvisioningDrafts",     platform."TenantOffboardings",
                        platform."SupportTickets",         platform."SupportTicketNotes",
                        platform."SupportTicketLinks"
                        TO nexora_pipeline_app;

                    -- Append-only by privilege as well as by trigger: the record of what happened
                    -- to a customer must not be editable by the application that acts on them.
                    GRANT SELECT, INSERT ON TABLE
                        platform."TenantLifecycleEvents", platform."TenantExportReceipts"
                        TO nexora_pipeline_app;

                    GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA platform
                        TO nexora_pipeline_app;

                    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE
                        platform."TenantLifecycleEvents", platform."TenantExportReceipts"
                        FROM nexora_pipeline_app;
                    -- The offboarding row is the tombstone that says a purge completed.
                    REVOKE DELETE, TRUNCATE ON TABLE platform."TenantOffboardings"
                        FROM nexora_pipeline_app;
                    -- A support note is correspondence: it may be deleted by an erasure, never
                    -- silently rewritten afterwards.
                    REVOKE UPDATE ON TABLE platform."SupportTicketNotes" FROM nexora_pipeline_app;

                    REVOKE ALL PRIVILEGES ON TABLE
                        platform."ProvisioningExecutions", platform."ProvisioningSteps",
                        platform."ProvisioningDrafts",     platform."TenantOffboardings",
                        platform."TenantLifecycleEvents",  platform."TenantExportReceipts",
                        platform."SupportTickets",         platform."SupportTicketNotes",
                        platform."SupportTicketLinks"
                        FROM nexora_tenant_app, nexora_identity_app;
                END
                $control_plane_grants$;
                """);
        }

        /// <summary>
        /// Makes the evidence tables append-only in the DATABASE rather than by privilege alone.
        ///
        /// <para><b>The gap this closes.</b> <c>PlatformAuditLogs</c> has been "immutable" since
        /// 20260728134117 by REVOKE — but a REVOKE binds a grantee, and the purge path runs on the
        /// OWNER connection under <c>session_replication_role = 'replica'</c>, where the owner
        /// bypasses table privileges and ordinary triggers do not fire. So the one operation in the
        /// system that deletes a customer's entire history was also the one operation nothing
        /// prevented from deleting the record of having done it.</para>
        ///
        /// <para><c>ENABLE ALWAYS</c> is the load-bearing clause: it is what makes a trigger fire in
        /// replica mode. A superuser can still disable or drop these, which is inherent and not the
        /// threat model — the threat model is the application and its own purge path, and both are
        /// stopped. TRUNCATE is guarded separately because it is a statement-level operation that a
        /// row-level trigger never sees.</para>
        /// </summary>
        private static void InstallAppendOnlyGuards(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform.nexora_guard_append_only_record()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION
                        'Table %.% is append-only: % is refused. This record is evidence about a '
                        'tenant and must outlive the tenant it describes.',
                        TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
                        USING ERRCODE = '55000';
                END
                $function$;
                """);
            migrationBuilder.Sql(
                "REVOKE ALL ON FUNCTION platform.nexora_guard_append_only_record() FROM PUBLIC;");

            // TenantOffboardings takes the DELETE/TRUNCATE guard only: it is the live working
            // record, and the purge writes its own completion into it.
            foreach (var (table, guardUpdates) in new[]
            {
                ("TenantLifecycleEvents", true),
                ("TenantExportReceipts", true),
                ("PlatformAuditLogs", true),
                ("TenantOffboardings", false)
            })
            {
                var rowOperations = guardUpdates ? "UPDATE OR DELETE" : "DELETE";
                var rowTrigger = $"{ToTriggerName(table)}_append_only";
                var truncateTrigger = $"{ToTriggerName(table)}_no_truncate";

                migrationBuilder.Sql($"""
                    DROP TRIGGER IF EXISTS {rowTrigger} ON platform."{table}";
                    CREATE TRIGGER {rowTrigger}
                        BEFORE {rowOperations} ON platform."{table}"
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                    ALTER TABLE platform."{table}" ENABLE ALWAYS TRIGGER {rowTrigger};

                    DROP TRIGGER IF EXISTS {truncateTrigger} ON platform."{table}";
                    CREATE TRIGGER {truncateTrigger}
                        BEFORE TRUNCATE ON platform."{table}"
                        FOR EACH STATEMENT EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                    ALTER TABLE platform."{table}" ENABLE ALWAYS TRIGGER {truncateTrigger};
                    """);
            }
        }

        /// <summary>PascalCase table name to the snake_case convention this schema's triggers use.</summary>
        private static string ToTriggerName(string table)
            => string.Concat(table.Select((c, i) =>
                char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Triggers first: PlatformAuditLogs is NOT dropped by this migration, so its guard
            // would otherwise survive as an orphan referencing a function that no longer exists.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                foreach (var table in new[]
                {
                    "TenantLifecycleEvents", "TenantExportReceipts", "PlatformAuditLogs", "TenantOffboardings"
                })
                {
                    migrationBuilder.Sql(
                        $"DROP TRIGGER IF EXISTS {ToTriggerName(table)}_append_only ON platform.\"{table}\";");
                    migrationBuilder.Sql(
                        $"DROP TRIGGER IF EXISTS {ToTriggerName(table)}_no_truncate ON platform.\"{table}\";");
                }
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.nexora_guard_append_only_record();");
            }

            migrationBuilder.DropTable(
                name: "ProvisioningDrafts",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ProvisioningSteps",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SupportTicketLinks",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SupportTicketNotes",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantExportReceipts",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantLifecycleEvents",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantOffboardings",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ProvisioningExecutions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SupportTickets",
                schema: "platform");
        }
    }
}

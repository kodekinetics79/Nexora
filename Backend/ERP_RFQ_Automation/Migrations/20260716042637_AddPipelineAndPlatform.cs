using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineAndPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "ExtractionJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FileType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    SchedulerTag = table.Column<double>(type: "double precision", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LeasedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResultLeadId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Weight = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrentExtractionJobs = table.Column<int>(type: "integer", nullable: false),
                    MaxDocsPerMonth = table.Column<int>(type: "integer", nullable: false),
                    MaxSeats = table.Column<int>(type: "integer", nullable: false),
                    Features = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAuditLogs",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    ActAsTenantId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformUsers",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    PlatformRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastLogin = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantQueueStates",
                columns: table => new
                {
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    InFlight = table.Column<int>(type: "integer", nullable: false),
                    LastVTime = table.Column<double>(type: "double precision", nullable: false),
                    Weight = table.Column<double>(type: "double precision", nullable: false, defaultValue: 1.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantQueueStates", x => x.BusinessUnitId);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlanId = table.Column<long>(type: "bigint", nullable: true),
                    PrimaryBusinessUnitId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenants_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "platform",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_BatchId",
                table: "ExtractionJobs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_BU_Status",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_Claim",
                table: "ExtractionJobs",
                columns: new[] { "Status", "NextAttemptAt", "Priority", "SchedulerTag" });

            migrationBuilder.CreateIndex(
                name: "UX_ExtractionJobs_BU_ContentHash",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Code",
                schema: "platform",
                table: "Plans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_ActAsTenantId_CreatedOn",
                schema: "platform",
                table: "PlatformAuditLogs",
                columns: new[] { "ActAsTenantId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_ActorPlatformUserId_CreatedOn",
                schema: "platform",
                table: "PlatformAuditLogs",
                columns: new[] { "ActorPlatformUserId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformUsers_Email",
                schema: "platform",
                table: "PlatformUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_PlanId",
                schema: "platform",
                table: "Tenants",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                schema: "platform",
                table: "Tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtractionJobs");

            migrationBuilder.DropTable(
                name: "PlatformAuditLogs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "PlatformUsers",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantQueueStates");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Plans",
                schema: "platform");
        }
    }
}

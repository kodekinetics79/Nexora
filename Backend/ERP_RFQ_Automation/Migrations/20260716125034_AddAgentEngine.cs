using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InputJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    DecidedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentApprovals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InputJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResultSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    ToolName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ToolInput = table.Column<string>(type: "jsonb", nullable: true),
                    ToolResult = table.Column<string>(type: "jsonb", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AutonomyLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MaxAutoAwardValue = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    MaxAutoOrderValue = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    RequireApprovalForAwards = table.Column<bool>(type: "boolean", nullable: false),
                    RequireApprovalForOrders = table.Column<bool>(type: "boolean", nullable: false),
                    RequireApprovalForSupplierEmails = table.Column<bool>(type: "boolean", nullable: false),
                    PerToolOverrides = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentApprovals_BU_Status",
                table: "AgentApprovals",
                columns: new[] { "BusinessUnitId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAuditLogs_BU_CreatedOn",
                table: "AgentAuditLogs",
                columns: new[] { "BusinessUnitId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMessages_Session_Sequence",
                table: "AgentMessages",
                columns: new[] { "SessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "UX_AgentPolicies_BU",
                table: "AgentPolicies",
                column: "BusinessUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_BU_UpdatedOn",
                table: "AgentSessions",
                columns: new[] { "BusinessUnitId", "UpdatedOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentApprovals");

            migrationBuilder.DropTable(
                name: "AgentAuditLogs");

            migrationBuilder.DropTable(
                name: "AgentMessages");

            migrationBuilder.DropTable(
                name: "AgentPolicies");

            migrationBuilder.DropTable(
                name: "AgentSessions");
        }
    }
}

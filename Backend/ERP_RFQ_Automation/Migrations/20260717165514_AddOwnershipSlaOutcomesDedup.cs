using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnershipSlaOutcomesDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leads_BusinessUnitID",
                table: "Leads");

            migrationBuilder.AddColumn<string>(
                name: "OutcomeNote",
                table: "Quotes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OutcomeOn",
                table: "Quotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutcomeReasonId",
                table: "Quotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RespondedOn",
                table: "Quotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentOn",
                table: "Quotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DuplicateOfLeadId",
                table: "Leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DuplicateResolvedBy",
                table: "Leads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DuplicateStatus",
                table: "Leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlaEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    UnassignedHours = table.Column<int>(type: "integer", nullable: false),
                    WarnDaysBeforeClose = table.Column<int>(type: "integer", nullable: false),
                    CriticalDaysBeforeClose = table.Column<int>(type: "integer", nullable: false),
                    StaleQuoteDays = table.Column<int>(type: "integer", nullable: false),
                    QuoteAutoExpireDays = table.Column<int>(type: "integer", nullable: false),
                    ApprovalEscalationHours = table.Column<int>(type: "integer", nullable: false),
                    DeadlineBufferHours = table.Column<int>(type: "integer", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lead_BU_DuplicateStatus",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "DuplicateStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaEvents_BU_Entity_Level",
                table: "SlaEvents",
                columns: new[] { "BusinessUnitId", "EntityType", "EntityId", "Level" });

            migrationBuilder.CreateIndex(
                name: "UX_SlaPolicies_BU",
                table: "SlaPolicies",
                column: "BusinessUnitId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlaEvents");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropIndex(
                name: "IX_Lead_BU_DuplicateStatus",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OutcomeNote",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "OutcomeOn",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "OutcomeReasonId",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "RespondedOn",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "SentOn",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "DuplicateOfLeadId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "DuplicateResolvedBy",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "DuplicateStatus",
                table: "Leads");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_BusinessUnitID",
                table: "Leads",
                column: "BusinessUnitID");
        }
    }
}

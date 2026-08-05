using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// ING-07: records the intake gate's decision on every fetched message. A message stopped
    /// as noise keeps its row, its raw .eml and — here — the machine-readable reason it was
    /// stopped, so a misjudged RFQ can be found and replayed instead of vanishing.
    /// Additive and nullable: existing rows read as "Legacy" (decided before the gate existed).
    /// </summary>
    public partial class EmailTriage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TriageDecidedOn",
                table: "EmailIngests",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriageOutcome",
                table: "EmailIngests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriageReasonJson",
                table: "EmailIngests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriageDecidedOn",
                table: "EmailIngests");

            migrationBuilder.DropColumn(
                name: "TriageOutcome",
                table: "EmailIngests");

            migrationBuilder.DropColumn(
                name: "TriageReasonJson",
                table: "EmailIngests");
        }
    }
}

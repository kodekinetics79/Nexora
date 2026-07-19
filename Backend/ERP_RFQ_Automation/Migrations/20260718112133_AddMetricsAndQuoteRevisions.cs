using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricsAndQuoteRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RevisionNo",
                table: "Quotes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "RevisionOfQuoteId",
                table: "Quotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MetricEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_RevisionOfQuoteId",
                table: "Quotes",
                column: "RevisionOfQuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricEvents_BU_Type_CreatedOn",
                table: "MetricEvents",
                columns: new[] { "BusinessUnitId", "Type", "CreatedOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricEvents");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_RevisionOfQuoteId",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "RevisionNo",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "RevisionOfQuoteId",
                table: "Quotes");
        }
    }
}

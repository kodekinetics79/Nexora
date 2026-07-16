using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcingLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingAwards",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: true),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    TotalValue = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AwardedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    AwardedByAgent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingAwards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierSolicitations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SentOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RespondedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierSolicitations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BU_Rfq",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BU_Rfq",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BU_Rfq_Supplier",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "RfqId", "SupplierId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourcingAwards");

            migrationBuilder.DropTable(
                name: "SupplierSolicitations");
        }
    }
}

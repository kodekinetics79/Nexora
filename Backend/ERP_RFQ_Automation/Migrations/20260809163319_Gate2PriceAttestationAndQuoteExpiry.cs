using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate2PriceAttestationAndQuoteExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuoteNoResponseExpiryDays",
                table: "SlaPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "QuotePriceAttestations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LineFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ConfirmedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotePriceAttestations", x => x.Id);
                    table.UniqueConstraint("AK_QuotePriceAttestations_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_QuotePriceAttestations_LineCount", "\"LineCount\" >= 0");
                    table.CheckConstraint("CK_QuotePriceAttestations_Source", "\"Source\" IN ('SALES_MANAGER','SUPPLIER_QUOTE')");
                    table.ForeignKey(
                        name: "FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId",
                        columns: x => new { x.BusinessUnitId, x.QuoteId },
                        principalTable: "Quotes",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuotePriceAttestationLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AttestationId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteItemId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: true),
                    ItemDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotePriceAttestationLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuotePriceAttestationLines_QuotePriceAttestations_BusinessU~",
                        columns: x => new { x.BusinessUnitId, x.AttestationId },
                        principalTable: "QuotePriceAttestations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuotePriceAttestationLines_BusinessUnitId_AttestationId",
                table: "QuotePriceAttestationLines",
                columns: new[] { "BusinessUnitId", "AttestationId" });

            migrationBuilder.CreateIndex(
                name: "UX_QuotePriceAttestationLines_Attestation_QuoteItem",
                table: "QuotePriceAttestationLines",
                columns: new[] { "AttestationId", "QuoteItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotePriceAttestations_BU_Quote_ConfirmedOn",
                table: "QuotePriceAttestations",
                columns: new[] { "BusinessUnitId", "QuoteId", "ConfirmedOn" });

            // Row-level security for the two new tenant-owned tables. An EF query filter is a
            // convenience, not a boundary — TenantIsolationTests fails any tenant table that has
            // a filter and no policy, and it is right to. Same shape as every other tenant table:
            // the predicate reads the transaction-local business unit the RLS interceptor sets.
            migrationBuilder.Sql("""
                ALTER TABLE public."QuotePriceAttestations" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."QuotePriceAttestations" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."QuotePriceAttestations" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."QuotePriceAttestationLines" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."QuotePriceAttestationLines" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."QuotePriceAttestationLines" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            // The grace knob changed meaning: it used to be "days after validity before expiring"
            // (shipped default 14) and is now "grace added to the validity date" (default 0).
            // Leaving stored rows at 14 would silently preserve the old behaviour for every tenant
            // that already has a policy row — the exact defect FR-QTM-07 describes.
            migrationBuilder.Sql("""UPDATE public."SlaPolicies" SET "QuoteAutoExpireDays" = 0;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuotePriceAttestationLines");

            migrationBuilder.DropTable(
                name: "QuotePriceAttestations");

            migrationBuilder.DropColumn(
                name: "QuoteNoResponseExpiryDays",
                table: "SlaPolicies");
        }
    }
}

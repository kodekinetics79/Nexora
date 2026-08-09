using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate4CaseSpineTradeTermsColumnPreferencesAndQuoteValidity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_order_lines_Costs",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_BusinessUnitID",
                table: "Shipments");

            migrationBuilder.AddColumn<string>(
                name: "CustomFields",
                table: "Suppliers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "HsCode",
                table: "supplier_purchase_order_lines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CountryOfOrigin",
                table: "supplier_purchase_order_lines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidityExtendedOn",
                table: "Quotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttestedPriceFingerprint",
                table: "quote_delivery_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomFields",
                table: "LeadItems",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomFields",
                table: "Customers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                table: "custom_field_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SupplierInputTaxRecoverable",
                table: "CommercialMatchingPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "QuoteValidityExtensions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousValidUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    NewValidUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExtendedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ExtendedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ExtendedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteValidityExtensions", x => x.Id);
                    table.UniqueConstraint("AK_QuoteValidityExtensions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_QuoteValidityExtensions_Reason", "trim(\"Reason\") <> ''");
                    table.ForeignKey(
                        name: "FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId",
                        columns: x => new { x.BusinessUnitId, x.QuoteId },
                        principalTable: "Quotes",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserColumnPreferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ViewKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Columns = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserColumnPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserColumnPreferences_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserColumnPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_orders_BusinessUnitId_CommercialCaseId",
                table: "supplier_purchase_orders",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_order_lines_Costs",
                table: "supplier_purchase_order_lines",
                sql: "\"UnitCost\" >= 0 AND \"LandedUnitCost\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_BusinessUnitID_CommercialCaseId",
                table: "Shipments",
                columns: new[] { "BusinessUnitID", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BU_ValidityExtendedOn",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "ValidityExtendedOn" },
                filter: "\"ValidityExtendedOn\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteValidityExtensions_BU_Quote_ExtendedOn",
                table: "QuoteValidityExtensions",
                columns: new[] { "BusinessUnitId", "QuoteId", "ExtendedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteValidityExtensions_BU_IdempotencyKey",
                table: "QuoteValidityExtensions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserColumnPreferences_UserId",
                table: "UserColumnPreferences",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_UserColumnPreferences_BU_User_View",
                table: "UserColumnPreferences",
                columns: new[] { "BusinessUnitId", "UserId", "ViewKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_CommercialCases_BusinessUnitID_CommercialCaseId",
                table: "Shipments",
                columns: new[] { "BusinessUnitID", "CommercialCaseId" },
                principalTable: "CommercialCases",
                principalColumns: new[] { "BusinessUnitID", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_purchase_orders_CommercialCases_BusinessUnitId_Com~",
                table: "supplier_purchase_orders",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" },
                principalTable: "CommercialCases",
                principalColumns: new[] { "BusinessUnitID", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Row-level security for the two new tenant-owned tables. An EF query filter is a
            // convenience, not a boundary — TenantIsolationTests fails any tenant table that has a
            // filter and no policy, and it is right to. Note both tables spell the column
            // "BusinessUnitId", not the legacy "BusinessUnitID" used by Quotes and Shipments;
            // copying the wrong precedent here would produce a policy that never matches.
            migrationBuilder.Sql("""
                ALTER TABLE public."QuoteValidityExtensions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."QuoteValidityExtensions" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."QuoteValidityExtensions" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."UserColumnPreferences" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."UserColumnPreferences" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."UserColumnPreferences" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            // Table privileges for the tenant role. These are NOT implied by anything: the
            // CompleteTenantRlsCoverage migration deliberately revoked the schema default
            // privileges, so `public` is deny-by-default and every new tenant table must be
            // granted explicitly. A policy without a grant is not a narrower boundary, it is a
            // table nobody can read — PostgreSQL checks the grant first and raises 42501 before
            // any row-level predicate is evaluated.
            //
            // Three earlier tables in this gate shipped with a policy and no grant, and the gap
            // stayed invisible because nothing running as the tenant role had read them yet. It
            // surfaced only when the landed-cost fix made the pricing path read
            // CommercialMatchingPolicies, at which point every supplier quote capture and
            // negotiation read returned 500. QuotePriceAttestations had the same defect and would
            // have failed the first time a tenant attested a price in production.
            //
            // RLS still does the isolating; these grants only make the tables reachable at all.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                    public."CommercialMatchingPolicies",
                    public."QuotePriceAttestations",
                    public."QuotePriceAttestationLines",
                    public."QuoteValidityExtensions",
                    public."UserColumnPreferences"
                TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_CommercialCases_BusinessUnitID_CommercialCaseId",
                table: "Shipments");

            migrationBuilder.DropForeignKey(
                name: "FK_supplier_purchase_orders_CommercialCases_BusinessUnitId_Com~",
                table: "supplier_purchase_orders");

            migrationBuilder.DropTable(
                name: "QuoteValidityExtensions");

            migrationBuilder.DropTable(
                name: "UserColumnPreferences");

            migrationBuilder.DropIndex(
                name: "IX_supplier_purchase_orders_BusinessUnitId_CommercialCaseId",
                table: "supplier_purchase_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_order_lines_Costs",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_BusinessUnitID_CommercialCaseId",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_BU_ValidityExtendedOn",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "CustomFields",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ValidityExtendedOn",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "AttestedPriceFingerprint",
                table: "quote_delivery_requests");

            migrationBuilder.DropColumn(
                name: "CustomFields",
                table: "LeadItems");

            migrationBuilder.DropColumn(
                name: "CustomFields",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "DisplayOrder",
                table: "custom_field_definitions");

            migrationBuilder.DropColumn(
                name: "SupplierInputTaxRecoverable",
                table: "CommercialMatchingPolicies");

            migrationBuilder.AlterColumn<string>(
                name: "HsCode",
                table: "supplier_purchase_order_lines",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CountryOfOrigin",
                table: "supplier_purchase_order_lines",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_order_lines_Costs",
                table: "supplier_purchase_order_lines",
                sql: "\"UnitCost\" >= 0 AND \"LandedUnitCost\" >= \"UnitCost\"");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_BusinessUnitID",
                table: "Shipments",
                column: "BusinessUnitID");
        }
    }
}

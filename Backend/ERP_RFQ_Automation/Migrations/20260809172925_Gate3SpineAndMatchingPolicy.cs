using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate3SpineAndMatchingPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CustomerOrderId",
                table: "supplier_purchase_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CustomerPurchaseOrderId",
                table: "supplier_purchase_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DemandSource",
                table: "supplier_purchase_orders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "QuoteId",
                table: "supplier_purchase_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommercialMatchingPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    PriceTolerancePercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false, defaultValue: 2.0m),
                    PriceToleranceMinimumAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false, defaultValue: 0m),
                    QuantityTolerancePercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false, defaultValue: 0m),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialMatchingPolicies", x => x.Id);
                    table.UniqueConstraint("AK_CommercialMatchingPolicies_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommercialMatchingPolicies_BusinessUnitId",
                table: "CommercialMatchingPolicies",
                column: "BusinessUnitId",
                unique: true);

            // Row-level security for the new tenant table. An EF query filter is a convenience;
            // the database boundary is the control, and TenantIsolationTests fails any tenant
            // table that has the former without the latter. The policy must be granted to
            // nexora_tenant_app specifically — a policy left on PUBLIC does not satisfy it.
            migrationBuilder.Sql("""
                ALTER TABLE public."CommercialMatchingPolicies" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."CommercialMatchingPolicies" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."CommercialMatchingPolicies" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            // Existing supplier purchase orders were all raised against a customer award — stock
            // replenishment had no way to exist — so CUSTOMER_DEMAND is the truthful backfill.
            // Their customer keys stay null: this migration cannot know which award each belonged
            // to, and inventing a link would be worse than an honest gap.
            migrationBuilder.Sql("""
                UPDATE public.supplier_purchase_orders SET "DemandSource" = 'CUSTOMER_DEMAND'
                WHERE "DemandSource" IS NULL OR "DemandSource" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommercialMatchingPolicies");

            migrationBuilder.DropColumn(
                name: "CustomerOrderId",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "CustomerPurchaseOrderId",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "DemandSource",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "QuoteId",
                table: "supplier_purchase_orders");
        }
    }
}

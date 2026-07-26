using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release02SupplierOfferPricingBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CommercialDemandLineId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceSupplierQuoteId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceSupplierQuoteLineId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceSupplierQuoteRevisionId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourcingCaseId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_QuoteItems_ID_QuoteID",
                table: "QuoteItems",
                columns: new[] { "ID", "QuoteID" });

            migrationBuilder.CreateTable(
                name: "customer_quote_sourcing_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteItemId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialDemandLineId = table.Column<long>(type: "bigint", nullable: false),
                    SourcingCaseId = table.Column<long>(type: "bigint", nullable: false),
                    SourcingAwardId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuotedItemId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteLineId = table.Column<long>(type: "bigint", nullable: false),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SupplierLandedUnitCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    TargetMarginPercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    CustomerUnitPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_quote_sourcing_decisions", x => x.Id);
                    table.UniqueConstraint("AK_customer_quote_sourcing_decisions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_customer_quote_sourcing_decisions_Values", "\"Quantity\" > 0 AND \"SupplierLandedUnitCost\" > 0 AND \"TargetMarginPercent\" >= 0 AND \"TargetMarginPercent\" < 95 AND \"CustomerUnitPrice\" > 0");
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_QuoteItems_QuoteItemId_Qu~",
                        columns: x => new { x.QuoteItemId, x.QuoteId },
                        principalTable: "QuoteItems",
                        principalColumns: new[] { "ID", "QuoteID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_Quotes_BusinessUnitId_Quo~",
                        columns: x => new { x.BusinessUnitId, x.QuoteId },
                        principalTable: "Quotes",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_SourcingAwards_BusinessUn~",
                        columns: x => new { x.BusinessUnitId, x.SourcingAwardId },
                        principalTable: "SourcingAwards",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_SupplierQuotedItems_Busin~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuotedItemId },
                        principalTable: "SupplierQuotedItems",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_commercial_demand_lines_B~",
                        columns: x => new { x.BusinessUnitId, x.CommercialDemandLineId },
                        principalTable: "commercial_demand_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_sourcing_cases_BusinessUn~",
                        columns: x => new { x.BusinessUnitId, x.SourcingCaseId },
                        principalTable: "sourcing_cases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_supplier_quote_lines_Busi~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteLineId },
                        principalTable: "supplier_quote_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_supplier_quote_revisions_~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId },
                        principalTable: "supplier_quote_revisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_quote_sourcing_decisions_supplier_quotes_BusinessU~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteId },
                        principalTable: "supplier_quotes",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_CommercialDemandLineId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourceSupplierQuoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteRevis~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourceSupplierQuoteRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SourcingCaseId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourcingCaseId" });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierQuotedItems_BU_SourceQuoteLine",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourceSupplierQuoteLineId" },
                unique: true,
                filter: "\"SourceSupplierQuoteLineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_Commercial~",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_Idempotenc~",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_QuoteId",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "QuoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_QuoteItemI~",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "QuoteItemId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_SourcingAw~",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "SourcingAwardId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_SourcingCa~",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "SourcingCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~1",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~2",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQ~3",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuotedItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_BusinessUnitId_SupplierQu~",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_quote_sourcing_decisions_QuoteItemId_QuoteId",
                table: "customer_quote_sourcing_decisions",
                columns: new[] { "QuoteItemId", "QuoteId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_commercial_demand_lines_BusinessUnitId_~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" },
                principalTable: "commercial_demand_lines",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_sourcing_cases_BusinessUnitId_SourcingC~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourcingCaseId" },
                principalTable: "sourcing_cases",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_supplier_quote_lines_BusinessUnitId_Sou~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourceSupplierQuoteLineId" },
                principalTable: "supplier_quote_lines",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_supplier_quote_revisions_BusinessUnitId~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourceSupplierQuoteRevisionId" },
                principalTable: "supplier_quote_revisions",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_supplier_quotes_BusinessUnitId_SourceSu~",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "SourceSupplierQuoteId" },
                principalTable: "supplier_quotes",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE TRIGGER customer_quote_sourcing_decisions_append_only
                    BEFORE UPDATE OR DELETE ON customer_quote_sourcing_decisions
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_supplier_quote_append_only_mutation();

                CREATE OR REPLACE FUNCTION nexora_protect_projected_supplier_quote_lineage()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF OLD."SourceSupplierQuoteId" IS NOT NULL AND (
                        NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                        OR NEW."SourceSupplierQuoteId" IS DISTINCT FROM OLD."SourceSupplierQuoteId"
                        OR NEW."SourceSupplierQuoteRevisionId" IS DISTINCT FROM OLD."SourceSupplierQuoteRevisionId"
                        OR NEW."SourceSupplierQuoteLineId" IS DISTINCT FROM OLD."SourceSupplierQuoteLineId"
                        OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
                        OR NEW."SourcingCaseId" IS DISTINCT FROM OLD."SourcingCaseId") THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'Projected Supplier Quote commercial lineage is immutable';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER supplier_quoted_items_projected_lineage_immutable
                    BEFORE UPDATE ON "SupplierQuotedItems"
                    FOR EACH ROW EXECUTE FUNCTION nexora_protect_projected_supplier_quote_lineage();

                ALTER TABLE public.customer_quote_sourcing_decisions ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public.customer_quote_sourcing_decisions;
                CREATE POLICY nexora_tenant_isolation ON public.customer_quote_sourcing_decisions
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                GRANT SELECT, INSERT ON TABLE public.customer_quote_sourcing_decisions TO nexora_tenant_app;
                REVOKE UPDATE, DELETE ON TABLE public.customer_quote_sourcing_decisions FROM nexora_tenant_app;
                DO $block$
                DECLARE sequence_name text;
                BEGIN
                    sequence_name := pg_get_serial_sequence('public.customer_quote_sourcing_decisions', 'Id');
                    IF sequence_name IS NOT NULL THEN
                        EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                    END IF;
                END
                $block$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_quoted_items_projected_lineage_immutable ON "SupplierQuotedItems";
                DROP FUNCTION IF EXISTS nexora_protect_projected_supplier_quote_lineage();
                DROP TRIGGER IF EXISTS customer_quote_sourcing_decisions_append_only ON customer_quote_sourcing_decisions;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_commercial_demand_lines_BusinessUnitId_~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_sourcing_cases_BusinessUnitId_SourcingC~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_supplier_quote_lines_BusinessUnitId_Sou~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_supplier_quote_revisions_BusinessUnitId~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_supplier_quotes_BusinessUnitId_SourceSu~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropTable(
                name: "customer_quote_sourcing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_CommercialDemandLineId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SourceSupplierQuoteRevis~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_SourcingCaseId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "UX_SupplierQuotedItems_BU_SourceQuoteLine",
                table: "SupplierQuotedItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_QuoteItems_ID_QuoteID",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "CommercialDemandLineId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "SourceSupplierQuoteId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "SourceSupplierQuoteLineId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "SourceSupplierQuoteRevisionId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "SourcingCaseId",
                table: "SupplierQuotedItems");
        }
    }
}

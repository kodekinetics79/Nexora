using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V2Gate04SupplierNegotiationIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RAISE EXCEPTION 'Required runtime role nexora_tenant_app is missing';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id",
                table: "supplier_quote_revisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteId", "Id" });

            migrationBuilder.CreateTable(
                name: "supplier_negotiation_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    RecommendationCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Disposition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ExpectedQuoteVersion = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DecidedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_negotiation_decisions", x => x.Id);
                    table.UniqueConstraint("AK_supplier_negotiation_decisions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_negotiation_decisions_Disposition", "\"Disposition\" IN ('PREPARED','DEFERRED','DISMISSED')");
                    table.CheckConstraint("CK_supplier_negotiation_decisions_ExpectedVersion", "\"ExpectedQuoteVersion\" > 0");
                    table.CheckConstraint("CK_supplier_negotiation_decisions_RecommendationCode", "\"RecommendationCode\" IN ('BEST_AND_FINAL_PRICE','QUANTITY_BREAK','FASTER_DELIVERY','FREIGHT_INCLUSIVE_OFFER','IMPROVED_PAYMENT_TERMS','PARTIAL_IMMEDIATE_AVAILABILITY','APPROVED_ALTERNATE')");
                    table.ForeignKey(
                        name: "FK_supplier_negotiation_decisions_supplier_quote_revisions_Bus~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteId, x.SupplierQuoteRevisionId },
                        principalTable: "supplier_quote_revisions",
                        principalColumns: new[] { "BusinessUnitId", "SupplierQuoteId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_negotiation_decisions_supplier_quotes_BusinessUnit~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteId },
                        principalTable: "supplier_quotes",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_negotiation_decisions_BusinessUnitId_IdempotencyKey",
                table: "supplier_negotiation_decisions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_negotiation_decisions_BusinessUnitId_SupplierQuote~",
                table: "supplier_negotiation_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteId", "SupplierQuoteRevisionId", "DecidedOn" });

            migrationBuilder.Sql("""
                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                SELECT 'Supplier Negotiation', 'Evidence-backed Supplier negotiation decisions', TRUE,
                    'V2Gate04SupplierNegotiationIntelligence', CURRENT_TIMESTAMP
                WHERE NOT EXISTS (
                    SELECT 1 FROM public."Module" WHERE "ModuleName" = 'Supplier Negotiation');

                INSERT INTO public."RolePermissions" (
                    "RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete",
                    "CreatedBy", "CreatedOn")
                SELECT source_permission."RoleID", negotiation_module."ID", source_permission."BusinessUnitID",
                    FALSE, COALESCE(source_permission."CanEdit", FALSE), FALSE,
                    'V2Gate04SupplierNegotiationIntelligence', CURRENT_TIMESTAMP
                FROM public."RolePermissions" source_permission
                JOIN public."Module" source_module
                    ON source_module."ID" = source_permission."ModuleID"
                   AND source_module."ModuleName" = 'Supplier History'
                CROSS JOIN public."Module" negotiation_module
                WHERE negotiation_module."ModuleName" = 'Supplier Negotiation'
                  AND NOT EXISTS (
                      SELECT 1 FROM public."RolePermissions" existing
                      WHERE existing."BusinessUnitID" = source_permission."BusinessUnitID"
                        AND existing."RoleID" IS NOT DISTINCT FROM source_permission."RoleID"
                        AND existing."ModuleID" = negotiation_module."ID");

                CREATE OR REPLACE FUNCTION nexora_reject_supplier_negotiation_decision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                        MESSAGE = 'supplier negotiation decisions are append-only';
                END
                $function$;

                CREATE TRIGGER supplier_negotiation_decisions_append_only
                    BEFORE UPDATE OR DELETE ON public.supplier_negotiation_decisions
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_supplier_negotiation_decision_mutation();
                CREATE TRIGGER supplier_negotiation_decisions_reject_truncate
                    BEFORE TRUNCATE ON public.supplier_negotiation_decisions
                    FOR EACH STATEMENT EXECUTE FUNCTION nexora_reject_supplier_negotiation_decision_mutation();

                ALTER TABLE public.supplier_negotiation_decisions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.supplier_negotiation_decisions FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public.supplier_negotiation_decisions
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(
                        current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(
                        current_setting('nexora.business_unit_id', true), '')::bigint);

                REVOKE ALL PRIVILEGES ON TABLE public.supplier_negotiation_decisions
                    FROM nexora_tenant_app;
                GRANT SELECT, INSERT ON TABLE public.supplier_negotiation_decisions
                    TO nexora_tenant_app;

                DO $block$
                DECLARE decision_sequence text;
                BEGIN
                    decision_sequence := pg_get_serial_sequence(
                        'public.supplier_negotiation_decisions', 'Id');
                    IF decision_sequence IS NOT NULL THEN
                        EXECUTE format('REVOKE ALL PRIVILEGES ON SEQUENCE %s FROM nexora_tenant_app',
                            decision_sequence);
                        EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app',
                            decision_sequence);
                    END IF;
                END
                $block$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_negotiation_decisions_reject_truncate
                    ON public.supplier_negotiation_decisions;
                DROP TRIGGER IF EXISTS supplier_negotiation_decisions_append_only
                    ON public.supplier_negotiation_decisions;
                DROP FUNCTION IF EXISTS nexora_reject_supplier_negotiation_decision_mutation();

                DO $block$
                DECLARE
                    negotiation_module_id bigint;
                    migration_owned boolean;
                BEGIN
                    SELECT "ID", "CreatedBy" = 'V2Gate04SupplierNegotiationIntelligence'
                    INTO negotiation_module_id, migration_owned
                    FROM public."Module"
                    WHERE "ModuleName" = 'Supplier Negotiation';

                    IF negotiation_module_id IS NOT NULL AND migration_owned THEN
                        DELETE FROM public."RolePermissions"
                        WHERE "ModuleID" = negotiation_module_id;
                        DELETE FROM public."Module"
                        WHERE "ID" = negotiation_module_id;
                    ELSIF negotiation_module_id IS NOT NULL THEN
                        DELETE FROM public."RolePermissions"
                        WHERE "ModuleID" = negotiation_module_id
                          AND "CreatedBy" = 'V2Gate04SupplierNegotiationIntelligence';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.DropTable(
                name: "supplier_negotiation_decisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Id",
                table: "supplier_quote_revisions");
        }
    }
}

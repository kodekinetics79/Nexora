using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release02SupplierQuoteInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CommercialDemandLineId",
                table: "SupplierSolicitations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NexoraSerial",
                table: "SupplierSolicitations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourcingCaseId",
                table: "SupplierSolicitations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierRfqNumber",
                table: "SupplierSolicitations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supplier_quotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierSolicitationId = table.Column<long>(type: "bigint", nullable: false),
                    SourcingCaseId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SupplierQuoteReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CurrentRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    InboxStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_quotes", x => x.Id);
                    table.UniqueConstraint("AK_supplier_quotes_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_quotes_CurrentRevision", "\"CurrentRevisionNumber\" > 0");
                    table.CheckConstraint("CK_supplier_quotes_InboxStatus", "\"InboxStatus\" IN ('REVIEW_REQUIRED','READY_FOR_COMPARISON')");
                    table.ForeignKey(
                        name: "FK_supplier_quotes_RFQ_BusinessUnitId_RfqId",
                        columns: x => new { x.BusinessUnitId, x.RfqId },
                        principalTable: "RFQ",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quotes_SupplierSolicitations_BusinessUnitId_Suppli~",
                        columns: x => new { x.BusinessUnitId, x.SupplierSolicitationId },
                        principalTable: "SupplierSolicitations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quotes_Suppliers_SupplierId_BusinessUnitId",
                        columns: x => new { x.SupplierId, x.BusinessUnitId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "ID", "BUID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quotes_sourcing_cases_BusinessUnitId_SourcingCaseId",
                        columns: x => new { x.BusinessUnitId, x.SourcingCaseId },
                        principalTable: "sourcing_cases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_quote_revisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    CaptureChannel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceIdentity = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Incoterms = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FreightAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    PaymentTerms = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RequiresReview = table.Column<bool>(type: "boolean", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapturedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CapturedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_quote_revisions", x => x.Id);
                    table.UniqueConstraint("AK_supplier_quote_revisions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_quote_revisions_Values", "\"RevisionNumber\" > 0 AND \"FreightAmount\" >= 0 AND \"TaxAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_supplier_quote_revisions_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quote_revisions_source_documents_BusinessUnitId_So~",
                        columns: x => new { x.BusinessUnitId, x.SourceDocumentId },
                        principalTable: "source_documents",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quote_revisions_supplier_quotes_BusinessUnitId_Sup~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteId },
                        principalTable: "supplier_quotes",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_quote_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialDemandLineId = table.Column<long>(type: "bigint", nullable: false),
                    PartNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SupplierPartNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AvailableQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitOfMeasure = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    MinimumOrderQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: true),
                    AvailabilityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    OriginCountry = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Warranty = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsAlternate = table.Column<bool>(type: "boolean", nullable: false),
                    Exceptions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_quote_lines", x => x.Id);
                    table.UniqueConstraint("AK_supplier_quote_lines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_quote_lines_Values", "\"LineNumber\" > 0 AND \"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND (\"AvailableQuantity\" IS NULL OR \"AvailableQuantity\" >= 0) AND (\"MinimumOrderQuantity\" IS NULL OR \"MinimumOrderQuantity\" > 0) AND (\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0)");
                    table.ForeignKey(
                        name: "FK_supplier_quote_lines_commercial_demand_lines_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.CommercialDemandLineId },
                        principalTable: "commercial_demand_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quote_lines_supplier_quote_revisions_BusinessUnitI~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId },
                        principalTable: "supplier_quote_revisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_quote_field_evidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteLineId = table.Column<long>(type: "bigint", nullable: true),
                    FieldName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginalValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    NormalizedValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    Method = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ModelOrRuleVersion = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SourcePage = table.Column<int>(type: "integer", nullable: true),
                    SourceRegion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Critical = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_quote_field_evidence", x => x.Id);
                    table.UniqueConstraint("AK_supplier_quote_field_evidence_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_quote_field_evidence_Confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
                    table.ForeignKey(
                        name: "FK_supplier_quote_field_evidence_supplier_quote_lines_Business~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteLineId },
                        principalTable: "supplier_quote_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quote_field_evidence_supplier_quote_revisions_Busi~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId },
                        principalTable: "supplier_quote_revisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_quote_review_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuoteFieldEvidenceId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CorrectedValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReviewedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_quote_review_decisions", x => x.Id);
                    table.CheckConstraint("CK_supplier_quote_review_decisions_Status", "\"Status\" IN ('ACCEPTED','CORRECTED','REJECTED')");
                    table.ForeignKey(
                        name: "FK_supplier_quote_review_decisions_supplier_quote_field_eviden~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteFieldEvidenceId },
                        principalTable: "supplier_quote_field_evidence",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_quote_review_decisions_supplier_quote_revisions_Bu~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId },
                        principalTable: "supplier_quote_revisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_CommercialDemandLineId",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_SourcingCaseId",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "SourcingCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_SupplierRfqNumber",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "SupplierRfqNumber" },
                unique: true,
                filter: "\"SupplierRfqNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_field_evidence_BusinessUnitId_SupplierQuoteL~",
                table: "supplier_quote_field_evidence",
                columns: new[] { "BusinessUnitId", "SupplierQuoteLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_field_evidence_BusinessUnitId_SupplierQuoteR~",
                table: "supplier_quote_field_evidence",
                columns: new[] { "BusinessUnitId", "SupplierQuoteRevisionId", "ReviewRequired" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_lines_BusinessUnitId_CommercialDemandLineId",
                table: "supplier_quote_lines",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_lines_BusinessUnitId_SupplierQuoteRevisionId~",
                table: "supplier_quote_lines",
                columns: new[] { "BusinessUnitId", "SupplierQuoteRevisionId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_review_decisions_BusinessUnitId_SupplierQuo~1",
                table: "supplier_quote_review_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteFieldEvidenceId", "ReviewedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_review_decisions_BusinessUnitId_SupplierQuot~",
                table: "supplier_quote_review_decisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_revisions_BusinessUnitId_CurrencyId",
                table: "supplier_quote_revisions",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_revisions_BusinessUnitId_IdempotencyKey",
                table: "supplier_quote_revisions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_revisions_BusinessUnitId_SourceDocumentId",
                table: "supplier_quote_revisions",
                columns: new[] { "BusinessUnitId", "SourceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quote_revisions_BusinessUnitId_SupplierQuoteId_Rev~",
                table: "supplier_quote_revisions",
                columns: new[] { "BusinessUnitId", "SupplierQuoteId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_BusinessUnitId_InboxStatus_UpdatedOn",
                table: "supplier_quotes",
                columns: new[] { "BusinessUnitId", "InboxStatus", "UpdatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_BusinessUnitId_NexoraSerial",
                table: "supplier_quotes",
                columns: new[] { "BusinessUnitId", "NexoraSerial" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_BusinessUnitId_RfqId",
                table: "supplier_quotes",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_BusinessUnitId_SourcingCaseId",
                table: "supplier_quotes",
                columns: new[] { "BusinessUnitId", "SourcingCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_BusinessUnitId_SupplierId_SupplierQuoteRefe~",
                table: "supplier_quotes",
                columns: new[] { "BusinessUnitId", "SupplierId", "SupplierQuoteReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_BusinessUnitId_SupplierSolicitationId",
                table: "supplier_quotes",
                columns: new[] { "BusinessUnitId", "SupplierSolicitationId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_quotes_SupplierId_BusinessUnitId",
                table: "supplier_quotes",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierSolicitations_commercial_demand_lines_BusinessUnitI~",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" },
                principalTable: "commercial_demand_lines",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierSolicitations_sourcing_cases_BusinessUnitId_Sourcin~",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "SourcingCaseId" },
                principalTable: "sourcing_cases",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_reject_supplier_quote_append_only_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                        MESSAGE = TG_TABLE_NAME || ' is append-only';
                END
                $function$;

                CREATE TRIGGER supplier_quote_revisions_append_only
                    BEFORE UPDATE OR DELETE ON supplier_quote_revisions
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_supplier_quote_append_only_mutation();
                CREATE TRIGGER supplier_quote_lines_append_only
                    BEFORE UPDATE OR DELETE ON supplier_quote_lines
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_supplier_quote_append_only_mutation();
                CREATE TRIGGER supplier_quote_field_evidence_append_only
                    BEFORE UPDATE OR DELETE ON supplier_quote_field_evidence
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_supplier_quote_append_only_mutation();
                CREATE TRIGGER supplier_quote_review_decisions_append_only
                    BEFORE UPDATE OR DELETE ON supplier_quote_review_decisions
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_supplier_quote_append_only_mutation();

                CREATE OR REPLACE FUNCTION nexora_protect_supplier_quote_lineage()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
                       OR NEW."SupplierSolicitationId" IS DISTINCT FROM OLD."SupplierSolicitationId"
                       OR NEW."SourcingCaseId" IS DISTINCT FROM OLD."SourcingCaseId"
                       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
                       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
                       OR NEW."SupplierQuoteReference" IS DISTINCT FROM OLD."SupplierQuoteReference" THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'Supplier Quote tenant and commercial lineage are immutable';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER supplier_quotes_lineage_immutable
                    BEFORE UPDATE ON supplier_quotes
                    FOR EACH ROW EXECUTE FUNCTION nexora_protect_supplier_quote_lineage();

                CREATE OR REPLACE FUNCTION nexora_protect_supplier_rfq_lineage()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF (OLD."SourcingCaseId" IS NOT NULL AND NEW."SourcingCaseId" IS DISTINCT FROM OLD."SourcingCaseId")
                       OR (OLD."CommercialDemandLineId" IS NOT NULL AND NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId")
                       OR (OLD."NexoraSerial" IS NOT NULL AND NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial")
                       OR (OLD."SupplierRfqNumber" IS NOT NULL AND NEW."SupplierRfqNumber" IS DISTINCT FROM OLD."SupplierRfqNumber") THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'Supplier RFQ commercial lineage is write-once';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER supplier_solicitations_commercial_lineage_write_once
                    BEFORE UPDATE ON "SupplierSolicitations"
                    FOR EACH ROW EXECUTE FUNCTION nexora_protect_supplier_rfq_lineage();

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'supplier_quotes', 'supplier_quote_revisions', 'supplier_quote_lines',
                        'supplier_quote_field_evidence', 'supplier_quote_review_decisions'
                    ]
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', table_name);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
                            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            table_name);
                    END LOOP;
                END
                $block$;

                GRANT SELECT, INSERT, UPDATE ON TABLE public.supplier_quotes TO nexora_tenant_app;
                REVOKE DELETE ON TABLE public.supplier_quotes FROM nexora_tenant_app;
                GRANT SELECT, INSERT ON TABLE public.supplier_quote_revisions, public.supplier_quote_lines,
                    public.supplier_quote_field_evidence, public.supplier_quote_review_decisions TO nexora_tenant_app;
                REVOKE UPDATE, DELETE ON TABLE public.supplier_quote_revisions, public.supplier_quote_lines,
                    public.supplier_quote_field_evidence, public.supplier_quote_review_decisions FROM nexora_tenant_app;

                DO $block$
                DECLARE table_name text; sequence_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'supplier_quotes', 'supplier_quote_revisions', 'supplier_quote_lines',
                        'supplier_quote_field_evidence', 'supplier_quote_review_decisions'
                    ]
                    LOOP
                        sequence_name := pg_get_serial_sequence(format('public.%I', table_name), 'Id');
                        IF sequence_name IS NOT NULL THEN
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                        END IF;
                    END LOOP;
                END
                $block$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_solicitations_commercial_lineage_write_once ON "SupplierSolicitations";
                DROP TRIGGER IF EXISTS supplier_quotes_lineage_immutable ON supplier_quotes;
                DROP TRIGGER IF EXISTS supplier_quote_review_decisions_append_only ON supplier_quote_review_decisions;
                DROP TRIGGER IF EXISTS supplier_quote_field_evidence_append_only ON supplier_quote_field_evidence;
                DROP TRIGGER IF EXISTS supplier_quote_lines_append_only ON supplier_quote_lines;
                DROP TRIGGER IF EXISTS supplier_quote_revisions_append_only ON supplier_quote_revisions;
                DROP FUNCTION IF EXISTS nexora_protect_supplier_rfq_lineage();
                DROP FUNCTION IF EXISTS nexora_protect_supplier_quote_lineage();
                DROP FUNCTION IF EXISTS nexora_reject_supplier_quote_append_only_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierSolicitations_commercial_demand_lines_BusinessUnitI~",
                table: "SupplierSolicitations");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierSolicitations_sourcing_cases_BusinessUnitId_Sourcin~",
                table: "SupplierSolicitations");

            migrationBuilder.DropTable(
                name: "supplier_quote_review_decisions");

            migrationBuilder.DropTable(
                name: "supplier_quote_field_evidence");

            migrationBuilder.DropTable(
                name: "supplier_quote_lines");

            migrationBuilder.DropTable(
                name: "supplier_quote_revisions");

            migrationBuilder.DropTable(
                name: "supplier_quotes");

            migrationBuilder.DropIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_CommercialDemandLineId",
                table: "SupplierSolicitations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_SourcingCaseId",
                table: "SupplierSolicitations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_SupplierRfqNumber",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "CommercialDemandLineId",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "NexoraSerial",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "SourcingCaseId",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "SupplierRfqNumber",
                table: "SupplierSolicitations");
        }
    }
}

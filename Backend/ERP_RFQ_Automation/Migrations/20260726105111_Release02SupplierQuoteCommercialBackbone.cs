using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release02SupplierQuoteCommercialBackbone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BUID",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "UQ__Supplier__FFA796CDFB352BC7",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_BusinessUnitID_CustomerID",
                table: "Contacts");

            migrationBuilder.AddColumn<string>(
                name: "ComplianceStatus",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UNKNOWN");

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Suppliers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveFrom",
                table: "Suppliers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GovernanceReviewedBy",
                table: "Suppliers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GovernanceReviewedOn",
                table: "Suppliers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GovernanceStatus",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UNVERIFIED");

            migrationBuilder.AddColumn<string>(
                name: "ReadinessStatus",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "REVIEW_REQUIRED");

            migrationBuilder.AddColumn<string>(
                name: "RiskStatus",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UNKNOWN");

            migrationBuilder.AddColumn<string>(
                name: "VerificationStatus",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UNKNOWN");

            migrationBuilder.CreateTable(
                name: "commercial_demand_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: false),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_demand_lines", x => x.Id);
                    table.UniqueConstraint("AK_commercial_demand_lines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_commercial_demand_lines_RFQItems_RfqItemId_RfqId",
                        columns: x => new { x.RfqItemId, x.RfqId },
                        principalTable: "RFQItems",
                        principalColumns: new[] { "ID", "RFQID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_demand_lines_RFQ_BusinessUnitId_RfqId",
                        columns: x => new { x.BusinessUnitId, x.RfqId },
                        principalTable: "RFQ",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_document_classifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    source_document_id = table.Column<long>(type: "bigint", nullable: false),
                    source_document_content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    source_object_version = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    review_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    classification_method = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    customer_rfq_id = table.Column<long>(type: "bigint", nullable: true),
                    supplier_rfq_id = table.Column<long>(type: "bigint", nullable: true),
                    sourcing_case_id = table.Column<long>(type: "bigint", nullable: true),
                    supplier_quote_id = table.Column<long>(type: "bigint", nullable: true),
                    purchase_order_id = table.Column<long>(type: "bigint", nullable: true),
                    supplier_invoice_id = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    reviewed_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    review_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reviewed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_document_classifications", x => x.id);
                    table.UniqueConstraint("ak_commercial_document_classifications_tenant_id", x => new { x.business_unit_id, x.id });
                    table.CheckConstraint("ck_commercial_document_classifications_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_commercial_document_classifications_confidence", "confidence >= 0 AND confidence <= 1");
                    table.CheckConstraint("ck_commercial_document_classifications_unknown_review", "document_type <> 'Unknown' OR review_status IN ('ReviewRequired', 'Rejected')");
                    table.CheckConstraint("ck_commercial_document_classifications_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_commercial_document_classifications_source_documents_busine~",
                        columns: x => new { x.business_unit_id, x.source_document_id },
                        principalTable: "source_documents",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sourcing_cases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialDemandLineId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: true),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    ProductId = table.Column<long>(type: "bigint", nullable: true),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestedPartNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RequestedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StockQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnfulfilledQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RequiredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeliveryLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SearchLimit = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NextAction = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ShortageDecisionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sourcing_cases", x => x.Id);
                    table.UniqueConstraint("AK_sourcing_cases_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_sourcing_cases_Quantities", "\"RequestedQuantity\" > 0 AND \"StockQuantity\" >= 0 AND \"UnfulfilledQuantity\" > 0 AND \"UnfulfilledQuantity\" <= \"RequestedQuantity\"");
                    table.CheckConstraint("CK_sourcing_cases_SearchLimit", "\"SearchLimit\" IN (10,20,50)");
                    table.CheckConstraint("CK_sourcing_cases_Status", "\"Status\" IN ('DRAFT','INTERNAL_SEARCH','DISCOVERY_REQUIRED','SUPPLIERS_SELECTED','OUTREACH_READY','OUTREACH_SENT','RESPONSES_PARTIAL','RESPONSES_COMPLETE','COMPARISON_READY','NEGOTIATION','AWARD_REVIEW','SUPPLIER_SELECTED','CUSTOMER_QUOTE_READY','CLOSED','CANCELLED')");
                    table.ForeignKey(
                        name: "FK_sourcing_cases_RFQItems_RfqItemId_RfqId",
                        columns: x => new { x.RfqItemId, x.RfqId },
                        principalTable: "RFQItems",
                        principalColumns: new[] { "ID", "RFQID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sourcing_cases_RFQ_BusinessUnitId_RfqId",
                        columns: x => new { x.BusinessUnitId, x.RfqId },
                        principalTable: "RFQ",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sourcing_cases_commercial_demand_lines_BusinessUnitId_Comme~",
                        columns: x => new { x.BusinessUnitId, x.CommercialDemandLineId },
                        principalTable: "commercial_demand_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sourcing_case_candidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SourcingCaseId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    EvidenceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RecommendationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceScore = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    EvidenceFreshOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Selected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sourcing_case_candidates", x => x.Id);
                    table.CheckConstraint("CK_sourcing_case_candidates_RankScore", "\"Rank\" > 0 AND \"EvidenceScore\" >= 0 AND \"EvidenceScore\" <= 100");
                    table.ForeignKey(
                        name: "FK_sourcing_case_candidates_Suppliers_SupplierId_BusinessUnitId",
                        columns: x => new { x.SupplierId, x.BusinessUnitId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "ID", "BUID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sourcing_case_candidates_sourcing_cases_BusinessUnitId_Sour~",
                        columns: x => new { x.BusinessUnitId, x.SourcingCaseId },
                        principalTable: "sourcing_cases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS pgcrypto;

                UPDATE "Suppliers"
                SET "ConcurrencyToken" = COALESCE("ConcurrencyToken", gen_random_uuid()),
                    "EffectiveFrom" = COALESCE("EffectiveFrom", "CreatedOn", now()::timestamp);

                WITH ranked_customer AS (
                    SELECT "ID", row_number() OVER (
                        PARTITION BY "BusinessUnitID", "CustomerID"
                        ORDER BY "CreatedOn", "ID") AS position
                    FROM "Contacts"
                    WHERE "IsPrimary" = TRUE AND "CustomerID" IS NOT NULL
                )
                UPDATE "Contacts" contact
                SET "IsPrimary" = FALSE
                FROM ranked_customer ranked
                WHERE contact."ID" = ranked."ID" AND ranked.position > 1;

                WITH ranked_supplier AS (
                    SELECT "ID", row_number() OVER (
                        PARTITION BY "BusinessUnitID", "SupplierID"
                        ORDER BY "CreatedOn", "ID") AS position
                    FROM "Contacts"
                    WHERE "IsPrimary" = TRUE AND "SupplierID" IS NOT NULL
                )
                UPDATE "Contacts" contact
                SET "IsPrimary" = FALSE
                FROM ranked_supplier ranked
                WHERE contact."ID" = ranked."ID" AND ranked.position > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BU_Governance_Readiness",
                table: "Suppliers",
                columns: new[] { "BUID", "GovernanceStatus", "ReadinessStatus" });

            migrationBuilder.CreateIndex(
                name: "UQ__Supplier__FFA796CDFB352BC7",
                table: "Suppliers",
                column: "ContactEmail");

            migrationBuilder.CreateIndex(
                name: "UX_Suppliers_BU_ContactEmail",
                table: "Suppliers",
                columns: new[] { "BUID", "ContactEmail" },
                unique: true,
                filter: "\"ContactEmail\" IS NOT NULL AND \"BUID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Suppliers_BU_DocId",
                table: "Suppliers",
                columns: new[] { "BUID", "DocId" },
                unique: true,
                filter: "\"DocId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_ComplianceStatus",
                table: "Suppliers",
                sql: "\"ComplianceStatus\" IN ('UNKNOWN','PENDING','CLEARED','RESTRICTED','BLOCKED','FAILED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_GovernanceStatus",
                table: "Suppliers",
                sql: "\"GovernanceStatus\" IN ('DISCOVERED','UNVERIFIED','REVIEW_REQUIRED','PROVISIONAL','APPROVED','PREFERRED','RESTRICTED','BLOCKED','INACTIVE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_ReadinessStatus",
                table: "Suppliers",
                sql: "\"ReadinessStatus\" IN ('REVIEW_REQUIRED','READY','RESTRICTED','BLOCKED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_RiskStatus",
                table: "Suppliers",
                sql: "\"RiskStatus\" IN ('UNKNOWN','LOW','MEDIUM','HIGH','BLOCKED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_VerificationStatus",
                table: "Suppliers",
                sql: "\"VerificationStatus\" IN ('UNKNOWN','PENDING','VERIFIED','FAILED','EXPIRED')");

            migrationBuilder.CreateIndex(
                name: "UX_Contacts_BU_Customer_Primary",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "CustomerID" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"CustomerID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Contacts_BU_Supplier_Primary",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "SupplierID" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"SupplierID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_commercial_demand_lines_BusinessUnitId_IdentityKey",
                table: "commercial_demand_lines",
                columns: new[] { "BusinessUnitId", "IdentityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_demand_lines_BusinessUnitId_RfqId",
                table: "commercial_demand_lines",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_demand_lines_BusinessUnitId_RfqItemId",
                table: "commercial_demand_lines",
                columns: new[] { "BusinessUnitId", "RfqItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_demand_lines_RfqItemId_RfqId",
                table: "commercial_demand_lines",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "ix_commercial_document_classifications_review_queue",
                table: "commercial_document_classifications",
                columns: new[] { "business_unit_id", "review_status", "created_on" });

            migrationBuilder.CreateIndex(
                name: "ux_commercial_document_classifications_tenant_document",
                table: "commercial_document_classifications",
                columns: new[] { "business_unit_id", "source_document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_commercial_document_classifications_tenant_idempotency",
                table: "commercial_document_classifications",
                columns: new[] { "business_unit_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_case_candidates_BusinessUnitId_SourcingCaseId_Rank",
                table: "sourcing_case_candidates",
                columns: new[] { "BusinessUnitId", "SourcingCaseId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_case_candidates_BusinessUnitId_SourcingCaseId_Supp~",
                table: "sourcing_case_candidates",
                columns: new[] { "BusinessUnitId", "SourcingCaseId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_case_candidates_SupplierId_BusinessUnitId",
                table: "sourcing_case_candidates",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_cases_BusinessUnitId_CommercialDemandLineId_Shorta~",
                table: "sourcing_cases",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId", "ShortageDecisionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_cases_BusinessUnitId_IdempotencyKey",
                table: "sourcing_cases",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_cases_BusinessUnitId_RfqId",
                table: "sourcing_cases",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_sourcing_cases_RfqItemId_RfqId",
                table: "sourcing_cases",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.Sql("""
                INSERT INTO commercial_demand_lines
                    ("BusinessUnitId", "RfqId", "RfqItemId", "NexoraSerial", "IdentityKey", "CreatedOn", "CreatedBy")
                SELECT rfq."BusinessUnitID", rfq."ID", item."ID", rfq."NexoraSerial",
                       'rfq:' || rfq."ID"::text || ':line:' || item."ID"::text,
                       COALESCE(item."CreatedDate", rfq."CreatedDate", now()::timestamp),
                       COALESCE(NULLIF(item."CreatedBy", ''), NULLIF(rfq."CreatedBy", ''), 'release-02-backfill')
                FROM "RFQItems" item
                JOIN "RFQ" rfq ON rfq."ID" = item."RFQID"
                WHERE rfq."BusinessUnitID" > 0
                  AND NULLIF(btrim(rfq."NexoraSerial"), '') IS NOT NULL
                ON CONFLICT DO NOTHING;

                CREATE OR REPLACE FUNCTION nexora_reject_commercial_demand_line_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                        MESSAGE = 'commercial Demand Line identity is immutable';
                END
                $function$;
                CREATE TRIGGER commercial_demand_lines_immutable
                    BEFORE UPDATE OR DELETE ON commercial_demand_lines
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_commercial_demand_line_mutation();

                CREATE OR REPLACE FUNCTION nexora_reject_sourcing_case_lineage_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
                       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
                       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
                       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial" THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'Sourcing Case tenant and commercial lineage are immutable';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER sourcing_cases_lineage_immutable
                    BEFORE UPDATE ON sourcing_cases
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_sourcing_case_lineage_mutation();

                CREATE OR REPLACE FUNCTION nexora_reject_classification_source_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW.business_unit_id IS DISTINCT FROM OLD.business_unit_id
                       OR NEW.source_document_id IS DISTINCT FROM OLD.source_document_id
                       OR NEW.source_document_content_hash IS DISTINCT FROM OLD.source_document_content_hash
                       OR NEW.source_object_version IS DISTINCT FROM OLD.source_object_version THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'commercial document source identity is immutable';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER commercial_document_classifications_source_immutable
                    BEFORE UPDATE ON commercial_document_classifications
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_classification_source_mutation();

                ALTER TABLE public."Suppliers" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."Suppliers";
                CREATE POLICY nexora_tenant_isolation ON public."Suppliers" TO nexora_tenant_app
                    USING ("BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                GRANT SELECT, INSERT, UPDATE ON TABLE public."Suppliers" TO nexora_tenant_app;
                REVOKE DELETE ON TABLE public."Suppliers" FROM nexora_tenant_app;

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'commercial_demand_lines', 'sourcing_cases', 'sourcing_case_candidates'
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

                ALTER TABLE public.commercial_document_classifications ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public.commercial_document_classifications;
                CREATE POLICY nexora_tenant_isolation ON public.commercial_document_classifications TO nexora_tenant_app
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                GRANT SELECT, INSERT ON TABLE public.commercial_demand_lines TO nexora_tenant_app;
                REVOKE UPDATE, DELETE ON TABLE public.commercial_demand_lines FROM nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE ON TABLE public.sourcing_cases TO nexora_tenant_app;
                REVOKE DELETE ON TABLE public.sourcing_cases FROM nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.sourcing_case_candidates TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE ON TABLE public.commercial_document_classifications TO nexora_tenant_app;
                REVOKE DELETE ON TABLE public.commercial_document_classifications FROM nexora_tenant_app;

                DO $block$
                DECLARE table_name text; sequence_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'commercial_demand_lines', 'sourcing_cases', 'sourcing_case_candidates'
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
                DROP TRIGGER IF EXISTS commercial_document_classifications_source_immutable
                    ON commercial_document_classifications;
                DROP FUNCTION IF EXISTS nexora_reject_classification_source_mutation();
                DROP TRIGGER IF EXISTS sourcing_cases_lineage_immutable ON sourcing_cases;
                DROP FUNCTION IF EXISTS nexora_reject_sourcing_case_lineage_mutation();
                DROP TRIGGER IF EXISTS commercial_demand_lines_immutable ON commercial_demand_lines;
                DROP FUNCTION IF EXISTS nexora_reject_commercial_demand_line_mutation();
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."Suppliers";
                ALTER TABLE public."Suppliers" DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(
                name: "commercial_document_classifications");

            migrationBuilder.DropTable(
                name: "sourcing_case_candidates");

            migrationBuilder.DropTable(
                name: "sourcing_cases");

            migrationBuilder.DropTable(
                name: "commercial_demand_lines");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BU_Governance_Readiness",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "UQ__Supplier__FFA796CDFB352BC7",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "UX_Suppliers_BU_ContactEmail",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "UX_Suppliers_BU_DocId",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_ComplianceStatus",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_GovernanceStatus",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_ReadinessStatus",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_RiskStatus",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_VerificationStatus",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "UX_Contacts_BU_Customer_Primary",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "UX_Contacts_BU_Supplier_Primary",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "ComplianceStatus",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "GovernanceReviewedBy",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "GovernanceReviewedOn",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "GovernanceStatus",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ReadinessStatus",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "RiskStatus",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                table: "Suppliers");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BUID",
                table: "Suppliers",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "UQ__Supplier__FFA796CDFB352BC7",
                table: "Suppliers",
                column: "ContactEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_BusinessUnitID_CustomerID",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "CustomerID" });
        }
    }
}

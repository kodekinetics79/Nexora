using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01BIntakeIdentityAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK__Contacts__Custom__17F790F9",
                table: "Contacts");

            migrationBuilder.DropForeignKey(
                name: "FK__Customers__BUID__0D7A0286",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "UX_ExtractionJobs_BU_ContentHash",
                table: "ExtractionJobs");

            migrationBuilder.DropIndex(
                name: "IX_Customers_BUID",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "UQ__Customer__FFA796CD4707A72F",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_Email",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "UQ__Contacts__A9D10534C4FF61F8",
                table: "Contacts");

            migrationBuilder.Sql("""
                DO $preflight$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Customers" WHERE "BUID" IS NULL) THEN
                        RAISE EXCEPTION 'Release 01B cannot infer tenant ownership for Customers with null BUID' USING ERRCODE = '23514';
                    END IF;
                    IF EXISTS (
                        SELECT 1
                        FROM "Contacts" contact
                        LEFT JOIN "Customers" customer ON customer."ID" = contact."CustomerID"
                        LEFT JOIN "Suppliers" supplier ON supplier."ID" = contact."SupplierID"
                        WHERE (contact."CustomerID" IS NULL AND contact."SupplierID" IS NULL)
                           OR (contact."CustomerID" IS NOT NULL AND customer."ID" IS NULL)
                           OR (contact."SupplierID" IS NOT NULL AND supplier."ID" IS NULL)
                           OR (contact."CustomerID" IS NOT NULL AND customer."BUID" IS NULL)
                           OR (contact."SupplierID" IS NOT NULL AND supplier."BUID" IS NULL)
                           OR (customer."BUID" IS NOT NULL AND supplier."BUID" IS NOT NULL AND customer."BUID" <> supplier."BUID")
                    ) THEN
                        RAISE EXCEPTION 'Release 01B cannot infer one valid tenant for every Contact' USING ERRCODE = '23514';
                    END IF;
                END $preflight$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "intake_status",
                table: "source_document_occurrences",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Accepted");

            migrationBuilder.AddColumn<string>(
                name: "last_error_code",
                table: "source_document_occurrences",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logical_group_key",
                table: "source_document_occurrences",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_on",
                table: "source_document_occurrences",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "LogicalGroupKey",
                table: "LeadIngestionOccurrences",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceDocumentOccurrenceId",
                table: "LeadIngestionOccurrences",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceDocumentOccurrenceId",
                table: "ExtractionJobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "BUID",
                table: "Customers",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BusinessUnitID",
                table: "Contacts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCurrency",
                table: "AiRequests",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostStatus",
                table: "AiRequests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotPriced");

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedCost",
                table: "AiRequests",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExtractionJobId",
                table: "AiRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceDocumentOccurrenceId",
                table: "AiRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_evidence_occurrence_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'source occurrence is immutable evidence' USING ERRCODE = '55000';
                    END IF;
                    IF (NEW.business_unit_id, NEW.id, NEW.source_document_id, NEW.corpus_id,
                        NEW.idempotency_key, NEW.source_metadata, NEW.received_on)
                       IS DISTINCT FROM
                       (OLD.business_unit_id, OLD.id, OLD.source_document_id, OLD.corpus_id,
                        OLD.idempotency_key, OLD.source_metadata, OLD.received_on)
                       OR (OLD.extraction_job_id IS NOT NULL AND NEW.extraction_job_id IS DISTINCT FROM OLD.extraction_job_id)
                       OR (OLD.logical_group_key IS NOT NULL AND NEW.logical_group_key IS DISTINCT FROM OLD.logical_group_key) THEN
                        RAISE EXCEPTION 'source occurrence provenance is immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;

                UPDATE "Contacts" contact
                SET "BusinessUnitID" = COALESCE(
                    (SELECT customer."BUID" FROM "Customers" customer WHERE customer."ID" = contact."CustomerID"),
                    (SELECT supplier."BUID" FROM "Suppliers" supplier WHERE supplier."ID" = contact."SupplierID"));

                UPDATE source_document_occurrences occurrence
                SET intake_status = CASE job."Status"
                    WHEN 'Succeeded' THEN 'Resolved'
                    WHEN 'DeadLetter' THEN 'DeadLetter'
                    WHEN 'Failed' THEN 'Retryable'
                    WHEN 'Leased' THEN 'Retryable'
                    WHEN 'Extracting' THEN 'Retryable'
                    WHEN 'Persisting' THEN 'Retryable'
                    ELSE 'Queued'
                END,
                updated_on = GREATEST(occurrence.received_on, COALESCE(job."UpdatedOn", occurrence.received_on))
                FROM "ExtractionJobs" job
                WHERE job."Id" = occurrence.extraction_job_id;

                UPDATE "ExtractionJobs" job
                SET "SourceDocumentOccurrenceId" = selected.id
                FROM (
                    SELECT DISTINCT ON (business_unit_id, extraction_job_id)
                        business_unit_id, extraction_job_id, id
                    FROM source_document_occurrences
                    WHERE extraction_job_id IS NOT NULL
                    ORDER BY business_unit_id, extraction_job_id, received_on, id
                ) selected
                WHERE selected.business_unit_id = job."BusinessUnitId"
                  AND selected.extraction_job_id = job."Id";

                UPDATE "LeadIngestionOccurrences" lead_occurrence
                SET "SourceDocumentOccurrenceId" = selected.id,
                    "LogicalGroupKey" = selected.logical_group_key
                FROM (
                    SELECT DISTINCT ON (business_unit_id, extraction_job_id)
                        business_unit_id, extraction_job_id, id, logical_group_key
                    FROM source_document_occurrences
                    WHERE extraction_job_id IS NOT NULL
                    ORDER BY business_unit_id, extraction_job_id, received_on, id
                ) selected
                WHERE selected.business_unit_id = lead_occurrence."BusinessUnitId"
                  AND selected.extraction_job_id = lead_occurrence."ExtractionJobId";

                UPDATE "AiRequests" request
                SET "ExtractionJobId" = substring(request."IdempotencyKey" FROM 'extraction:([0-9]+)')::bigint
                WHERE request."IdempotencyKey" ~ '^extraction:[0-9]+';

                UPDATE "AiRequests" request
                SET "SourceDocumentOccurrenceId" = job."SourceDocumentOccurrenceId"
                FROM "ExtractionJobs" job
                WHERE job."BusinessUnitId" = request."BusinessUnitId"
                  AND job."Id" = request."ExtractionJobId";
                """);

            migrationBuilder.AlterColumn<long>(
                name: "BusinessUnitID",
                table: "Contacts",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Customers_BUID_ID",
                table: "Customers",
                columns: new[] { "BUID", "ID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Contacts_BusinessUnitID_ID",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.CreateIndex(
                name: "ix_source_document_occurrences_tenant_group",
                table: "source_document_occurrences",
                columns: new[] { "business_unit_id", "logical_group_key", "received_on" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentOccur~",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "SourceDocumentOccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_BU_ContentHash",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "UX_ExtractionJobs_BU_SourceOccurrence",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "SourceDocumentOccurrenceId" },
                unique: true,
                filter: "\"SourceDocumentOccurrenceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_Customers_BUID_ContactEmail",
                table: "Customers",
                columns: new[] { "BUID", "ContactEmail" },
                unique: true,
                filter: "\"ContactEmail\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_BusinessUnitID_CustomerID",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "CustomerID" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_BusinessUnitID_Email",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "Email" });

            migrationBuilder.CreateIndex(
                name: "UQ_Contacts_BusinessUnitID_Email",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "Email" },
                unique: true,
                filter: "\"Email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_BusinessUnitId_ExtractionJobId",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "ExtractionJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_BusinessUnitId_SourceDocumentOccurrenceId",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "SourceDocumentOccurrenceId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AiRequests_ExtractionJobs_BusinessUnitId_ExtractionJobId",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "ExtractionJobId" },
                principalTable: "ExtractionJobs",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AiRequests_source_document_occurrences_BusinessUnitId_Sourc~",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "SourceDocumentOccurrenceId" },
                principalTable: "source_document_occurrences",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK__Contacts__Custom__17F790F9",
                table: "Contacts",
                columns: new[] { "BusinessUnitID", "CustomerID" },
                principalTable: "Customers",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK__Customers__BUID__0D7A0286",
                table: "Customers",
                column: "BUID",
                principalTable: "BusinessUnits",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ExtractionJobs_source_document_occurrences_BusinessUnitId_S~",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "SourceDocumentOccurrenceId" },
                principalTable: "source_document_occurrences",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LeadIngestionOccurrences_source_document_occurrences_Busine~",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "SourceDocumentOccurrenceId" },
                principalTable: "source_document_occurrences",
                principalColumns: new[] { "business_unit_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Contacts_BusinessUnitID_ContactID",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "ContactID" },
                principalTable: "Contacts",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Customers_BusinessUnitID_CustomerID",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "CustomerID" },
                principalTable: "Customers",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE source_document_occurrences
                    ADD CONSTRAINT ck_source_document_occurrences_intake_status
                    CHECK (intake_status IN ('Accepted','Queued','Processing','Retryable','Resolved','ReviewRequired','Rejected','DeadLetter'));

                CREATE OR REPLACE FUNCTION nexora_release01b_contact_tenant_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."CustomerID" IS NULL AND NEW."SupplierID" IS NULL THEN
                        RAISE EXCEPTION 'Contact requires a tenant-owned Customer or Supplier' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Customers" customer
                        WHERE customer."ID" = NEW."CustomerID" AND customer."BUID" = NEW."BusinessUnitID") THEN
                        RAISE EXCEPTION 'Contact Customer tenant mismatch' USING ERRCODE = '23503';
                    END IF;
                    IF NEW."SupplierID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Suppliers" supplier
                        WHERE supplier."ID" = NEW."SupplierID" AND supplier."BUID" = NEW."BusinessUnitID") THEN
                        RAISE EXCEPTION 'Contact Supplier tenant mismatch' USING ERRCODE = '23503';
                    END IF;
                    RETURN NEW;
                END; $function$;
                DROP TRIGGER IF EXISTS trg_release01b_contact_tenant_guard ON "Contacts";
                CREATE TRIGGER trg_release01b_contact_tenant_guard
                    BEFORE INSERT OR UPDATE OF "BusinessUnitID", "CustomerID", "SupplierID" ON "Contacts"
                    FOR EACH ROW EXECUTE FUNCTION nexora_release01b_contact_tenant_guard();

                CREATE OR REPLACE FUNCTION nexora_release01b_intake_before_claim_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."Status" = 'Leased' AND OLD."Status" IS DISTINCT FROM NEW."Status" AND NOT EXISTS (
                        SELECT 1 FROM source_document_occurrences occurrence
                        WHERE occurrence.business_unit_id = NEW."BusinessUnitId"
                          AND occurrence.id = NEW."SourceDocumentOccurrenceId"
                          AND occurrence.extraction_job_id = NEW."Id"
                          AND occurrence.intake_status IN ('Queued', 'Retryable')) THEN
                        RAISE EXCEPTION 'Extraction cannot start before its durable intake occurrence is queued' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $function$;
                DROP TRIGGER IF EXISTS trg_release01b_intake_before_claim_guard ON "ExtractionJobs";
                CREATE TRIGGER trg_release01b_intake_before_claim_guard
                    BEFORE UPDATE OF "Status" ON "ExtractionJobs"
                    FOR EACH ROW EXECUTE FUNCTION nexora_release01b_intake_before_claim_guard();

                CREATE OR REPLACE FUNCTION nexora_release01b_lead_occurrence_source_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."SourceDocumentOccurrenceId" IS DISTINCT FROM OLD."SourceDocumentOccurrenceId"
                       OR NEW."LogicalGroupKey" IS DISTINCT FROM OLD."LogicalGroupKey" THEN
                        RAISE EXCEPTION 'Lead occurrence source linkage is immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;
                DROP TRIGGER IF EXISTS trg_release01b_lead_occurrence_source_guard ON "LeadIngestionOccurrences";
                CREATE TRIGGER trg_release01b_lead_occurrence_source_guard
                    BEFORE UPDATE ON "LeadIngestionOccurrences"
                    FOR EACH ROW EXECUTE FUNCTION nexora_release01b_lead_occurrence_source_guard();

                ALTER TABLE "Customers" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "Customers" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "Contacts" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "Contacts" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "Leads" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "Leads" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Contacts";
                CREATE POLICY nexora_tenant_isolation ON "Contacts" TO nexora_tenant_app
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "Contacts" TO nexora_tenant_app;
                GRANT SELECT ON TABLE "Module" TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_release01b_lead_occurrence_source_guard ON "LeadIngestionOccurrences";
                DROP FUNCTION IF EXISTS nexora_release01b_lead_occurrence_source_guard();
                DROP TRIGGER IF EXISTS trg_release01b_intake_before_claim_guard ON "ExtractionJobs";
                DROP FUNCTION IF EXISTS nexora_release01b_intake_before_claim_guard();
                DROP TRIGGER IF EXISTS trg_release01b_contact_tenant_guard ON "Contacts";
                DROP FUNCTION IF EXISTS nexora_release01b_contact_tenant_guard();
                ALTER TABLE source_document_occurrences DROP CONSTRAINT IF EXISTS ck_source_document_occurrences_intake_status;
                ALTER TABLE "Contacts" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "Customers" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "Leads" NO FORCE ROW LEVEL SECURITY;
                REVOKE SELECT ON TABLE "Module" FROM nexora_tenant_app;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Contacts";
                CREATE POLICY nexora_tenant_isolation ON "Contacts" TO nexora_tenant_app
                    USING (
                        EXISTS (SELECT 1 FROM "Customers" customer
                                WHERE customer."ID" = "Contacts"."CustomerID"
                                  AND customer."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                        OR EXISTS (SELECT 1 FROM "Suppliers" supplier
                                   WHERE supplier."ID" = "Contacts"."SupplierID"
                                     AND (supplier."BUID" IS NULL OR supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)))
                    WITH CHECK (
                        EXISTS (SELECT 1 FROM "Customers" customer
                                WHERE customer."ID" = "Contacts"."CustomerID"
                                  AND customer."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                        OR EXISTS (SELECT 1 FROM "Suppliers" supplier
                                   WHERE supplier."ID" = "Contacts"."SupplierID"
                                     AND (supplier."BUID" IS NULL OR supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)));
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AiRequests_ExtractionJobs_BusinessUnitId_ExtractionJobId",
                table: "AiRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_AiRequests_source_document_occurrences_BusinessUnitId_Sourc~",
                table: "AiRequests");

            migrationBuilder.DropForeignKey(
                name: "FK__Contacts__Custom__17F790F9",
                table: "Contacts");

            migrationBuilder.DropForeignKey(
                name: "FK__Customers__BUID__0D7A0286",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_ExtractionJobs_source_document_occurrences_BusinessUnitId_S~",
                table: "ExtractionJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_LeadIngestionOccurrences_source_document_occurrences_Busine~",
                table: "LeadIngestionOccurrences");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Contacts_BusinessUnitID_ContactID",
                table: "Leads");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Customers_BusinessUnitID_CustomerID",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "ix_source_document_occurrences_tenant_group",
                table: "source_document_occurrences");

            migrationBuilder.DropIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentOccur~",
                table: "LeadIngestionOccurrences");

            migrationBuilder.DropIndex(
                name: "IX_ExtractionJobs_BU_ContentHash",
                table: "ExtractionJobs");

            migrationBuilder.DropIndex(
                name: "UX_ExtractionJobs_BU_SourceOccurrence",
                table: "ExtractionJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Customers_BUID_ID",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "UQ_Customers_BUID_ContactEmail",
                table: "Customers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Contacts_BusinessUnitID_ID",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_BusinessUnitID_CustomerID",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_BusinessUnitID_Email",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "UQ_Contacts_BusinessUnitID_Email",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_AiRequests_BusinessUnitId_ExtractionJobId",
                table: "AiRequests");

            migrationBuilder.DropIndex(
                name: "IX_AiRequests_BusinessUnitId_SourceDocumentOccurrenceId",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "intake_status",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "last_error_code",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "logical_group_key",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "updated_on",
                table: "source_document_occurrences");

            migrationBuilder.DropColumn(
                name: "LogicalGroupKey",
                table: "LeadIngestionOccurrences");

            migrationBuilder.DropColumn(
                name: "SourceDocumentOccurrenceId",
                table: "LeadIngestionOccurrences");

            migrationBuilder.DropColumn(
                name: "SourceDocumentOccurrenceId",
                table: "ExtractionJobs");

            migrationBuilder.DropColumn(
                name: "BusinessUnitID",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "CostCurrency",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "CostStatus",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "EstimatedCost",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "ExtractionJobId",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "SourceDocumentOccurrenceId",
                table: "AiRequests");

            migrationBuilder.AlterColumn<long>(
                name: "BUID",
                table: "Customers",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.CreateIndex(
                name: "UX_ExtractionJobs_BU_ContentHash",
                table: "ExtractionJobs",
                columns: new[] { "BusinessUnitId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BUID",
                table: "Customers",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "UQ__Customer__FFA796CD4707A72F",
                table: "Customers",
                column: "ContactEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Email",
                table: "Contacts",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "UQ__Contacts__A9D10534C4FF61F8",
                table: "Contacts",
                column: "Email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK__Contacts__Custom__17F790F9",
                table: "Contacts",
                column: "CustomerID",
                principalTable: "Customers",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK__Customers__BUID__0D7A0286",
                table: "Customers",
                column: "BUID",
                principalTable: "BusinessUnits",
                principalColumn: "ID");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_evidence_occurrence_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'source occurrence is immutable evidence' USING ERRCODE = '55000';
                    END IF;
                    IF OLD.extraction_job_id IS NOT NULL
                       OR NEW.extraction_job_id IS NULL
                       OR (NEW.business_unit_id, NEW.id, NEW.source_document_id, NEW.corpus_id,
                           NEW.idempotency_key, NEW.source_metadata, NEW.received_on)
                          IS DISTINCT FROM
                          (OLD.business_unit_id, OLD.id, OLD.source_document_id, OLD.corpus_id,
                           OLD.idempotency_key, OLD.source_metadata, OLD.received_on) THEN
                        RAISE EXCEPTION 'source occurrence permits only initial job binding' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;
                """);
        }
    }
}

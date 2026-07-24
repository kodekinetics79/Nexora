using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01CommercialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RFQ_BusinessUnitID",
                table: "RFQ");

            migrationBuilder.AddColumn<long>(
                name: "CommercialCaseID",
                table: "RFQ",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContactID",
                table: "RFQ",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NexoraSerial",
                table: "RFQ",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CommercialCaseID",
                table: "Quotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContactID",
                table: "Quotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LifecycleVersion",
                table: "Quotes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "NexoraSerial",
                table: "Quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContactID",
                table: "Leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CustomerID",
                table: "Leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerMatchStatus",
                table: "Leads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UNRESOLVED");

            migrationBuilder.Sql("""
                WITH matches AS (
                    SELECT lead."ID" AS lead_id,
                           min(contact."ID") AS contact_id,
                           min(contact."CustomerID") AS customer_id,
                           count(*) AS match_count
                    FROM "Leads" lead
                    JOIN "Contacts" contact
                      ON lower(trim(contact."Email")) = lower(trim(lead."Clientemail"))
                    JOIN "Customers" customer
                      ON customer."ID" = contact."CustomerID"
                     AND customer."BUID" = lead."BusinessUnitID"
                    WHERE NULLIF(trim(lead."Clientemail"), '') IS NOT NULL
                    GROUP BY lead."ID"
                )
                UPDATE "Leads" lead
                   SET "CustomerID" = matches.customer_id,
                       "ContactID" = matches.contact_id,
                       "CustomerMatchStatus" = 'VERIFIED_EMAIL'
                  FROM matches
                 WHERE lead."ID" = matches.lead_id
                   AND matches.match_count = 1;

                DO $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "RFQ" rfq
                        WHERE rfq."LeadID" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM "Leads" lead
                            WHERE lead."ID" = rfq."LeadID"
                              AND lead."BusinessUnitID" = rfq."BusinessUnitID")) THEN
                        RAISE EXCEPTION 'legacy RFQ has a missing or cross-tenant Lead; reconcile before migration'
                            USING ERRCODE = '23503';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "Quotes" quote
                        WHERE quote."RFQID" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM "RFQ" rfq
                            WHERE rfq."ID" = quote."RFQID"
                              AND rfq."BusinessUnitID" = quote."BusinessUnitID")) THEN
                        RAISE EXCEPTION 'legacy Quote has a missing or cross-tenant RFQ; reconcile before migration'
                            USING ERRCODE = '23503';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "RFQ" rfq JOIN "Leads" lead
                          ON lead."ID" = rfq."LeadID" AND lead."BusinessUnitID" = rfq."BusinessUnitID"
                        WHERE rfq."CustomerID" IS NOT NULL
                          AND rfq."CustomerID" IS DISTINCT FROM lead."CustomerID") THEN
                        RAISE EXCEPTION 'legacy RFQ customer conflicts with Lead customer; reconcile before migration'
                            USING ERRCODE = '23514';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "Quotes" quote JOIN "RFQ" rfq
                          ON rfq."ID" = quote."RFQID" AND rfq."BusinessUnitID" = quote."BusinessUnitID"
                        WHERE quote."CustomerID" IS NOT NULL
                          AND quote."CustomerID" IS DISTINCT FROM rfq."CustomerID") THEN
                        RAISE EXCEPTION 'legacy Quote customer conflicts with RFQ customer; reconcile before migration'
                            USING ERRCODE = '23514';
                    END IF;
                END; $function$;

                UPDATE "RFQ" rfq
                   SET "CommercialCaseID" = lead."CommercialCaseId",
                       "NexoraSerial" = lead."CommercialCaseReference",
                       "CustomerID" = COALESCE(rfq."CustomerID", lead."CustomerID"),
                       "ContactID" = CASE
                           WHEN rfq."CustomerID" IS NULL OR rfq."CustomerID" = lead."CustomerID"
                           THEN lead."ContactID" ELSE NULL END
                  FROM "Leads" lead
                 WHERE rfq."LeadID" = lead."ID"
                   AND rfq."BusinessUnitID" = lead."BusinessUnitID";

                UPDATE "Quotes" quote
                   SET "CommercialCaseID" = rfq."CommercialCaseID",
                       "NexoraSerial" = rfq."NexoraSerial",
                       "CustomerID" = COALESCE(quote."CustomerID", rfq."CustomerID"),
                       "ContactID" = CASE
                           WHEN quote."CustomerID" IS NULL OR quote."CustomerID" = rfq."CustomerID"
                           THEN rfq."ContactID" ELSE NULL END
                  FROM "RFQ" rfq
                 WHERE quote."RFQID" = rfq."ID"
                   AND quote."BusinessUnitID" = rfq."BusinessUnitID";

                WITH states(code, label) AS (VALUES
                    ('DRAFT','Draft'),
                    ('SENT','Sent'),
                    ('ACCEPTED','Accepted'),
                    ('REJECTED','Rejected'),
                    ('EXPIRED','Expired'),
                    ('ORDERED','Ordered')
                )
                INSERT INTO "Setup_Master"
                    ("SetupType", "SetupCode", "SetupValue", "Description", "BusinessUnitID",
                     "IsActive", "CreatedBy", "CreatedOn")
                SELECT 'QuoteStatus', states.code, states.label,
                       'Governed lifecycle state (2026-07-22.v1)', business_unit."ID",
                       true, 'migration:lifecycle:v1', now()
                FROM "BusinessUnits" business_unit CROSS JOIN states
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Setup_Master" existing
                    WHERE existing."BusinessUnitID" = business_unit."ID"
                      AND lower(replace(existing."SetupType", ' ', '')) = 'quotestatus'
                      AND upper(COALESCE(existing."SetupCode", '')) = states.code);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_BusinessUnitID_CommercialCaseID",
                table: "RFQ",
                columns: new[] { "BusinessUnitID", "CommercialCaseID" });

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_BusinessUnitID_NexoraSerial",
                table: "RFQ",
                columns: new[] { "BusinessUnitID", "NexoraSerial" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BusinessUnitID_CommercialCaseID",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "CommercialCaseID" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BusinessUnitID_NexoraSerial",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "NexoraSerial" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_BusinessUnitID_ContactID",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "ContactID" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_BusinessUnitID_CustomerID",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "CustomerID" });

            migrationBuilder.AddForeignKey(
                name: "FK_Quotes_CommercialCases_BusinessUnitID_CommercialCaseID",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "CommercialCaseID" },
                principalTable: "CommercialCases",
                principalColumns: new[] { "BusinessUnitID", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RFQ_CommercialCases_BusinessUnitID_CommercialCaseID",
                table: "RFQ",
                columns: new[] { "BusinessUnitID", "CommercialCaseID" },
                principalTable: "CommercialCases",
                principalColumns: new[] { "BusinessUnitID", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropCheckConstraint(
                name: "CK_lifecycle_events_AggregateType",
                table: "commercial_lifecycle_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_AggregateType",
                table: "commercial_lifecycle_events",
                sql: "\"AggregateType\" IN ('Lead', 'Rfq', 'Quote')");

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Quotes_RequireLifecycleCommand"
                    BEFORE UPDATE OF "StatusID" ON "Quotes"
                    FOR EACH ROW WHEN (OLD."StatusID" IS DISTINCT FROM NEW."StatusID")
                    EXECUTE FUNCTION nexora_require_lifecycle_command();

                CREATE OR REPLACE FUNCTION nexora_validate_lead_commercial_identity()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
                        RAISE EXCEPTION 'Lead customer identity is immutable once resolved' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
                        RAISE EXCEPTION 'Lead contact identity is immutable once resolved' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Customers" customer
                        WHERE customer."ID" = NEW."CustomerID"
                          AND customer."BUID" = NEW."BusinessUnitID") THEN
                        RAISE EXCEPTION 'Lead customer must belong to the same tenant' USING ERRCODE = '23503';
                    END IF;
                    IF NEW."ContactID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Contacts" contact
                        WHERE contact."ID" = NEW."ContactID"
                          AND contact."CustomerID" = NEW."CustomerID") THEN
                        RAISE EXCEPTION 'Lead contact must belong to the resolved customer' USING ERRCODE = '23503';
                    END IF;
                    RETURN NEW;
                END; $function$;
                CREATE TRIGGER "TR_Leads_CommercialIdentity"
                    BEFORE INSERT OR UPDATE OF "CustomerID", "ContactID" ON "Leads"
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_lead_commercial_identity();

                CREATE OR REPLACE FUNCTION nexora_validate_downstream_commercial_identity()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD."CommercialCaseID" IS NOT NULL
                       AND (NEW."CommercialCaseID", NEW."NexoraSerial") IS DISTINCT FROM
                           (OLD."CommercialCaseID", OLD."NexoraSerial") THEN
                        RAISE EXCEPTION 'Nexora Serial lineage is immutable once assigned' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
                        RAISE EXCEPTION 'Commercial customer identity is immutable once assigned' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
                        RAISE EXCEPTION 'Commercial contact identity is immutable once assigned' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."CommercialCaseID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "CommercialCases" commercial_case
                        WHERE commercial_case."BusinessUnitID" = NEW."BusinessUnitID"
                          AND commercial_case."Id" = NEW."CommercialCaseID"
                          AND commercial_case."MasterReference" = NEW."NexoraSerial") THEN
                        RAISE EXCEPTION 'Nexora Serial must match the tenant commercial case' USING ERRCODE = '23503';
                    END IF;
                    IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Customers" customer
                        WHERE customer."ID" = NEW."CustomerID"
                          AND customer."BUID" = NEW."BusinessUnitID") THEN
                        RAISE EXCEPTION 'Commercial customer must belong to the same tenant' USING ERRCODE = '23503';
                    END IF;
                    IF NEW."ContactID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Contacts" contact
                        WHERE contact."ID" = NEW."ContactID"
                          AND contact."CustomerID" = NEW."CustomerID") THEN
                        RAISE EXCEPTION 'Commercial contact must belong to the assigned customer' USING ERRCODE = '23503';
                    END IF;
                    IF TG_TABLE_NAME = 'RFQ' AND (to_jsonb(NEW)->>'LeadID') IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Leads" lead
                        WHERE lead."ID" = (to_jsonb(NEW)->>'LeadID')::bigint
                          AND lead."BusinessUnitID" = NEW."BusinessUnitID"
                          AND (lead."CommercialCaseId", lead."CommercialCaseReference", lead."CustomerID", lead."ContactID")
                              IS NOT DISTINCT FROM
                              (NEW."CommercialCaseID", NEW."NexoraSerial", NEW."CustomerID", NEW."ContactID")) THEN
                        RAISE EXCEPTION 'RFQ commercial identity must match its Lead' USING ERRCODE = '23503';
                    END IF;
                    IF TG_TABLE_NAME = 'Quotes' AND (to_jsonb(NEW)->>'RFQID') IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "RFQ" rfq
                        WHERE rfq."ID" = (to_jsonb(NEW)->>'RFQID')::bigint
                          AND rfq."BusinessUnitID" = NEW."BusinessUnitID"
                          AND (rfq."CommercialCaseID", rfq."NexoraSerial", rfq."CustomerID", rfq."ContactID")
                              IS NOT DISTINCT FROM
                              (NEW."CommercialCaseID", NEW."NexoraSerial", NEW."CustomerID", NEW."ContactID")) THEN
                        RAISE EXCEPTION 'Quote commercial identity must match its RFQ' USING ERRCODE = '23503';
                    END IF;
                    RETURN NEW;
                END; $function$;
                CREATE TRIGGER "TR_RFQ_CommercialIdentity"
                    BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID" ON "RFQ"
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_downstream_commercial_identity();
                CREATE TRIGGER "TR_Quotes_CommercialIdentity"
                    BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID" ON "Quotes"
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_downstream_commercial_identity();

                ALTER TABLE "Customers" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Customers";
                CREATE POLICY nexora_tenant_isolation ON "Customers" TO nexora_tenant_app
                    USING ("BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "Contacts" ENABLE ROW LEVEL SECURITY;
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Contacts";
                ALTER TABLE "Contacts" DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Customers";
                ALTER TABLE "Customers" DISABLE ROW LEVEL SECURITY;
                DROP TRIGGER IF EXISTS "TR_Quotes_CommercialIdentity" ON "Quotes";
                DROP TRIGGER IF EXISTS "TR_RFQ_CommercialIdentity" ON "RFQ";
                DROP FUNCTION IF EXISTS nexora_validate_downstream_commercial_identity();
                DROP TRIGGER IF EXISTS "TR_Leads_CommercialIdentity" ON "Leads";
                DROP FUNCTION IF EXISTS nexora_validate_lead_commercial_identity();
                DROP TRIGGER IF EXISTS "TR_Quotes_RequireLifecycleCommand" ON "Quotes";
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_lifecycle_events_AggregateType",
                table: "commercial_lifecycle_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_AggregateType",
                table: "commercial_lifecycle_events",
                sql: "\"AggregateType\" IN ('Lead', 'Rfq')");

            migrationBuilder.DropForeignKey(
                name: "FK_Quotes_CommercialCases_BusinessUnitID_CommercialCaseID",
                table: "Quotes");

            migrationBuilder.DropForeignKey(
                name: "FK_RFQ_CommercialCases_BusinessUnitID_CommercialCaseID",
                table: "RFQ");

            migrationBuilder.DropIndex(
                name: "IX_RFQ_BusinessUnitID_CommercialCaseID",
                table: "RFQ");

            migrationBuilder.DropIndex(
                name: "IX_RFQ_BusinessUnitID_NexoraSerial",
                table: "RFQ");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_BusinessUnitID_CommercialCaseID",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_BusinessUnitID_NexoraSerial",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Leads_BusinessUnitID_ContactID",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_BusinessUnitID_CustomerID",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CommercialCaseID",
                table: "RFQ");

            migrationBuilder.DropColumn(
                name: "ContactID",
                table: "RFQ");

            migrationBuilder.DropColumn(
                name: "NexoraSerial",
                table: "RFQ");

            migrationBuilder.DropColumn(
                name: "CommercialCaseID",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "ContactID",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "LifecycleVersion",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "NexoraSerial",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "ContactID",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerID",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerMatchStatus",
                table: "Leads");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_BusinessUnitID",
                table: "RFQ",
                column: "BusinessUnitID");
        }
    }
}

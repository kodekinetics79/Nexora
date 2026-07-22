using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddPermanentLeadReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "CommercialCaseReferenceSequence");

            migrationBuilder.AddColumn<long>(
                name: "CommercialCaseId",
                table: "Leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommercialCaseReference",
                table: "Leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommercialCases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    AllocationNumber = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('\"CommercialCaseReferenceSequence\"')"),
                    MasterReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercialCases_BusinessUnits_BusinessUnitID",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadReferenceConfigurations",
                columns: table => new
                {
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    Prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Format = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SequencePadding = table.Column<int>(type: "integer", nullable: false, defaultValue: 6),
                    FinancialYearStartMonth = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadReferenceConfigurations", x => x.BusinessUnitID);
                    table.ForeignKey(
                        name: "FK_LeadReferenceConfigurations_BusinessUnits_BusinessUnitID",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadStatusHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LeadID = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseID = table.Column<long>(type: "bigint", nullable: false),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    PreviousStatusID = table.Column<long>(type: "bigint", nullable: true),
                    NewStatusID = table.Column<long>(type: "bigint", nullable: true),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ActorSource = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CommercialCaseReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadStatusHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadStatusHistories_BusinessUnits_BusinessUnitID",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadStatusHistories_CommercialCases_CommercialCaseID",
                        column: x => x.CommercialCaseID,
                        principalTable: "CommercialCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadStatusHistories_Leads_LeadID",
                        column: x => x.LeadID,
                        principalTable: "Leads",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CommercialCases_AllocationNumber",
                table: "CommercialCases",
                column: "AllocationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CommercialCases_BU_MasterReference",
                table: "CommercialCases",
                columns: new[] { "BusinessUnitID", "MasterReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadStatusHistories_CommercialCaseID",
                table: "LeadStatusHistories",
                column: "CommercialCaseID");

            migrationBuilder.CreateIndex(
                name: "IX_LeadStatusHistories_LeadID",
                table: "LeadStatusHistories",
                column: "LeadID");

            migrationBuilder.CreateIndex(
                name: "IX_LeadStatusHistory_BU_Lead_ChangedOn",
                table: "LeadStatusHistories",
                columns: new[] { "BusinessUnitID", "LeadID", "ChangedOn" });

            migrationBuilder.Sql("""
                ALTER TABLE "LeadReferenceConfigurations"
                    ADD CONSTRAINT "CK_LeadReferenceConfiguration_Padding"
                        CHECK ("SequencePadding" BETWEEN 1 AND 18),
                    ADD CONSTRAINT "CK_LeadReferenceConfiguration_FYMonth"
                        CHECK ("FinancialYearStartMonth" BETWEEN 1 AND 12),
                    ADD CONSTRAINT "CK_LeadReferenceConfiguration_SequenceToken"
                        CHECK (position('{SEQUENCE}' in "Format") > 0);

                INSERT INTO "LeadReferenceConfigurations"
                    ("BusinessUnitID", "Prefix", "Format", "SequencePadding", "FinancialYearStartMonth", "CreatedOn")
                SELECT "ID", 'NXR', '{PREFIX}-{YEAR}-{SEQUENCE}', 6, 1, now()
                FROM "BusinessUnits"
                ON CONFLICT ("BusinessUnitID") DO NOTHING;

                WITH numbered AS (
                    SELECT "ID", "BusinessUnitID", "CreatedDate", "CreatedBy",
                           row_number() OVER (ORDER BY "CreatedDate", "ID")::bigint AS allocation_number
                    FROM "Leads"
                )
                INSERT INTO "CommercialCases"
                    ("Id", "BusinessUnitID", "AllocationNumber", "MasterReference", "CreatedOn", "CreatedBy")
                SELECT "ID", "BusinessUnitID", allocation_number,
                       'NXR-' || to_char(COALESCE("CreatedDate", now()), 'YYYY') || '-' || lpad(allocation_number::text, 6, '0'),
                       COALESCE("CreatedDate", now()), COALESCE(NULLIF("CreatedBy", ''), 'System')
                FROM numbered;

                SELECT setval(
                    pg_get_serial_sequence('"CommercialCases"', 'Id'),
                    COALESCE((SELECT max("Id") FROM "CommercialCases"), 1),
                    EXISTS (SELECT 1 FROM "CommercialCases"));

                SELECT setval(
                    '"CommercialCaseReferenceSequence"',
                    COALESCE((SELECT max("AllocationNumber") FROM "CommercialCases"), 1),
                    EXISTS (SELECT 1 FROM "CommercialCases"));

                UPDATE "Leads" AS lead
                SET "CommercialCaseId" = commercial_case."Id",
                    "CommercialCaseReference" = commercial_case."MasterReference"
                FROM "CommercialCases" AS commercial_case
                WHERE commercial_case."Id" = lead."ID";

                INSERT INTO "LeadStatusHistories"
                    ("LeadID", "CommercialCaseID", "BusinessUnitID", "PreviousStatusID", "NewStatusID",
                     "EventType", "ChangedBy", "ActorSource", "ChangedOn", "Reason", "CommercialCaseReference")
                SELECT "ID", "CommercialCaseId", "BusinessUnitID", NULL, "LeadStatusId",
                       'Created', NULL, 'MigrationBackfill', COALESCE("CreatedDate", now()),
                       'Backfilled when permanent commercial cases were introduced', "CommercialCaseReference"
                FROM "Leads";

                ALTER TABLE "Leads" ALTER COLUMN "CommercialCaseId" SET NOT NULL;
                ALTER TABLE "Leads" ALTER COLUMN "CommercialCaseReference" SET NOT NULL;

                CREATE UNIQUE INDEX "UX_Leads_BU_CommercialCaseReference"
                    ON "Leads" ("BusinessUnitID", "CommercialCaseReference");
                CREATE UNIQUE INDEX "UX_Leads_CommercialCaseID"
                    ON "Leads" ("CommercialCaseId");
                ALTER TABLE "Leads"
                    ADD CONSTRAINT "FK_Leads_CommercialCases_CommercialCaseId"
                    FOREIGN KEY ("CommercialCaseId") REFERENCES "CommercialCases" ("Id") ON DELETE RESTRICT;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_assign_commercial_case()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    cfg "LeadReferenceConfigurations"%ROWTYPE;
                    created_at timestamp;
                    allocation_number bigint;
                    financial_year_start integer;
                    financial_year_label text;
                    generated_reference text;
                    business_unit_code text;
                    commercial_case_id bigint;
                BEGIN
                    IF NEW."CommercialCaseId" IS NOT NULL OR NEW."CommercialCaseReference" IS NOT NULL THEN
                        RAISE EXCEPTION 'Commercial-case identity is generated by the server and cannot be supplied manually.';
                    END IF;

                    SELECT * INTO cfg FROM "LeadReferenceConfigurations"
                    WHERE "BusinessUnitID" = NEW."BusinessUnitID";
                    IF NOT FOUND THEN
                        INSERT INTO "LeadReferenceConfigurations"
                            ("BusinessUnitID", "Prefix", "Format", "SequencePadding", "FinancialYearStartMonth", "CreatedOn")
                        VALUES (NEW."BusinessUnitID", 'NXR', '{PREFIX}-{YEAR}-{SEQUENCE}', 6, 1, now())
                        ON CONFLICT ("BusinessUnitID") DO NOTHING;
                        SELECT * INTO cfg FROM "LeadReferenceConfigurations"
                        WHERE "BusinessUnitID" = NEW."BusinessUnitID";
                    END IF;

                    created_at := COALESCE(NEW."CreatedDate", now());
                    allocation_number := nextval('"CommercialCaseReferenceSequence"');
                    financial_year_start := CASE
                        WHEN extract(month FROM created_at) < cfg."FinancialYearStartMonth" THEN extract(year FROM created_at)::integer - 1
                        ELSE extract(year FROM created_at)::integer
                    END;
                    financial_year_label := financial_year_start::text || '-' || right((financial_year_start + 1)::text, 2);

                    SELECT regexp_replace(upper("BusinessUnitCode"), '[^A-Z0-9_-]', '', 'g')
                    INTO business_unit_code FROM "BusinessUnits" WHERE "ID" = NEW."BusinessUnitID";

                    generated_reference := cfg."Format";
                    generated_reference := replace(generated_reference, '{PREFIX}', regexp_replace(upper(cfg."Prefix"), '[^A-Z0-9_-]', '', 'g'));
                    generated_reference := replace(generated_reference, '{YEAR}', to_char(created_at, 'YYYY'));
                    generated_reference := replace(generated_reference, '{FY}', financial_year_label);
                    generated_reference := replace(generated_reference, '{BU}', COALESCE(business_unit_code, ''));
                    generated_reference := replace(generated_reference, '{SOURCE}', regexp_replace(upper(COALESCE(NEW."LeadSource", '')), '[^A-Z0-9_-]', '', 'g'));
                    generated_reference := replace(generated_reference, '{SEQUENCE}', lpad(allocation_number::text, cfg."SequencePadding", '0'));

                    IF length(generated_reference) = 0 OR length(generated_reference) > 100
                       OR position('{' in generated_reference) > 0 OR position('}' in generated_reference) > 0 THEN
                        RAISE EXCEPTION 'Lead-reference configuration produced an invalid reference.';
                    END IF;

                    INSERT INTO "CommercialCases"
                        ("BusinessUnitID", "AllocationNumber", "MasterReference", "CreatedOn", "CreatedBy")
                    VALUES
                        (NEW."BusinessUnitID", allocation_number, generated_reference, created_at,
                         COALESCE(NULLIF(NEW."CreatedBy", ''), 'System'))
                    RETURNING "Id" INTO commercial_case_id;

                    NEW."CommercialCaseId" := commercial_case_id;
                    NEW."CommercialCaseReference" := generated_reference;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_Leads_AssignCommercialCase"
                BEFORE INSERT ON "Leads"
                FOR EACH ROW EXECUTE FUNCTION nexora_assign_commercial_case();

                CREATE OR REPLACE FUNCTION nexora_protect_commercial_identity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_TABLE_NAME = 'Leads' THEN
                        IF NEW."CommercialCaseId" IS DISTINCT FROM OLD."CommercialCaseId"
                           OR NEW."CommercialCaseReference" IS DISTINCT FROM OLD."CommercialCaseReference"
                           OR NEW."BusinessUnitID" IS DISTINCT FROM OLD."BusinessUnitID" THEN
                            RAISE EXCEPTION 'A lead commercial-case identity and tenant cannot be changed.';
                        END IF;
                    ELSE
                        IF TG_OP = 'DELETE' OR NEW."AllocationNumber" IS DISTINCT FROM OLD."AllocationNumber"
                           OR NEW."MasterReference" IS DISTINCT FROM OLD."MasterReference"
                           OR NEW."BusinessUnitID" IS DISTINCT FROM OLD."BusinessUnitID" THEN
                            RAISE EXCEPTION 'The permanent commercial-case identity cannot be changed or deleted.';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_Leads_ProtectCommercialIdentity"
                BEFORE UPDATE OF "CommercialCaseId", "CommercialCaseReference", "BusinessUnitID" ON "Leads"
                FOR EACH ROW EXECUTE FUNCTION nexora_protect_commercial_identity();
                CREATE TRIGGER "TR_CommercialCases_ProtectIdentity"
                BEFORE UPDATE OF "AllocationNumber", "MasterReference", "BusinessUnitID" OR DELETE ON "CommercialCases"
                FOR EACH ROW EXECUTE FUNCTION nexora_protect_commercial_identity();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_record_lead_status_history()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    actor text;
                    actor_source text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        actor := NULLIF(NEW."CreatedBy", '');
                        actor_source := 'LeadCreation';
                    ELSE
                        actor := NULLIF(current_setting('nexora.actor', true), '');
                        actor_source := COALESCE(NULLIF(current_setting('nexora.actor_source', true), ''), 'DatabaseTrigger');
                    END IF;

                    IF TG_OP = 'INSERT' OR NEW."LeadStatusId" IS DISTINCT FROM OLD."LeadStatusId" THEN
                        INSERT INTO "LeadStatusHistories"
                            ("LeadID", "CommercialCaseID", "BusinessUnitID", "PreviousStatusID", "NewStatusID",
                             "EventType", "ChangedBy", "ActorSource", "ChangedOn", "Reason", "CommercialCaseReference")
                        VALUES
                            (NEW."ID", NEW."CommercialCaseId", NEW."BusinessUnitID",
                             CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD."LeadStatusId" END,
                             NEW."LeadStatusId", CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE 'StatusChanged' END,
                             actor, actor_source, now(),
                             CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE NEW."AssignComment" END,
                             NEW."CommercialCaseReference");
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_Leads_RecordStatusHistory"
                AFTER INSERT OR UPDATE OF "LeadStatusId" ON "Leads"
                FOR EACH ROW EXECUTE FUNCTION nexora_record_lead_status_history();

                CREATE OR REPLACE FUNCTION nexora_protect_lead_status_history()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION 'Lead status history is append-only.';
                END;
                $function$;
                CREATE TRIGGER "TR_LeadStatusHistories_AppendOnly"
                BEFORE UPDATE OR DELETE ON "LeadStatusHistories"
                FOR EACH ROW EXECUTE FUNCTION nexora_protect_lead_status_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_LeadStatusHistories_AppendOnly" ON "LeadStatusHistories";
                DROP TRIGGER IF EXISTS "TR_Leads_RecordStatusHistory" ON "Leads";
                DROP TRIGGER IF EXISTS "TR_CommercialCases_ProtectIdentity" ON "CommercialCases";
                DROP TRIGGER IF EXISTS "TR_Leads_ProtectCommercialIdentity" ON "Leads";
                DROP TRIGGER IF EXISTS "TR_Leads_AssignCommercialCase" ON "Leads";
                DROP FUNCTION IF EXISTS nexora_protect_lead_status_history();
                DROP FUNCTION IF EXISTS nexora_record_lead_status_history();
                DROP FUNCTION IF EXISTS nexora_protect_commercial_identity();
                DROP FUNCTION IF EXISTS nexora_assign_commercial_case();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_CommercialCases_CommercialCaseId",
                table: "Leads");

            migrationBuilder.DropTable(
                name: "LeadReferenceConfigurations");

            migrationBuilder.DropTable(
                name: "LeadStatusHistories");

            migrationBuilder.DropTable(
                name: "CommercialCases");

            migrationBuilder.DropIndex(
                name: "UX_Leads_BU_CommercialCaseReference",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "UX_Leads_CommercialCaseID",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CommercialCaseId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CommercialCaseReference",
                table: "Leads");

            migrationBuilder.DropSequence(
                name: "CommercialCaseReferenceSequence");
        }
    }
}

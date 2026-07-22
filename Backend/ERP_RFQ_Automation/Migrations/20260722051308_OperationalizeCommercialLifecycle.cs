using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class OperationalizeCommercialLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK__RolePermi__RoleI__03F0984C",
                table: "RolePermissions");

            migrationBuilder.DropForeignKey(
                name: "FK__Users__RoleID__7B5B524B",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_RoleID",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_RolePermissions_RoleID",
                table: "RolePermissions");

            migrationBuilder.AddColumn<int>(
                name: "LifecycleVersion",
                table: "RFQ",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "LifecycleVersion",
                table: "Leads",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Setup_Master_BusinessUnitID_SetupID",
                table: "Setup_Master",
                columns: new[] { "BusinessUnitID", "SetupID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CommercialCases_BusinessUnitID_Id",
                table: "CommercialCases",
                columns: new[] { "BusinessUnitID", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CommercialCases_BusinessUnitID_Id_MasterReference",
                table: "CommercialCases",
                columns: new[] { "BusinessUnitID", "Id", "MasterReference" });

            migrationBuilder.Sql("""
                WITH states(setup_type, code, label) AS (VALUES
                    ('LeadStatus','RECEIVED','Received'),
                    ('LeadStatus','PENDING_IDENTIFICATION','Pending Identification'),
                    ('LeadStatus','UNASSIGNED','Unassigned'),
                    ('LeadStatus','ASSIGNED','Assigned'),
                    ('LeadStatus','UNDER_REVIEW','Under Review'),
                    ('LeadStatus','QUALIFIED','Qualified'),
                    ('LeadStatus','DISQUALIFIED','Disqualified'),
                    ('LeadStatus','CONVERTED_TO_RFQ','Converted to RFQ'),
                    ('LeadStatus','QUOTED','Quoted'),
                    ('LeadStatus','NEGOTIATION','Negotiation'),
                    ('LeadStatus','AWARDED','Awarded'),
                    ('LeadStatus','PARTIALLY_AWARDED','Partially Awarded'),
                    ('LeadStatus','LOST','Lost'),
                    ('LeadStatus','CANCELLED','Cancelled'),
                    ('LeadStatus','COMPLETED','Completed'),
                    ('LeadStatus','DUPLICATED','Duplicated'),
                    ('RFQStatus','DRAFT','Draft'),
                    ('RFQStatus','VALIDATING','Validating'),
                    ('RFQStatus','NEEDS_REVIEW','Needs Review'),
                    ('RFQStatus','READY_FOR_SALES','Ready for Sales'),
                    ('RFQStatus','INVENTORY_CHECK','Inventory Check'),
                    ('RFQStatus','SUPPLIER_SOURCING','Supplier Sourcing'),
                    ('RFQStatus','AWAITING_SUPPLIER_RESPONSE','Awaiting Supplier Response'),
                    ('RFQStatus','PRICING_COMPLETE','Pricing Complete'),
                    ('RFQStatus','QUOTE_PREPARATION','Quote Preparation'),
                    ('RFQStatus','QUOTE_SENT','Quote Sent'),
                    ('RFQStatus','CUSTOMER_FOLLOW_UP','Customer Follow-Up'),
                    ('RFQStatus','REVISION_REQUESTED','Revision Requested'),
                    ('RFQStatus','AWARDED','Awarded'),
                    ('RFQStatus','PARTIALLY_AWARDED','Partially Awarded'),
                    ('RFQStatus','LOST','Lost'),
                    ('RFQStatus','EXPIRED','Expired'),
                    ('RFQStatus','CANCELLED','Cancelled')
                )
                INSERT INTO "Setup_Master"
                    ("SetupType", "SetupCode", "SetupValue", "Description", "BusinessUnitID",
                     "IsActive", "CreatedBy", "CreatedOn")
                SELECT s.setup_type, s.code, s.label, 'Governed lifecycle state (2026-07-22.v1)',
                       b."ID", true, 'migration:lifecycle:v1', now()
                FROM "BusinessUnits" b CROSS JOIN states s
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Setup_Master" existing
                    WHERE existing."BusinessUnitID" = b."ID"
                      AND lower(replace(existing."SetupType", ' ', '')) = lower(s.setup_type)
                      AND upper(COALESCE(existing."SetupCode", '')) = s.code);
                """);

            migrationBuilder.CreateTable(
                name: "commercial_lifecycle_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PreviousStatusId = table.Column<long>(type: "bigint", nullable: true),
                    PreviousStatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NewStatusId = table.Column<long>(type: "bigint", nullable: false),
                    NewStatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AggregateVersion = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ActorSource = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReasonNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PolicyVersion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_lifecycle_events", x => x.Id);
                    table.UniqueConstraint("AK_commercial_lifecycle_events_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_commercial_lifecycle_events_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_lifecycle_events_CommercialCases_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId, x.CommercialCaseReference },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id", "MasterReference" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_lifecycle_events_Setup_Master_BusinessUnitId_New~",
                        columns: x => new { x.BusinessUnitId, x.NewStatusId },
                        principalTable: "Setup_Master",
                        principalColumns: new[] { "BusinessUnitID", "SetupID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_lifecycle_events_Setup_Master_BusinessUnitId_Pre~",
                        columns: x => new { x.BusinessUnitId, x.PreviousStatusId },
                        principalTable: "Setup_Master",
                        principalColumns: new[] { "BusinessUnitID", "SetupID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lifecycle_outbox_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LifecycleEventId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AvailableOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ProcessedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DeadLetteredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lifecycle_outbox_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lifecycle_outbox_messages_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lifecycle_outbox_messages_commercial_lifecycle_events_Busin~",
                        columns: x => new { x.BusinessUnitId, x.LifecycleEventId },
                        principalTable: "commercial_lifecycle_events",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_BUID_RoleID",
                table: "Users",
                columns: new[] { "BUID", "RoleID" });

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_BusinessUnitID_RoleID",
                table: "RolePermissions",
                columns: new[] { "BusinessUnitID", "RoleID" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_lifecycle_events_BusinessUnitId_AggregateType_Ag~",
                table: "commercial_lifecycle_events",
                columns: new[] { "BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_lifecycle_events_BusinessUnitId_CommercialCaseI~1",
                table: "commercial_lifecycle_events",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_lifecycle_events_BusinessUnitId_CommercialCaseId~",
                table: "commercial_lifecycle_events",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "CommercialCaseReference" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_lifecycle_events_BusinessUnitId_IdempotencyKey",
                table: "commercial_lifecycle_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_lifecycle_events_BusinessUnitId_NewStatusId",
                table: "commercial_lifecycle_events",
                columns: new[] { "BusinessUnitId", "NewStatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_lifecycle_events_BusinessUnitId_PreviousStatusId",
                table: "commercial_lifecycle_events",
                columns: new[] { "BusinessUnitId", "PreviousStatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_outbox_messages_AvailableOn_LockedUntil_OccurredO~",
                table: "lifecycle_outbox_messages",
                columns: new[] { "AvailableOn", "LockedUntil", "OccurredOn", "Id" },
                filter: "\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_outbox_messages_BusinessUnitId_LifecycleEventId",
                table: "lifecycle_outbox_messages",
                columns: new[] { "BusinessUnitId", "LifecycleEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_outbox_messages_LifecycleEventId",
                table: "lifecycle_outbox_messages",
                column: "LifecycleEventId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_AggregateType",
                table: "commercial_lifecycle_events",
                sql: "\"AggregateType\" IN ('Lead', 'Rfq')");
            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_EventType",
                table: "commercial_lifecycle_events",
                sql: "\"EventType\" IN ('StatusTransitioned', 'Reopened')");
            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_VersionPositive",
                table: "commercial_lifecycle_events",
                sql: "\"AggregateVersion\" > 0");
            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_StatusChanged",
                table: "commercial_lifecycle_events",
                sql: "\"PreviousStatusId\" IS NULL OR \"PreviousStatusId\" <> \"NewStatusId\"");
            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_RequestHash",
                table: "commercial_lifecycle_events",
                sql: "length(\"RequestHash\") = 64 AND \"RequestHash\" ~ '^[0-9a-f]{64}$'");
            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_events_RequiredReason",
                table: "commercial_lifecycle_events",
                sql: "(\"EventType\" <> 'Reopened' AND \"NewStatusCode\" NOT IN ('DISQUALIFIED','LOST','EXPIRED','CANCELLED','AWARDED','PARTIALLY_AWARDED')) OR length(btrim(COALESCE(\"ReasonCode\", ''))) > 0");
            migrationBuilder.AddCheckConstraint(
                name: "CK_lifecycle_outbox_State",
                table: "lifecycle_outbox_messages",
                sql: "\"AttemptCount\" >= 0 AND \"SchemaVersion\" > 0 AND ((\"LockedBy\" IS NULL) = (\"LockedUntil\" IS NULL)) AND NOT (\"ProcessedOn\" IS NOT NULL AND \"DeadLetteredOn\" IS NOT NULL) AND ((\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL) OR (\"LockedBy\" IS NULL AND \"LockedUntil\" IS NULL))");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "RFQ" WHERE "LeadID" IS NOT NULL GROUP BY "BusinessUnitID", "LeadID" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Duplicate tenant/lead RFQs must be reconciled before lifecycle migration.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Quotes" WHERE "RFQID" IS NOT NULL GROUP BY "BusinessUnitID", "RFQID" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Duplicate tenant/RFQ quotes must be reconciled before lifecycle migration.';
                    END IF;
                END $$;
                CREATE UNIQUE INDEX "UX_RFQ_BusinessUnitID_LeadID" ON "RFQ" ("BusinessUnitID", "LeadID") WHERE "LeadID" IS NOT NULL;
                CREATE UNIQUE INDEX "UX_Quotes_BusinessUnitID_RFQID" ON "Quotes" ("BusinessUnitID", "RFQID") WHERE "RFQID" IS NOT NULL;

                ALTER TABLE "RolePermissions" ADD CONSTRAINT "FK__RolePermi__RoleI__03F0984C"
                    FOREIGN KEY ("BusinessUnitID", "RoleID")
                    REFERENCES "Setup_Master" ("BusinessUnitID", "SetupID") NOT VALID;
                ALTER TABLE "Users" ADD CONSTRAINT "FK__Users__RoleID__7B5B524B"
                    FOREIGN KEY ("BUID", "RoleID")
                    REFERENCES "Setup_Master" ("BusinessUnitID", "SetupID") NOT VALID;

                CREATE OR REPLACE FUNCTION nexora_protect_commercial_lifecycle_event()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                        MESSAGE = 'Commercial lifecycle events are append-only.';
                END;
                $$;

                CREATE TRIGGER "TR_CommercialLifecycleEvents_Immutable"
                    BEFORE UPDATE OR DELETE ON commercial_lifecycle_events
                    FOR EACH ROW EXECUTE FUNCTION nexora_protect_commercial_lifecycle_event();
                CREATE TRIGGER "TR_CommercialLifecycleEvents_NoTruncate"
                    BEFORE TRUNCATE ON commercial_lifecycle_events
                    FOR EACH STATEMENT EXECUTE FUNCTION nexora_protect_commercial_lifecycle_event();

                CREATE OR REPLACE FUNCTION nexora_require_lifecycle_command()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF current_setting('nexora.lifecycle_command', true) IS DISTINCT FROM 'true'
                       OR NEW."LifecycleVersion" <> OLD."LifecycleVersion" + 1 THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'Status must be changed through the governed lifecycle command.';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER "TR_Leads_RequireLifecycleCommand"
                    BEFORE UPDATE OF "LeadStatusId" ON "Leads"
                    FOR EACH ROW WHEN (OLD."LeadStatusId" IS DISTINCT FROM NEW."LeadStatusId")
                    EXECUTE FUNCTION nexora_require_lifecycle_command();
                CREATE TRIGGER "TR_RFQ_RequireLifecycleCommand"
                    BEFORE UPDATE OF "RFQStatusID" ON "RFQ"
                    FOR EACH ROW WHEN (OLD."RFQStatusID" IS DISTINCT FROM NEW."RFQStatusID")
                    EXECUTE FUNCTION nexora_require_lifecycle_command();

                CREATE OR REPLACE FUNCTION nexora_record_lead_status_history()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE actor text; actor_source text; transition_reason text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        actor := NULLIF(NEW."CreatedBy", ''); actor_source := 'LeadCreation'; transition_reason := NULL;
                    ELSE
                        actor := NULLIF(current_setting('nexora.actor', true), '');
                        actor_source := COALESCE(NULLIF(current_setting('nexora.actor_source', true), ''), 'DatabaseTrigger');
                        transition_reason := COALESCE(NULLIF(current_setting('nexora.reason', true), ''), NEW."AssignComment");
                    END IF;
                    IF TG_OP = 'INSERT' OR NEW."LeadStatusId" IS DISTINCT FROM OLD."LeadStatusId" THEN
                        INSERT INTO "LeadStatusHistories"
                            ("LeadID", "CommercialCaseID", "BusinessUnitID", "PreviousStatusID", "NewStatusID",
                             "EventType", "ChangedBy", "ActorSource", "ChangedOn", "Reason", "CommercialCaseReference")
                        VALUES (NEW."ID", NEW."CommercialCaseId", NEW."BusinessUnitID",
                            CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD."LeadStatusId" END,
                            NEW."LeadStatusId", CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE 'StatusChanged' END,
                            actor, actor_source, now(), transition_reason, NEW."CommercialCaseReference");
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "UX_Quotes_BusinessUnitID_RFQID";
                DROP INDEX IF EXISTS "UX_RFQ_BusinessUnitID_LeadID";
                DROP TRIGGER IF EXISTS "TR_RFQ_RequireLifecycleCommand" ON "RFQ";
                DROP TRIGGER IF EXISTS "TR_Leads_RequireLifecycleCommand" ON "Leads";
                DROP FUNCTION IF EXISTS nexora_require_lifecycle_command();
                DROP TRIGGER IF EXISTS "TR_CommercialLifecycleEvents_NoTruncate" ON commercial_lifecycle_events;
                DROP TRIGGER IF EXISTS "TR_CommercialLifecycleEvents_Immutable" ON commercial_lifecycle_events;
                DROP FUNCTION IF EXISTS nexora_protect_commercial_lifecycle_event();

                CREATE OR REPLACE FUNCTION nexora_record_lead_status_history()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE actor text; actor_source text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        actor := NULLIF(NEW."CreatedBy", ''); actor_source := 'LeadCreation';
                    ELSE
                        actor := NULLIF(current_setting('nexora.actor', true), '');
                        actor_source := COALESCE(NULLIF(current_setting('nexora.actor_source', true), ''), 'DatabaseTrigger');
                    END IF;
                    IF TG_OP = 'INSERT' OR NEW."LeadStatusId" IS DISTINCT FROM OLD."LeadStatusId" THEN
                        INSERT INTO "LeadStatusHistories"
                            ("LeadID", "CommercialCaseID", "BusinessUnitID", "PreviousStatusID", "NewStatusID",
                             "EventType", "ChangedBy", "ActorSource", "ChangedOn", "Reason", "CommercialCaseReference")
                        VALUES (NEW."ID", NEW."CommercialCaseId", NEW."BusinessUnitID",
                            CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD."LeadStatusId" END,
                            NEW."LeadStatusId", CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE 'StatusChanged' END,
                            actor, actor_source, now(),
                            CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE NEW."AssignComment" END,
                            NEW."CommercialCaseReference");
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK__RolePermi__RoleI__03F0984C",
                table: "RolePermissions");

            migrationBuilder.DropForeignKey(
                name: "FK__Users__RoleID__7B5B524B",
                table: "Users");

            migrationBuilder.DropTable(
                name: "lifecycle_outbox_messages");

            migrationBuilder.DropTable(
                name: "commercial_lifecycle_events");

            migrationBuilder.DropIndex(
                name: "IX_Users_BUID_RoleID",
                table: "Users");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Setup_Master_BusinessUnitID_SetupID",
                table: "Setup_Master");

            migrationBuilder.DropIndex(
                name: "IX_RolePermissions_BusinessUnitID_RoleID",
                table: "RolePermissions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CommercialCases_BusinessUnitID_Id",
                table: "CommercialCases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CommercialCases_BusinessUnitID_Id_MasterReference",
                table: "CommercialCases");

            migrationBuilder.DropColumn(
                name: "LifecycleVersion",
                table: "RFQ");

            migrationBuilder.DropColumn(
                name: "LifecycleVersion",
                table: "Leads");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleID",
                table: "Users",
                column: "RoleID");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleID",
                table: "RolePermissions",
                column: "RoleID");

            migrationBuilder.AddForeignKey(
                name: "FK__RolePermi__RoleI__03F0984C",
                table: "RolePermissions",
                column: "RoleID",
                principalTable: "Setup_Master",
                principalColumn: "SetupID");

            migrationBuilder.AddForeignKey(
                name: "FK__Users__RoleID__7B5B524B",
                table: "Users",
                column: "RoleID",
                principalTable: "Setup_Master",
                principalColumn: "SetupID");
        }
    }
}

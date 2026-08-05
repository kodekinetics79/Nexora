using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V2Gate01CommercialExceptionCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_unassigned_work_items_BusinessUnitId_Id",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.CreateTable(
                name: "commercial_exception_cases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: false),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExceptionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExceptionKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    FollowUpTaskId = table.Column<long>(type: "bigint", nullable: true),
                    UnassignedWorkItemId = table.Column<long>(type: "bigint", nullable: true),
                    OwnerUserId = table.Column<long>(type: "bigint", nullable: true),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecommendedActionCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FirstDetectedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastDetectedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SlaDueAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_exception_cases", x => x.Id);
                    table.UniqueConstraint("AK_commercial_exception_cases_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_commercial_exception_cases_Severity", "\"Severity\" IN ('Low','Medium','High','Critical')");
                    table.CheckConstraint("CK_commercial_exception_cases_Source", "(\"ExceptionType\" = 'UnassignedLead' AND \"UnassignedWorkItemId\" IS NOT NULL AND \"FollowUpTaskId\" IS NULL) OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"FollowUpTaskId\" IS NOT NULL AND \"UnassignedWorkItemId\" IS NULL)");
                    table.CheckConstraint("CK_commercial_exception_cases_SourceIdentity", "(\"ExceptionType\" = 'UnassignedLead' AND \"SourceType\" = 'UnassignedWorkItem' AND \"SourceId\" = \"UnassignedWorkItemId\") OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"SourceType\" = 'FollowUpTask' AND \"SourceId\" = \"FollowUpTaskId\")");
                    table.CheckConstraint("CK_commercial_exception_cases_SourceVersion", "\"SourceVersion\" >= 1");
                    table.CheckConstraint("CK_commercial_exception_cases_Status", "\"Status\" IN ('Open','Acknowledged','Resolved','Dismissed')");
                    table.CheckConstraint("CK_commercial_exception_cases_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_commercial_exception_cases_CommercialCases_BusinessUnitId_C~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId, x.NexoraSerial },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id", "MasterReference" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_exception_cases_follow_up_tasks_BusinessUnitId_F~",
                        columns: x => new { x.BusinessUnitId, x.FollowUpTaskId },
                        principalTable: "follow_up_tasks",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_exception_cases_unassigned_work_items_BusinessUn~",
                        columns: x => new { x.BusinessUnitId, x.UnassignedWorkItemId },
                        principalTable: "unassigned_work_items",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_exception_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialExceptionCaseId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FromVersion = table.Column<long>(type: "bigint", nullable: false),
                    ToVersion = table.Column<long>(type: "bigint", nullable: false),
                    ActionCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_exception_events", x => x.Id);
                    table.UniqueConstraint("AK_commercial_exception_events_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_commercial_exception_events_Status", "(\"FromStatus\" IS NULL OR \"FromStatus\" IN ('Open','Acknowledged','Resolved','Dismissed')) AND \"ToStatus\" IN ('Open','Acknowledged','Resolved','Dismissed')");
                    table.CheckConstraint("CK_commercial_exception_events_Version", "\"FromVersion\" >= 0 AND \"ToVersion\" = \"FromVersion\" + 1");
                    table.ForeignKey(
                        name: "FK_commercial_exception_events_commercial_exception_cases_Busi~",
                        columns: x => new { x.BusinessUnitId, x.CommercialExceptionCaseId },
                        principalTable: "commercial_exception_cases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_exception_operations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CommercialExceptionCaseId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_exception_operations", x => x.Id);
                    table.UniqueConstraint("AK_commercial_exception_operations_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_commercial_exception_operations_Case", "(\"OperationType\" = 'Refresh' AND \"CommercialExceptionCaseId\" IS NULL) OR (\"OperationType\" = 'Transition' AND \"CommercialExceptionCaseId\" IS NOT NULL)");
                    table.CheckConstraint("CK_commercial_exception_operations_Type", "\"OperationType\" IN ('Refresh','Transition')");
                    table.ForeignKey(
                        name: "FK_commercial_exception_operations_commercial_exception_cases_~",
                        columns: x => new { x.BusinessUnitId, x.CommercialExceptionCaseId },
                        principalTable: "commercial_exception_cases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_exception_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialExceptionEventId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_exception_outbox", x => x.Id);
                    table.CheckConstraint("CK_commercial_exception_outbox_Attempts", "\"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_commercial_exception_outbox_commercial_exception_events_Bus~",
                        columns: x => new { x.BusinessUnitId, x.CommercialExceptionEventId },
                        principalTable: "commercial_exception_events",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_CommercialCaseId_~",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "NexoraSerial" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_ExceptionKey",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "ExceptionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_FollowUpTaskId",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "FollowUpTaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_Status_Severity_S~",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "Status", "Severity", "SlaDueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_UnassignedWorkIte~",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "UnassignedWorkItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_events_BusinessUnitId_CommercialExcept~",
                table: "commercial_exception_events",
                columns: new[] { "BusinessUnitId", "CommercialExceptionCaseId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_events_BusinessUnitId_IdempotencyKey",
                table: "commercial_exception_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_operations_BusinessUnitId_CommercialEx~",
                table: "commercial_exception_operations",
                columns: new[] { "BusinessUnitId", "CommercialExceptionCaseId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_operations_BusinessUnitId_IdempotencyK~",
                table: "commercial_exception_operations",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_outbox_BusinessUnitId_CommercialExcept~",
                table: "commercial_exception_outbox",
                columns: new[] { "BusinessUnitId", "CommercialExceptionEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_outbox_CommercialExceptionEventId",
                table: "commercial_exception_outbox",
                column: "CommercialExceptionEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_outbox_ProcessedAtUtc_AvailableAtUtc",
                table: "commercial_exception_outbox",
                columns: new[] { "ProcessedAtUtc", "AvailableAtUtc" });

            migrationBuilder.Sql("""
                ALTER TABLE public.commercial_exception_cases ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_cases FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_events FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_operations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_operations FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_outbox ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_exception_outbox FORCE ROW LEVEL SECURITY;

                CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_cases TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_events TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_operations TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_exception_outbox TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_exception_case()
                RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN
                    IF TG_OP = 'UPDATE' AND (
                        NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId" OR
                        NEW."CommercialCaseId" IS DISTINCT FROM OLD."CommercialCaseId" OR
                        NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial" OR
                        NEW."ExceptionType" IS DISTINCT FROM OLD."ExceptionType" OR
                        NEW."ExceptionKey" IS DISTINCT FROM OLD."ExceptionKey" OR
                        NEW."SourceType" IS DISTINCT FROM OLD."SourceType" OR
                        NEW."SourceId" IS DISTINCT FROM OLD."SourceId" OR
                        NEW."FollowUpTaskId" IS DISTINCT FROM OLD."FollowUpTaskId" OR
                        NEW."UnassignedWorkItemId" IS DISTINCT FROM OLD."UnassignedWorkItemId" OR
                        NEW."FirstDetectedAtUtc" IS DISTINCT FROM OLD."FirstDetectedAtUtc"
                    ) THEN
                        RAISE EXCEPTION 'commercial exception lineage is immutable';
                    END IF;

                    IF NEW."OwnerUserId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM public."Users" u
                        WHERE u."ID" = NEW."OwnerUserId" AND u."BUID" = NEW."BusinessUnitId"
                    ) THEN
                        RAISE EXCEPTION 'commercial exception owner must belong to the same tenant';
                    END IF;
                    RETURN NEW;
                END;
                $guard$;

                CREATE TRIGGER trg_guard_commercial_exception_case
                    BEFORE INSERT OR UPDATE ON public.commercial_exception_cases
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_exception_case();

                CREATE OR REPLACE FUNCTION public.nexora_require_commercial_exception_event()
                RETURNS trigger LANGUAGE plpgsql AS $audit$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NOT EXISTS (
                            SELECT 1
                            FROM public.commercial_exception_events event
                            WHERE event."BusinessUnitId" = NEW."BusinessUnitId"
                              AND event."CommercialExceptionCaseId" = NEW."Id"
                              AND event."FromStatus" IS NULL
                              AND event."FromVersion" = 0
                              AND event."ToStatus" = NEW."Status"
                              AND event."ToVersion" = NEW."Version"
                        ) THEN
                            RAISE EXCEPTION 'commercial exception creation requires a matching append-only event';
                        END IF;
                    ELSIF ROW(
                        NEW."SourceVersion", NEW."OwnerUserId", NEW."Severity", NEW."Status",
                        NEW."ReasonCode", NEW."Title", NEW."Summary", NEW."RecommendedActionCode",
                        NEW."EvidenceJson", NEW."RuleVersion", NEW."LastDetectedAtUtc",
                        NEW."SlaDueAtUtc", NEW."ResolvedAtUtc", NEW."Version"
                    ) IS DISTINCT FROM ROW(
                        OLD."SourceVersion", OLD."OwnerUserId", OLD."Severity", OLD."Status",
                        OLD."ReasonCode", OLD."Title", OLD."Summary", OLD."RecommendedActionCode",
                        OLD."EvidenceJson", OLD."RuleVersion", OLD."LastDetectedAtUtc",
                        OLD."SlaDueAtUtc", OLD."ResolvedAtUtc", OLD."Version"
                    ) THEN
                        IF NEW."Version" <> OLD."Version" + 1 OR NOT EXISTS (
                            SELECT 1
                            FROM public.commercial_exception_events event
                            WHERE event."BusinessUnitId" = NEW."BusinessUnitId"
                              AND event."CommercialExceptionCaseId" = NEW."Id"
                              AND event."FromStatus" IS NOT DISTINCT FROM OLD."Status"
                              AND event."FromVersion" = OLD."Version"
                              AND event."ToStatus" = NEW."Status"
                              AND event."ToVersion" = NEW."Version"
                        ) THEN
                            RAISE EXCEPTION 'commercial exception material changes require the next version and a matching append-only event';
                        END IF;
                    END IF;
                    RETURN NULL;
                END;
                $audit$;

                CREATE CONSTRAINT TRIGGER trg_require_commercial_exception_event
                    AFTER INSERT OR UPDATE ON public.commercial_exception_cases
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_require_commercial_exception_event();

                CREATE OR REPLACE FUNCTION public.nexora_reject_commercial_exception_event_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $immutable$
                BEGIN
                    RAISE EXCEPTION 'commercial exception events are append-only';
                END;
                $immutable$;

                CREATE TRIGGER trg_commercial_exception_events_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_exception_events
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_exception_event_mutation();

                ALTER TABLE public.commercial_exception_events
                    ADD CONSTRAINT "CK_commercial_exception_events_ActionStatus" CHECK (
                        ("ActionCode" = 'DETECTED' AND "FromStatus" IS NULL AND "FromVersion" = 0 AND "ToStatus" = 'Open' AND "ToVersion" = 1) OR
                        ("ActionCode" = 'REFRESHED' AND "FromStatus" IS NOT NULL AND "ToStatus" = "FromStatus") OR
                        ("ActionCode" = 'ACKNOWLEDGE' AND "FromStatus" = 'Open' AND "ToStatus" = 'Acknowledged') OR
                        ("ActionCode" = 'RESOLVE' AND "FromStatus" IN ('Open','Acknowledged') AND "ToStatus" = 'Resolved') OR
                        ("ActionCode" = 'SOURCE_RESOLVED' AND "FromStatus" IN ('Open','Acknowledged') AND "ToStatus" = 'Resolved') OR
                        ("ActionCode" = 'DISMISS' AND "FromStatus" IN ('Open','Acknowledged') AND "ToStatus" = 'Dismissed') OR
                        ("ActionCode" IN ('REOPEN','REOPENED') AND "FromStatus" IN ('Acknowledged','Resolved','Dismissed') AND "ToStatus" = 'Open')
                    );

                CREATE OR REPLACE FUNCTION public.nexora_reject_commercial_exception_operation_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $immutable$
                BEGIN
                    RAISE EXCEPTION 'commercial exception operation receipts are append-only';
                END;
                $immutable$;

                CREATE TRIGGER trg_commercial_exception_operations_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_exception_operations
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_commercial_exception_operation_mutation();

                CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_exception_outbox()
                RETURNS trigger LANGUAGE plpgsql AS $outbox$
                BEGIN
                    IF TG_OP = 'DELETE' OR
                       NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId" OR
                       NEW."CommercialExceptionEventId" IS DISTINCT FROM OLD."CommercialExceptionEventId" OR
                       NEW."EventType" IS DISTINCT FROM OLD."EventType" OR
                       NEW."Payload" IS DISTINCT FROM OLD."Payload" OR
                       NEW."OccurredAtUtc" IS DISTINCT FROM OLD."OccurredAtUtc" THEN
                        RAISE EXCEPTION 'commercial exception outbox identity and payload are immutable';
                    END IF;
                    RETURN NEW;
                END;
                $outbox$;

                CREATE TRIGGER trg_guard_commercial_exception_outbox
                    BEFORE UPDATE OR DELETE ON public.commercial_exception_outbox
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_commercial_exception_outbox();

                REVOKE ALL ON public.commercial_exception_cases FROM PUBLIC;
                REVOKE ALL ON public.commercial_exception_events FROM PUBLIC;
                REVOKE ALL ON public.commercial_exception_operations FROM PUBLIC;
                REVOKE ALL ON public.commercial_exception_outbox FROM PUBLIC;

                DO $roles$
                DECLARE
                    governed_table text;
                    sequence_name text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON public.commercial_exception_cases TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public.commercial_exception_events TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public.commercial_exception_operations TO nexora_tenant_app;
                        GRANT SELECT, INSERT, UPDATE ON public.commercial_exception_outbox TO nexora_tenant_app;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON public.commercial_exception_cases TO nexora_pipeline_app;
                        GRANT SELECT, INSERT ON public.commercial_exception_events TO nexora_pipeline_app;
                        GRANT SELECT, INSERT ON public.commercial_exception_operations TO nexora_pipeline_app;
                        GRANT SELECT, INSERT, UPDATE ON public.commercial_exception_outbox TO nexora_pipeline_app;
                    END IF;

                    FOREACH governed_table IN ARRAY ARRAY[
                        'commercial_exception_cases',
                        'commercial_exception_events',
                        'commercial_exception_operations',
                        'commercial_exception_outbox'
                    ] LOOP
                        sequence_name := pg_get_serial_sequence(format('public.%I', governed_table), 'Id');
                        IF sequence_name IS NOT NULL AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                            EXECUTE format('REVOKE ALL PRIVILEGES ON SEQUENCE %s FROM nexora_tenant_app', sequence_name);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                        END IF;
                        IF sequence_name IS NOT NULL AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                            EXECUTE format('REVOKE ALL PRIVILEGES ON SEQUENCE %s FROM nexora_pipeline_app', sequence_name);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_pipeline_app', sequence_name);
                        END IF;
                    END LOOP;
                END;
                $roles$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.nexora_guard_commercial_exception_outbox() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_reject_commercial_exception_operation_mutation() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_reject_commercial_exception_event_mutation() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_require_commercial_exception_event() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_guard_commercial_exception_case() CASCADE;
                """);

            migrationBuilder.DropTable(
                name: "commercial_exception_operations");

            migrationBuilder.DropTable(
                name: "commercial_exception_outbox");

            migrationBuilder.DropTable(
                name: "commercial_exception_events");

            migrationBuilder.DropTable(
                name: "commercial_exception_cases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_unassigned_work_items_BusinessUnitId_Id",
                table: "unassigned_work_items");
        }
    }
}

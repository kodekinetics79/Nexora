using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V1Gate04LocalFirstAiLearningGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ExternalCost",
                table: "LeadIngestionOccurrences",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AddColumn<int>(
                name: "ocr_page_count",
                table: "extraction_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ocr_status",
                table: "extraction_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.AddColumn<bool>(
                name: "ocr_truncated",
                table: "extraction_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "processing_path",
                table: "extraction_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyUnknown");

            migrationBuilder.AddColumn<bool>(
                name: "BudgetWarning",
                table: "AiRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostPricingVersion",
                table: "AiRequests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCostCurrency",
                table: "AiProcessingPolicies",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExternalInputCostPerMillionTokens",
                table: "AiProcessingPolicies",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExternalOutputCostPerMillionTokens",
                table: "AiProcessingPolicies",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPricingVersion",
                table: "AiProcessingPolicies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MaxTokensPerDocument",
                table: "AiProcessingPolicies",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learning_governance_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SignalId = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    RevertsVersion = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_governance_events", x => x.Id);
                    table.CheckConstraint("CK_learning_governance_events_Action", "\"Action\" IN ('APPROVED','DISABLED','ROLLED_BACK')");
                    table.CheckConstraint("CK_learning_governance_events_Version", "\"Version\" > 0 AND (\"Action\" = 'ROLLED_BACK') = (\"RevertsVersion\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_learning_governance_events_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_extraction_runs_ocr_evidence",
                table: "extraction_runs",
                sql: "ocr_page_count >= 0 AND (ocr_status <> 'NotRequired' OR (ocr_page_count = 0 AND ocr_truncated = FALSE))");

            migrationBuilder.CreateIndex(
                name: "IX_learning_governance_events_BU_Signal_OccurredOn",
                table: "learning_governance_events",
                columns: new[] { "BusinessUnitId", "SignalId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "UX_learning_governance_events_BU_Idempotency",
                table: "learning_governance_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_learning_governance_events_BU_Signal_Version",
                table: "learning_governance_events",
                columns: new[] { "BusinessUnitId", "SignalId", "Version" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."AiProcessingPolicies"
                    ADD CONSTRAINT "CK_AiProcessingPolicies_DocumentBudget"
                    CHECK ("MaxTokensPerDocument" IS NULL OR "MaxTokensPerDocument" > 0),
                    ADD CONSTRAINT "CK_AiProcessingPolicies_ExternalRates"
                    CHECK (("ExternalInputCostPerMillionTokens" IS NULL
                            AND "ExternalOutputCostPerMillionTokens" IS NULL
                            AND "ExternalCostCurrency" IS NULL
                            AND "ExternalPricingVersion" IS NULL)
                        OR ("ExternalInputCostPerMillionTokens" >= 0
                            AND "ExternalOutputCostPerMillionTokens" >= 0
                            AND "ExternalCostCurrency" ~ '^[A-Z]{3}$'
                            AND length(trim("ExternalPricingVersion")) > 0));

                ALTER TABLE public.learning_governance_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.learning_governance_events FORCE ROW LEVEL SECURITY;

                DO $rls$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        CREATE POLICY nexora_tenant_isolation ON public.learning_governance_events
                            TO nexora_tenant_app
                            USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                            WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                        GRANT SELECT, INSERT ON TABLE public.learning_governance_events TO nexora_tenant_app;
                        REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
                            ON TABLE public.learning_governance_events FROM nexora_tenant_app;
                        GRANT USAGE ON SEQUENCE public."learning_governance_events_Id_seq" TO nexora_tenant_app;
                    END IF;
                END
                $rls$;

                CREATE OR REPLACE FUNCTION public.nexora_reject_learning_governance_mutation()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    RAISE EXCEPTION 'learning governance events are append-only' USING ERRCODE = '55000';
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_reject_learning_governance_mutation() FROM PUBLIC;

                CREATE TRIGGER learning_governance_events_immutable
                    BEFORE UPDATE OR DELETE ON public.learning_governance_events
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_learning_governance_mutation();
                CREATE TRIGGER learning_governance_events_reject_truncate
                    BEFORE TRUNCATE ON public.learning_governance_events
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_learning_governance_mutation();

                CREATE OR REPLACE FUNCTION public.nexora_validate_learning_governance_insert()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF NEW."Action" = 'ROLLED_BACK'
                       AND NEW."RevertsVersion" IS DISTINCT FROM NEW."Version" - 1 THEN
                        RAISE EXCEPTION 'rollback must compensate the immediately preceding version'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_validate_learning_governance_insert() FROM PUBLIC;
                CREATE TRIGGER learning_governance_events_validate_insert
                    BEFORE INSERT ON public.learning_governance_events
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_learning_governance_insert();

                CREATE OR REPLACE FUNCTION public.nexora_guard_ai_request_update()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF OLD."CompletedOn" IS NOT NULL THEN
                        RAISE EXCEPTION 'Completed AI requests are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF ROW(NEW."BusinessUnitId", NEW."ExtractionJobId", NEW."SourceDocumentOccurrenceId",
                           NEW."Operation", NEW."IdempotencyKey", NEW."PromptHash", NEW."PromptVersion",
                           NEW."Provider", NEW."ProviderClass", NEW."Model", NEW."InputCharacters",
                           NEW."InputHash", NEW."InjectionDetected", NEW."EstimatedInputTokens",
                           NEW."ReservedTokens", NEW."BudgetWarning", NEW."CreatedOn")
                       IS DISTINCT FROM
                       ROW(OLD."BusinessUnitId", OLD."ExtractionJobId", OLD."SourceDocumentOccurrenceId",
                           OLD."Operation", OLD."IdempotencyKey", OLD."PromptHash", OLD."PromptVersion",
                           OLD."Provider", OLD."ProviderClass", OLD."Model", OLD."InputCharacters",
                           OLD."InputHash", OLD."InjectionDetected", OLD."EstimatedInputTokens",
                           OLD."ReservedTokens", OLD."BudgetWarning", OLD."CreatedOn") THEN
                        RAISE EXCEPTION 'AI request identity, linkage and reservation fields are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF (OLD."Status" = 'Reserved' AND NEW."Status" NOT IN ('Reserved', 'Running', 'Succeeded', 'Failed', 'Unknown'))
                       OR (OLD."Status" = 'Running' AND NEW."Status" NOT IN ('Running', 'Succeeded', 'Failed', 'Unknown'))
                       OR OLD."Status" IN ('Succeeded', 'Denied', 'Failed', 'Unknown') THEN
                        RAISE EXCEPTION 'Invalid AI request status transition' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Status" IN ('Reserved', 'Running')
                       AND ROW(NEW."OutputCharacters", NEW."OutputHash", NEW."InputTokens", NEW."OutputTokens",
                               NEW."TokenSource", NEW."EstimatedCost", NEW."CostCurrency", NEW."CostStatus",
                               NEW."CostPricingVersion", NEW."ErrorCode", NEW."CompletedOn")
                           IS DISTINCT FROM
                           ROW(OLD."OutputCharacters", OLD."OutputHash", OLD."InputTokens", OLD."OutputTokens",
                               OLD."TokenSource", OLD."EstimatedCost", OLD."CostCurrency", OLD."CostStatus",
                               OLD."CostPricingVersion", OLD."ErrorCode", OLD."CompletedOn") THEN
                        RAISE EXCEPTION 'AI usage and cost can only be finalized with a terminal state' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_guard_ai_request_update() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS learning_governance_events_immutable ON public.learning_governance_events;
                DROP TRIGGER IF EXISTS learning_governance_events_reject_truncate ON public.learning_governance_events;
                DROP TRIGGER IF EXISTS learning_governance_events_validate_insert ON public.learning_governance_events;
                DROP FUNCTION IF EXISTS public.nexora_reject_learning_governance_mutation();
                DROP FUNCTION IF EXISTS public.nexora_validate_learning_governance_insert();
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public.learning_governance_events;
                DROP TRIGGER IF EXISTS ai_requests_guard_update ON public."AiRequests";
                DROP FUNCTION IF EXISTS public.nexora_guard_ai_request_update();
                ALTER TABLE public."AiProcessingPolicies"
                    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_DocumentBudget",
                    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_ExternalRates";
                """);

            migrationBuilder.DropTable(
                name: "learning_governance_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_extraction_runs_ocr_evidence",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "ocr_page_count",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "ocr_status",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "ocr_truncated",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "processing_path",
                table: "extraction_runs");

            migrationBuilder.DropColumn(
                name: "BudgetWarning",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "CostPricingVersion",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "ExternalCostCurrency",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "ExternalInputCostPerMillionTokens",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "ExternalOutputCostPerMillionTokens",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "ExternalPricingVersion",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "MaxTokensPerDocument",
                table: "AiProcessingPolicies");

            migrationBuilder.Sql("""
                UPDATE public."LeadIngestionOccurrences"
                SET "ExternalCost" = 0
                WHERE "ExternalCost" IS NULL;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "ExternalCost",
                table: "LeadIngestionOccurrences",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6,
                oldNullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_guard_ai_request_update()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF OLD."CompletedOn" IS NOT NULL THEN
                        RAISE EXCEPTION 'Completed AI requests are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF ROW(NEW."BusinessUnitId", NEW."Operation", NEW."IdempotencyKey", NEW."PromptHash",
                           NEW."PromptVersion", NEW."Provider", NEW."Model", NEW."InputCharacters",
                           NEW."InputHash", NEW."InjectionDetected", NEW."EstimatedInputTokens",
                           NEW."ReservedTokens", NEW."CreatedOn")
                       IS DISTINCT FROM
                       ROW(OLD."BusinessUnitId", OLD."Operation", OLD."IdempotencyKey", OLD."PromptHash",
                           OLD."PromptVersion", OLD."Provider", OLD."Model", OLD."InputCharacters",
                           OLD."InputHash", OLD."InjectionDetected", OLD."EstimatedInputTokens",
                           OLD."ReservedTokens", OLD."CreatedOn") THEN
                        RAISE EXCEPTION 'AI request identity and reservation fields are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF (OLD."Status" = 'Reserved' AND NEW."Status" NOT IN ('Reserved', 'Running', 'Succeeded', 'Failed', 'Unknown'))
                       OR (OLD."Status" = 'Running' AND NEW."Status" NOT IN ('Running', 'Succeeded', 'Failed', 'Unknown'))
                       OR OLD."Status" IN ('Succeeded', 'Denied', 'Failed', 'Unknown') THEN
                        RAISE EXCEPTION 'Invalid AI request status transition' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Status" IN ('Reserved', 'Running')
                       AND ROW(NEW."OutputCharacters", NEW."OutputHash", NEW."InputTokens", NEW."OutputTokens",
                               NEW."TokenSource", NEW."ErrorCode", NEW."CompletedOn")
                           IS DISTINCT FROM
                           ROW(OLD."OutputCharacters", OLD."OutputHash", OLD."InputTokens", OLD."OutputTokens",
                               OLD."TokenSource", OLD."ErrorCode", OLD."CompletedOn") THEN
                        RAISE EXCEPTION 'AI usage can only be finalized with a terminal state' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_guard_ai_request_update() FROM PUBLIC;
                CREATE TRIGGER ai_requests_guard_update
                    BEFORE UPDATE ON public."AiRequests"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_ai_request_update();
                """);
        }
    }
}

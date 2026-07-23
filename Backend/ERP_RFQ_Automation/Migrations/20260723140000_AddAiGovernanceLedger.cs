using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddAiGovernanceLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiBudgetPeriods",
                columns: table => new
                {
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SoftTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    HardTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    ReservedTokens = table.Column<long>(type: "bigint", nullable: false),
                    SettledTokens = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBudgetPeriods", x => new { x.BusinessUnitId, x.PeriodStartUtc });
                    table.ForeignKey(
                        name: "FK_AiBudgetPeriods_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiProcessingPolicies",
                columns: table => new
                {
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalProcessingAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedPurposes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AllowedProvider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AllowedModel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MonthlySoftTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    MonthlyHardTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProcessingPolicies", x => x.BusinessUnitId);
                    table.ForeignKey(
                        name: "FK_AiProcessingPolicies_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PromptHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InputCharacters = table.Column<int>(type: "integer", nullable: false),
                    OutputCharacters = table.Column<int>(type: "integer", nullable: false),
                    InputHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    OutputHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    InjectionDetected = table.Column<bool>(type: "boolean", nullable: false),
                    EstimatedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ReservedTokens = table.Column<long>(type: "bigint", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TokenSource = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    StartedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiRequests", x => x.Id);
                    table.UniqueConstraint("AK_AiRequests_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_AiRequests_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiCallAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    ProviderRequestId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TokenSource = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LatencyMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    ProviderDurationNanoseconds = table.Column<long>(type: "bigint", nullable: true),
                    ResponseHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StartedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCallAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiCallAttempts_AiRequests_BusinessUnitId_RequestId",
                        columns: x => new { x.BusinessUnitId, x.RequestId },
                        principalTable: "AiRequests",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCallAttempts_BU_StartedOn",
                table: "AiCallAttempts",
                columns: new[] { "BusinessUnitId", "StartedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_AiCallAttempts_BusinessUnitId_RequestId",
                table: "AiCallAttempts",
                columns: new[] { "BusinessUnitId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "UX_AiCallAttempts_Request_Attempt",
                table: "AiCallAttempts",
                columns: new[] { "RequestId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_BU_CreatedOn",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_AiRequests_BU_IdempotencyKey",
                table: "AiRequests",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."AiProcessingPolicies"
                    ADD CONSTRAINT "CK_AiProcessingPolicies_TokenLimits"
                    CHECK (("MonthlySoftTokenLimit" IS NULL OR "MonthlySoftTokenLimit" >= 0)
                       AND ("MonthlyHardTokenLimit" IS NULL OR "MonthlyHardTokenLimit" >= 0)
                       AND ("MonthlySoftTokenLimit" IS NULL OR "MonthlyHardTokenLimit" IS NULL
                            OR "MonthlySoftTokenLimit" <= "MonthlyHardTokenLimit"));
                ALTER TABLE public."AiRequests"
                    ADD CONSTRAINT "CK_AiRequests_UsageNonNegative"
                    CHECK ("InputCharacters" >= 0 AND "OutputCharacters" >= 0
                       AND "EstimatedInputTokens" >= 0 AND "ReservedTokens" >= 0
                       AND "InputTokens" >= 0 AND "OutputTokens" >= 0);
                ALTER TABLE public."AiRequests"
                    ADD CONSTRAINT "CK_AiRequests_Status"
                    CHECK ("Status" IN ('Reserved', 'Running', 'Succeeded', 'Denied', 'Failed', 'Unknown')),
                    ADD CONSTRAINT "CK_AiRequests_TokenSource"
                    CHECK ("TokenSource" IN ('ProviderExact', 'ProviderApproximate', 'Estimated')),
                    ADD CONSTRAINT "CK_AiRequests_HashShape"
                    CHECK ("PromptHash" ~ '^[0-9A-F]{64}$'
                       AND ("InputHash" IS NULL OR "InputHash" ~ '^[0-9A-F]{64}$')
                       AND ("OutputHash" IS NULL OR "OutputHash" ~ '^[0-9A-F]{64}$')),
                    ADD CONSTRAINT "CK_AiRequests_Timestamps"
                    CHECK (("StartedOn" IS NULL OR "StartedOn" >= "CreatedOn")
                       AND ("CompletedOn" IS NULL OR "CompletedOn" >= "CreatedOn")
                       AND ("CompletedOn" IS NULL OR "StartedOn" IS NULL OR "CompletedOn" >= "StartedOn")
                       AND ("Status" NOT IN ('Succeeded', 'Denied', 'Failed', 'Unknown') OR "CompletedOn" IS NOT NULL));
                ALTER TABLE public."AiCallAttempts"
                    ADD CONSTRAINT "CK_AiCallAttempts_UsageNonNegative"
                    CHECK ("AttemptNumber" > 0 AND "InputTokens" >= 0 AND "OutputTokens" >= 0
                       AND "LatencyMilliseconds" >= 0),
                    ADD CONSTRAINT "CK_AiCallAttempts_Status"
                    CHECK ("Status" IN ('Succeeded', 'Failed', 'Unknown')),
                    ADD CONSTRAINT "CK_AiCallAttempts_TokenSource"
                    CHECK ("TokenSource" IN ('ProviderExact', 'ProviderApproximate', 'Estimated')),
                    ADD CONSTRAINT "CK_AiCallAttempts_HashShape"
                    CHECK ("ResponseHash" IS NULL OR "ResponseHash" ~ '^[0-9A-F]{64}$'),
                    ADD CONSTRAINT "CK_AiCallAttempts_Timestamps"
                    CHECK ("CompletedOn" >= "StartedOn");
                ALTER TABLE public."AiBudgetPeriods"
                    ADD CONSTRAINT "CK_AiBudgetPeriods_UsageNonNegative"
                    CHECK ("ReservedTokens" >= 0 AND "SettledTokens" >= 0
                       AND ("SoftTokenLimit" IS NULL OR "SoftTokenLimit" >= 0)
                       AND ("HardTokenLimit" IS NULL OR "HardTokenLimit" >= 0)
                       AND ("SoftTokenLimit" IS NULL OR "HardTokenLimit" IS NULL
                            OR "SoftTokenLimit" <= "HardTokenLimit"));

                CREATE UNIQUE INDEX "UX_AiCallAttempts_ProviderRequestId"
                    ON public."AiCallAttempts" ("Provider", "ProviderRequestId")
                    WHERE "ProviderRequestId" IS NOT NULL;

                INSERT INTO public."AiProcessingPolicies"
                    ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                     "Version", "UpdatedOn", "UpdatedBy")
                SELECT "ID", TRUE, FALSE, 'RfqExtraction,BoqDraft', 1, now(), 'migration-fail-closed'
                FROM public."BusinessUnits"
                ON CONFLICT ("BusinessUnitId") DO NOTHING;

                DO $role$
                BEGIN
                    EXECUTE format('ALTER ROLE %I NOINHERIT', current_user);
                END
                $role$;

                CREATE OR REPLACE FUNCTION public.nexora_create_default_ai_policy()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                BEGIN
                    INSERT INTO public."AiProcessingPolicies"
                        ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                         "Version", "UpdatedOn", "UpdatedBy")
                    VALUES (NEW."ID", TRUE, FALSE, 'RfqExtraction,BoqDraft', 1, now(), 'tenant-provisioning')
                    ON CONFLICT ("BusinessUnitId") DO NOTHING;
                    RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_create_default_ai_policy() FROM PUBLIC;
                CREATE TRIGGER business_units_create_ai_policy
                    AFTER INSERT ON public."BusinessUnits"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_create_default_ai_policy();

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'AiProcessingPolicies', 'AiRequests', 'AiCallAttempts', 'AiBudgetPeriods']
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', table_name);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
                            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            table_name);
                    END LOOP;
                END
                $block$;

                CREATE POLICY nexora_ai_default_provisioning ON public."AiProcessingPolicies"
                    FOR INSERT TO PUBLIC
                    WITH CHECK (
                        "IsEnabled" = TRUE
                        AND "ExternalProcessingAllowed" = FALSE
                        AND "AllowedPurposes" = 'RfqExtraction,BoqDraft'
                        AND "AllowedProvider" IS NULL
                        AND "AllowedModel" IS NULL
                        AND "MonthlySoftTokenLimit" IS NULL
                        AND "MonthlyHardTokenLimit" IS NULL
                        AND "Version" = 1
                        AND "UpdatedBy" = 'tenant-provisioning');

                CREATE OR REPLACE FUNCTION public.nexora_ai_policy_audit_allowed(
                    tenant_id bigint, action_name text, target_type text, target_id text)
                RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, public, platform AS $function$
                    SELECT action_name = 'tenant.ai-policy.update'
                       AND target_type = 'AiProcessingPolicy'
                       AND target_id = NULLIF(current_setting('nexora.business_unit_id', true), '')
                       AND EXISTS (
                           SELECT 1 FROM platform."Tenants" tenant
                           WHERE tenant."Id" = tenant_id
                             AND tenant."PrimaryBusinessUnitId" =
                                 NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_ai_policy_audit_allowed(bigint, text, text, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_ai_policy_audit_allowed(bigint, text, text, text)
                    TO nexora_tenant_app;

                ALTER TABLE platform."PlatformAuditLogs" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_ai_policy_audit_insert ON platform."PlatformAuditLogs";
                CREATE POLICY nexora_ai_policy_audit_insert ON platform."PlatformAuditLogs"
                    FOR INSERT TO nexora_tenant_app
                    WITH CHECK (public.nexora_ai_policy_audit_allowed(
                        "ActAsTenantId", "Action", "TargetType", "TargetId"));
                GRANT USAGE ON SCHEMA platform TO nexora_tenant_app;
                GRANT INSERT ON TABLE platform."PlatformAuditLogs" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE platform."PlatformAuditLogs_Id_seq" TO nexora_tenant_app;

                REVOKE ALL PRIVILEGES ON TABLE public."AiProcessingPolicies", public."AiRequests",
                    public."AiCallAttempts", public."AiBudgetPeriods" FROM nexora_tenant_app;
                GRANT SELECT, UPDATE ON TABLE public."AiProcessingPolicies" TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."AiRequests" TO nexora_tenant_app;
                GRANT SELECT, INSERT ON TABLE public."AiCallAttempts" TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."AiBudgetPeriods" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."AiCallAttempts_Id_seq" TO nexora_tenant_app;

                CREATE INDEX "IX_AiRequests_Unresolved_CreatedOn"
                    ON public."AiRequests" ("CreatedOn")
                    WHERE "CompletedOn" IS NULL AND "Status" IN ('Reserved', 'Running');

                CREATE OR REPLACE FUNCTION public.nexora_reject_ai_ledger_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION 'AI accounting history is immutable' USING ERRCODE = '55000';
                END
                $function$;
                REVOKE ALL ON FUNCTION public.nexora_reject_ai_ledger_mutation() FROM PUBLIC;

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

                CREATE TRIGGER ai_call_attempts_immutable
                    BEFORE UPDATE OR DELETE ON public."AiCallAttempts"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
                CREATE TRIGGER ai_call_attempts_reject_truncate
                    BEFORE TRUNCATE ON public."AiCallAttempts"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
                CREATE TRIGGER ai_requests_reject_delete
                    BEFORE DELETE ON public."AiRequests"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
                CREATE TRIGGER ai_requests_reject_truncate
                    BEFORE TRUNCATE ON public."AiRequests"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
                CREATE TRIGGER ai_requests_guard_update
                    BEFORE UPDATE ON public."AiRequests"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_ai_request_update();
                CREATE TRIGGER platform_ai_policy_audits_immutable
                    BEFORE UPDATE OR DELETE ON platform."PlatformAuditLogs"
                    FOR EACH ROW
                    WHEN (OLD."Action" = 'tenant.ai-policy.update')
                    EXECUTE FUNCTION public.nexora_reject_ai_ledger_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS ai_call_attempts_immutable ON public."AiCallAttempts";
                DROP TRIGGER IF EXISTS ai_call_attempts_reject_truncate ON public."AiCallAttempts";
                DROP TRIGGER IF EXISTS ai_requests_reject_delete ON public."AiRequests";
                DROP TRIGGER IF EXISTS ai_requests_reject_truncate ON public."AiRequests";
                DROP TRIGGER IF EXISTS ai_requests_guard_update ON public."AiRequests";
                DROP TRIGGER IF EXISTS platform_ai_policy_audits_immutable ON platform."PlatformAuditLogs";
                DROP TRIGGER IF EXISTS business_units_create_ai_policy ON public."BusinessUnits";
                DROP POLICY IF EXISTS nexora_ai_policy_audit_insert ON platform."PlatformAuditLogs";
                REVOKE INSERT ON TABLE platform."PlatformAuditLogs" FROM nexora_tenant_app;
                REVOKE USAGE ON SEQUENCE platform."PlatformAuditLogs_Id_seq" FROM nexora_tenant_app;
                DROP FUNCTION IF EXISTS public.nexora_ai_policy_audit_allowed(bigint, text, text, text);
                DROP FUNCTION IF EXISTS public.nexora_reject_ai_ledger_mutation();
                DROP FUNCTION IF EXISTS public.nexora_guard_ai_request_update();
                DROP FUNCTION IF EXISTS public.nexora_create_default_ai_policy();
                """);

            migrationBuilder.DropTable(
                name: "AiBudgetPeriods");

            migrationBuilder.DropTable(
                name: "AiCallAttempts");

            migrationBuilder.DropTable(
                name: "AiProcessingPolicies");

            migrationBuilder.DropTable(
                name: "AiRequests");
        }
    }
}

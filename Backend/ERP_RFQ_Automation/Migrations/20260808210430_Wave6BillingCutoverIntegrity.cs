using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Wave6BillingCutoverIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReadinessManifestJson",
                schema: "platform",
                table: "BillingStatements",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "ReadinessManifestSha256",
                schema: "platform",
                table: "BillingStatements",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "0000000000000000000000000000000000000000000000000000000000000000");

            migrationBuilder.AddColumn<string>(
                name: "ReadinessStatus",
                schema: "platform",
                table: "BillingStatements",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Blocked");

            migrationBuilder.CreateTable(
                name: "TenantMeterSourcePolicies",
                schema: "platform",
                columns: table => new
                {
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    MeterKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProposedEffectiveAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CutoverAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProposedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProposedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMeterSourcePolicies", x => new { x.TenantId, x.MeterKey });
                    table.ForeignKey(
                        name: "FK_TenantMeterSourcePolicies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsageCoverageSegments",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    MeterKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AuthoritativeSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Completeness = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    QuantityTotal = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    AllowanceAppliedTotal = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    OverageQuantityTotal = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    RatedAmountTotal = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RateLineageJson = table.Column<string>(type: "jsonb", nullable: false),
                    RateLineageSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CompletenessWatermarkUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CutoverAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReconciliationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CounterpartEventCount = table.Column<int>(type: "integer", nullable: true),
                    CounterpartQuantityTotal = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: true),
                    CounterpartEvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageCoverageSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageCoverageSegments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsageEventRatings",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    UsageEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContractId = table.Column<long>(type: "bigint", nullable: true),
                    PlanId = table.Column<long>(type: "bigint", nullable: true),
                    RateCardId = table.Column<long>(type: "bigint", nullable: true),
                    RateCardLineId = table.Column<long>(type: "bigint", nullable: true),
                    RateCardVersion = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AllowanceApplied = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    OverageQuantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    RatedAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageEventRatings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageEventRatings_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "platform",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageEventRatings_RateCardLines_RateCardLineId",
                        column: x => x.RateCardLineId,
                        principalSchema: "platform",
                        principalTable: "RateCardLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageEventRatings_RateCards_RateCardId",
                        column: x => x.RateCardId,
                        principalSchema: "platform",
                        principalTable: "RateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageEventRatings_UsageEvents_TenantId_UsageEventId",
                        columns: x => new { x.TenantId, x.UsageEventId },
                        principalSchema: "platform",
                        principalTable: "UsageEvents",
                        principalColumns: new[] { "TenantId", "UsageEventId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_UsageCoverageSegments_Tenant_Meter_Range",
                schema: "platform",
                table: "UsageCoverageSegments",
                columns: new[] { "TenantId", "MeterKey", "StartUtc", "EndUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageEventRatings_PlanId",
                schema: "platform",
                table: "UsageEventRatings",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageEventRatings_RateCardId",
                schema: "platform",
                table: "UsageEventRatings",
                column: "RateCardId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageEventRatings_RateCardLineId",
                schema: "platform",
                table: "UsageEventRatings",
                column: "RateCardLineId");

            migrationBuilder.CreateIndex(
                name: "UX_UsageEventRatings_Event_Attempt",
                schema: "platform",
                table: "UsageEventRatings",
                columns: new[] { "TenantId", "UsageEventId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_UsageEventRatings_Tenant_Idempotency",
                schema: "platform",
                table: "UsageEventRatings",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS btree_gist;

                ALTER TABLE platform."TenantMeterSourcePolicies"
                    ADD CONSTRAINT "CK_TenantMeterSourcePolicies_Mode" CHECK ("Mode" IN
                        ('LegacyAuthoritative','CanonicalShadow','CanonicalAuthoritative','BillingBlocked')),
                    ADD CONSTRAINT "CK_TenantMeterSourcePolicies_Approval" CHECK (
                        ("ApprovedAtUtc" IS NULL AND "ApprovedBy" IS NULL)
                        OR ("ApprovedAtUtc" IS NOT NULL AND "ApprovedBy" IS NOT NULL
                            AND "ProposedBy" IS NOT NULL AND "ApprovedBy"<>"ProposedBy"
                            AND "ApprovalReason" IS NOT NULL));
                ALTER TABLE platform."UsageCoverageSegments"
                    ADD CONSTRAINT "CK_UsageCoverageSegments_Range" CHECK ("StartUtc"<"EndUtc"),
                    ADD CONSTRAINT "CK_UsageCoverageSegments_Source" CHECK ("AuthoritativeSource" IN ('Legacy','Canonical')),
                    ADD CONSTRAINT "CK_UsageCoverageSegments_Completeness" CHECK ("Completeness" IN ('Pending','Complete','Incomplete','Unknown')),
                    ADD CONSTRAINT "CK_UsageCoverageSegments_Reconciliation" CHECK ("ReconciliationStatus" IN
                        ('Pending','Matched','WithinApprovedTolerance','Mismatch','NotApplicable')),
                    ADD CONSTRAINT "CK_UsageCoverageSegments_Counts" CHECK (
                        "EventCount">=0 AND "QuantityTotal">=0 AND "AllowanceAppliedTotal">=0
                        AND "OverageQuantityTotal">=0 AND "RatedAmountTotal">=0),
                    ADD CONSTRAINT "CK_UsageCoverageSegments_Hashes" CHECK (
                        "EvidenceSha256" ~ '^[0-9a-f]{64}$' AND "RateLineageSha256" ~ '^[0-9a-f]{64}$'
                        AND ("CounterpartEvidenceSha256" IS NULL OR "CounterpartEvidenceSha256" ~ '^[0-9a-f]{64}$')),
                    ADD CONSTRAINT "EX_UsageCoverageSegments_NoAuthoritativeOverlap" EXCLUDE USING gist
                        ("TenantId" WITH =,"MeterKey" WITH =,tstzrange("StartUtc","EndUtc",'[)') WITH &&)
                        WHERE ("Completeness"='Complete');
                ALTER TABLE platform."UsageEventRatings"
                    ADD CONSTRAINT "CK_UsageEventRatings_Status" CHECK ("Status" IN
                        ('Rated','RatedZeroWithReason','ExcludedWithReason','Unrated','RatingFailed')),
                    ADD CONSTRAINT "CK_UsageEventRatings_Attempt" CHECK ("AttemptNumber">0),
                    ADD CONSTRAINT "CK_UsageEventRatings_Hash" CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT "CK_UsageEventRatings_Result" CHECK (
                        ("Status"='Rated' AND "ReasonCode" IS NULL AND "RateCardId" IS NOT NULL
                            AND "RateCardLineId" IS NOT NULL AND "RateCardVersion" IS NOT NULL
                            AND "UnitPrice" IS NOT NULL AND "RatedAmount" IS NOT NULL AND "RatedAmount"<>0)
                        OR ("Status"='RatedZeroWithReason' AND "ReasonCode" IS NOT NULL
                            AND "RatedAmount"=0)
                        OR ("Status" IN ('ExcludedWithReason','Unrated','RatingFailed')
                            AND "ReasonCode" IS NOT NULL AND "RatedAmount" IS NULL));

                CREATE TRIGGER usage_coverage_segments_immutable
                    BEFORE UPDATE OR DELETE ON platform."UsageCoverageSegments"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                ALTER TABLE platform."UsageCoverageSegments" ENABLE ALWAYS TRIGGER usage_coverage_segments_immutable;
                CREATE TRIGGER usage_event_ratings_immutable
                    BEFORE UPDATE OR DELETE ON platform."UsageEventRatings"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                ALTER TABLE platform."UsageEventRatings" ENABLE ALWAYS TRIGGER usage_event_ratings_immutable;

                ALTER TABLE platform."TenantMeterSourcePolicies" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform."TenantMeterSourcePolicies" FORCE ROW LEVEL SECURITY;
                ALTER TABLE platform."UsageCoverageSegments" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform."UsageCoverageSegments" FORCE ROW LEVEL SECURITY;
                ALTER TABLE platform."UsageEventRatings" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform."UsageEventRatings" FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_meter_source_policies_platform_fleet ON platform."TenantMeterSourcePolicies"
                    FOR ALL TO nexora_pipeline_app USING (true) WITH CHECK (true);
                CREATE POLICY usage_coverage_segments_platform_fleet ON platform."UsageCoverageSegments"
                    FOR ALL TO nexora_pipeline_app USING (true) WITH CHECK (true);
                CREATE POLICY usage_event_ratings_platform_fleet ON platform."UsageEventRatings"
                    FOR ALL TO nexora_pipeline_app USING (true) WITH CHECK (true);

                REVOKE ALL ON TABLE platform."TenantMeterSourcePolicies",platform."UsageCoverageSegments",
                    platform."UsageEventRatings" FROM PUBLIC;
                DO $roles$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='nexora_pipeline_app') THEN
                        GRANT SELECT,INSERT,UPDATE ON platform."TenantMeterSourcePolicies" TO nexora_pipeline_app;
                        GRANT SELECT,INSERT ON platform."UsageCoverageSegments",platform."UsageEventRatings" TO nexora_pipeline_app;
                        GRANT USAGE,SELECT ON SEQUENCE platform."UsageCoverageSegments_Id_seq",
                            platform."UsageEventRatings_Id_seq" TO nexora_pipeline_app;
                        REVOKE DELETE,TRUNCATE ON platform."TenantMeterSourcePolicies",platform."UsageCoverageSegments",
                            platform."UsageEventRatings" FROM nexora_pipeline_app;
                        REVOKE UPDATE ON platform."UsageCoverageSegments",platform."UsageEventRatings" FROM nexora_pipeline_app;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='nexora_tenant_app') THEN
                        REVOKE ALL ON platform."TenantMeterSourcePolicies",platform."UsageCoverageSegments",
                            platform."UsageEventRatings" FROM nexora_tenant_app;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='nexora_identity_app') THEN
                        REVOKE ALL ON platform."TenantMeterSourcePolicies",platform."UsageCoverageSegments",
                            platform."UsageEventRatings" FROM nexora_identity_app;
                    END IF;
                END $roles$;

                INSERT INTO platform."TenantMeterSourcePolicies" ("TenantId","MeterKey","Mode","Version")
                SELECT t."Id",m.meter_key,m.mode,1
                  FROM platform."Tenants" t
                  CROSS JOIN (VALUES
                    ('base.subscription','LegacyAuthoritative'),('documents','LegacyAuthoritative'),
                    ('ai.tokens.external','LegacyAuthoritative'),('seats','LegacyAuthoritative'),
                    ('processing.minutes','BillingBlocked'),('pages.processed','BillingBlocked'),
                    ('rfqs','BillingBlocked'),('quotes','BillingBlocked'),('orders','BillingBlocked'),
                    ('emails','BillingBlocked'),('pages.ocr','BillingBlocked'),('api.calls','BillingBlocked'),
                    ('storage.gb','BillingBlocked'),('supplier.searches','BillingBlocked'),
                    ('automation.runs','BillingBlocked'),('dedicated.infrastructure','BillingBlocked')) AS m(meter_key,mode)
                ON CONFLICT ("TenantId","MeterKey") DO NOTHING;

                CREATE OR REPLACE FUNCTION platform.nexora_seed_tenant_meter_source_policies()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,platform AS $seed$
                BEGIN
                    INSERT INTO platform."TenantMeterSourcePolicies" ("TenantId","MeterKey","Mode","Version")
                    SELECT NEW."Id",m.meter_key,m.mode,1
                      FROM (VALUES
                        ('base.subscription','LegacyAuthoritative'),('documents','LegacyAuthoritative'),
                        ('ai.tokens.external','LegacyAuthoritative'),('seats','LegacyAuthoritative'),
                        ('processing.minutes','BillingBlocked'),('pages.processed','BillingBlocked'),
                        ('rfqs','BillingBlocked'),('quotes','BillingBlocked'),('orders','BillingBlocked'),
                        ('emails','BillingBlocked'),('pages.ocr','BillingBlocked'),('api.calls','BillingBlocked'),
                        ('storage.gb','BillingBlocked'),('supplier.searches','BillingBlocked'),
                        ('automation.runs','BillingBlocked'),('dedicated.infrastructure','BillingBlocked')) AS m(meter_key,mode)
                    ON CONFLICT ("TenantId","MeterKey") DO NOTHING;
                    RETURN NEW;
                END $seed$;
                REVOKE ALL ON FUNCTION platform.nexora_seed_tenant_meter_source_policies() FROM PUBLIC;
                CREATE TRIGGER tenants_seed_meter_source_policies
                    AFTER INSERT ON platform."Tenants" FOR EACH ROW
                    EXECUTE FUNCTION platform.nexora_seed_tenant_meter_source_policies();
                ALTER TABLE platform."Tenants"
                    ENABLE ALWAYS TRIGGER tenants_seed_meter_source_policies;

                CREATE OR REPLACE FUNCTION platform.nexora_guard_usage_event_insert()
                RETURNS trigger LANGUAGE plpgsql AS $guard$
                DECLARE
                    original platform."UsageEvents"%ROWTYPE;
                    prior_quantity numeric; prior_cost numeric; prior_rated numeric;
                    card platform."RateCards"%ROWTYPE; line platform."RateCardLines"%ROWTYPE;
                    expected_meter text; priced_divisor numeric;
                BEGIN
                    IF NEW."Kind"='Consumption' THEN
                        IF NEW."OverageQuantity"<>GREATEST(NEW."Quantity"-NEW."AllowanceApplied",0) THEN
                            RAISE EXCEPTION 'usage overage does not reconcile';
                        END IF;
                    ELSE
                        SELECT * INTO original FROM platform."UsageEvents"
                         WHERE "TenantId"=NEW."TenantId" AND "UsageEventId"=NEW."AdjustsUsageEventId" FOR KEY SHARE;
                        IF NOT FOUND OR original."Kind"<>'Consumption' THEN RAISE EXCEPTION 'adjustment must reference same-tenant consumption'; END IF;
                        IF NEW."EventType"<>original."EventType" OR NEW."Unit"<>original."Unit" OR NEW."Currency"<>original."Currency"
                           OR NEW."RateCardId" IS DISTINCT FROM original."RateCardId" OR NEW."RateCardLineId" IS DISTINCT FROM original."RateCardLineId"
                           OR NEW."RateCardVersion" IS DISTINCT FROM original."RateCardVersion" OR NEW."UnitPrice" IS DISTINCT FROM original."UnitPrice"
                           OR NEW."RatingStatus"<>original."RatingStatus" OR NEW."AllowanceApplied"<>0 OR NEW."OverageQuantity"<>NEW."Quantity" THEN
                            RAISE EXCEPTION 'adjustment lineage does not match original usage';
                        END IF;
                        SELECT COALESCE(SUM("Quantity"),0),COALESCE(SUM("CostAmount"),0),COALESCE(SUM("RatedAmount"),0)
                          INTO prior_quantity,prior_cost,prior_rated FROM platform."UsageEvents"
                         WHERE "TenantId"=NEW."TenantId" AND "AdjustsUsageEventId"=NEW."AdjustsUsageEventId";
                        IF original."Quantity"+prior_quantity+NEW."Quantity"<0 OR original."CostAmount"+prior_cost+NEW."CostAmount"<0
                           OR (original."RatedAmount" IS NOT NULL AND original."RatedAmount"+prior_rated+COALESCE(NEW."RatedAmount",0)<0) THEN
                            RAISE EXCEPTION 'cumulative adjustment exceeds original usage';
                        END IF;
                    END IF;
                    IF NEW."RatingStatus"='Rated' THEN
                        IF NEW."RateCardId" IS NULL OR NEW."RateCardLineId" IS NULL OR NEW."RateCardVersion" IS NULL OR NEW."UnitPrice" IS NULL THEN
                            RAISE EXCEPTION 'rated usage requires complete rate-card lineage';
                        END IF;
                        SELECT * INTO card FROM platform."RateCards" WHERE "Id"=NEW."RateCardId";
                        SELECT * INTO line FROM platform."RateCardLines" WHERE "Id"=NEW."RateCardLineId";
                        expected_meter:=CASE NEW."EventType" WHEN 'ai.tokens' THEN 'ai.tokens.external' WHEN 'storage.gb-hours' THEN 'storage.gb' ELSE NEW."EventType" END;
                        priced_divisor:=CASE NEW."EventType" WHEN 'ai.tokens' THEN 1000 WHEN 'storage.gb-hours' THEN 1073741824 ELSE 1 END;
                        IF card."Id" IS NULL OR line."Id" IS NULL OR line."RateCardId"<>card."Id" OR line."MeterKey"<>expected_meter
                           OR card."Version"<>NEW."RateCardVersion" OR card."Currency"<>NEW."Currency" OR NOT card."IsActive"
                           OR card."EffectiveFromUtc">(NEW."OccurredAtUtc" AT TIME ZONE 'UTC')
                           OR (card."EffectiveToUtc" IS NOT NULL AND card."EffectiveToUtc"<=(NEW."OccurredAtUtc" AT TIME ZONE 'UTC'))
                           OR line."UnitPrice"<>NEW."UnitPrice" OR NEW."AllowanceApplied">line."IncludedQuantity"
                           OR NEW."RatedAmount" IS DISTINCT FROM ROUND(NEW."OverageQuantity"*NEW."UnitPrice"/priced_divisor,6) THEN
                            RAISE EXCEPTION 'rated usage does not match the effective rate-card line';
                        END IF;
                    ELSIF NEW."RatedAmount" IS NOT NULL THEN
                        RAISE EXCEPTION 'unrated usage cannot carry a rated amount';
                    END IF;
                    RETURN NEW;
                END $guard$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_usage_event_insert() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the exact pre-cutover insert guard before removing the governance tables.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform.nexora_guard_usage_event_insert()
                RETURNS trigger LANGUAGE plpgsql AS $guard$
                DECLARE
                    original platform."UsageEvents"%ROWTYPE;
                    prior_quantity numeric; prior_cost numeric; prior_rated numeric;
                    card platform."RateCards"%ROWTYPE; line platform."RateCardLines"%ROWTYPE;
                    expected_meter text;
                BEGIN
                    IF NEW."Kind"='Consumption' THEN
                        IF NEW."OverageQuantity"<>GREATEST(NEW."Quantity"-NEW."AllowanceApplied",0) THEN
                            RAISE EXCEPTION 'usage overage does not reconcile';
                        END IF;
                    ELSE
                        SELECT * INTO original FROM platform."UsageEvents"
                         WHERE "TenantId"=NEW."TenantId" AND "UsageEventId"=NEW."AdjustsUsageEventId" FOR KEY SHARE;
                        IF NOT FOUND OR original."Kind"<>'Consumption' THEN RAISE EXCEPTION 'adjustment must reference same-tenant consumption'; END IF;
                        IF NEW."EventType"<>original."EventType" OR NEW."Unit"<>original."Unit" OR NEW."Currency"<>original."Currency"
                           OR NEW."RateCardId" IS DISTINCT FROM original."RateCardId" OR NEW."RateCardLineId" IS DISTINCT FROM original."RateCardLineId"
                           OR NEW."RateCardVersion" IS DISTINCT FROM original."RateCardVersion" OR NEW."UnitPrice" IS DISTINCT FROM original."UnitPrice"
                           OR NEW."RatingStatus"<>original."RatingStatus" OR NEW."AllowanceApplied"<>0 OR NEW."OverageQuantity"<>NEW."Quantity" THEN
                            RAISE EXCEPTION 'adjustment lineage does not match original usage';
                        END IF;
                        SELECT COALESCE(SUM("Quantity"),0),COALESCE(SUM("CostAmount"),0),COALESCE(SUM("RatedAmount"),0)
                          INTO prior_quantity,prior_cost,prior_rated FROM platform."UsageEvents"
                         WHERE "TenantId"=NEW."TenantId" AND "AdjustsUsageEventId"=NEW."AdjustsUsageEventId";
                        IF original."Quantity"+prior_quantity+NEW."Quantity"<0 OR original."CostAmount"+prior_cost+NEW."CostAmount"<0
                           OR (original."RatedAmount" IS NOT NULL AND original."RatedAmount"+prior_rated+COALESCE(NEW."RatedAmount",0)<0) THEN
                            RAISE EXCEPTION 'cumulative adjustment exceeds original usage';
                        END IF;
                    END IF;
                    IF NEW."RatingStatus"='Rated' THEN
                        IF NEW."RateCardId" IS NULL OR NEW."RateCardLineId" IS NULL OR NEW."RateCardVersion" IS NULL OR NEW."UnitPrice" IS NULL THEN
                            RAISE EXCEPTION 'rated usage requires complete rate-card lineage';
                        END IF;
                        SELECT * INTO card FROM platform."RateCards" WHERE "Id"=NEW."RateCardId";
                        SELECT * INTO line FROM platform."RateCardLines" WHERE "Id"=NEW."RateCardLineId";
                        expected_meter:=CASE NEW."EventType" WHEN 'ai.tokens' THEN 'ai.tokens.external' WHEN 'storage.gb-hours' THEN 'storage.gb' ELSE NEW."EventType" END;
                        IF card."Id" IS NULL OR line."Id" IS NULL OR line."RateCardId"<>card."Id" OR line."MeterKey"<>expected_meter
                           OR card."Version"<>NEW."RateCardVersion" OR card."Currency"<>NEW."Currency" OR NOT card."IsActive"
                           OR card."EffectiveFromUtc">(NEW."OccurredAtUtc" AT TIME ZONE 'UTC')
                           OR (card."EffectiveToUtc" IS NOT NULL AND card."EffectiveToUtc"<=(NEW."OccurredAtUtc" AT TIME ZONE 'UTC'))
                           OR line."UnitPrice"<>NEW."UnitPrice" OR NEW."AllowanceApplied">line."IncludedQuantity"
                           OR NEW."RatedAmount" IS DISTINCT FROM ROUND(NEW."OverageQuantity"*NEW."UnitPrice",6) THEN
                            RAISE EXCEPTION 'rated usage does not match the effective rate-card line';
                        END IF;
                    ELSIF NEW."RatedAmount" IS NOT NULL THEN
                        RAISE EXCEPTION 'unrated usage cannot carry a rated amount';
                    END IF;
                    RETURN NEW;
                END $guard$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_usage_event_insert() FROM PUBLIC;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tenants_seed_meter_source_policies ON platform."Tenants";
                DROP FUNCTION IF EXISTS platform.nexora_seed_tenant_meter_source_policies();
                """);

            migrationBuilder.DropTable(
                name: "TenantMeterSourcePolicies",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UsageCoverageSegments",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UsageEventRatings",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "ReadinessManifestJson",
                schema: "platform",
                table: "BillingStatements");

            migrationBuilder.DropColumn(
                name: "ReadinessManifestSha256",
                schema: "platform",
                table: "BillingStatements");

            migrationBuilder.DropColumn(
                name: "ReadinessStatus",
                schema: "platform",
                table: "BillingStatements");
        }
    }
}

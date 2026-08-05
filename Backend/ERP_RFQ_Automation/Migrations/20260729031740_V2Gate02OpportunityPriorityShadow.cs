using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V2Gate02OpportunityPriorityShadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commercial_opportunity_recommendations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: false),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    LeadVersion = table.Column<long>(type: "bigint", nullable: false),
                    RecommendationKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    FeatureSchemaVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    EvidenceCutoffAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EvidenceSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PriorityScore = table.Column<int>(type: "integer", nullable: false),
                    PriorityBand = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    Completeness = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    RecommendedActionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RecommendedActionLabel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RationaleJson = table.Column<string>(type: "jsonb", nullable: false),
                    CohortKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SupersedesRecommendationId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_opportunity_recommendations", x => x.Id);
                    table.UniqueConstraint("AK_commercial_opportunity_recommendations_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_opportunity_recommendations_Completeness", "\"Completeness\" BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_opportunity_recommendations_Confidence", "\"Confidence\" BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_opportunity_recommendations_EvidenceHash", "length(\"EvidenceHash\") = 64");
                    table.CheckConstraint("CK_opportunity_recommendations_Generated", "\"GeneratedAtUtc\" >= \"EvidenceCutoffAtUtc\"");
                    table.CheckConstraint("CK_opportunity_recommendations_Mode", "\"Mode\" = 'Shadow'");
                    table.CheckConstraint("CK_opportunity_recommendations_SampleSize", "\"SampleSize\" >= 0");
                    table.CheckConstraint("CK_opportunity_recommendations_Score", "\"PriorityScore\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_recommendations_CommercialCases_Busi~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId, x.NexoraSerial },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id", "MasterReference" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_recommendations_Leads_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.LeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_recommendations_commercial_opportuni~",
                        columns: x => new { x.BusinessUnitId, x.SupersedesRecommendationId },
                        principalTable: "commercial_opportunity_recommendations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_opportunity_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OpportunityRecommendationId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_opportunity_events", x => x.Id);
                    table.UniqueConstraint("AK_commercial_opportunity_events_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_opportunity_events_RequestHash", "length(\"RequestHash\") = 64");
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_events_commercial_opportunity_recomm~",
                        columns: x => new { x.BusinessUnitId, x.OpportunityRecommendationId },
                        principalTable: "commercial_opportunity_recommendations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_opportunity_feedback",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OpportunityRecommendationId = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReplacementActionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SupersedesFeedbackId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_opportunity_feedback", x => x.Id);
                    table.UniqueConstraint("AK_commercial_opportunity_feedback_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_opportunity_feedback_Decision", "\"Decision\" IN ('Accepted','Rejected','Replaced','Deferred','Reverted')");
                    table.CheckConstraint("CK_opportunity_feedback_Replacement", "(\"Decision\" = 'Replaced' AND \"ReplacementActionCode\" IS NOT NULL) OR (\"Decision\" <> 'Replaced' AND \"ReplacementActionCode\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_feedback_commercial_opportunity_feed~",
                        columns: x => new { x.BusinessUnitId, x.SupersedesFeedbackId },
                        principalTable: "commercial_opportunity_feedback",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_feedback_commercial_opportunity_reco~",
                        columns: x => new { x.BusinessUnitId, x.OpportunityRecommendationId },
                        principalTable: "commercial_opportunity_recommendations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_opportunity_operations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    OpportunityRecommendationId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_opportunity_operations", x => x.Id);
                    table.UniqueConstraint("AK_commercial_opportunity_operations_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_opportunity_operations_RequestHash", "length(\"RequestHash\") = 64");
                    table.CheckConstraint("CK_opportunity_operations_Type", "\"OperationType\" IN ('Reconcile','Feedback')");
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_operations_commercial_opportunity_re~",
                        columns: x => new { x.BusinessUnitId, x.OpportunityRecommendationId },
                        principalTable: "commercial_opportunity_recommendations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_opportunity_outcomes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OpportunityRecommendationId = table.Column<long>(type: "bigint", nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_opportunity_outcomes", x => x.Id);
                    table.UniqueConstraint("AK_commercial_opportunity_outcomes_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_opportunity_outcomes_Code", "\"OutcomeCode\" IN ('ORDER_CREATED','QUOTE_WON','QUOTE_LOST','QUOTE_EXPIRED')");
                    table.CheckConstraint("CK_opportunity_outcomes_EvidenceHash", "length(\"EvidenceHash\") = 64");
                    table.CheckConstraint("CK_opportunity_outcomes_Source", "\"SourceType\" IN ('Order','Quote')");
                    table.CheckConstraint("CK_opportunity_outcomes_SourceVersion", "\"SourceVersion\" >= 1");
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_outcomes_commercial_opportunity_reco~",
                        columns: x => new { x.BusinessUnitId, x.OpportunityRecommendationId },
                        principalTable: "commercial_opportunity_recommendations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commercial_opportunity_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OpportunityEventId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_opportunity_outbox", x => x.Id);
                    table.UniqueConstraint("AK_commercial_opportunity_outbox_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_opportunity_outbox_Attempts", "\"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_commercial_opportunity_outbox_commercial_opportunity_events~",
                        columns: x => new { x.BusinessUnitId, x.OpportunityEventId },
                        principalTable: "commercial_opportunity_events",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_events_BusinessUnitId_IdempotencyKey",
                table: "commercial_opportunity_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_events_BusinessUnitId_OpportunityRec~",
                table: "commercial_opportunity_events",
                columns: new[] { "BusinessUnitId", "OpportunityRecommendationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_feedback_BusinessUnitId_IdempotencyK~",
                table: "commercial_opportunity_feedback",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_feedback_BusinessUnitId_OpportunityR~",
                table: "commercial_opportunity_feedback",
                columns: new[] { "BusinessUnitId", "OpportunityRecommendationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_feedback_BusinessUnitId_SupersedesFe~",
                table: "commercial_opportunity_feedback",
                columns: new[] { "BusinessUnitId", "SupersedesFeedbackId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_operations_BusinessUnitId_Commercial~",
                table: "commercial_opportunity_operations",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_operations_BusinessUnitId_Idempotenc~",
                table: "commercial_opportunity_operations",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_operations_BusinessUnitId_Opportunit~",
                table: "commercial_opportunity_operations",
                columns: new[] { "BusinessUnitId", "OpportunityRecommendationId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_outbox_BusinessUnitId_OpportunityEve~",
                table: "commercial_opportunity_outbox",
                columns: new[] { "BusinessUnitId", "OpportunityEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_outbox_BusinessUnitId_ProcessedAtUtc~",
                table: "commercial_opportunity_outbox",
                columns: new[] { "BusinessUnitId", "ProcessedAtUtc", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_outcomes_BusinessUnitId_OpportunityR~",
                table: "commercial_opportunity_outcomes",
                columns: new[] { "BusinessUnitId", "OpportunityRecommendationId", "SourceType", "SourceId", "SourceVersion", "OutcomeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_recommendations_BusinessUnitId_Comm~1",
                table: "commercial_opportunity_recommendations",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "PolicyVersion", "EvidenceHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_recommendations_BusinessUnitId_Comme~",
                table: "commercial_opportunity_recommendations",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "NexoraSerial" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_recommendations_BusinessUnitId_Gener~",
                table: "commercial_opportunity_recommendations",
                columns: new[] { "BusinessUnitId", "GeneratedAtUtc", "PriorityScore" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_recommendations_BusinessUnitId_LeadId",
                table: "commercial_opportunity_recommendations",
                columns: new[] { "BusinessUnitId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_recommendations_BusinessUnitId_Recom~",
                table: "commercial_opportunity_recommendations",
                columns: new[] { "BusinessUnitId", "RecommendationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_opportunity_recommendations_BusinessUnitId_Super~",
                table: "commercial_opportunity_recommendations",
                columns: new[] { "BusinessUnitId", "SupersedesRecommendationId" });

            migrationBuilder.Sql("""
                ALTER TABLE public.commercial_opportunity_recommendations
                    ADD CONSTRAINT "CK_opportunity_recommendations_Band"
                    CHECK ("PriorityBand" IN ('Low','Medium','High','Critical')),
                    ADD CONSTRAINT "CK_opportunity_recommendations_LeadVersion"
                    CHECK ("LeadVersion" >= 1);
                ALTER TABLE public.commercial_opportunity_feedback
                    ADD CONSTRAINT "CK_opportunity_feedback_Reason"
                    CHECK (length(btrim("Reason")) > 0),
                    ADD CONSTRAINT "CK_opportunity_feedback_Supersedes"
                    CHECK (("Decision" = 'Reverted') = ("SupersedesFeedbackId" IS NOT NULL));
                ALTER TABLE public.commercial_opportunity_operations
                    ADD CONSTRAINT "CK_opportunity_operations_Target" CHECK (
                        ("OperationType" = 'Reconcile' AND "CommercialCaseId" IS NULL AND "OpportunityRecommendationId" IS NULL) OR
                        ("OperationType" = 'Feedback' AND "CommercialCaseId" IS NOT NULL AND "OpportunityRecommendationId" IS NOT NULL)
                    );
                ALTER TABLE public.commercial_opportunity_events
                    ADD CONSTRAINT "CK_opportunity_events_Source" CHECK (
                        ("EventType" = 'OpportunityRecommendation.Generated' AND "SourceType" = 'Recommendation') OR
                        ("EventType" = 'OpportunityRecommendation.FeedbackRecorded' AND "SourceType" = 'Feedback') OR
                        ("EventType" = 'OpportunityRecommendation.OutcomeObserved' AND "SourceType" = 'Outcome')
                    );

                ALTER TABLE public.commercial_opportunity_recommendations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_recommendations FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_outcomes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_outcomes FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_feedback ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_feedback FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_events FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_outbox ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_outbox FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_operations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.commercial_opportunity_operations FORCE ROW LEVEL SECURITY;

                CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_recommendations TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_outcomes TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_feedback TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_events TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_outbox TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.commercial_opportunity_operations TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                CREATE OR REPLACE FUNCTION public.nexora_reject_opportunity_immutable_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $immutable$
                BEGIN
                    RAISE EXCEPTION 'commercial opportunity intelligence records are append-only';
                END;
                $immutable$;

                CREATE TRIGGER trg_opportunity_recommendations_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_opportunity_recommendations
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
                CREATE TRIGGER trg_opportunity_outcomes_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_opportunity_outcomes
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
                CREATE TRIGGER trg_opportunity_feedback_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_opportunity_feedback
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
                CREATE TRIGGER trg_opportunity_events_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_opportunity_events
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();
                CREATE TRIGGER trg_opportunity_operations_append_only
                    BEFORE UPDATE OR DELETE ON public.commercial_opportunity_operations
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_opportunity_immutable_mutation();

                CREATE OR REPLACE FUNCTION public.nexora_guard_opportunity_outbox()
                RETURNS trigger LANGUAGE plpgsql AS $outbox$
                BEGIN
                    IF TG_OP = 'DELETE' OR
                       NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId" OR
                       NEW."OpportunityEventId" IS DISTINCT FROM OLD."OpportunityEventId" OR
                       NEW."EventType" IS DISTINCT FROM OLD."EventType" OR
                       NEW."PayloadJson" IS DISTINCT FROM OLD."PayloadJson" OR
                       NEW."OccurredAtUtc" IS DISTINCT FROM OLD."OccurredAtUtc" THEN
                        RAISE EXCEPTION 'commercial opportunity outbox identity and payload are immutable';
                    END IF;
                    RETURN NEW;
                END;
                $outbox$;

                CREATE TRIGGER trg_guard_opportunity_outbox
                    BEFORE UPDATE OR DELETE ON public.commercial_opportunity_outbox
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_opportunity_outbox();

                CREATE OR REPLACE FUNCTION public.nexora_validate_opportunity_recommendation_lineage()
                RETURNS trigger LANGUAGE plpgsql AS $recommendation_lineage$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM public."Leads" lead
                        WHERE lead."BusinessUnitID" = NEW."BusinessUnitId"
                          AND lead."ID" = NEW."LeadId"
                          AND lead."CommercialCaseId" = NEW."CommercialCaseId"
                          AND lead."CommercialCaseReference" = NEW."NexoraSerial"
                    ) THEN
                        RAISE EXCEPTION 'opportunity recommendation must retain tenant-qualified lead and Nexora Serial lineage';
                    END IF;

                    IF NEW."SupersedesRecommendationId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM public.commercial_opportunity_recommendations prior
                        WHERE prior."BusinessUnitId" = NEW."BusinessUnitId"
                          AND prior."Id" = NEW."SupersedesRecommendationId"
                          AND prior."CommercialCaseId" = NEW."CommercialCaseId"
                          AND prior."LeadId" = NEW."LeadId"
                          AND prior."NexoraSerial" = NEW."NexoraSerial"
                    ) THEN
                        RAISE EXCEPTION 'superseded recommendation must retain the same commercial identity';
                    END IF;
                    RETURN NEW;
                END;
                $recommendation_lineage$;

                CREATE TRIGGER trg_validate_opportunity_recommendation_lineage
                    BEFORE INSERT ON public.commercial_opportunity_recommendations
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_recommendation_lineage();

                CREATE OR REPLACE FUNCTION public.nexora_validate_opportunity_outcome()
                RETURNS trigger LANGUAGE plpgsql AS $outcome_linkage$
                DECLARE recommendation_created timestamp without time zone;
                BEGIN
                    SELECT recommendation."GeneratedAtUtc" INTO recommendation_created
                    FROM public.commercial_opportunity_recommendations recommendation
                    WHERE recommendation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND recommendation."Id" = NEW."OpportunityRecommendationId";

                    IF NEW."ObservedAtUtc" <= recommendation_created THEN
                        RAISE EXCEPTION 'observed outcomes must occur after the shadow recommendation';
                    END IF;
                    RETURN NEW;
                END;
                $outcome_linkage$;

                CREATE OR REPLACE FUNCTION public.nexora_validate_opportunity_feedback()
                RETURNS trigger LANGUAGE plpgsql AS $feedback_linkage$
                BEGIN
                    IF NEW."SupersedesFeedbackId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM public.commercial_opportunity_feedback prior
                        WHERE prior."BusinessUnitId" = NEW."BusinessUnitId"
                          AND prior."Id" = NEW."SupersedesFeedbackId"
                          AND prior."OpportunityRecommendationId" = NEW."OpportunityRecommendationId"
                    ) THEN
                        RAISE EXCEPTION 'superseded feedback must belong to the same recommendation';
                    END IF;
                    RETURN NEW;
                END;
                $feedback_linkage$;

                CREATE TRIGGER trg_validate_opportunity_outcome
                    BEFORE INSERT ON public.commercial_opportunity_outcomes
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_outcome();
                CREATE TRIGGER trg_validate_opportunity_feedback
                    BEFORE INSERT ON public.commercial_opportunity_feedback
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_opportunity_feedback();

                CREATE OR REPLACE FUNCTION public.nexora_require_opportunity_event()
                RETURNS trigger LANGUAGE plpgsql AS $audit$
                DECLARE source_kind text;
                DECLARE recommendation_id bigint;
                BEGIN
                    IF TG_TABLE_NAME = 'commercial_opportunity_recommendations' THEN
                        source_kind := 'Recommendation'; recommendation_id := NEW."Id";
                    ELSIF TG_TABLE_NAME = 'commercial_opportunity_feedback' THEN
                        source_kind := 'Feedback'; recommendation_id := NEW."OpportunityRecommendationId";
                    ELSE
                        source_kind := 'Outcome'; recommendation_id := NEW."OpportunityRecommendationId";
                    END IF;
                    IF NOT EXISTS (
                        SELECT 1 FROM public.commercial_opportunity_events event
                        WHERE event."BusinessUnitId" = NEW."BusinessUnitId"
                          AND event."OpportunityRecommendationId" = recommendation_id
                          AND event."SourceType" = source_kind
                          AND event."SourceId" = NEW."Id"
                    ) THEN
                        RAISE EXCEPTION 'commercial opportunity record requires a matching append-only event';
                    END IF;
                    RETURN NULL;
                END;
                $audit$;

                CREATE CONSTRAINT TRIGGER trg_require_opportunity_recommendation_event
                    AFTER INSERT ON public.commercial_opportunity_recommendations
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();
                CREATE CONSTRAINT TRIGGER trg_require_opportunity_feedback_event
                    AFTER INSERT ON public.commercial_opportunity_feedback
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();
                CREATE CONSTRAINT TRIGGER trg_require_opportunity_outcome_event
                    AFTER INSERT ON public.commercial_opportunity_outcomes
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_event();

                CREATE OR REPLACE FUNCTION public.nexora_require_opportunity_outbox()
                RETURNS trigger LANGUAGE plpgsql AS $outbox_audit$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM public.commercial_opportunity_outbox message
                        WHERE message."BusinessUnitId" = NEW."BusinessUnitId"
                          AND message."OpportunityEventId" = NEW."Id"
                          AND message."EventType" = NEW."EventType"
                          AND message."PayloadJson" = NEW."PayloadJson"
                    ) THEN
                        RAISE EXCEPTION 'commercial opportunity event requires a matching outbox message';
                    END IF;
                    RETURN NULL;
                END;
                $outbox_audit$;

                CREATE CONSTRAINT TRIGGER trg_require_opportunity_outbox
                    AFTER INSERT ON public.commercial_opportunity_events
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_require_opportunity_outbox();

                REVOKE ALL ON public.commercial_opportunity_recommendations FROM PUBLIC;
                REVOKE ALL ON public.commercial_opportunity_outcomes FROM PUBLIC;
                REVOKE ALL ON public.commercial_opportunity_feedback FROM PUBLIC;
                REVOKE ALL ON public.commercial_opportunity_events FROM PUBLIC;
                REVOKE ALL ON public.commercial_opportunity_outbox FROM PUBLIC;
                REVOKE ALL ON public.commercial_opportunity_operations FROM PUBLIC;

                DO $roles$
                DECLARE governed_table text;
                DECLARE sequence_name text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT SELECT, INSERT ON public.commercial_opportunity_recommendations TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_outcomes TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_feedback TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_events TO nexora_tenant_app;
                        GRANT SELECT, INSERT, UPDATE ON public.commercial_opportunity_outbox TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_operations TO nexora_tenant_app;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT ON public.commercial_opportunity_recommendations TO nexora_pipeline_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_outcomes TO nexora_pipeline_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_feedback TO nexora_pipeline_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_events TO nexora_pipeline_app;
                        GRANT SELECT, INSERT, UPDATE ON public.commercial_opportunity_outbox TO nexora_pipeline_app;
                        GRANT SELECT, INSERT ON public.commercial_opportunity_operations TO nexora_pipeline_app;
                    END IF;
                    FOREACH governed_table IN ARRAY ARRAY[
                        'commercial_opportunity_recommendations','commercial_opportunity_outcomes',
                        'commercial_opportunity_feedback','commercial_opportunity_events',
                        'commercial_opportunity_outbox','commercial_opportunity_operations'
                    ] LOOP
                        sequence_name := pg_get_serial_sequence(format('public.%I', governed_table), 'Id');
                        IF sequence_name IS NOT NULL AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                        END IF;
                        IF sequence_name IS NOT NULL AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
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
                DROP FUNCTION IF EXISTS public.nexora_require_opportunity_outbox() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_require_opportunity_event() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_validate_opportunity_recommendation_lineage() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_validate_opportunity_outcome() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_validate_opportunity_feedback() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_guard_opportunity_outbox() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_reject_opportunity_immutable_mutation() CASCADE;
                """);

            migrationBuilder.DropTable(
                name: "commercial_opportunity_feedback");

            migrationBuilder.DropTable(
                name: "commercial_opportunity_operations");

            migrationBuilder.DropTable(
                name: "commercial_opportunity_outbox");

            migrationBuilder.DropTable(
                name: "commercial_opportunity_outcomes");

            migrationBuilder.DropTable(
                name: "commercial_opportunity_events");

            migrationBuilder.DropTable(
                name: "commercial_opportunity_recommendations");
        }
    }
}

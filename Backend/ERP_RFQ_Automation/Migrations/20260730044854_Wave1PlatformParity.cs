using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Wave1PlatformParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governed_artifacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CurrentVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    ProductionVersionNumber = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governed_artifacts", x => x.Id);
                    table.UniqueConstraint("AK_governed_artifacts_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_governed_artifacts_versions", "\"CurrentVersionNumber\" > 0 AND \"Version\" > 0 AND (\"ProductionVersionNumber\" IS NULL OR \"ProductionVersionNumber\" > 0)");
                    table.ForeignKey(
                        name: "FK_governed_artifacts_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "human_action_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    CommercialImpact = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ResumeActionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AssignedToUserId = table.Column<long>(type: "bigint", nullable: true),
                    DueOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_human_action_items", x => x.Id);
                    table.UniqueConstraint("AK_human_action_items_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_human_action_items_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1 AND \"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_human_action_items_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_governance_audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Area = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AggregateReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_governance_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_governance_audit_events_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "governed_artifact_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    GovernedArtifactId = table.Column<long>(type: "bigint", nullable: false),
                    ArtifactVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governed_artifact_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_governed_artifact_events_governed_artifacts_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.GovernedArtifactId },
                        principalTable: "governed_artifacts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "governed_artifact_versions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    GovernedArtifactId = table.Column<long>(type: "bigint", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    TestedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PublishedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governed_artifact_versions", x => x.Id);
                    table.UniqueConstraint("AK_governed_artifact_versions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_governed_artifact_versions_governed_artifacts_BusinessUnitI~",
                        columns: x => new { x.BusinessUnitId, x.GovernedArtifactId },
                        principalTable: "governed_artifacts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "human_action_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    HumanActionItemId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_human_action_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_human_action_events_human_action_items_BusinessUnitId_Human~",
                        columns: x => new { x.BusinessUnitId, x.HumanActionItemId },
                        principalTable: "human_action_items",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_governed_artifact_events_BusinessUnitId_GovernedArtifactId",
                table: "governed_artifact_events",
                columns: new[] { "BusinessUnitId", "GovernedArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_governed_artifact_events_BusinessUnitId_IdempotencyKey",
                table: "governed_artifact_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_governed_artifact_versions_BusinessUnitId_GovernedArtifactI~",
                table: "governed_artifact_versions",
                columns: new[] { "BusinessUnitId", "GovernedArtifactId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_governed_artifacts_BusinessUnitId_ArtifactType_ArtifactKey",
                table: "governed_artifacts",
                columns: new[] { "BusinessUnitId", "ArtifactType", "ArtifactKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_human_action_events_BusinessUnitId_HumanActionItemId",
                table: "human_action_events",
                columns: new[] { "BusinessUnitId", "HumanActionItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_human_action_events_BusinessUnitId_IdempotencyKey",
                table: "human_action_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_human_action_items_BusinessUnitId_Status_Priority_DueOn",
                table: "human_action_items",
                columns: new[] { "BusinessUnitId", "Status", "Priority", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_governance_audit_events_BusinessUnitId_Area_Occurred~",
                table: "tenant_governance_audit_events",
                columns: new[] { "BusinessUnitId", "Area", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_governance_audit_events_BusinessUnitId_IdempotencyKey",
                table: "tenant_governance_audit_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RAISE EXCEPTION 'Required runtime role nexora_tenant_app is missing';
                    END IF;
                END $$;

                ALTER TABLE public.governed_artifacts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.governed_artifacts FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.governed_artifact_versions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.governed_artifact_versions FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.governed_artifact_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.governed_artifact_events FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.human_action_items ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.human_action_items FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.human_action_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.human_action_events FORCE ROW LEVEL SECURITY;
                ALTER TABLE public.tenant_governance_audit_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.tenant_governance_audit_events FORCE ROW LEVEL SECURITY;

                CREATE POLICY nexora_tenant_isolation ON public.governed_artifacts
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.governed_artifact_versions
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.governed_artifact_events
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.human_action_items
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.human_action_events
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                CREATE POLICY nexora_tenant_isolation ON public.tenant_governance_audit_events
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                REVOKE ALL ON public.governed_artifacts, public.governed_artifact_versions,
                    public.governed_artifact_events, public.human_action_items,
                    public.human_action_events, public.tenant_governance_audit_events
                    FROM nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE ON public.governed_artifacts,
                    public.governed_artifact_versions, public.human_action_items TO nexora_tenant_app;
                GRANT SELECT, INSERT ON public.governed_artifact_events,
                    public.human_action_events, public.tenant_governance_audit_events TO nexora_tenant_app;

                DO $$
                DECLARE sequence_name text;
                BEGIN
                    FOREACH sequence_name IN ARRAY ARRAY[
                        pg_get_serial_sequence('public.governed_artifacts', 'Id'),
                        pg_get_serial_sequence('public.governed_artifact_versions', 'Id'),
                        pg_get_serial_sequence('public.governed_artifact_events', 'Id'),
                        pg_get_serial_sequence('public.human_action_items', 'Id'),
                        pg_get_serial_sequence('public.human_action_events', 'Id'),
                        pg_get_serial_sequence('public.tenant_governance_audit_events', 'Id')]
                    LOOP
                        IF sequence_name IS NOT NULL THEN
                            EXECUTE format('REVOKE ALL ON SEQUENCE %s FROM nexora_tenant_app', sequence_name);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                        END IF;
                    END LOOP;
                END $$;

                CREATE OR REPLACE FUNCTION public.wave1_reject_append_only_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Wave 1 governance events are append-only';
                END $$;

                CREATE TRIGGER governed_artifact_events_append_only
                    BEFORE UPDATE OR DELETE ON public.governed_artifact_events
                    FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();
                CREATE TRIGGER human_action_events_append_only
                    BEFORE UPDATE OR DELETE ON public.human_action_events
                    FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();
                CREATE TRIGGER tenant_governance_audit_events_append_only
                    BEFORE UPDATE OR DELETE ON public.tenant_governance_audit_events
                    FOR EACH ROW EXECUTE FUNCTION public.wave1_reject_append_only_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS governed_artifact_events_append_only ON public.governed_artifact_events;
                DROP TRIGGER IF EXISTS human_action_events_append_only ON public.human_action_events;
                DROP TRIGGER IF EXISTS tenant_governance_audit_events_append_only ON public.tenant_governance_audit_events;
                DROP FUNCTION IF EXISTS public.wave1_reject_append_only_mutation();
                """);

            migrationBuilder.DropTable(
                name: "governed_artifact_events");

            migrationBuilder.DropTable(
                name: "governed_artifact_versions");

            migrationBuilder.DropTable(
                name: "human_action_events");

            migrationBuilder.DropTable(
                name: "tenant_governance_audit_events");

            migrationBuilder.DropTable(
                name: "governed_artifacts");

            migrationBuilder.DropTable(
                name: "human_action_items");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class PilotReadinessDeadLetterOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "extraction_dead_letter_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ExtractionJobId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extraction_dead_letter_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_extraction_dead_letter_events_ExtractionJobs_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.ExtractionJobId },
                        principalTable: "ExtractionJobs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_extraction_dead_letter_events_BusinessUnitId_ExtractionJobI~",
                table: "extraction_dead_letter_events",
                columns: new[] { "BusinessUnitId", "ExtractionJobId", "AttemptNumber", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_extraction_dead_letter_events_tenant_job_idempotency",
                table: "extraction_dead_letter_events",
                columns: new[] { "BusinessUnitId", "ExtractionJobId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public.extraction_dead_letter_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.extraction_dead_letter_events FORCE ROW LEVEL SECURITY;

                DO $security$
                DECLARE event_sequence text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        CREATE POLICY nexora_tenant_isolation ON public.extraction_dead_letter_events
                            TO nexora_tenant_app
                            USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                            WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                        REVOKE ALL ON TABLE public.extraction_dead_letter_events FROM nexora_tenant_app;
                        GRANT SELECT, INSERT ON TABLE public.extraction_dead_letter_events TO nexora_tenant_app;
                        event_sequence := pg_get_serial_sequence('public.extraction_dead_letter_events', 'Id');
                        IF event_sequence IS NOT NULL THEN
                            EXECUTE format('REVOKE ALL ON SEQUENCE %s FROM nexora_tenant_app', event_sequence);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', event_sequence);
                        END IF;
                    END IF;
                END
                $security$;

                CREATE FUNCTION public.nexora_reject_extraction_dead_letter_event_mutation()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $body$
                BEGIN
                    RAISE EXCEPTION 'extraction dead-letter events are append-only' USING ERRCODE = '55000';
                END
                $body$;

                CREATE TRIGGER extraction_dead_letter_events_append_only
                    BEFORE UPDATE OR DELETE ON public.extraction_dead_letter_events
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_extraction_dead_letter_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extraction_dead_letter_events");
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.nexora_reject_extraction_dead_letter_event_mutation();
                """);
        }
    }
}

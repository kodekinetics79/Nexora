using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Two horizontal-scale fixes.
    ///
    /// 1. <c>SlaEvents.DedupKey</c> + a UNIQUE (BusinessUnitId, DedupKey) index. The SLA
    ///    sweep's send-once guarantee was a lookup-before-insert against a deliberately
    ///    NON-unique index, so two worker instances both passed the lookup and both sent
    ///    the same bid-deadline email and manager escalation to real customers. The
    ///    worker now INSERTs its claim before sending and treats 23505 as "another
    ///    instance owns this alert". The unique index is what makes that true.
    ///
    ///    Existing rows are backfilled from their natural key. Any pre-existing
    ///    duplicates — which can only have come from the race this migration closes —
    ///    are collapsed onto the earliest row so the unique index can be built; the
    ///    number collapsed is raised as a notice for the deployment log.
    ///
    /// 2. <c>FolderIngestionRetryStates</c>. FolderService kept its per-file staging
    ///    retry counter in "&lt;file&gt;.nexora-retry.json" sidecars on Render's EPHEMERAL
    ///    per-instance disk: the counter reset on every restart (a poison document could
    ///    retry forever) and was invisible to other instances (the three-strikes
    ///    quarantine rule was per-instance rather than per-file).
    /// </summary>
    public partial class PilotScaleOutSlaIdempotencyAndFolderRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DedupKey",
                table: "SlaEvents",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            // Backfill from the natural key. The stale-quote digest is once per owner per
            // UTC day, so its key carries the day; every other event type is once ever.
            migrationBuilder.Sql("""
                UPDATE public."SlaEvents"
                SET "DedupKey" = CASE
                    WHEN "EntityType" = 'quote-stale-digest'
                        THEN "EntityType" || ':' || "EntityId"::text || ':' || "Level"
                             || ':' || to_char("CreatedOn"::date, 'YYYY-MM-DD')
                    ELSE "EntityType" || ':' || "EntityId"::text || ':' || "Level"
                END
                WHERE "DedupKey" = '';

                DO $collapse$
                DECLARE collapsed bigint;
                BEGIN
                    WITH ranked AS (
                        SELECT "Id",
                               row_number() OVER (
                                   PARTITION BY "BusinessUnitId", "DedupKey"
                                   ORDER BY "CreatedOn", "Id") AS position
                        FROM public."SlaEvents")
                    DELETE FROM public."SlaEvents" e
                    USING ranked
                    WHERE e."Id" = ranked."Id" AND ranked.position > 1;

                    GET DIAGNOSTICS collapsed = ROW_COUNT;
                    IF collapsed > 0 THEN
                        RAISE NOTICE
                            'Collapsed % duplicate SlaEvents row(s) produced by the pre-unique-index send-once race.',
                            collapsed;
                    END IF;
                END
                $collapse$;
                """);

            migrationBuilder.CreateTable(
                name: "FolderIngestionRetryStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SourceLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastErrorType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FirstFailedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastFailedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderIngestionRetryStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SlaEvents_BU_DedupKey",
                table: "SlaEvents",
                columns: new[] { "BusinessUnitId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FolderIngestionRetryStates_BU_Source_File",
                table: "FolderIngestionRetryStates",
                columns: new[] { "BusinessUnitId", "SourceLabel", "FileName" },
                unique: true);

            // The tenant-isolation migrations enable RLS by scanning for a BusinessUnitID-ish
            // column, so a table added later must opt in explicitly or the tenant role would
            // read it across tenants.
            migrationBuilder.Sql("""
                ALTER TABLE public."FolderIngestionRetryStates" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."FolderIngestionRetryStates";
                CREATE POLICY nexora_tenant_isolation ON public."FolderIngestionRetryStates"
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."FolderIngestionRetryStates"
                    TO nexora_tenant_app, nexora_pipeline_app;
                -- USAGE only for the tenant role: nextval() needs USAGE, and the
                -- PostgreSqlProductionDialectTests sequence guard forbids nexora_tenant_app
                -- holding SELECT or UPDATE on ANY sequence (reading currval/last_value across
                -- tenants leaks row-creation volume, and UPDATE would let a tenant reset the
                -- counter). This matches every other tenant sequence grant in Migrations/.
                GRANT USAGE ON SEQUENCE public."FolderIngestionRetryStates_Id_seq"
                    TO nexora_tenant_app;
                GRANT USAGE, SELECT ON SEQUENCE public."FolderIngestionRetryStates_Id_seq"
                    TO nexora_pipeline_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderIngestionRetryStates");

            migrationBuilder.DropIndex(
                name: "UX_SlaEvents_BU_DedupKey",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "DedupKey",
                table: "SlaEvents");
        }
    }
}

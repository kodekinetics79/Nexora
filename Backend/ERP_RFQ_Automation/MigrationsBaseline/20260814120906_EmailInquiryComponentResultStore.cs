using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.MigrationsBaseline
{
    /// <inheritdoc />
    public partial class EmailInquiryComponentResultStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- BOUNDED-LOCK DDL ON THE HOT QUEUE ------------------------------------
            //
            // ExtractionJobs is the live work queue: workers claim from it under an advisory
            // lock every few hundred milliseconds. Any ACCESS EXCLUSIVE lock taken here does
            // not merely block for its own duration — it queues BEHIND the running claims and
            // every subsequent claim queues behind IT, so a DDL statement that waits ten
            // seconds stalls extraction for ten seconds across every tenant.
            //
            // lock_timeout is therefore the primary control: the DDL either takes its lock
            // promptly or fails the deployment, which is recoverable. A pile-up is not.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '3s';");

            // Instant in PostgreSQL 11+: a nullable column with no default is a catalogue
            // change, not a table rewrite.
            migrationBuilder.AddColumn<long>(
                name: "EmailInquiryComponentId",
                table: "ExtractionJobs",
                type: "bigint",
                nullable: true);

            // A job may name a component ONLY if it came from email. Without this the column is
            // an unenforced convention, and a manual upload that acquired a component id would
            // silently divert into the message barrier and never produce a Lead.
            //
            // The literal is the string PostgreSQL actually stores: SourceType is mapped
            // HasConversion<string>() onto character varying(30), NOT an integer. Guessing the
            // enum's numeric value here would compile, migrate, and never match a row.
            //
            // NOT VALID, then VALIDATE, deliberately: the first takes a brief ACCESS EXCLUSIVE
            // to write the catalogue entry with no scan, and the second takes only SHARE UPDATE
            // EXCLUSIVE, which runs concurrently with the workers' claims and updates.
            migrationBuilder.Sql("""
                ALTER TABLE public."ExtractionJobs"
                    ADD CONSTRAINT "CK_ExtractionJobs_EmailInquiryComponent_IsEmail"
                    CHECK ("EmailInquiryComponentId" IS NULL OR "SourceType" = 'Email')
                    NOT VALID;
                """);
            migrationBuilder.Sql("""
                ALTER TABLE public."ExtractionJobs"
                    VALIDATE CONSTRAINT "CK_ExtractionJobs_EmailInquiryComponent_IsEmail";
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_EmailInquiryComponents_BusinessUnitId_Id",
                table: "EmailInquiryComponents",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.CreateTable(
                name: "EmailInquiryComponentResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AssemblyId = table.Column<long>(type: "bigint", nullable: false),
                    ComponentId = table.Column<long>(type: "bigint", nullable: false),
                    ExtractionJobId = table.Column<long>(type: "bigint", nullable: false),
                    PayloadContractVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProcessingPath = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    AiProviderClass = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ModelIdentifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HeaderConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    ExpectedItemCount = table.Column<int>(type: "integer", nullable: false),
                    ExtractedItemCount = table.Column<int>(type: "integer", nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailInquiryComponentResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailInquiryComponentResults_EmailInquiryComponents_Busines~",
                        columns: x => new { x.BusinessUnitId, x.ComponentId },
                        principalTable: "EmailInquiryComponents",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            // PARTIAL, and that is a lock decision as much as a storage one. Every query
            // against this column asks "which job belongs to this component", which is never
            // satisfied by a NULL. Excluding NULLs means the index covers only email jobs —
            // zero rows on every existing deployment, so the build finishes immediately instead
            // of scanning a large queue under a lock.
            //
            // CREATE INDEX CONCURRENTLY would remove the lock entirely, but it cannot run
            // inside a transaction, and every migration in this repository is applied inside
            // one. Splitting that contract for a single index would mean a failed deployment
            // could leave the schema half-applied and an INVALID index behind — a worse
            // failure than a short lock on an index that builds over nothing.
            migrationBuilder.Sql("""
                CREATE INDEX "IX_ExtractionJobs_BusinessUnitId_EmailInquiryComponentId"
                    ON public."ExtractionJobs" ("BusinessUnitId", "EmailInquiryComponentId")
                    WHERE "EmailInquiryComponentId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryComponentResults_BusinessUnitId_AssemblyId",
                table: "EmailInquiryComponentResults",
                columns: new[] { "BusinessUnitId", "AssemblyId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryComponentResults_BusinessUnitId_ComponentId",
                table: "EmailInquiryComponentResults",
                columns: new[] { "BusinessUnitId", "ComponentId" },
                unique: true);

            // FORCE ROW LEVEL SECURITY has to come off for the duration of the constraint work.
            //
            // PostgreSQL runs referential-integrity checks with row security disabled, and
            // raises 42501 ("query would be affected by row-level security policy") rather than
            // silently filtering when it cannot. FORCE makes the TABLE OWNER subject to its own
            // policies — which is the whole point of FORCE — and migrations run as the owner on
            // the managed target. So VALIDATE CONSTRAINT against EmailInquiryComponents fails
            // there while passing anywhere migrations happen to run as a superuser.
            //
            // That is a production-only failure, and the deployment would have stopped at this
            // migration. It is safe to lift for the duration: this transaction already holds an
            // ACCESS EXCLUSIVE lock on the table, so no other session can read or write it while
            // FORCE is off, and it is restored below in the same transaction — a rollback
            // restores it too. Nothing about the policies themselves changes.
            migrationBuilder.Sql("""
                ALTER TABLE public."EmailInquiryComponents" NO FORCE ROW LEVEL SECURITY;
                """);

            // The tenant is INSIDE the key, so a job can never reference another tenant's
            // component however the application misbehaves. RESTRICT: a component may not be
            // deleted out from under a job that is mid-flight — removal goes through the
            // assembly, which cascades.
            //
            // NOT VALID then VALIDATE for the same lock reason as the CHECK above.
            migrationBuilder.Sql("""
                ALTER TABLE public."ExtractionJobs"
                    ADD CONSTRAINT "FK_ExtractionJobs_EmailInquiryComponents_BusinessUnitId_EmailI~"
                    FOREIGN KEY ("BusinessUnitId", "EmailInquiryComponentId")
                    REFERENCES public."EmailInquiryComponents" ("BusinessUnitId", "Id")
                    ON DELETE RESTRICT
                    NOT VALID;
                """);
            migrationBuilder.Sql("""
                ALTER TABLE public."ExtractionJobs"
                    VALIDATE CONSTRAINT "FK_ExtractionJobs_EmailInquiryComponents_BusinessUnitId_EmailI~";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public."EmailInquiryComponents" FORCE ROW LEVEL SECURITY;
                """);

            // ---- TENANT ISOLATION FOR THE NEW TABLE -----------------------------------
            //
            // Identical treatment to 20260813134002. A results table holding extracted customer
            // content that is readable across tenants is the worst row in the schema to get
            // wrong, and a table added by a later migration inherits none of this.
            migrationBuilder.Sql("""
                ALTER TABLE public."EmailInquiryComponentResults" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EmailInquiryComponentResults" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."EmailInquiryComponentResults" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            // A policy without a grant is not a narrower boundary — it is a table nobody can
            // read. `public` is deny-by-default since CompleteTenantRlsCoverage.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                    public."EmailInquiryComponentResults"
                TO nexora_tenant_app;
                """);

            // USAGE only on the sequence — never SELECT (currval leaks another tenant's row
            // volume) and never UPDATE (setval collides with a neighbour's future keys).
            migrationBuilder.Sql("""
                GRANT USAGE ON SEQUENCE
                    public."EmailInquiryComponentResults_Id_seq"
                TO nexora_tenant_app;
                """);

            // TenantPurgeExecutor.AssertPurgeReachAsync refuses to run a sweep it cannot prove
            // it can reach, so a table missing this grant and policy blocks EVERY tenant purge
            // rather than silently leaving data behind. Extraction payloads are derived customer
            // content and are precisely what a deletion request covers.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        GRANT SELECT, DELETE ON public."EmailInquiryComponentResults" TO nexora_purge_app;

                        IF NOT EXISTS (
                            SELECT 1 FROM pg_policy p
                            WHERE p.polrelid = 'public."EmailInquiryComponentResults"'::regclass
                              AND p.polname = 'nexora_tenant_purge') THEN
                            CREATE POLICY nexora_tenant_purge ON public."EmailInquiryComponentResults"
                                AS PERMISSIVE FOR ALL TO nexora_purge_app
                                USING ("BusinessUnitId" = NULLIF(current_setting('nexora.purge_business_unit_id', true), '')::bigint);
                        END IF;
                    END IF;
                END
                $$;
                """);

            // The extraction worker writes these rows while draining a shared queue, outside a
            // pushed tenant scope, so it executes as nexora_pipeline_app.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON TABLE
                            public."EmailInquiryComponentResults"
                        TO nexora_pipeline_app;
                        GRANT USAGE ON SEQUENCE
                            public."EmailInquiryComponentResults_Id_seq"
                        TO nexora_pipeline_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS nexora_tenant_purge ON public."EmailInquiryComponentResults";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."EmailInquiryComponentResults";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public."ExtractionJobs"
                    DROP CONSTRAINT IF EXISTS "CK_ExtractionJobs_EmailInquiryComponent_IsEmail";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ExtractionJobs_EmailInquiryComponents_BusinessUnitId_EmailI~",
                table: "ExtractionJobs");

            migrationBuilder.DropTable(
                name: "EmailInquiryComponentResults");

            migrationBuilder.DropIndex(
                name: "IX_ExtractionJobs_BusinessUnitId_EmailInquiryComponentId",
                table: "ExtractionJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_EmailInquiryComponents_BusinessUnitId_Id",
                table: "EmailInquiryComponents");

            migrationBuilder.DropColumn(
                name: "EmailInquiryComponentId",
                table: "ExtractionJobs");
        }
    }
}

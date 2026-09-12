using System;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The parser learns each customer's field headings from the reviewer's corrections.
    ///
    /// <para><b>Why.</b> Every customer heads the same field differently — "BCD", "Response
    /// Date", "Last Date for Submission" — and sends the same export every time. A heading the
    /// built-in vocabulary does not know is missed on every file that customer ever sends, and
    /// the rep types the value in by hand each time. Two things make the miss teachable: the
    /// unrecognised document-level labels are now kept with the canonical inquiry
    /// (<c>canonical_inquiries.unmapped_headers</c>), and a tenant-scoped table
    /// (<c>header_spellings</c>) records the label-to-field readings a reviewer's approval
    /// confirmed. The parser reads the tenant's rows as an overlay on the built-in list.</para>
    ///
    /// <para>Additive: one nullable jsonb column and one new table. Tenant isolation on the new
    /// table follows 20260814120906 exactly — a table added by a later migration inherits none
    /// of it, and a spellings table readable across tenants would leak one customer's document
    /// labels to another.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260910110000_HeaderSpellingsAndUnmappedHeaders")]
    public partial class HeaderSpellingsAndUnmappedHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "unmapped_headers",
                table: "canonical_inquiries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "header_spellings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Spelling = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OriginalLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LearnedFromLeadId = table.Column<long>(type: "bigint", nullable: true),
                    LearnedFromReviewAuditId = table.Column<long>(type: "bigint", nullable: true),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    // DateTime maps to "timestamp without time zone" in this schema (legacy Npgsql
                    // timestamp behaviour, set in Program.cs), exactly as customer_identifiers does.
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastObservedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_header_spellings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_header_spellings_tenant_spelling",
                table: "header_spellings",
                columns: new[] { "BusinessUnitId", "Spelling" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_header_spellings_learned_from_lead",
                table: "header_spellings",
                columns: new[] { "BusinessUnitId", "LearnedFromLeadId" },
                filter: "\"LearnedFromLeadId\" IS NOT NULL");

            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            // ---- TENANT ISOLATION FOR THE NEW TABLE (identical treatment to 20260814120906) ----
            migrationBuilder.Sql("""
                ALTER TABLE public."header_spellings" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."header_spellings" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."header_spellings" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            // A policy without a grant is not a narrower boundary — it is a table nobody can read.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."header_spellings" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."header_spellings_Id_seq" TO nexora_tenant_app;
                """);

            // A tenant purge must be able to reach what the tenant taught.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        GRANT SELECT, DELETE ON public."header_spellings" TO nexora_purge_app;
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_policy p
                            WHERE p.polrelid = 'public."header_spellings"'::regclass
                              AND p.polname = 'nexora_tenant_purge') THEN
                            CREATE POLICY nexora_tenant_purge ON public."header_spellings"
                                AS PERMISSIVE FOR ALL TO nexora_purge_app
                                USING ("BusinessUnitId" = NULLIF(current_setting('nexora.purge_business_unit_id', true), '')::bigint);
                        END IF;
                    END IF;
                END
                $$;
                """);

            // The extraction worker READS the tenant's spellings while draining a shared queue,
            // outside a pushed tenant scope, so it executes as nexora_pipeline_app. It never
            // writes them: only a reviewer's approval teaches.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT ON TABLE public."header_spellings" TO nexora_pipeline_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DROP POLICY IF EXISTS nexora_tenant_purge ON public."header_spellings";
                    DROP POLICY IF EXISTS nexora_tenant_isolation ON public."header_spellings";
                    """);
            }

            migrationBuilder.DropTable(name: "header_spellings");

            migrationBuilder.DropColumn(
                name: "unmapped_headers",
                table: "canonical_inquiries");
        }
    }
}

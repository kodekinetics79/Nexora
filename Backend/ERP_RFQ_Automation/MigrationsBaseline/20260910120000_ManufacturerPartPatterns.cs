using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The tenant's own manufacturer knowledge: which part-number prefixes its reviewed leads
    /// have paired with which makers.
    ///
    /// <para><b>Why.</b> Customers' bid lists routinely omit a manufacturer column. Sometimes the
    /// maker is written inside the description; sometimes only a part number is given whose shape
    /// this tenant already knows, because earlier reviewed leads stated both fields. Today the
    /// canonical line and the lead item carry the manufacturer verbatim or not at all, and the
    /// product catalogue has no manufacturer column to consult, so the knowledge has nowhere to
    /// live. This table is that place: one row per (tenant, prefix, maker), counting how many
    /// reviewed lines have said so. <c>ManufacturerInference</c> reads it; the review path's
    /// <c>ManufacturerPatternLearner</c> writes it.</para>
    ///
    /// <para><b>Why per tenant, under row-level security.</b> "X7-M5" belongs to whichever maker
    /// THIS tenant's buyers have established; a neighbouring tenant's answer to the same prefix
    /// may be a different maker's range, and a shared table would let one tenant's typo name the
    /// maker on another tenant's quote. Same treatment as <c>customer_identifiers</c>: ENABLE and
    /// FORCE row-level security, the <c>nexora_tenant_isolation</c> policy on
    /// <c>"BusinessUnitId"</c>, and a post-check that FORCE survived.</para>
    ///
    /// <para><b>Why three roles.</b> <c>nexora_tenant_app</c> learns during a lead review.
    /// <c>nexora_pipeline_app</c> reads during extraction, which drains a shared queue outside any
    /// tenant scope (it is BYPASSRLS, so the application's own tenant predicate is the boundary
    /// there, and it gets the same DML the pipeline holds on <c>customer_identifiers</c> rather
    /// than a narrower grant that the next learning path would trip over at runtime).
    /// <c>nexora_purge_app</c> needs SELECT and DELETE plus the <c>nexora_tenant_purge</c> policy,
    /// because <c>TenantPurgeExecutor.AssertPurgeReachAsync</c> refuses to run a sweep it cannot
    /// prove reaches every table carrying a business-unit column — a new tenant table without
    /// them blocks EVERY tenant purge by name rather than leaving rows behind.</para>
    ///
    /// <para>Additive: one new table, no existing column touched. A deployment that never reads it
    /// behaves exactly as today.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260910120000_ManufacturerPartPatterns")]
    public partial class ManufacturerPartPatterns : Migration
    {
        private const string Table = "manufacturer_part_patterns";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: Table,
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedManufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    LearnedFromLeadId = table.Column<long>(type: "bigint", nullable: true),
                    LearnedFromReviewAuditId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manufacturer_part_patterns", x => x.Id);
                });

            // Leads with the two columns every lookup filters on, so it serves the read path too.
            migrationBuilder.CreateIndex(
                name: "UX_manufacturer_part_patterns_tenant_pattern_maker",
                table: Table,
                columns: new[] { "BusinessUnitId", "Pattern", "NormalizedManufacturer" },
                unique: true);

            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            // ---- TENANT ISOLATION -------------------------------------------------------
            //
            // Identical treatment to 20260814120906 (EmailInquiryComponentResults). A table
            // added by a later migration inherits none of this, and a maker-knowledge table
            // readable across tenants would let one customer's numbering habits name the
            // manufacturer on another customer's quote.
            migrationBuilder.Sql($"""
                ALTER TABLE public.{Table} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.{Table} FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public.{Table} TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF NOT (SELECT relforcerowsecurity FROM pg_class
                            WHERE oid = 'public.{{Table}}'::regclass) THEN
                        RAISE EXCEPTION
                            '{{Table}} lost FORCE ROW LEVEL SECURITY during migration '
                            '20260910120000. Refusing to complete: the table owner would be '
                            'unbounded by tenant.';
                    END IF;
                END
                $$;
                """);

            // A policy without a grant is not a narrower boundary — it is a table nobody can
            // read. `public` is deny-by-default since CompleteTenantRlsCoverage.
            migrationBuilder.Sql($"""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.{Table} TO nexora_tenant_app;
                """);

            // USAGE only on the sequence — never SELECT (currval leaks another tenant's row
            // volume) and never UPDATE (setval collides with a neighbour's future keys).
            migrationBuilder.Sql($"""
                GRANT USAGE ON SEQUENCE public."{Table}_Id_seq" TO nexora_tenant_app;
                """);

            // The extraction worker reads this table while draining a shared queue, outside a
            // pushed tenant scope, so it executes as nexora_pipeline_app. Same DML the pipeline
            // already holds on customer_identifiers.
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.{{Table}} TO nexora_pipeline_app;
                        GRANT USAGE ON SEQUENCE public."{{Table}}_Id_seq" TO nexora_pipeline_app;
                        REVOKE TRUNCATE ON TABLE public.{{Table}} FROM nexora_pipeline_app;
                    END IF;
                END
                $$;
                """);

            // TenantPurgeExecutor.AssertPurgeReachAsync refuses to run a sweep it cannot prove
            // it can reach, so a table missing this grant and policy blocks EVERY tenant purge
            // rather than silently leaving data behind. Learned patterns are derived from the
            // tenant's documents and are precisely what a deletion request covers.
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        GRANT SELECT, DELETE ON public.{{Table}} TO nexora_purge_app;

                        IF NOT EXISTS (
                            SELECT 1 FROM pg_policy p
                            WHERE p.polrelid = 'public.{{Table}}'::regclass
                              AND p.polname = 'nexora_tenant_purge') THEN
                            CREATE POLICY nexora_tenant_purge ON public.{{Table}}
                                AS PERMISSIVE FOR ALL TO nexora_purge_app
                                USING ("BusinessUnitId" = NULLIF(current_setting('nexora.purge_business_unit_id', true), '')::bigint);
                        END IF;
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
                // Grants fall with the table; policies are dropped explicitly so a partial Down
                // never leaves a policy pointing at a relation that is about to disappear.
                migrationBuilder.Sql($"""
                    DROP POLICY IF EXISTS nexora_tenant_purge ON public.{Table};
                    DROP POLICY IF EXISTS nexora_tenant_isolation ON public.{Table};
                    """);
            }

            migrationBuilder.DropTable(name: Table);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Internet supplier discovery from a sourcing case ("Ask suppliers" when the company knows too
    /// few). Three things land together because they are one feature: two supplier columns
    /// (<c>Website</c>, how a search hit is recognised as a supplier the company already has, and
    /// <c>Role</c>, maker / distributor / reseller), and the 30-day search cache
    /// <c>supplier_discovery_searches</c>, one row per (tenant, part + makers searched).
    ///
    /// <para>No data migration. Both columns are nullable and every existing supplier starts with
    /// "nobody has said", which is the truthful state; nothing infers a role or a website.</para>
    ///
    /// <para>The cache table is tenant data derived from what the tenant searched for, so it gets
    /// the full tenant-isolation treatment every business table here has: ENABLE and FORCE row-level
    /// security, the <c>nexora_tenant_isolation</c> policy, a GRANT to <c>nexora_tenant_app</c>
    /// (the schema is deny-by-default, so a policy without a grant is a table nobody can read), and
    /// the purge role's SELECT/DELETE plus <c>nexora_tenant_purge</c> policy, because
    /// <c>TenantPurgeExecutor.AssertPurgeReachAsync</c> refuses to sweep a tenant while any table
    /// carrying a business-unit column is out of its reach.</para>
    /// </summary>
    public partial class SupplierDiscovery : Migration
    {
        private const string Table = "supplier_discovery_searches";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "Suppliers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supplier_discovery_searches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    QueriesJson = table.Column<string>(type: "jsonb", nullable: false),
                    HitsJson = table.Column<string>(type: "jsonb", nullable: false),
                    HitCount = table.Column<int>(type: "integer", nullable: false),
                    SearchedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SearchedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_discovery_searches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supplier_discovery_searches_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_Role",
                table: "Suppliers",
                sql: "\"Role\" IS NULL OR \"Role\" IN ('Manufacturer','Distributor','Reseller','Unknown')");

            migrationBuilder.CreateIndex(
                name: "UX_supplier_discovery_searches_BU_IdentityKey",
                table: "supplier_discovery_searches",
                columns: new[] { "BusinessUnitId", "IdentityKey" },
                unique: true);

            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            // ---- TENANT ISOLATION -------------------------------------------------------
            //
            // Identical treatment to 20260910120000 (manufacturer_part_patterns). A table added by
            // a later migration inherits none of this, and a search cache readable across tenants
            // would tell one customer which parts another customer is sourcing.
            migrationBuilder.Sql("""
                ALTER TABLE public."supplier_discovery_searches" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."supplier_discovery_searches" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."supplier_discovery_searches" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT (SELECT relforcerowsecurity FROM pg_class
                            WHERE oid = 'public."supplier_discovery_searches"'::regclass) THEN
                        RAISE EXCEPTION
                            'supplier_discovery_searches lost FORCE ROW LEVEL SECURITY during migration '
                            '20260916132941. Refusing to complete: the table owner would be '
                            'unbounded by tenant.';
                    END IF;
                END
                $$;
                """);

            // A policy without a grant is not a narrower boundary — it is a table nobody can
            // read. `public` is deny-by-default since CompleteTenantRlsCoverage.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."supplier_discovery_searches" TO nexora_tenant_app;
                """);

            // USAGE only on the sequence — never SELECT (currval leaks another tenant's row
            // volume) and never UPDATE (setval collides with a neighbour's future keys).
            migrationBuilder.Sql("""
                GRANT USAGE ON SEQUENCE public."supplier_discovery_searches_Id_seq" TO nexora_tenant_app;
                """);

            // No pipeline grant: the search only ever runs inside a rep's request, under a pushed
            // tenant scope. The background workers never read or write this table.

            // TenantPurgeExecutor.AssertPurgeReachAsync refuses to run a sweep it cannot prove
            // it can reach, so a table missing this grant and policy blocks EVERY tenant purge
            // rather than silently leaving data behind. What a tenant searched for is precisely
            // what a deletion request covers.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        GRANT SELECT, DELETE ON public."supplier_discovery_searches" TO nexora_purge_app;

                        IF NOT EXISTS (
                            SELECT 1 FROM pg_policy p
                            WHERE p.polrelid = 'public."supplier_discovery_searches"'::regclass
                              AND p.polname = 'nexora_tenant_purge') THEN
                            CREATE POLICY nexora_tenant_purge ON public."supplier_discovery_searches"
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
                migrationBuilder.Sql("""
                    DROP POLICY IF EXISTS nexora_tenant_purge ON public."supplier_discovery_searches";
                    DROP POLICY IF EXISTS nexora_tenant_isolation ON public."supplier_discovery_searches";
                    """);
            }

            migrationBuilder.DropTable(
                name: Table);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_Role",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "Suppliers");
        }
    }
}

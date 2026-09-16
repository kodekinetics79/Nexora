using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    /// <summary>How a company, and each of its sales people, word the request emailed to suppliers. Tenant-isolated.</summary>
    public partial class SupplierEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_email_settings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    Subject = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: true),
                    Greeting = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Opening = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DefaultMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SignOff = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_email_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supplier_email_settings_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_supplier_email_settings_BU_Company",
                table: "supplier_email_settings",
                column: "BusinessUnitId",
                unique: true,
                filter: "\"UserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_supplier_email_settings_BU_User",
                table: "supplier_email_settings",
                columns: new[] { "BusinessUnitId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            // ---- TENANT ISOLATION -------------------------------------------------------
            // Same treatment as supplier_discovery_searches: one company's email wording and its
            // sales people's signatures are never readable by another company.
            migrationBuilder.Sql("""
                ALTER TABLE public."supplier_email_settings" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."supplier_email_settings" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."supplier_email_settings" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT (SELECT relforcerowsecurity FROM pg_class
                            WHERE oid = 'public."supplier_email_settings"'::regclass) THEN
                        RAISE EXCEPTION
                            'supplier_email_settings lost FORCE ROW LEVEL SECURITY during migration '
                            '20260916222210. Refusing to complete: the table owner would be '
                            'unbounded by tenant.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."supplier_email_settings" TO nexora_tenant_app;
                """);

            migrationBuilder.Sql("""
                GRANT USAGE ON SEQUENCE public."supplier_email_settings_Id_seq" TO nexora_tenant_app;
                """);

            // Read only inside a user's request, under a pushed tenant scope: the prepared request
            // carries the resolved wording, so the dispatch worker never reads this table.

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        GRANT SELECT, DELETE ON public."supplier_email_settings" TO nexora_purge_app;

                        IF NOT EXISTS (
                            SELECT 1 FROM pg_policy p
                            WHERE p.polrelid = 'public."supplier_email_settings"'::regclass
                              AND p.polname = 'nexora_tenant_purge') THEN
                            CREATE POLICY nexora_tenant_purge ON public."supplier_email_settings"
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
                migrationBuilder.Sql("""
                    DROP POLICY IF EXISTS nexora_tenant_purge ON public."supplier_email_settings";
                    DROP POLICY IF EXISTS nexora_tenant_isolation ON public."supplier_email_settings";
                    """);
            }

            migrationBuilder.DropTable(
                name: "supplier_email_settings");
        }
    }
}

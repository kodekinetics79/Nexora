using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate2SupplierTiersAndComparisonWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreditDays",
                table: "Suppliers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tier",
                table: "Suppliers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupplierComparisonWeights",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    PriceWeight = table.Column<int>(type: "integer", nullable: false, defaultValue: 80),
                    LeadTimeWeight = table.Column<int>(type: "integer", nullable: false, defaultValue: 20),
                    WarrantyWeight = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PaymentTermsWeight = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierComparisonWeights", x => x.Id);
                    table.UniqueConstraint("AK_SupplierComparisonWeights_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_SupplierComparisonWeights_Total", "\"PriceWeight\" >= 0 AND \"LeadTimeWeight\" >= 0 AND \"WarrantyWeight\" >= 0 AND \"PaymentTermsWeight\" >= 0 AND (\"PriceWeight\" + \"LeadTimeWeight\" + \"WarrantyWeight\" + \"PaymentTermsWeight\") = 100");
                    table.ForeignKey(
                        name: "FK_SupplierComparisonWeights_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_Tier",
                table: "Suppliers",
                sql: "\"Tier\" IS NULL OR \"Tier\" IN ('TIER_1_PARTNER','TIER_2_EXTENDED','TIER_3_OUT_OF_NETWORK')");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierComparisonWeights_BusinessUnitId",
                table: "SupplierComparisonWeights",
                column: "BusinessUnitId",
                unique: true);

            // FR-QTM-01 filters the supplier dispatch list by tier within one tenant, so the tenant
            // discriminator leads. Partial on NOT NULL: "not yet classified" is the state every
            // existing supplier is in and is never a filter target, so indexing those rows would be
            // indexing the majority to serve none of the queries.
            // The column is "BUID", not "Buid" — the EF property is Buid but the mapped column name
            // is upper case (MigrationsBaseline/Sql/03_tables_and_sequences.sql, CREATE TABLE
            // public."Suppliers"). PostgreSQL folds unquoted identifiers to lower case but these are
            // quoted, so "Buid" is a DIFFERENT identifier and the statement would fail with
            // 42703 column does not exist — on deploy, not in any portable test.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Suppliers_BUID_Tier"
                    ON public."Suppliers" ("BUID", "Tier")
                    WHERE "Tier" IS NOT NULL;
                """);

            // ---------------------------------------------------------------- tenant isolation
            //
            // EF cannot generate any of this, and its absence is not visible in a green build: the
            // query filter on the entity makes the boundary hold in EF and nowhere else. A raw
            // connection, a report, or any path that does not go through the DbContext would read
            // every tenant's weights. TenantIsolationTests reads migration SOURCE TEXT precisely
            // because a table can otherwise ship "isolated" by nothing more than convention.
            //
            // ENABLE without FORCE is not enough here either: the table owner bypasses ENABLE-only
            // RLS, which is a property of who happens to run the query rather than of the schema.
            //
            // The DO block guards on pg_policy because PostgreSQL has no CREATE POLICY IF NOT
            // EXISTS, and this migration must be re-runnable against a database that already has it.
            migrationBuilder.Sql("""
                ALTER TABLE public."SupplierComparisonWeights" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."SupplierComparisonWeights" FORCE ROW LEVEL SECURITY;

                DO $nexora_idem$
                BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_policy
                    WHERE polname = 'nexora_tenant_isolation'
                      AND polrelid = to_regclass('public."SupplierComparisonWeights"')
                ) THEN
                CREATE POLICY nexora_tenant_isolation ON public."SupplierComparisonWeights"
                    TO nexora_tenant_app
                    USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))
                    WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
                END IF;
                END
                $nexora_idem$;
                """);

            // A policy with no GRANT is not a tighter boundary — it is a table nobody can read.
            // PostgreSQL raises 42501 on the privilege check BEFORE it evaluates any row predicate,
            // and three tables have already shipped exactly that with every test passing.
            //
            // SELECT, INSERT, UPDATE and deliberately not DELETE: there is one weights row per
            // business unit, created on demand and thereafter amended. Nothing in the product
            // removes it, and "reset to defaults" is a save like any other — it must leave the same
            // audit trail as every other change rather than vanishing without one.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON TABLE public."SupplierComparisonWeights"
                    TO nexora_tenant_app;
                """);

            // ---------------------------------------------------------------- tenant offboarding
            //
            // 20260811154500_TenantPurgeExecutionRole granted nexora_purge_app and created the
            // nexora_tenant_purge policy by sweeping the LIVE CATALOGUE at the time it ran. This
            // table did not exist then, so it inherits neither — and because it is FORCE ROW LEVEL
            // SECURITY, the purge role would be refused on it.
            //
            // That does not fail quietly in one corner: TenantPurgeExecutor asserts its reach across
            // every table carrying a tenant column BEFORE it deletes a single row, so one unreachable
            // table refuses the offboarding of EVERY tenant. A weights row would have blocked the
            // strongest module in the product.
            //
            // SELECT and DELETE only — purge reads to verify its reach, then removes. It never writes.
            migrationBuilder.Sql("""
                GRANT SELECT, DELETE ON TABLE public."SupplierComparisonWeights" TO nexora_purge_app;

                DO $nexora_idem$
                BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_policy
                    WHERE polname = 'nexora_tenant_purge'
                      AND polrelid = to_regclass('public."SupplierComparisonWeights"')
                ) THEN
                CREATE POLICY nexora_tenant_purge ON public."SupplierComparisonWeights"
                    AS PERMISSIVE FOR ALL TO nexora_purge_app
                    USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.purge_business_unit_id'::text, true), ''::text))::bigint));
                END IF;
                END
                $nexora_idem$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policy and grants go with the table; the tier index does not, so it is dropped
            // explicitly. Dropping a table drops its policies with it, so no DROP POLICY is needed.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS public."IX_Suppliers_BUID_Tier";""");

            migrationBuilder.DropTable(
                name: "SupplierComparisonWeights");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_Tier",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CreditDays",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "Suppliers");
        }
    }
}

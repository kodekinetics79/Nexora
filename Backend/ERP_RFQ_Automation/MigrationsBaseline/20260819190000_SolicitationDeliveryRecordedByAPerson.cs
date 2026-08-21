using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class SolicitationDeliveryRecordedByAPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_solicitation_delivery_records",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierSolicitationId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RecordedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_solicitation_delivery_records", x => x.Id);
                    table.UniqueConstraint("AK_supplier_solicitation_delivery_records_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_solicitation_delivery_records_Channel", "\"Channel\" IN ('Phone','InPerson','SupplierPortal','BuyerEmail')");
                    table.CheckConstraint("CK_supplier_solicitation_delivery_records_Note", "trim(\"Note\") <> ''");
                    table.ForeignKey(
                        name: "FK_supplier_solicitation_delivery_records_BusinessUnits_Busine~",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_solicitation_delivery_records_SupplierSolicitation~",
                        columns: x => new { x.BusinessUnitId, x.SupplierSolicitationId },
                        principalTable: "SupplierSolicitations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierSolicitationDeliveryRecords_BU_IdempotencyKey",
                table: "supplier_solicitation_delivery_records",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SupplierSolicitationDeliveryRecords_BU_Solicitation",
                table: "supplier_solicitation_delivery_records",
                columns: new[] { "BusinessUnitId", "SupplierSolicitationId" },
                unique: true);

            // ---------------------------------------------------------------- tenant isolation
            //
            // This row says a named person asserts they reached a supplier, and it is what lets the
            // capture guard pass afterwards. Read across a tenant boundary it would leak who a
            // competitor talked to and what was agreed; WRITEABLE across one, it would let a
            // neighbour manufacture the evidence that unlocks quoting. The EF query filter holds
            // this in EF and nowhere else — a raw connection or a report would see everything.
            //
            // FORCE as well as ENABLE: the table owner bypasses ENABLE-only RLS, which makes
            // isolation a property of who happens to run the query rather than of the schema.
            migrationBuilder.Sql("""
                ALTER TABLE public."supplier_solicitation_delivery_records" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."supplier_solicitation_delivery_records" FORCE ROW LEVEL SECURITY;

                DO $nexora_idem$
                BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_policy
                    WHERE polname = 'nexora_tenant_isolation'
                      AND polrelid = to_regclass('public."supplier_solicitation_delivery_records"')
                ) THEN
                CREATE POLICY nexora_tenant_isolation ON public."supplier_solicitation_delivery_records"
                    TO nexora_tenant_app
                    USING (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint))
                    WITH CHECK (("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id'::text, true), ''::text))::bigint));
                END IF;
                END
                $nexora_idem$;
                """);

            // A policy with no GRANT is not a tighter boundary — it is a table nobody can read.
            // PostgreSQL raises 42501 on the privilege check before any row predicate is evaluated.
            //
            // SELECT and INSERT only. This is an APPEND-ONLY account of what a person did: no UPDATE,
            // because an assertion someone made cannot be quietly rewritten afterwards, and no DELETE,
            // because it is the evidence a capture depended on. A correction is a new record with its
            // own actor and timestamp, not an edit to the old one.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON TABLE public."supplier_solicitation_delivery_records"
                    TO nexora_tenant_app;
                """);

            // ---------------------------------------------------------------- tenant offboarding
            //
            // 20260811154500_TenantPurgeExecutionRole granted nexora_purge_app by sweeping the live
            // catalogue when it ran; a table created afterwards inherits nothing, and under FORCE RLS
            // the purge role is refused on it. TenantPurgeExecutor asserts its reach across every
            // table carrying a tenant column BEFORE deleting a single row — so one unreachable table
            // refuses the offboarding of EVERY tenant, not just this one's rows.
            //
            // Learned the hard way in Gate 2, where exactly this was missed and caught in review.
            migrationBuilder.Sql("""
                GRANT SELECT, DELETE ON TABLE public."supplier_solicitation_delivery_records"
                    TO nexora_purge_app;

                DO $nexora_idem$
                BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_policy
                    WHERE polname = 'nexora_tenant_purge'
                      AND polrelid = to_regclass('public."supplier_solicitation_delivery_records"')
                ) THEN
                CREATE POLICY nexora_tenant_purge ON public."supplier_solicitation_delivery_records"
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
            migrationBuilder.DropTable(
                name: "supplier_solicitation_delivery_records");
        }
    }
}

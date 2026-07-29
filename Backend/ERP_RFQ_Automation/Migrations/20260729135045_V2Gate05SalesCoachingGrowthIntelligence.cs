using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V2Gate05SalesCoachingGrowthIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RAISE EXCEPTION 'Required runtime role nexora_tenant_app is missing';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.CreateTable(
                name: "sales_coaching_acknowledgements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    FindingKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FindingCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SalesRepUserId = table.Column<long>(type: "bigint", nullable: false),
                    ManagerUserId = table.Column<long>(type: "bigint", nullable: false),
                    DecisionCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SourceAggregateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceAggregateId = table.Column<long>(type: "bigint", nullable: false),
                    SourceAggregateVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvidenceSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FindingGeneratedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_coaching_acknowledgements", x => x.Id);
                    table.UniqueConstraint("AK_sales_coaching_acknowledgements_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_sales_coaching_ack_decision", "\"DecisionCode\" IN ('ACKNOWLEDGED','RESOLVED','DISMISSED')");
                    table.CheckConstraint("CK_sales_coaching_ack_hashes", "length(\"FindingKey\") = 64 AND length(\"RequestHash\") = 64");
                    table.ForeignKey(
                        name: "FK_sales_coaching_acknowledgements_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_coaching_acknowledgements_BusinessUnitId_FindingKey_C~",
                table: "sales_coaching_acknowledgements",
                columns: new[] { "BusinessUnitId", "FindingKey", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_coaching_acknowledgements_BusinessUnitId_IdempotencyK~",
                table: "sales_coaching_acknowledgements",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_coaching_acknowledgements_BusinessUnitId_SalesRepUser~",
                table: "sales_coaching_acknowledgements",
                columns: new[] { "BusinessUnitId", "SalesRepUserId", "CreatedAtUtc" });

            migrationBuilder.Sql("""
                ALTER TABLE public.sales_coaching_acknowledgements
                    ADD CONSTRAINT "FK_sales_coaching_ack_manager_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "ManagerUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;
                ALTER TABLE public.sales_coaching_acknowledgements
                    ADD CONSTRAINT "FK_sales_coaching_ack_rep_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "SalesRepUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;

                CREATE OR REPLACE FUNCTION nexora_reject_sales_coaching_ack_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                        MESSAGE = 'sales coaching acknowledgements are append-only';
                END
                $function$;

                CREATE TRIGGER sales_coaching_acknowledgements_append_only
                    BEFORE UPDATE OR DELETE ON public.sales_coaching_acknowledgements
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_sales_coaching_ack_mutation();
                CREATE TRIGGER sales_coaching_acknowledgements_reject_truncate
                    BEFORE TRUNCATE ON public.sales_coaching_acknowledgements
                    FOR EACH STATEMENT EXECUTE FUNCTION nexora_reject_sales_coaching_ack_mutation();

                ALTER TABLE public.sales_coaching_acknowledgements ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.sales_coaching_acknowledgements FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public.sales_coaching_acknowledgements
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(
                        current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(
                        current_setting('nexora.business_unit_id', true), '')::bigint);

                REVOKE ALL PRIVILEGES ON TABLE public.sales_coaching_acknowledgements
                    FROM nexora_tenant_app;
                GRANT SELECT, INSERT ON TABLE public.sales_coaching_acknowledgements
                    TO nexora_tenant_app;

                DO $block$
                DECLARE acknowledgement_sequence text;
                BEGIN
                    acknowledgement_sequence := pg_get_serial_sequence(
                        'public.sales_coaching_acknowledgements', 'Id');
                    IF acknowledgement_sequence IS NOT NULL THEN
                        EXECUTE format('REVOKE ALL PRIVILEGES ON SEQUENCE %s FROM nexora_tenant_app',
                            acknowledgement_sequence);
                        EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app',
                            acknowledgement_sequence);
                    END IF;
                END
                $block$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS sales_coaching_acknowledgements_reject_truncate
                    ON public.sales_coaching_acknowledgements;
                DROP TRIGGER IF EXISTS sales_coaching_acknowledgements_append_only
                    ON public.sales_coaching_acknowledgements;
                DROP FUNCTION IF EXISTS nexora_reject_sales_coaching_ack_mutation();
                """);

            migrationBuilder.DropTable(
                name: "sales_coaching_acknowledgements");

        }
    }
}

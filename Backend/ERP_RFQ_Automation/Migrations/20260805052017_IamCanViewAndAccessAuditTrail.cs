using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class IamCanViewAndAccessAuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeactivatedAtUtc",
                table: "Users",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanView",
                table: "RolePermissions",
                type: "boolean",
                nullable: true,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "IamAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: true),
                    ActorRoleId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: true),
                    TargetLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IamAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IamAuditEvents_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IamAuditEvents_BU_OccurredOn",
                table: "IamAuditEvents",
                columns: new[] { "BusinessUnitId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_IamAuditEvents_BU_Target",
                table: "IamAuditEvents",
                columns: new[] { "BusinessUnitId", "TargetType", "TargetId" });

            // Tenant isolation for the IAM access-audit trail. This table records who
            // granted or revoked whose access and when — the SOC 2 CC6.2/CC6.3 evidence a
            // prior compliance review found entirely absent — so a cross-tenant read here
            // would leak another customer's administrative activity.
            //
            // APPEND-ONLY BY GRANT: the tenant role may SELECT and INSERT, never UPDATE or
            // DELETE. An audit trail a tenant can rewrite is not an audit trail. This
            // matches the AiProviderAuthorizations idiom (which withholds DELETE) and goes
            // one step further because these rows are evidence, not state.
            migrationBuilder.Sql("""
                ALTER TABLE public."IamAuditEvents" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."IamAuditEvents";
                CREATE POLICY nexora_tenant_isolation ON public."IamAuditEvents"
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                GRANT SELECT, INSERT ON TABLE public."IamAuditEvents" TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."IamAuditEvents"
                    TO nexora_pipeline_app;

                -- USAGE only for the tenant role: nextval() needs USAGE, while SELECT or
                -- UPDATE on the sequence would leak row-creation volume across tenants and
                -- allow counter tampering. PostgreSqlProductionDialectTests enforces this.
                GRANT USAGE ON SEQUENCE public."IamAuditEvents_Id_seq" TO nexora_tenant_app;
                GRANT USAGE, SELECT ON SEQUENCE public."IamAuditEvents_Id_seq"
                    TO nexora_pipeline_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IamAuditEvents");

            migrationBuilder.DropColumn(
                name: "DeactivatedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanView",
                table: "RolePermissions");
        }
    }
}

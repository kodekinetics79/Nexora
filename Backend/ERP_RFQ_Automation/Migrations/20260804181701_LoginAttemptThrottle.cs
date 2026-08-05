using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// SEC-H6: durable failed-login counter behind the progressive lockout on both login
    /// planes (POST /api/Auth/Login and POST /api/platform/auth/login), neither of which
    /// previously had any lockout or failed-attempt tracking.
    ///
    /// Hand-written body: a concurrently generated migration folded the entity into the
    /// model snapshot without emitting the CreateTable, so the scaffolded Up() came out
    /// empty and the table would never have been created.
    ///
    /// The table deliberately has NO BusinessUnitId, NO EF global query filter and therefore
    /// NO RLS policy: rows are written BEFORE authentication, when no tenant claim exists.
    /// The Plane column ("tenant" / "platform") keeps the two auth planes in separate key
    /// namespaces instead, so a tenant lockout can never lock out a platform operator.
    /// </summary>
    public partial class LoginAttemptThrottle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoginAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Plane = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    FirstFailedOnUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastFailedOnUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginAttempts", x => x.Id);
                });

            // The uniqueness constraint is what makes the counter correct across instances:
            // concurrent first-failure inserts collide instead of double-counting.
            migrationBuilder.CreateIndex(
                name: "UX_LoginAttempts_Plane_AttemptKey",
                table: "LoginAttempts",
                columns: new[] { "Plane", "AttemptKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginAttempts_LockedUntilUtc",
                table: "LoginAttempts",
                column: "LockedUntilUtc");

            // No RLS policy by design (see the class comment) — this table is not tenant
            // scoped; it is written before authentication, when no tenant claim exists.
            //
            // Grants deliberately EXCLUDE nexora_tenant_app. TenantRlsCommandInterceptor
            // .ResolveDatabaseRole routes "/api/Auth/Login" to nexora_identity_app and the
            // "/api/platform" plane to nexora_pipeline_app, so those two are the only roles
            // that ever record an attempt. Granting tenant_app would also violate the
            // PostgreSqlProductionDialectTests guard that no table lacking RLS may be
            // reachable by the tenant role — the guard is correct and this table has no
            // tenant column to scope on, so the right answer is to withhold the grant.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."LoginAttempts"
                    TO nexora_identity_app, nexora_pipeline_app;
                GRANT USAGE, SELECT ON SEQUENCE public."LoginAttempts_Id_seq"
                    TO nexora_identity_app, nexora_pipeline_app;
                REVOKE ALL PRIVILEGES ON TABLE public."LoginAttempts" FROM nexora_tenant_app;
                REVOKE ALL PRIVILEGES ON SEQUENCE public."LoginAttempts_Id_seq" FROM nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LoginAttempts");
        }
    }
}

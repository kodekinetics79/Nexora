using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Token revocation for the tenant plane (docs/design/token-revocation.md).
///
/// <para>Adds <c>"Users"."SecurityStamp"</c>, the opaque per-account value every tenant JWT now
/// carries and <c>TenantSessionValidator</c> re-checks on each request. The column is added with a
/// VOLATILE default so PostgreSQL evaluates it once per existing row — every current user gets a
/// distinct stamp rather than one shared value — and the default is dropped immediately after,
/// because from then on the application supplies the value (<c>User.SecurityStamp</c> initialiser).</para>
///
/// <para>Grants. <c>nexora_tenant_app</c> and <c>nexora_pipeline_app</c> hold table-level UPDATE on
/// "Users" and need nothing. <c>nexora_identity_app</c> — the role the anonymous password-reset
/// and invitation-activation paths run as — holds only column-level UPDATE on "Password_Hash"
/// and "IsActive" (20260807002456), so rotating the stamp on those paths needs the column grant
/// below or the first customer password reset fails with 42501. Row-level security on "Users"
/// is unchanged; a column adds no policy.</para>
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260902120000_UserSecurityStamp")]
public sealed class UserSecurityStamp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."Users"
                ADD COLUMN IF NOT EXISTS "SecurityStamp" character varying(64) NOT NULL
                DEFAULT replace(gen_random_uuid()::text, '-', '');
            ALTER TABLE public."Users" ALTER COLUMN "SecurityStamp" DROP DEFAULT;
            """);

        migrationBuilder.Sql("""
            DO $security$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                    GRANT UPDATE("SecurityStamp") ON TABLE public."Users" TO nexora_identity_app;
                END IF;
            END
            $security$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $security$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                    REVOKE UPDATE("SecurityStamp") ON TABLE public."Users" FROM nexora_identity_app;
                END IF;
            END
            $security$;
            """);
        migrationBuilder.DropColumn(name: "SecurityStamp", table: "Users");
    }
}

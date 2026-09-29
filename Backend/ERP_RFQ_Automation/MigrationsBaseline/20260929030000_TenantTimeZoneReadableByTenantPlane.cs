using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Lets the tenant plane read the company's time zone, so a buyer's closing time is read on the
/// company's clock.
///
/// <para><b>The defect.</b> The time zone is chosen when the company is created ("Deadlines and
/// SLA clocks are read in this zone"), but nothing read it: every deadline — days left, past
/// deadline, closing-soon counts, the reminder e-mails — compared the buyer's wall-clock closing
/// time with UTC. A Riyadh 5:00 PM close stayed open until 8:00 PM and "today" turned over at
/// 3:00 AM. <c>TenantAccessService</c> now reads the column as its own optional layer.</para>
///
/// <para><b>Why a grant.</b> 20260805105320 narrowed <c>nexora_tenant_app</c> and
/// <c>nexora_identity_app</c> to column-level SELECT on <c>platform."Tenants"</c>. A time zone
/// id is not confidential. It is NOT added to <c>TenantAccessGrantContract</c>: without it the
/// layer logs once and deadlines stay in UTC, which is not a reason to refuse to start.
/// <c>nexora_pipeline_app</c> holds table-level SELECT and needs nothing.</para>
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260929030000_TenantTimeZoneReadableByTenantPlane")]
public partial class TenantTimeZoneReadableByTenantPlane : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        // Guarded on the role existing: a single-role development database has none of these
        // roles, and GRANT to an absent role raises 42704.
        migrationBuilder.Sql("""
            DO $tenant_time_zone_grant$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                    RETURN;
                END IF;
                GRANT SELECT ("TimeZoneId") ON TABLE platform."Tenants"
                    TO nexora_tenant_app, nexora_identity_app;
            END $tenant_time_zone_grant$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        migrationBuilder.Sql("""
            DO $tenant_time_zone_revoke$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                    RETURN;
                END IF;
                REVOKE SELECT ("TimeZoneId") ON TABLE platform."Tenants"
                    FROM nexora_tenant_app, nexora_identity_app;
            END $tenant_time_zone_revoke$;
            """);
    }
}

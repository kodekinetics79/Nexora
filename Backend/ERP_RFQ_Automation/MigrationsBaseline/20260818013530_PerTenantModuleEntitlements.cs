using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Moves module and capability access from the PLAN onto the TENANT.
    ///
    /// <para><b>The defect.</b> <c>EntitlementService.CheckFeatureAsync</c> resolved every typed
    /// entitlement from <c>Plan.Features</c>, so what a customer could reach was a property of its
    /// price tier and not of the customer. Granting one tenant Procurement, or revoking Inventory
    /// from one tenant, was only expressible by moving them to a different plan — which also moved
    /// their seat cap, their monthly document quota and what they are charged. The console showed
    /// this honestly and offered no control: the tenant Entitlements tab printed the plan's chips
    /// read-only. The practical remedy was a plan per customer, which ends with a plan catalogue
    /// that no longer describes the commercial offer.</para>
    ///
    /// <para><b>After this migration a plan carries capacity and price; the tenant carries scope of
    /// access.</b> <c>Tenants."Entitlements"</c> is the authority. Plan features remain, and remain
    /// editable, as the TEMPLATE copied into a tenant at provisioning — a copy taken once, not an
    /// inheritance re-read per request, so editing a plan cannot silently re-open a module an
    /// operator deliberately revoked from a live customer.</para>
    ///
    /// <para><b>Nobody's access changes on the day this ships.</b> The backfill copies each
    /// tenant's current plan features into its own column, so every tenant resolves to exactly what
    /// it resolved to yesterday. Tenants with no plan land on <c>{}</c> — which is also what they
    /// effectively had, since <c>CheckFeatureAsync</c> denied every key for a plan-less tenant.</para>
    ///
    /// <para><b>The grant is the load-bearing half.</b> 20260805105320 narrowed the two tenant-plane
    /// roles to column-level SELECT on <c>platform."Tenants"</c>, so a projection reading a column
    /// they were never granted answers 42501 on EVERY tenant request — the exact shape of the
    /// <c>Plans."Features"</c> gap that <c>TenantAccessGrantContract</c> exists to catch. The new
    /// column is projected by <c>CoreQuery</c>, so it is granted here, in the same migration that
    /// creates it, and added to that contract's <c>RequiredColumns</c> so a boot without the grant
    /// refuses to start instead of refusing every customer. <c>nexora_pipeline_app</c> needs
    /// nothing: it holds table-level SELECT on <c>platform."Tenants"</c>.</para>
    /// </summary>
    public partial class PerTenantModuleEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Entitlements",
                schema: "platform",
                table: "Tenants",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            // Seed each tenant from the plan it is on today. Guarded on the column still holding
            // the store default so a re-run cannot overwrite a decision an operator has since made
            // through the console — the same reason the plan-completion backfill guards on
            // IS DISTINCT FROM.
            migrationBuilder.Sql("""
                UPDATE platform."Tenants" AS t
                SET "Entitlements" = COALESCE(p."Features", '{}'::jsonb)
                FROM platform."Plans" AS p
                WHERE t."PlanId" = p."Id"
                  AND t."Entitlements" = '{}'::jsonb;
                """);

            // Guarded on the role existing, like every other grant block in this project: a
            // single-role development database has none of these roles, and GRANT to an absent
            // role raises 42704 and takes the whole migration down with it.
            migrationBuilder.Sql("""
                DO $per_tenant_entitlement_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RETURN;
                    END IF;
                    GRANT SELECT ("Entitlements") ON TABLE platform."Tenants"
                        TO nexora_tenant_app, nexora_identity_app;
                END $per_tenant_entitlement_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The grant dies with the column; PostgreSQL drops column privileges on DROP COLUMN.
            migrationBuilder.DropColumn(
                name: "Entitlements",
                schema: "platform",
                table: "Tenants");
        }
    }
}

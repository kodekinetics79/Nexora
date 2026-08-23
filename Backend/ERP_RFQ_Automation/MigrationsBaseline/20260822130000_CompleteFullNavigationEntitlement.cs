using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Adds <c>capability.full-navigation</c> as <c>false</c> wherever it is absent — on every
    /// tenant's <c>"Entitlements"</c> and every plan's <c>"Features"</c>.
    ///
    /// <para>WHY A KEY ADDITION NEEDS A MIGRATION AT ALL. The activation control
    /// <c>entitlements.typed-hard-limits</c> asks whether every catalogue key is PRESENT on the
    /// tenant's grant (<c>TypedEntitlementCatalog.Keys.All(values.ContainsKey)</c>). Growing the
    /// catalogue therefore un-activates every activated tenant overnight unless their stored
    /// declarations are completed in the same release. 20260812150000 repaired exactly this shape
    /// of defect for plans created as <c>{}</c>, and its own design note says a key added later
    /// "becomes the next migration's problem". This is that migration, for the first key added
    /// since.</para>
    ///
    /// <para>Plans are completed too, not because the control reads them — it reads the tenant —
    /// but because a plan's <c>"Features"</c> is the provisioning template for FUTURE tenants,
    /// and writes complete against the catalogue of the day they run. A template completed here
    /// cannot produce a partial grant no matter which code path copies it.</para>
    ///
    /// <para>A COMPLETION, NOT A RELAXATION. The key is added as <c>false</c>: every tenant keeps
    /// the trimmed pilot rail it has today, and a declaration that somehow already carries the
    /// key keeps its value because the predicate only touches rows where it is absent. Granting
    /// the full rail remains an explicit, audited act on the tenant's Modules screen.</para>
    ///
    /// <para>Idempotent: a repeat run matches no rows and writes nothing.</para>
    /// </summary>
    /// <remarks>
    /// BOTH attributes are load-bearing. EF filters migration types on
    /// <c>[DbContext]</c> BEFORE reading the id, so a hand-written migration carrying only
    /// <c>[Migration]</c> is not rejected — it is silently never seen. 20260812150000 shipped
    /// that way once; <c>MigrationDiscoveryTests</c> now asks EF's own <c>IMigrationsAssembly</c>
    /// to prevent a recurrence.
    /// </remarks>
    [DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
    // Stamped AFTER 20260822120246_DispatchRoundOnProcurementOutbox, which merged first and is
    // already applied in production: EF would apply an earlier-stamped pending migration anyway,
    // but the history table should read in the order things actually happened.
    [Migration("20260822130000_CompleteFullNavigationEntitlement")]
    public partial class CompleteFullNavigationEntitlement : Migration
    {
        private const string Key = "capability.full-navigation";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // jsonb_exists is the function spelling of the `?` operator, used so this SQL never
            // meets a driver or tool that mistakes `?` for a parameter placeholder.
            migrationBuilder.Sql($$"""
                UPDATE platform."Tenants"
                SET "Entitlements" = "Entitlements" || '{"{{Key}}": false}'::jsonb
                WHERE NOT jsonb_exists("Entitlements", '{{Key}}');
                """);

            migrationBuilder.Sql($$"""
                UPDATE platform."Plans"
                SET "Features" = COALESCE("Features", '{}'::jsonb) || '{"{{Key}}": false}'::jsonb
                WHERE "Features" IS NULL OR NOT jsonb_exists("Features", '{{Key}}');
                """);
        }

        /// <summary>
        /// Deliberately empty, for the same reason as 20260812150000: "remove the key where it is
        /// false" cannot be told apart from a declaration that deliberately says off, and
        /// restoring an activation-blocking defect is not a service anyone wants.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

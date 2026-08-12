using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Fills every catalogue key that is absent from a plan's <c>"Features"</c> with <c>false</c>.
    ///
    /// <para>THE DEFECT. <c>PlatformOperationsController.ValidatePlanRequest</c> turned an absent
    /// or blank <c>Features</c> into <c>"{}"</c>, and <c>TypedEntitlementCatalog.TryParse</c>
    /// accepts that happily: it is valid JSON, carries no unknown key, and default-deny makes
    /// every capability read as off. Nothing about the plan looks wrong on any screen.</para>
    ///
    /// <para>But <c>TenantActivationPolicyService</c>'s <c>entitlements.typed-hard-limits</c> control
    /// asks a different question — whether every key is PRESENT:
    /// <c>TypedEntitlementCatalog.Keys.All(values.ContainsKey)</c>. A plan created without
    /// entitlements fails it permanently, and so does every tenant ever put on that plan. The
    /// tenant provisions cleanly, its other controls go green, and activation is refused by a
    /// control pointing at a plan nobody has touched since it was created. The only remedy was to
    /// open the plan and re-save it, which is not something the error says.</para>
    ///
    /// <para>It is not hypothetical. Plan <c>001 / Test</c> was created this way, carrying exactly
    /// <c>{}</c>, and tenant 3 (Noor Sons) sat unactivatable behind it with all eight provisioning
    /// steps recorded Succeeded.</para>
    ///
    /// <para>WHY A BACKFILL AS WELL AS THE CODE FIX. Writes now store the completed set, so no NEW
    /// plan can land partial. An existing plan would only heal when somebody happened to re-save
    /// it — and the operator has no reason to, because the plan screen shows nothing wrong. The
    /// blocked activation is on a different screen and names a control, not a plan.</para>
    ///
    /// <para>A COMPLETION, NOT A RELAXATION. Every key this adds is <c>false</c>, so no capability
    /// is granted to anyone. A plan that already declares a key keeps its value — the
    /// <c>COALESCE</c> reads the stored boolean first and only falls back to false when the key is
    /// absent. What the control actually gates on, positive seat / document / extraction limits,
    /// is untouched.</para>
    ///
    /// <para>Idempotent: re-running rewrites the same object, so it is safe on a database that has
    /// already been repaired and on one created after the code fix.</para>
    /// </summary>
    [Migration("20260812150000_CompleteTypedPlanEntitlements")]
    public partial class CompleteTypedPlanEntitlements : Migration
    {
        /// <summary>
        /// The closed catalogue, spelled as <c>TypedEntitlementCatalog.Keys</c> spells it. Written
        /// out rather than generated because a migration must keep meaning what it meant on the day
        /// it ran — if a key is added to the catalogue later, THIS migration must still produce the
        /// set it produced originally, and the new key becomes the next migration's problem.
        /// </summary>
        private const string CatalogueKeys =
            "'module.rfq','module.quotes','module.orders','module.procurement','module.inventory'," +
            "'capability.ai','capability.ocr','capability.api','capability.email-intake'," +
            "'capability.supplier-search','capability.integrations','capability.exports'," +
            "'capability.automation','capability.sso','capability.scim','capability.dedicated-resources'";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE platform."Plans" AS p
                SET "Features" = completed.value
                FROM (
                    SELECT plan."Id" AS id,
                           jsonb_object_agg(
                               catalogue.key,
                               COALESCE((plan."Features" -> catalogue.key)::boolean, false)
                           ) AS value
                    FROM platform."Plans" AS plan
                    CROSS JOIN unnest(ARRAY[{CatalogueKeys}]) AS catalogue(key)
                    GROUP BY plan."Id", plan."Features"
                ) AS completed
                WHERE p."Id" = completed.id
                  -- Only rows that are actually incomplete, so a repeat run writes nothing and the
                  -- statement reports 0 rather than rewriting every plan on every deploy.
                  AND p."Features" IS DISTINCT FROM completed.value;
                """);
        }

        /// <summary>
        /// Deliberately empty. The inverse is "remove keys that are false", which cannot be
        /// distinguished from a plan that deliberately declares a capability off — and restoring
        /// the defect is not a service anyone wants. The Up is idempotent and additive; there is
        /// nothing to undo.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

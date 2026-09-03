namespace ERP_RFQ_Automation.Billing;

/// <summary>
/// The currency Nexora bills its tenants in. A PLATFORM constant, not a tenant attribute.
///
/// <para>v1 billing is USD-only by construction: plan base prices are <c>MonthlyPriceUsd</c>,
/// every rate card is refused in any other currency on create and update, and statement
/// computation 409s on a legacy non-USD card. That is the whole reason it can be a constant —
/// there is no per-tenant billing currency column, and none is needed until multi-currency
/// billing exists (which needs a column and a migration).</para>
///
/// <para>It is deliberately NOT <c>Tenant.BaseCurrencyCode</c>. That column is the tenant's
/// FUNCTIONAL currency — the base its own quotes, ledger and FX conversions are computed in —
/// and is seeded into the tenant's <c>Currency</c> table. Comparing it to "USD" at activation is
/// what made a Saudi client quoting in SAR unactivatable as Production.</para>
/// </summary>
public static class PlatformBillingCurrency
{
    public const string Code = "USD";

    public static bool Matches(string? currency) =>
        string.Equals(currency?.Trim(), Code, StringComparison.OrdinalIgnoreCase);
}

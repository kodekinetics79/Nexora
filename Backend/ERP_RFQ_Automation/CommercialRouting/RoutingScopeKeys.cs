using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.CommercialRouting;

/// <summary>
/// What one ownership scope's key was derived from for a single RFQ — and, when nothing could
/// be derived, why not.
///
/// <para>A null <see cref="Key"/> is a first-class outcome, not a failure to be papered over.
/// <c>DeterministicRoutingEngine.ScopeMatches</c> refuses a blank key on either side, so an
/// underived scope matches NOTHING. That is deliberate: an ownership rule that fires on every
/// RFQ because both sides of the comparison were empty is far worse than one that never fires,
/// because it silently outranks every rule beneath it in the precedence order.</para>
/// </summary>
public sealed record ScopeKeyDerivation(OwnershipScope Scope, string? Key, string Source)
{
    public bool IsDerived => !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// The region fields of a customer row. The caller is responsible for having read it under the
/// routing tenant, so nothing here can leak across business units.
/// </summary>
public sealed record CustomerRegionEvidence(
    string? ShippingState, string? ShippingCity, string? BillingState, string? BillingCity);

/// <summary>
/// One row of a tenant's region master flattened to "this wording means this region" — a
/// <c>SetState</c> code or name, or a <c>SetCity</c> name standing in for its parent state.
/// </summary>
public sealed record TerritoryRegionAlias(string Alias, string RegionName);

/// <summary>
/// Deterministic derivation of the ownership scope keys for one RFQ (FR-RFQ-07).
///
/// <para>Pure by construction: every input is a plain value the caller has already read under
/// the routing tenant. Nothing here infers, scores or guesses — a scope either has a real source
/// on this RFQ or it reports that it has none.</para>
/// </summary>
public static partial class RoutingScopeKeys
{
    public const string CallerSupplied = "caller-supplied";

    public const string BranchUnavailable =
        "UNAVAILABLE: the business unit has no BusinessUnitCode.";

    public const string ProductCategoryUnavailable =
        "UNAVAILABLE: no lead item on this RFQ carries a CommodityProduct.";

    public const string TerritoryUnavailable =
        "UNAVAILABLE: this RFQ states no delivery location and the customer recorded on the " +
        "lead carries no shipping or billing region. Nothing is inferred from unrelated data.";

    /// <summary>
    /// Why a KeyAccountTeam key is unavailable for a particular RFQ, now that the scope is
    /// derivable in principle.
    ///
    /// <para>This constant used to read UNDERIVABLE and explain that NOTHING in the model linked a
    /// customer to a team: <c>SalesTeamMembership</c> maps users to teams, so the only path from an
    /// RFQ to a team ran customer → ownership → user → team, which is circular — it would use the
    /// ownership rows to choose between the ownership rows. <c>Customer.AccountTeamId</c>
    /// (FR-CST-02) supplies the missing customers-to-teams edge, so the scope now derives from the
    /// customer recorded on the lead. It is UNAVAILABLE, per RFQ, only when this RFQ names no
    /// customer or that customer has been put in no account team.</para>
    /// </summary>
    public const string KeyAccountTeamUnavailable =
        "UNAVAILABLE: this RFQ names no customer, or the customer recorded on the lead has no " +
        "account team assigned. Nothing is inferred from ownership rows — this scope exists to " +
        "select between them.";

    /// <summary>The branch is the routing tenant's own business unit code.</summary>
    public static ScopeKeyDerivation Branch(string? businessUnitCode)
    {
        var code = CollapseWhitespace(businessUnitCode);
        return new(OwnershipScope.Branch, Blank(code), code.Length > 0
            ? "business_units.BusinessUnitCode"
            : BranchUnavailable);
    }

    /// <summary>The category is the first commodity stated on the RFQ's own lines.</summary>
    public static ScopeKeyDerivation ProductCategory(string? commodityProduct)
    {
        var category = CollapseWhitespace(commodityProduct);
        return new(OwnershipScope.ProductCategory, Blank(category), category.Length > 0
            ? "lead_items.CommodityProduct"
            : ProductCategoryUnavailable);
    }

    /// <summary>
    /// The territory is the region this RFQ will be delivered into, taken from the first source
    /// that actually states one — in strict precedence, because a nearer source is never
    /// overruled by a further one:
    ///
    /// <list type="number">
    /// <item><c>Leads.DeliveryLocation</c> — what the buyer wrote on this inquiry (FR-RFQ-04).
    /// It is the requirement's own source and the only RFQ-specific one.</item>
    /// <item>the shipping, then the billing, region of the customer RECORDED on the lead — the
    /// customer's own registered address, used only when this RFQ states nothing.</item>
    /// </list>
    ///
    /// <para>The stated wording is then resolved against the tenant's region masters so that a
    /// rule written on a province still fires when the buyer named a city inside it: an exact
    /// match on a state code or state name wins, then an exact match on a city name, which
    /// resolves to that city's state. Matching is whitespace- and case-insensitive equality
    /// only — never a prefix, a substring or a distance.</para>
    ///
    /// <para>When an alias resolves to more than one distinct region (two cities of the same
    /// name in different states) the masters cannot settle it, so the wording is kept as stated
    /// rather than a region being picked. When no master claims the wording at all, the wording
    /// IS the territory: region masters are optional configuration, and refusing to route on
    /// what the buyer plainly stated would be a worse answer than routing on it.</para>
    /// </summary>
    public static ScopeKeyDerivation Territory(
        string? deliveryLocation,
        CustomerRegionEvidence? customer,
        IReadOnlyCollection<TerritoryRegionAlias> stateAliases,
        IReadOnlyCollection<TerritoryRegionAlias> cityAliases)
    {
        foreach (var (source, value) in TerritoryEvidence(deliveryLocation, customer))
        {
            var stated = CollapseWhitespace(value);
            if (stated.Length == 0) continue;

            if (UniqueRegion(stated, stateAliases) is { } byState)
                return new(OwnershipScope.Territory, byState, $"{source} -> set_states.StateName");

            if (UniqueRegion(stated, cityAliases) is { } byCity)
                return new(OwnershipScope.Territory, byCity, $"{source} -> set_cities -> set_states.StateName");

            return new(OwnershipScope.Territory, stated, $"{source} (no region master match)");
        }

        return new(OwnershipScope.Territory, null, TerritoryUnavailable);
    }

    /// <summary>
    /// The account team of the customer recorded on this RFQ's lead, by NAME — because an
    /// ownership rule's <c>ScopeKey</c> is the wording a person typed when they wrote the rule, and
    /// every other scope in this type is keyed the same way (a branch code, a commodity, a region
    /// name). Keying it by team id would make the rules unwritable without a lookup.
    ///
    /// <para>A blank or absent team name yields a NULL key, and
    /// <c>DeterministicRoutingEngine.ScopeMatches</c> refuses a blank key on either side — so an
    /// unassigned customer matches NO KeyAccountTeam rule rather than matching every one of them.</para>
    /// </summary>
    public static ScopeKeyDerivation KeyAccountTeam(string? accountTeamName)
    {
        var team = CollapseWhitespace(accountTeamName);
        return new(OwnershipScope.KeyAccountTeam, Blank(team), team.Length > 0
            ? "customers.AccountTeamId -> Teams.TeamName"
            : KeyAccountTeamUnavailable);
    }

    /// <summary>
    /// Wraps keys a caller supplied explicitly, so an operator-driven route carries the same
    /// provenance record on its decision as an automatically derived one.
    /// </summary>
    public static IReadOnlyList<ScopeKeyDerivation> FromSuppliedKeys(
        IReadOnlyDictionary<OwnershipScope, string?> keys) => keys
        .OrderBy(pair => pair.Key)
        .Select(pair => new ScopeKeyDerivation(
            pair.Key, Blank(CollapseWhitespace(pair.Value)), CallerSupplied))
        .ToList();

    /// <summary>
    /// Trims and collapses runs of internal whitespace. Scope keys are compared as text, and a
    /// region copied out of a PDF ("Eastern  Province") must not miss a rule that was typed by
    /// hand ("Eastern Province"). Applied to BOTH sides of every comparison, so it can only
    /// remove false negatives — it never makes two different regions equal.
    /// </summary>
    public static string CollapseWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : WhitespaceRun().Replace(value.Trim(), " ");

    private static IEnumerable<(string Source, string? Value)> TerritoryEvidence(
        string? deliveryLocation, CustomerRegionEvidence? customer)
    {
        yield return ("leads.DeliveryLocation", deliveryLocation);
        if (customer is null) yield break;
        yield return ("customers.ShippingState", customer.ShippingState);
        yield return ("customers.ShippingCity", customer.ShippingCity);
        yield return ("customers.BillingState", customer.BillingState);
        yield return ("customers.BillingCity", customer.BillingCity);
    }

    /// <summary>
    /// The one region an alias names, or null when the masters name none or more than one.
    /// Distinctness is evaluated over the whole collection rather than taking the first hit, so
    /// the answer does not depend on the order rows came back from the database.
    /// </summary>
    private static string? UniqueRegion(
        string stated, IReadOnlyCollection<TerritoryRegionAlias> aliases)
    {
        var regions = aliases
            .Where(alias => string.Equals(
                CollapseWhitespace(alias.Alias), stated, StringComparison.OrdinalIgnoreCase))
            .Select(alias => CollapseWhitespace(alias.RegionName))
            .Where(region => region.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return regions.Length == 1 ? regions[0] : null;
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}

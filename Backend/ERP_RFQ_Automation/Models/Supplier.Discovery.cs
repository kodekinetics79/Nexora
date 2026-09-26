namespace ERP_RFQ_Automation.Models;

// Where a supplier lives on the internet and what kind of company it is. Kept in a partial so the
// scaffolded Supplier.cs stays untouched; column configuration lives in
// Models/ErpRfqAutomationContext.SupplierDiscovery.cs (ConfigureSupplierDiscoveryModel).
public partial class Supplier
{
    /// <summary>
    /// The supplier's website, as an absolute URL. Internet supplier discovery matches a search hit
    /// against this by domain, which is how a supplier the company already knows always outranks a
    /// stranger with the same name, and how adopting the same hit twice yields one supplier.
    /// </summary>
    public string? Website { get; set; }

    /// <summary>
    /// What kind of company this is in the supply chain — one of <see cref="SupplierRoles"/>. Null
    /// means nobody has said, which is the state every existing supplier starts in. Set from the
    /// internet search when a supplier is adopted from it, and editable on the supplier form.
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// The supplier's place in the supply chain. A manufacturer makes the part; a distributor is
/// appointed by the maker to stock and sell it; a reseller sells it without an appointment. The
/// internet search ranks hits in that order because it is the order a buyer trusts them in.
/// </summary>
public static class SupplierRoles
{
    public const string Manufacturer = "Manufacturer";
    public const string Distributor = "Distributor";
    public const string Reseller = "Reseller";
    public const string Unknown = "Unknown";

    /// <summary>Every permitted value. The CHECK constraint and the API both read this list.</summary>
    public static readonly IReadOnlyList<string> All = [Manufacturer, Distributor, Reseller, Unknown];

    /// <summary>Null (not stated) is permitted; anything else must be a listed role.</summary>
    public static bool IsValid(string? role) => role is null || All.Contains(role);

    /// <summary>
    /// Accepts the spelling a person types ("distributor", "DISTRIBUTOR") and returns the stored
    /// spelling, or null for blank. An unrecognised word comes back unchanged so the caller can name
    /// it in the refusal.
    /// </summary>
    public static string? Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var trimmed = role.Trim();
        return All.FirstOrDefault(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }

    /// <summary>Rank order for the internet search: manufacturer first, then distributors, then resellers.</summary>
    public static int RankOrder(string? role) => role switch
    {
        Manufacturer => 0,
        Distributor => 1,
        Reseller => 2,
        _ => 3
    };
}

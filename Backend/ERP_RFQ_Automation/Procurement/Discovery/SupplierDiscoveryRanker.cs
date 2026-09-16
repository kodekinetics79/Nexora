using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>A supplier already on this company's list, reduced to what the ranker needs to recognise it in a hit.</summary>
public sealed record KnownSupplier(long Id, string Name, string? Website)
{
    public string? Domain => SupplierDiscoveryClassifier.RegistrableDomain(Website);
}

/// <summary>
/// Orders hits the way a buyer trusts them: the maker before its appointed distributors before
/// everyone else; within a role, a supplier this company already knows before a stranger, a Saudi
/// company before a foreign one, and otherwise the order the search engine gave. Then pages.
/// </summary>
public static class SupplierDiscoveryRanker
{
    public const int MinLimit = 10;
    public const int MaxLimit = 50;

    /// <summary>
    /// Ranks every cached hit against the company's current supplier list. A hit is "known" when
    /// its domain is a known supplier's website domain, or — for suppliers with no website on
    /// record — when the names match word for word.
    /// </summary>
    public static IReadOnlyList<SupplierDiscoveryHit> Rank(
        IEnumerable<SupplierDiscoveryStoredHit> hits, IReadOnlyCollection<KnownSupplier> knownSuppliers)
    {
        var byDomain = new Dictionary<string, KnownSupplier>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, KnownSupplier>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplier in knownSuppliers.OrderBy(x => x.Id))
        {
            if (supplier.Domain is { } domain) byDomain.TryAdd(domain, supplier);
            var name = NormaliseName(supplier.Name);
            if (name.Length > 0) byName.TryAdd(name, supplier);
        }

        return hits
            .Select(hit =>
            {
                var known = byDomain.TryGetValue(hit.Domain, out var byWebsite) ? byWebsite
                    : byName.TryGetValue(NormaliseName(hit.Name), out var byTitle) ? byTitle
                    : null;
                return (Hit: new SupplierDiscoveryHit(hit.Id, hit.Name, hit.Website, hit.Domain, hit.Role, hit.Country,
                    hit.Why, hit.ContactEmail, known?.Id), hit.ProviderOrder);
            })
            .OrderBy(x => SupplierRoles.RankOrder(x.Hit.Role))
            .ThenBy(x => x.Hit.ExistingSupplierId.HasValue ? 0 : 1)
            .ThenBy(x => x.Hit.Country == "SA" ? 0 : 1)
            .ThenBy(x => x.ProviderOrder)
            .Select(x => x.Hit)
            .ToArray();
    }

    public static IReadOnlyList<SupplierDiscoveryHit> Page(IReadOnlyList<SupplierDiscoveryHit> ranked, int offset, int limit)
        => ranked.Skip(offset).Take(limit).ToArray();

    public static void ValidatePage(int offset, int limit)
    {
        if (offset < 0)
            throw new ProcurementValidationException("The page start cannot be negative.");
        if (limit is < MinLimit or > MaxLimit)
            throw new ProcurementValidationException($"Ask for between {MinLimit} and {MaxLimit} suppliers at a time.");
    }

    private static string NormaliseName(string? name)
        => new(string.Concat((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit)));
}

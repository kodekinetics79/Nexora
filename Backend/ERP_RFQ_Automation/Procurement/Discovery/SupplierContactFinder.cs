using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>
/// Finds the address a supplier wants enquiries sent to. A search hit rarely carries an email in
/// its excerpt, so a company found on the internet used to arrive with "No contact email" and
/// could not be asked until the rep went looking. This asks the same search engine one more
/// question — "acme.com contact email sales" — and reads the addresses out of the answers.
/// </summary>
public interface ISupplierContactFinder
{
    /// <summary>The best enquiry address on the supplier's own domain, or null when none was found. Never throws.</summary>
    Task<string?> FindEmailAsync(string domain, string supplierName, CancellationToken ct);
}

public sealed class SupplierContactFinder : ISupplierContactFinder
{
    /// <summary>Answers per lookup. Five is enough: the contact page and the company's directory listings come first.</summary>
    public const int ResultsPerLookup = 5;

    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    // Directory sites print the pattern a company's addresses follow, not an address.
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "jdoe", "john.doe", "j.doe", "jd", "flast", "first.last", "firstname.lastname", "f.last", "first",
        "last", "name", "example", "email", "user", "test", "yourname", "your.name", "someone", "firstlast", "fl"
    };

    // Addresses that exist to refuse mail, or that belong to a department that does not sell.
    private static readonly Regex NeverAsk = new(
        @"^(no-?reply|donotreply|do-not-reply|privacy|abuse|unsubscribe|webmaster|postmaster|hostmaster|dpo|legal|hr|careers?|jobs?|recruit(ing|ment)?|press|media|investors?|ir|security|billing|accounts?payable|ap|payroll)($|[._\-+])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISupplierWebSearchProvider _provider;
    private readonly ILogger<SupplierContactFinder> _log;

    public SupplierContactFinder(ISupplierWebSearchProvider provider, ILogger<SupplierContactFinder> log)
    {
        _provider = provider;
        _log = log;
    }

    /// <summary>"imssupply.com contact email sales" — the shape that surfaced sales@ addresses on every domain tried on 2026-09-16.</summary>
    public static string Query(string domain) => $"{domain} contact email sales";

    public async Task<string?> FindEmailAsync(string domain, string supplierName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        try
        {
            var results = await _provider.SearchAsync(Query(domain), ResultsPerLookup, ct);
            var email = PickEmail(domain, results);
            _log.LogInformation("SupplierDiscovery contact lookup for {Domain}: {Outcome}.", domain,
                email is null ? "no address on its domain" : "address found");
            return email;
        }
        catch (SupplierWebSearchException exception)
        {
            _log.LogWarning(exception, "SupplierDiscovery contact lookup for {Domain} did not answer.", domain);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("SupplierDiscovery contact lookup for {Domain} timed out.", domain);
            return null;
        }
    }

    /// <summary>
    /// The one address to store, from everything the answers mention: on the supplier's own
    /// domain (or its sibling, cellpacksolutions.co.uk for cellpacksolutions.com), never a
    /// directory's placeholder, never a no-reply or HR box, and the sales desk before a named
    /// person. Pure string work so every rule has a test.
    /// </summary>
    public static string? PickEmail(string domain, IEnumerable<WebSearchResult> results)
    {
        var wanted = SupplierDiscoveryClassifier.RegistrableDomain(domain) ?? domain.ToLowerInvariant();
        var wantedLabel = wanted.Split('.')[0];

        string? best = null;
        var bestScore = int.MaxValue;
        foreach (var result in results)
        {
            foreach (Match match in Email.Matches(result.Title + " " + result.Snippet))
            {
                var candidate = match.Value.TrimEnd('.').ToLowerInvariant();
                var at = candidate.IndexOf('@');
                var local = candidate[..at];
                var host = candidate[(at + 1)..];
                if (host.EndsWith(".png") || host.EndsWith(".jpg") || host.EndsWith(".svg") || host.EndsWith(".gif")) continue;

                var hostDomain = SupplierDiscoveryClassifier.RegistrableDomain(host) ?? host;
                var sameDomain = hostDomain.Equals(wanted, StringComparison.OrdinalIgnoreCase);
                var sibling = !sameDomain && hostDomain.Split('.')[0].Equals(wantedLabel, StringComparison.OrdinalIgnoreCase) && wantedLabel.Length >= 5;
                if (!sameDomain && !sibling) continue;
                if (Placeholders.Contains(local) || NeverAsk.IsMatch(local)) continue;

                var score = Rank(local) * 2 + (sameDomain ? 0 : 1);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }
        return best;
    }

    /// <summary>Lower is better: the desk that answers enquiries first, a named person last.</summary>
    internal static int Rank(string local)
    {
        var key = local.ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty).Replace(".", string.Empty);
        if (key.StartsWith("sales")) return 0;
        if (key.StartsWith("rfq") || key.StartsWith("tender") || key.StartsWith("bid")) return 1;
        if (key.StartsWith("quot")) return 2;
        if (key.StartsWith("enquir") || key.StartsWith("inquir")) return 3;
        if (key.StartsWith("info")) return 4;
        if (key.StartsWith("contact") || key == "hello" || key == "mail" || key == "office") return 5;
        if (key.StartsWith("export") || key.StartsWith("international") || key.StartsWith("gulf") || key.StartsWith("mea")) return 6;
        if (key.StartsWith("order")) return 7;
        if (key.StartsWith("support") || key.StartsWith("customerservice") || key.StartsWith("service")) return 8;
        if (key.StartsWith("purchas") || key.StartsWith("procure") || key.StartsWith("buy")) return 10;
        return 9;
    }
}

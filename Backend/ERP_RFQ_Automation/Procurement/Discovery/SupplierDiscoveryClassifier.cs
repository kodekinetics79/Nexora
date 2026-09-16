using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>
/// One hit as it is stored in the 30-day cache: everything the search decided about the page, and
/// nothing about this company's supplier list. Whether the domain is already a known supplier is
/// looked up again on every read, because the supplier list changes — most obviously when the rep
/// adopts a hit — and the answer must follow it.
/// </summary>
public sealed record SupplierDiscoveryStoredHit(
    string Id,
    string Name,
    string Website,
    string Domain,
    string Role,
    string? Country,
    string Why,
    string? ContactEmail,
    int ProviderOrder);

/// <summary>
/// Turns a raw search result into a hit the rep can read: which company, what kind, where, why it
/// is here. Pure string work, deliberately, so every rule has a test that runs in milliseconds.
/// </summary>
public static class SupplierDiscoveryClassifier
{
    /// <summary>
    /// Hosts that are never a supplier the rep can ask. Marketplaces list everybody and answer for
    /// nobody; social networks, video and encyclopaedias describe suppliers without being one; a PDF
    /// host is a datasheet, not a company. Kept as one list in code so a new noise source is a
    /// one-line change with a test beside it.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedDomains =
    [
        "alibaba.com", "aliexpress.com", "amazon.com", "amazon.sa", "amazon.ae", "ebay.com", "ebay.co.uk",
        "made-in-china.com", "indiamart.com", "tradekey.com", "globalsources.com", "tradeindia.com",
        "exportersindia.com", "ec21.com", "dhgate.com", "noon.com", "desertcart.com",
        "linkedin.com", "facebook.com", "instagram.com", "twitter.com", "x.com", "youtube.com", "tiktok.com",
        "pinterest.com", "reddit.com", "quora.com",
        "wikipedia.org", "wikimedia.org",
        "scribd.com", "manualslib.com", "pdfcoffee.com", "datasheetspdf.com", "alldatasheet.com",
        "datasheets.com", "docplayer.net", "yumpu.com", "issuu.com", "academia.edu", "researchgate.net"
    ];

    // Second-level public suffixes whose registrable domain is three labels long ("acme.com.sa").
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.sa", "net.sa", "org.sa", "edu.sa", "gov.sa", "med.sa", "sch.sa",
        "co.ae", "net.ae", "org.ae", "ac.ae",
        "com.qa", "net.qa", "org.qa", "com.kw", "net.kw", "org.kw", "com.om", "co.om", "net.om",
        "com.bh", "net.bh", "org.bh", "com.eg", "com.jo", "com.lb", "com.tr", "com.pk", "com.bd",
        "co.uk", "org.uk", "ac.uk", "co.in", "net.in", "org.in", "co.za", "com.au", "net.au", "com.br",
        "com.mx", "com.sg", "com.my", "com.hk", "co.jp", "co.kr", "com.cn", "com.tw", "co.nz", "com.ar",
        "com.co", "com.pe", "com.ph", "co.id", "com.vn", "com.ng", "co.ke"
    };

    private static readonly Dictionary<string, string> GulfTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sa"] = "SA", ["ae"] = "AE", ["qa"] = "QA", ["kw"] = "KW", ["om"] = "OM", ["bh"] = "BH"
    };

    private static readonly Regex SaudiPlaces = new(
        @"\b(Saudi Arabia|Saudi|KSA|Riyadh|Dammam|Jeddah|Jubail|Yanbu|Khobar|Dhahran|Makkah|Mecca|Madinah|Medina|Tabuk|Abha|Hail|Qassim|Al[- ]?Ahsa|Hofuf|Ras Tanura)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DistributorWords = new(
        @"\b((authori[sz]ed|official)\s+(distributor|dealer|partner|reseller|stockist)|distributor|stockist|channel\s+partner|distribution\s+partner|value[- ]added\s+reseller)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    private static readonly Regex TitleSeparators = new(@"\s+[|\-–—:»·]\s+|\s+::\s+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9]", RegexOptions.Compiled);

    /// <summary>
    /// "www.acme.com.sa/products?x=1" → "acme.com.sa"; "https://shop.acme.com/a" → "acme.com".
    /// Null when the URL has no host worth the name.
    /// </summary>
    public static string? RegistrableDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.HostNameType is not (UriHostNameType.Dns))
            return null;
        var host = uri.Host.ToLowerInvariant().TrimEnd('.');
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        var lastTwo = string.Join('.', labels[^2..]);
        if (TwoLevelSuffixes.Contains(lastTwo) && labels.Length >= 3)
            return string.Join('.', labels[^3..]);
        return lastTwo;
    }

    public static bool IsExcluded(string domain)
        => ExcludedDomains.Any(x => domain.Equals(x, StringComparison.OrdinalIgnoreCase)
                                    || domain.EndsWith("." + x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable id for one company across searches: the domain, hashed, so the id never carries a URL.</summary>
    public static string HitId(string domain)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(domain.ToLowerInvariant()))).ToLowerInvariant();

    /// <summary>
    /// Null when the result is noise (no usable host, or an excluded host). Otherwise the hit as it
    /// will be cached. <paramref name="providerOrder"/> is the position the search engine gave it.
    /// </summary>
    public static SupplierDiscoveryStoredHit? Classify(
        WebSearchResult result, IReadOnlyList<string> makers, IReadOnlyList<string> partNumbers, int providerOrder)
    {
        var domain = RegistrableDomain(result.Url);
        if (domain is null || IsExcluded(domain)) return null;
        var website = Website(result.Url, domain);
        var role = Role(domain, result.Title, result.Snippet, makers);
        return new SupplierDiscoveryStoredHit(
            HitId(domain),
            CompanyName(result.Title, domain),
            website,
            domain,
            role,
            Country(domain, result.Title + " " + result.Snippet),
            Why(role, result.Snippet, makers, partNumbers),
            FirstEmail(result.Snippet),
            providerOrder);
    }

    /// <summary>
    /// Manufacturer when the maker's name is the site's domain ("schneider-electric.com") or the
    /// site calls itself by the maker's name ("… | Schneider Electric", which is how se.com titles
    /// its pages); Distributor when the page says it is one; else Reseller. The domain check runs
    /// first and the site-name check after the distributor words, so a distributor page whose
    /// title merely mentions the maker it stocks ("Schneider LV431831 | Gulf Switchgear") is not
    /// promoted to the maker itself.
    /// </summary>
    public static string Role(string domain, string title, string snippet, IReadOnlyList<string> makers)
    {
        var domainKey = NonAlphanumeric.Replace(domain.ToLowerInvariant(), string.Empty);
        foreach (var key in MakerKeys(makers))
            if (domainKey.Contains(key, StringComparison.Ordinal))
                return SupplierRoles.Manufacturer;

        if (DistributorWords.IsMatch(title) || DistributorWords.IsMatch(snippet))
            return SupplierRoles.Distributor;

        var siteName = NonAlphanumeric.Replace(CompanyName(title, domain).ToLowerInvariant(), string.Empty);
        foreach (var maker in makers)
        {
            var whole = NonAlphanumeric.Replace(maker.ToLowerInvariant(), string.Empty);
            if (whole.Length >= 3 && siteName.StartsWith(whole, StringComparison.Ordinal))
                return SupplierRoles.Manufacturer;
        }

        return SupplierRoles.Reseller;
    }

    /// <summary>ISO code from a Gulf country TLD, "SA" when the text names Saudi Arabia or one of its cities; else null.</summary>
    public static string? Country(string domain, string text)
    {
        var tld = domain[(domain.LastIndexOf('.') + 1)..];
        if (GulfTlds.TryGetValue(tld, out var code)) return code;
        return SaudiPlaces.IsMatch(text) ? "SA" : null;
    }

    public static string? FirstEmail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Email.Match(text);
        if (!match.Success) return null;
        var email = match.Value.TrimEnd('.').ToLowerInvariant();
        // Image and file names look like addresses to a regex ("logo@2x.png") and never are.
        return email.EndsWith(".png", StringComparison.Ordinal) || email.EndsWith(".jpg", StringComparison.Ordinal)
            ? null : email;
    }

    /// <summary>
    /// One sentence in the rep's words. Names the maker and part when the excerpt does, says what
    /// the page calls itself when it claims a distributorship, and otherwise quotes the excerpt's
    /// first sentence, clipped.
    /// </summary>
    public static string Why(string role, string snippet, IReadOnlyList<string> makers, IReadOnlyList<string> partNumbers)
    {
        var text = Whitespace.Replace(snippet ?? string.Empty, " ").Trim();
        var maker = makers.FirstOrDefault(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        var part = partNumbers.FirstOrDefault(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

        if (part is not null && maker is not null)
            return $"Lists {maker} {part} on its page";
        if (part is not null)
            return $"Lists {part} on its page";
        if (role == SupplierRoles.Distributor && maker is not null)
            return $"Describes itself as a {maker} distributor";
        if (role == SupplierRoles.Distributor)
            return "Describes itself as a distributor";
        if (role == SupplierRoles.Manufacturer && maker is not null)
            return $"{maker}'s own website";
        if (maker is not null)
            return $"Mentions {maker} on its page";
        if (text.Length == 0)
            return "Came up in the internet search for this part";

        var sentenceEnd = text.IndexOfAny(['.', '!', '?']);
        var sentence = sentenceEnd > 20 ? text[..(sentenceEnd + 1)] : text;
        return sentence.Length <= 140 ? sentence : sentence[..137].TrimEnd() + "…";
    }

    /// <summary>
    /// "Schneider LV431831 | Gulf Switchgear Trading Co." → "Gulf Switchgear Trading Co."; sites put
    /// their own name in the last title segment. A title with no separator is the name. An empty
    /// title falls back to the domain.
    /// </summary>
    public static string CompanyName(string? title, string domain)
    {
        var cleaned = Whitespace.Replace(title ?? string.Empty, " ").Trim();
        if (cleaned.Length == 0) return domain;
        var segments = TitleSeparators.Split(cleaned).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        var name = segments.Length > 1 ? segments[^1] : cleaned;
        foreach (var filler in new[] { "Official Site", "Official Website", "Home", "Homepage" })
            if (name.Equals(filler, StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
                name = segments[^2];
        return name.Length <= 200 ? name : name[..200].TrimEnd();
    }

    /// <summary>The page's origin — "https://gulfswitchgear.com" — never the deep link the search happened to land on.</summary>
    public static string Website(string url, string domain)
    {
        var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}"
            : $"https://{domain}";
    }

    private static IEnumerable<string> MakerKeys(IReadOnlyList<string> makers)
    {
        foreach (var maker in makers)
        {
            var whole = NonAlphanumeric.Replace(maker.ToLowerInvariant(), string.Empty);
            if (whole.Length >= 3) yield return whole;
            var firstWord = NonAlphanumeric.Replace(maker.Split(' ', 2)[0].ToLowerInvariant(), string.Empty);
            if (firstWord.Length >= 4 && firstWord != whole) yield return firstWord;
        }
    }

}

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
        "datasheets.com", "docplayer.net", "yumpu.com", "issuu.com", "academia.edu", "researchgate.net",
        // Company registers, business directories, archives and patent libraries describe a
        // company without selling anything. Seen live on 2026-09-16 for "JAMES NORTH NS 301".
        "gracesguide.co.uk", "sciencemuseumgroup.org.uk", "freepatentsonline.com", "patents.google.com",
        "nsnlookup.com", "listofcompaniesin.com", "webportunities.net", "opencorporates.com", "dnb.com",
        "zoominfo.com", "crunchbase.com", "bloomberg.com", "kompass.com", "europages.com", "thomasnet.com",
        "yellowpages.com", "yelp.com", "glassdoor.com", "indeed.com", "companieshouse.gov.uk", "bizearch.com",
        "cybo.com", "hotfrog.com", "manta.com", "tofler.in", "zaubacorp.com"
    ];

    // Labels that mark a host as public sector, academic or a museum, whichever country it is in:
    // "company-information.service.gov.uk", "x.gov.sa", "y.edu", "z.museum".
    private static readonly HashSet<string> NonCommercialLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gov", "edu", "mil", "museum", "ac"
    };

    // A page that is about companies rather than by one: registers, directories, archives, patents.
    private static readonly Regex DirectoryWords = new(
        @"\b(list of companies|company information|companies house|cage code|patents?|museum|grace'?s guide|business directory|supplier directory|company database|yellow pages|encyclop(a|ae)dia|wiki)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // An article about buying the product is not a company selling it.
    private static readonly Regex ArticleTitle = new(
        @"^(how to|what is|what are|why |where to|top \d+|best \d+|\d+ best|guide to|a guide|the ultimate|everything you)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A page that says the company makes the product itself.
    private static readonly Regex ManufacturerWords = new(
        @"\b(manufacturer|manufacturers|manufacturing company|we manufacture|our factory|factory direct|oem manufacturer)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A segment that ends in what the company does or sells is a tagline, not its name:
    // "welding gloves manufacturer", "Industrial Gloves Supplier", "Ready Stock".
    private static readonly Regex TaglineEnding = new(
        @"\b(manufacturers?|suppliers?|distributors?|stockists?|dealers?|traders?|exporters?|importers?|wholesalers?|stock|store|shop|online|catalogue|catalog|price|prices|sale)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Titles that are the page's function, not the company's name.
    private static readonly Regex GenericTitle = new(
        @"^(welcome( to .*)?|home|homepage|data ?sheets?|quotes?, address, contact|contact( us)?|products?|catalog(ue)?s?|untitled|loading.*|index|about( us)?|shop|store|login|sign in|search results?|official (site|website))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        // "co.zw", "com.ng", "org.pk": a generic second level under a two-letter country is a public
        // suffix whatever the country, so the list above only needs the exceptions to that shape.
        var genericUnderCountry = labels[^1].Length == 2
            && labels[^2] is "co" or "com" or "net" or "org" or "ac" or "gov" or "edu" or "ltd" or "plc";
        if ((TwoLevelSuffixes.Contains(lastTwo) || genericUnderCountry) && labels.Length >= 3)
            return string.Join('.', labels[^3..]);
        return lastTwo;
    }

    public static bool IsExcluded(string domain)
        => ExcludedDomains.Any(x => domain.Equals(x, StringComparison.OrdinalIgnoreCase)
                                    || domain.EndsWith("." + x, StringComparison.OrdinalIgnoreCase))
           || domain.Split('.').Any(NonCommercialLabels.Contains);

    /// <summary>A register, directory, archive or patent page: about companies, not one of them.</summary>
    public static bool IsDirectoryPage(string title, string snippet)
        => DirectoryWords.IsMatch(title) || DirectoryWords.IsMatch(FirstWordsOf(snippet, 30)) || ArticleTitle.IsMatch(title.Trim());

    /// <summary>
    /// True when the page mentions what was asked about: the maker's search name, the part number
    /// (as one token, so "301" alone never matches "NS 301"), or one of the product words. A page
    /// that mentions none of them came up for a different meaning of the same characters — grade
    /// 301 stainless steel for a "NS 301" glove — and is dropped. With nothing to judge against,
    /// every page is relevant.
    /// </summary>
    public static bool IsRelevant(
        string domain, string title, string snippet,
        IReadOnlyList<string> makers, IReadOnlyList<string> partNumbers, IReadOnlyList<string> productWords)
    {
        if (makers.Count == 0 && partNumbers.Count == 0 && productWords.Count == 0) return true;
        var text = title + " " + snippet;
        var compact = NonAlphanumeric.Replace(text.ToLowerInvariant(), string.Empty);
        var domainKey = NonAlphanumeric.Replace(domain.ToLowerInvariant(), string.Empty);

        foreach (var part in partNumbers)
        {
            var key = NonAlphanumeric.Replace(part.ToLowerInvariant(), string.Empty);
            if (key.Length >= 4 && compact.Contains(key, StringComparison.Ordinal)) return true;
        }
        foreach (var maker in makers)
        {
            var key = NonAlphanumeric.Replace(SupplierDiscoveryIdentity.SearchName(maker).ToLowerInvariant(), string.Empty);
            if (key.Length >= 3 && (compact.Contains(key, StringComparison.Ordinal) || domainKey.Contains(key, StringComparison.Ordinal)))
                return true;
        }
        foreach (var word in productWords)
        {
            if (word.Length < 4) continue;
            var stem = word.TrimEnd('s', 'S');
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(stem)}(s|es)?\b", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Stable id for one company across searches: the domain, hashed, so the id never carries a URL.</summary>
    public static string HitId(string domain)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(domain.ToLowerInvariant()))).ToLowerInvariant();

    /// <summary>
    /// Null when the result is noise (no usable host, or an excluded host). Otherwise the hit as it
    /// will be cached. <paramref name="providerOrder"/> is the position the search engine gave it.
    /// </summary>
    public static SupplierDiscoveryStoredHit? Classify(
        WebSearchResult result, IReadOnlyList<string> makers, IReadOnlyList<string> partNumbers, int providerOrder,
        IReadOnlyList<string>? productWords = null)
    {
        var domain = RegistrableDomain(result.Url);
        if (domain is null || IsExcluded(domain)) return null;
        if (IsDirectoryPage(result.Title, result.Snippet)) return null;
        if (!IsRelevant(domain, result.Title, result.Snippet, makers, partNumbers, productWords ?? [])) return null;
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

        // "North Gloves Industry - Gloves Manufacturer": makes the product itself, so it sits with the
        // makers, above the dealers, which is the order the rep asked for.
        if (ManufacturerWords.IsMatch(title) || ManufacturerWords.IsMatch(snippet))
            return SupplierRoles.Manufacturer;

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
        if (role == SupplierRoles.Manufacturer)
            return "Describes itself as a manufacturer";
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
        if (cleaned.Length == 0) return NameFromDomain(domain);
        var segments = TitleSeparators.Split(cleaned).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        // Last segment first, skipping the ones that are the page's function rather than a name
        // ("… : Quotes, Address, Contact" → the segment before it). A title that is nothing but
        // function ("Welcome to WebPortunities.net", "DATA SHEET") is named from the domain.
        // Sites put their name last. The segment before it counts only when the last one is the
        // page's function ("Quotes, Address, Contact", "Home"); when the last segment is a tagline
        // or a product ("welding gloves manufacturer"), the one before it is the page's subject,
        // not the company, and the domain names the company instead.
        // One exception: "North Gloves Industry - Work Wear, Leather Accessories and Gloves
        // Manufacturer" is name then tagline, and the name is Title Case. "Hot mill gloves |
        // welding gloves manufacturer" is subject then tagline, and the subject is not.
        var last = segments[^1];
        var previous = segments.Length > 1 ? segments[^2] : null;
        var name = LooksLikeCompanyName(last) ? last
            : previous is not null && LooksLikeCompanyName(previous) && (GenericTitle.IsMatch(last) || IsTitleCase(previous)) ? previous
            : NameFromDomain(domain);
        return name.Length <= 200 ? name : name[..200].TrimEnd();
    }

    /// <summary>
    /// "Gulf Switchgear Trading Co." yes; "301 Heat Resistant Gloves, Z-Flex® Aluminized Back" no
    /// (a product line: long, starts with a number, commas), "360mm" no (a size), "How to Find a
    /// Reliable Safety Gloves Supplier" no (an article). Names are short, start with a letter and
    /// have at most six words.
    /// </summary>
    public static bool LooksLikeCompanyName(string segment)
    {
        var text = segment.Trim();
        if (text.Length < 2 || text.Length > 48) return false;
        if (!char.IsLetter(text[0])) return false;
        if (GenericTitle.IsMatch(text) || ArticleTitle.IsMatch(text)) return false;
        if (text.Contains(',') || text.Contains('®') || text.Contains('™')) return false;
        if (TaglineEnding.IsMatch(text)) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "POLAT 301" is a product code, not a company: a word that is only digits gives it away.
        return words.Length <= 6 && !words.Any(x => x.All(char.IsDigit));
    }

    /// <summary>"North Gloves Industry" yes; "Hot mill gloves" no. Short joining words ("and", "of", "&") do not count.</summary>
    public static bool IsTitleCase(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 4).ToArray();
        return words.Length > 0 && words.All(x => char.IsUpper(x[0]));
    }

    /// <summary>"saf-t-glove.com" → "Saf T Glove"; "everprogloves.com.sa" → "Everprogloves".</summary>
    public static string NameFromDomain(string domain)
    {
        var label = domain.Split('.')[0].Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }

    private static string FirstWordsOf(string? text, int count)
        => string.Join(' ', Whitespace.Split((text ?? string.Empty).Trim()).Where(x => x.Length > 0).Take(count));

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

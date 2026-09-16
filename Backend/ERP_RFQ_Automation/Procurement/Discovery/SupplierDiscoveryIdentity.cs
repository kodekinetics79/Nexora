using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>
/// What a sourcing case is asking the internet about: which part, whose make, described how.
///
/// <para>Built from the case's part number and maker, the RFQ line's description, and — when the
/// customer's document carried one — its "Approved manufacturers" list, which is split into maker
/// names and the part numbers each maker sells the item under. A line that names "ABB (S203-C16);
/// SIEMENS 5SY6316-7" is searched under both makers and both numbers, because either one is an
/// acceptable answer to the customer.</para>
/// </summary>
public sealed record SupplierDiscoveryIdentity(
    string? Maker,
    string? PartNumber,
    string Description,
    IReadOnlyList<string> Makers,
    IReadOnlyList<string> PartNumbers,
    IReadOnlyList<string> AcceptableMakers,
    IReadOnlyList<MakerPart> Pairs,
    bool AsTyped = false)
{
    /// <summary>Bound on how many queries one discovery may post. Each is one paid provider call.</summary>
    public const int MaxQueries = 8;

    // A part number is a token with a digit in it and at least three characters of the kind part
    // numbers are made of. "5SY6316-7", "S203-C16", "LV431831" qualify; "3M" (two characters) and
    // "Electric" (no digit) do not, so they stay part of the maker's name.
    private static readonly Regex PartNumberToken = new(@"^[A-Za-z0-9][A-Za-z0-9\-_./]{2,}$", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[;,|\n\r]|\s/\s|\s+or\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DescriptionSeparators = new(@"[\s:;,/()\[\]|+]+", RegexOptions.Compiled);

    // The legal tail of a company name is noise to a search engine and to a relevance check:
    // "JAMES NORTH AND SONS COMPANY" is searched, and matched, as "JAMES NORTH".
    private static readonly HashSet<string> CorporateSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "&", "sons", "son", "company", "co", "co.", "ltd", "ltd.", "limited", "inc", "inc.", "llc", "plc",
        "gmbh", "ag", "bv", "b.v.", "sa", "s.a.", "sas", "pty", "corporation", "corp", "corp.", "est", "est.",
        "establishment", "the"
    };

    // Words that describe the order, not the product: sizes, quantities and packaging carry no
    // signal about who sells the item and would only narrow the search to nothing.
    private static readonly HashSet<string> OrderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "for", "with", "of", "size", "type", "large", "small", "medium", "each", "per", "pcs",
        "pieces", "nos", "qty", "quantity", "set", "sets", "pair", "pairs", "pack", "box", "unit", "units"
    };

    public static SupplierDiscoveryIdentity From(
        string? partNumber, string? maker, string? description, string? approvedMakersText)
    {
        var makers = new List<string>();
        var partNumbers = new List<string>();
        var acceptable = new List<string>();
        var pairs = new List<MakerPart>();

        var primaryMaker = Clean(maker);
        var primaryPart = Clean(partNumber);
        if (primaryMaker is not null) makers.Add(primaryMaker);
        if (primaryPart is not null) partNumbers.Add(primaryPart);
        if (primaryMaker is not null || primaryPart is not null) pairs.Add(new MakerPart(primaryMaker, primaryPart));

        foreach (var segment in SplitApproved(approvedMakersText))
        {
            acceptable.Add(segment);
            var (segmentMaker, segmentParts) = SplitSegment(segment);
            if (segmentMaker is not null) makers.Add(segmentMaker);
            partNumbers.AddRange(segmentParts);
            // Each approved maker is searched with ITS OWN number: ABB does not sell Schneider's
            // number, so pairing every maker with every part would spend calls on nothing.
            if (segmentParts.Count == 0) pairs.Add(new MakerPart(segmentMaker, null));
            else foreach (var part in segmentParts) pairs.Add(new MakerPart(segmentMaker, part));
        }

        var distinctMakers = makers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var distinctParts = partNumbers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new SupplierDiscoveryIdentity(
            distinctMakers.FirstOrDefault(),
            distinctParts.FirstOrDefault(),
            Clean(description) ?? string.Empty,
            distinctMakers,
            distinctParts,
            acceptable,
            pairs.Distinct().ToArray());
    }

    /// <summary>
    /// The maker as a search engine should see it: "JAMES NORTH AND SONS COMPANY" → "JAMES NORTH",
    /// "ABB Ltd" → "ABB", "Schneider Electric" unchanged. At least one word always survives.
    /// </summary>
    public static string SearchName(string maker)
    {
        var words = Whitespace.Split(maker.Trim()).Where(x => x.Length > 0).ToList();
        while (words.Count > 1 && CorporateSuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>
    /// The words of the description that name the product: "GLOVES:WORKING,HEAT RESISTANT,LARGE,LG 1"
    /// → ["GLOVES", "WORKING", "HEAT", "RESISTANT"]. Sizes, quantities and codes with digits are left
    /// out. At most four, in the order written, because the first words name the thing.
    /// </summary>
    public static IReadOnlyList<string> ProductWordsOf(string? description, int count = 4)
        => DescriptionSeparators.Split(description ?? string.Empty)
            .Where(x => x.Length >= 3 && !x.Any(char.IsDigit) && !OrderWords.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();

    public IReadOnlyList<string> ProductWords => AsTyped
        ? Whitespace.Split(Description).Where(x => x.Length >= 3).Take(8).ToArray()
        : ProductWordsOf(Description);

    /// <summary>The supplier page's free-text box: the words are the description and nothing is inferred from them.</summary>
    public static SupplierDiscoveryIdentity FromQuery(string query)
        => new(null, null, Clean(query) ?? string.Empty, [], [], [], [], AsTyped: true);

    /// <summary>
    /// The search strings, in the order they are posted: the line's own maker and number first,
    /// then each approved maker with its own number. A maker with no number is searched with the
    /// description; a number with no maker likewise; a line with neither is searched by description.
    /// </summary>
    public IReadOnlyList<string> Queries()
    {
        var queries = new List<string>();
        var words = string.Join(' ', ProductWords);

        foreach (var pair in Pairs)
        {
            var maker = pair.Maker is null ? null : SearchName(pair.Maker);
            if (maker is not null && pair.Part is not null)
            {
                // The product words ride along with the number: "NS 301" alone finds stainless
                // steel grade 301; "JAMES NORTH NS 301 GLOVES WORKING HEAT RESISTANT" finds gloves.
                Add(queries, Join($"{maker} {pair.Part}", words, "supplier"));
                Add(queries, words.Length > 0
                    ? Join(maker, words, "distributor Saudi Arabia")
                    : $"{maker} {pair.Part} distributor Saudi Arabia");
            }
            else if (maker is not null)
            {
                Add(queries, Join(maker, words, "distributor Saudi Arabia"));
                Add(queries, Join(maker, words, "supplier"));
            }
            else if (pair.Part is not null)
            {
                Add(queries, Join(pair.Part, words, "supplier Saudi Arabia"));
            }
        }
        if (Pairs.Count == 0)
        {
            // A rep's typed query is searched as typed; a line description is searched by its product words.
            var typed = AsTyped ? FirstWords(Description, 8) : words;
            if (typed.Length > 0)
            {
                Add(queries, $"{typed} supplier Saudi Arabia");
                Add(queries, $"{typed} supplier");
            }
        }
        return queries.Take(MaxQueries).ToArray();
    }

    /// <summary>
    /// The cache key: the same part, makers and description hash to the same key however the case
    /// was reached, so two cases for one part share one search for 30 days.
    /// </summary>
    public string Key()
    {
        var canonical = string.Join("|",
            string.Join(",", Makers.Select(Normalise).Order(StringComparer.Ordinal)),
            string.Join(",", PartNumbers.Select(Normalise).Order(StringComparer.Ordinal)),
            Normalise(Description));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>"LV431831 (Schneider Electric)" — how the messages name what was searched for.</summary>
    public string Subject()
    {
        if (PartNumber is not null && Maker is not null) return $"{PartNumber} ({Maker})";
        if (PartNumber is not null) return PartNumber;
        if (Maker is not null) return $"{Maker} {FirstWords(Description, 5)}".Trim();
        return FirstWords(Description, 8) is { Length: > 0 } words ? words : "this item";
    }

    /// <summary>
    /// The tags an adopted supplier is created with: the part numbers and makers, so the existing
    /// candidate rule ("supplier metadata matches the requested part or manufacturer") finds it.
    /// </summary>
    public string? Tags()
    {
        var tags = PartNumbers.Concat(Makers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return tags.Length == 0 ? null : string.Join("; ", tags);
    }

    public SupplierDiscoverySearchedFor SearchedFor() => new(Maker, PartNumber, Description, AcceptableMakers);

    public bool IsEmpty => Makers.Count == 0 && PartNumbers.Count == 0 && Description.Length == 0;

    internal static IReadOnlyList<string> SplitApproved(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return Separators.Split(text)
            .Select(x => Whitespace.Replace(x, " ").Trim())
            .Where(x => x.Length > 0)
            .ToArray();
    }

    /// <summary>"SIEMENS 5SY6316-7" → ("SIEMENS", ["5SY6316-7"]); "ABB (S203-C16)" → ("ABB", ["S203-C16"]); "Eaton" → ("Eaton", []).</summary>
    internal static (string? Maker, IReadOnlyList<string> PartNumbers) SplitSegment(string segment)
    {
        var words = segment.Replace("(", " ").Replace(")", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var makerWords = new List<string>();
        var parts = new List<string>();
        foreach (var word in words)
        {
            if (PartNumberToken.IsMatch(word) && word.Any(char.IsDigit)) parts.Add(word);
            else makerWords.Add(word);
        }
        var maker = makerWords.Count == 0 ? null : string.Join(' ', makerWords);
        return (maker, parts);
    }

    private static void Add(List<string> queries, string query)
    {
        if (queries.Count >= MaxQueries) return;
        var cleaned = Whitespace.Replace(query, " ").Trim();
        if (cleaned.Length > 0 && !queries.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            queries.Add(cleaned);
    }

    private static string Join(string head, string words, string tail)
        => words.Length == 0 ? $"{head} {tail}" : $"{head} {words} {tail}";

    private static string FirstWords(string text, int count)
        => string.Join(' ', Whitespace.Split(text.Trim()).Where(x => x.Length > 0).Take(count));

    private static string Normalise(string value)
        => Whitespace.Replace(value.Trim(), " ").ToLowerInvariant();

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Whitespace.Replace(value, " ").Trim();
}

/// <summary>One maker with the number it sells the item under; either half may be missing, never both.</summary>
public sealed record MakerPart(string? Maker, string? Part);

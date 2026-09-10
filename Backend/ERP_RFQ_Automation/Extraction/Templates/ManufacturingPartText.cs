using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction.Templates;

/// <summary>
/// What an SAP sourcing export packs into one "Manufacturing Part Text" cell, read back out.
///
/// <para><b>What the cell looks like.</b> An Aramco/ASMO event print states, per line, one run
/// of text that concatenates every approved vendor record and every keyed attribute of the
/// material, with no separators but " - ":</para>
/// <code>
/// 4000062813 - 0060001092 - SCHNEIDER ELECTRIC THE NETHERLANDS - NL 000000005002831975 - 10001675 -
/// SCHNEIDER ELECTRIC THE NETHERLANDS - NL ED_PART_NUMBER - LV429827 CUSTOMER_MATERIAL_CODE -
/// 000000005002831975 PART_NUMBER - LV429827 CUSTOMER_MATERIAL_DESCRIPTION1 - CIRCUIT BREAKER ...
/// </code>
/// <para>Each vendor record is "vendor - vendor-sub - VENDOR NAME - CC material - plant -
/// MANUFACTURER NAME - CC"; the keyed attributes follow, and a further record may follow those.
/// A name may carry a status flag ("** ", "## ", "@@ "). The makers named here are the buyer's
/// APPROVED manufacturers for the line — commercially decisive, since a quote for another maker
/// is rejected — and the part numbers are the makers' own, distinct from the buyer's 18-digit
/// material number that heads the line.</para>
///
/// <para><b>Why deterministic.</b> The grammar is fixed by the exporting system, so a regex
/// reads it exactly; a model would be asked to transcribe 1,500 of these per document. The
/// reader fires only when the cell carries the export's own key tokens, so any other buyer's
/// free text is left untouched.</para>
/// </summary>
public static class ManufacturingPartText
{
    public sealed record Reading(
        IReadOnlyList<string> Manufacturers,
        IReadOnlyList<string> PartNumbers,
        IReadOnlyList<string> SupersededNumbers,
        string? CustomerMaterialCode)
    {
        public bool IsEmpty => Manufacturers.Count == 0 && PartNumbers.Count == 0 && SupersededNumbers.Count == 0 && CustomerMaterialCode is null;
    }

    private static readonly Regex VendorRecord = new(
        @"(?<!\d)\d{10} - \d{10} - (?<vendor>.+?) - [A-Z]{2} \d{18} - \d{8} - (?<maker>.+?) - [A-Z]{2}(?= |$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A keyed attribute: an UPPER_SNAKE token followed by " - ". The export has dozens
    /// (PART_NUMBER, DRAWING_NUMBER, MAT_TYPE, …, each with an "ED_" twin whose value has its
    /// punctuation stripped), so the token SHAPE is the delimiter, not a list of names.
    /// </summary>
    private const string KeyToken = @"[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+";

    private static readonly Regex KeyedValue = new(
        @"\b(?<key>" + KeyToken + @") - (?<value>.*?)(?=(?: \b" + KeyToken + @" - )|(?: \d{10} - \d{10} - )|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The keys whose value is a maker's own number for the goods.</summary>
    private static readonly HashSet<string> PartNumberKeys = new(StringComparer.Ordinal)
    {
        "PART_NUMBER", "MODEL_NUMBER", "CATALOG_NUMBER", "PART_NUMBER_C1",
    };

    private static readonly HashSet<string> SupersededKeys = new(StringComparer.Ordinal)
    {
        "SUPERSEDED_NUMBER", "SUPERSEDED_NUMBER_C1",
    };

    private static readonly Regex StatusFlag = new(@"^[\*#@]+\s*", RegexOptions.Compiled);

    /// <summary>True when the text carries the export's own key tokens, so the reader applies.</summary>
    public static bool Recognises(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && (text.Contains(" - ", StringComparison.Ordinal))
           && (VendorRecord.IsMatch(text) || text.Contains("PART_NUMBER - ", StringComparison.Ordinal));

    public static Reading Read(string? text)
    {
        if (!Recognises(text))
            return new Reading([], [], [], null);

        var makers = new List<string>();
        foreach (Match record in VendorRecord.Matches(text!))
        {
            var maker = Clean(record.Groups["maker"].Value);
            if (maker.Length > 0 && !makers.Contains(maker, StringComparer.OrdinalIgnoreCase))
                makers.Add(maker);
        }

        var parts = new List<string>();
        var superseded = new List<string>();
        string? customerCode = null;
        foreach (Match keyed in KeyedValue.Matches(text!))
        {
            var key = keyed.Groups["key"].Value;
            var value = keyed.Groups["value"].Value.Trim();
            if (value.Length == 0) continue;

            // "ED_PART_NUMBER - GVAE11" is "PART_NUMBER - GV-AE11" with its punctuation
            // stripped: the same number, kept once, in the buyer's own formatting when both
            // are present.
            var stripped = key.StartsWith("ED_", StringComparison.Ordinal);
            var plain = stripped ? key[3..] : key;

            if (PartNumberKeys.Contains(plain))
                Add(parts, value, stripped);
            else if (SupersededKeys.Contains(plain))
                Add(superseded, value, stripped);
            else if (plain == "CUSTOMER_MATERIAL_CODE")
                customerCode ??= value;
        }

        return new Reading(makers, parts, superseded, customerCode);

        static void Add(List<string> list, string value, bool stripped)
        {
            var folded = Fold(value);
            var twin = list.FindIndex(existing => Fold(existing) == folded);
            if (twin < 0)
                list.Add(value);
            else if (!stripped && list[twin].Length < value.Length)
                list[twin] = value; // the punctuated original replaces its stripped twin
        }
    }

    private static string Clean(string name) => StatusFlag.Replace(name.Trim(), string.Empty).Trim();

    private static string Fold(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

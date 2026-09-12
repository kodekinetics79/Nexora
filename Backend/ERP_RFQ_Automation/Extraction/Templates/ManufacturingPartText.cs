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
        string? CustomerMaterialCode,
        IReadOnlyList<ApprovedVendor> Vendors)
    {
        public bool IsEmpty => Manufacturers.Count == 0 && PartNumbers.Count == 0 && SupersededNumbers.Count == 0 && CustomerMaterialCode is null;

        /// <summary>
        /// The one number every approved vendor states for the part, when they all agree — the
        /// buyer is naming the same maker part through two suppliers, and that IS the part number.
        /// Null when the vendors name different numbers, or none.
        /// </summary>
        public string? AgreedPartNumber
        {
            get
            {
                var numbers = Vendors.Select(v => v.PartNumber).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
                if (numbers.Count == 0 || numbers.Count != Vendors.Count) return null;
                return numbers.Select(Fold).Distinct().Count() == 1 ? numbers.OrderByDescending(n => n.Length).First() : null;
            }
        }
    }

    /// <summary>One approved vendor record and the keyed values that followed it.</summary>
    /// <param name="Maker">The manufacturer named in the record (status flags removed).</param>
    /// <param name="Vendor">The selling vendor named in the record, when different from the maker.</param>
    /// <param name="Country">The two-letter country the record closes with.</param>
    public sealed record ApprovedVendor(
        string Maker, string? Vendor, string? Country, string? PartNumber, string? ModelNumber,
        IReadOnlyList<string> SupersededNumbers, string? Remarks, string? CustomerMaterialCode);

    // Names are bounded: a record that omits its trailing country code must not let the lazy
    // group run on into the next record and hand a hundred-character "maker" to the line.
    private static readonly Regex VendorRecord = new(
        @"(?<!\d)\d{10} - \d{10} - (?<vendor>[^\r\n]{1,80}?) - [A-Z]{2} \d{18} - \d{8} - (?<maker>[^\r\n]{1,80}?) - [A-Z]{2}(?=\s|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A keyed attribute: an UPPER_SNAKE token followed by " - ". The export has dozens
    /// (PART_NUMBER, DRAWING_NUMBER, MAT_TYPE, …, each with an "ED_" twin whose value has its
    /// punctuation stripped), so the token SHAPE is the delimiter, not a list of names.
    /// </summary>
    private static readonly Regex VendorRecordWithCountry = new(
        @"(?<!\d)\d{10} - \d{10} - (?<vendor>[^\r\n]{1,80}?) - [A-Z]{2} \d{18} - \d{8} - (?<maker>[^\r\n]{1,80}?) - (?<country>[A-Z]{2})(?=\s|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A key is UPPER_SNAKE with at least one underscore — plus REMARKS, the one single-word key
    // these records use ("REMARKS - WILL SHIP AS PARTS 149986-01").
    private const string KeyToken = @"(?:[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+|REMARKS)";

    private static readonly Regex KeyedValue = new(
        @"\b(?<key>" + KeyToken + @") - (?<value>.*?)(?=(?:\s\b" + KeyToken + @" - )|(?:\s\d{10} - \d{10} - )|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

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
            return new Reading([], [], [], null, []);

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

        return new Reading(makers, parts, superseded, customerCode, ReadVendors(text!));

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

    /// <summary>
    /// The records one by one: each vendor record owns the keyed values printed after it and
    /// before the next record, so a part number is attributed to the vendor that states it.
    /// </summary>
    private static IReadOnlyList<ApprovedVendor> ReadVendors(string text)
    {
        var records = VendorRecordWithCountry.Matches(text).Cast<Match>().ToList();
        var vendors = new List<ApprovedVendor>();
        for (var i = 0; i < records.Count; i++)
        {
            var start = records[i].Index + records[i].Length;
            var end = i + 1 < records.Count ? records[i + 1].Index : text.Length;
            var block = text[start..end];

            string? part = null, model = null, remarks = null, customerCode = null;
            var superseded = new List<string>();
            foreach (Match keyed in KeyedValue.Matches(block))
            {
                var key = keyed.Groups["key"].Value;
                var value = keyed.Groups["value"].Value.Trim();
                if (value.Length == 0) continue;
                var stripped = key.StartsWith("ED_", StringComparison.Ordinal);
                var plain = stripped ? key[3..] : key;
                // "_C1", "_C2" are continuation lines of the same value ("REMARKS - WILL SHIP AS
                // PARTS 149986-01", "REMARKS_C1 - AND 149992-02"); the stripped ED_ twins of a
                // value already read in the buyer's own punctuation are ignored.
                var continuation = Regex.Match(plain, @"_C(\d)$");
                var field = continuation.Success ? plain[..continuation.Index] : plain;
                if (stripped && continuation.Success) continue;
                switch (field)
                {
                    case "PART_NUMBER" or "CATALOG_NUMBER": part = continuation.Success ? Append(part, value) : Prefer(part, value, stripped); break;
                    case "MODEL_NUMBER": model = continuation.Success ? Append(model, value) : Prefer(model, value, stripped); break;
                    case "SUPERSEDED_NUMBER":
                        if (continuation.Success && superseded.Count > 0) { superseded[^1] = Append(superseded[^1], value)!; break; }
                        var twin = superseded.FindIndex(s => Fold(s) == Fold(value));
                        if (twin < 0) superseded.Add(value);
                        else if (!stripped && superseded[twin].Length < value.Length) superseded[twin] = value;
                        break;
                    case "REMARKS": remarks = continuation.Success ? Append(remarks, value) : Prefer(remarks, value, stripped); break;
                    case "CUSTOMER_MATERIAL_CODE": customerCode ??= value; break;
                }
            }

            var maker = Clean(records[i].Groups["maker"].Value);
            var vendor = Clean(records[i].Groups["vendor"].Value);
            vendors.Add(new ApprovedVendor(
                maker, string.Equals(vendor, maker, StringComparison.OrdinalIgnoreCase) ? null : vendor,
                records[i].Groups["country"].Value, part, model, superseded, remarks, customerCode));
        }
        return vendors;

        static string? Prefer(string? current, string value, bool stripped)
            => current is null ? value : (!stripped && current.Length < value.Length ? value : current);

        static string? Append(string? current, string value)
            => current is null ? value : $"{current} {value}";
    }

    private static string Clean(string name) => StatusFlag.Replace(name.Trim(), string.Empty).Trim();

    private static string Fold(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

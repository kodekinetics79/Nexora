using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Services.Uom;

/// <summary>What the canonicaliser was able to say about one raw unit-of-measure string.</summary>
public enum UomResolution
{
    /// <summary>The source stated no unit. Null in, null out — a unit is NEVER invented.</summary>
    Absent,

    /// <summary>Mapped onto a canonical code with no loss of commercial meaning.</summary>
    Canonical,

    /// <summary>
    /// A real token we deliberately refuse to guess at. The source wording is kept
    /// verbatim and a human is asked. See <see cref="UomReviewReason"/>.
    /// </summary>
    NeedsReview
}

/// <summary>Why a token was refused rather than mapped.</summary>
public enum UomReviewReason
{
    None,

    /// <summary>
    /// Packaging, not a unit of the thing being bought. "Pack" / "Pallet" / "Carton"
    /// describe how the goods arrive, not how many arrived. A pallet of gaskets and a
    /// pallet of cable are different counts; nothing in the string says which.
    /// </summary>
    Packaging,

    /// <summary>
    /// Form factor with an unstated magnitude. "Length" / "Pipe" / "Coil" name a shape,
    /// not a quantity — "3 lengths" is only a number once someone states the length.
    /// </summary>
    FormFactor,

    /// <summary>
    /// The token has more than one accepted meaning and the document does not settle it
    /// (e.g. "ST" is Stück/piece in SAP, "set" in others, and "short ton" in US trade).
    /// </summary>
    Ambiguous,

    /// <summary>Not in the vocabulary and not in the tenant's own UoM master data.</summary>
    Unknown
}

/// <summary>
/// The outcome of canonicalising one raw unit string.
/// </summary>
/// <param name="Value">
/// What callers should STORE. The canonical code when resolved; the cleaned source wording
/// when refused; null only when the source stated nothing.
/// </param>
/// <param name="CanonicalCode">Non-null only for <see cref="UomResolution.Canonical"/>.</param>
/// <param name="SourceText">The source wording after cleaning, for evidence and review.</param>
/// <param name="TenantUomId">
/// <c>SetUom.UomId</c> when the tenant's own UoM table carries this unit. Supplied for the
/// foreign key ONLY — it never changes <see cref="Value"/> and never clears a refusal.
/// </param>
public sealed record UomCanonicalization(
    string? Value,
    string? CanonicalCode,
    string? SourceText,
    int? TenantUomId,
    UomResolution Resolution,
    UomReviewReason ReviewReason)
{
    public bool NeedsReview => Resolution == UomResolution.NeedsReview;

    internal static readonly UomCanonicalization Nothing =
        new(null, null, null, null, UomResolution.Absent, UomReviewReason.None);
}

/// <summary>A tenant's own unit master data, probed by folded key.</summary>
public interface IUomVocabulary
{
    /// <summary>
    /// True when the tenant transacts in this unit. <paramref name="probe"/> may be a raw
    /// spelling or a canonical code; implementations fold it themselves.
    /// </summary>
    bool TryLookup(string probe, out string code, out int uomId);
}

/// <summary>
/// The ONE canonicaliser for units of measure across Nexora.
///
/// WHY THIS EXISTS
/// 2,966 stored lead line items spell one concept up to five ways — <c>each</c> (2,868),
/// <c>EA</c> (19), <c>pcs</c> (3), <c>Pcs</c> (1), <c>piece</c> (1), <c>NOS</c> (12) — because
/// every ingestion door wrote the model's raw string straight through. Four things break as
/// a direct result: duplicate RFQs fail to dedup (the fingerprint hashes the spelling),
/// lead→RFQ conversion mints a SIXTH spelling on a lookup miss, the tenant's SetUom table
/// accretes a row per casing, and a buyer sees a different unit on the quote than on their
/// own enquiry.
///
/// WHAT IT WILL NOT DO — the important half
/// A synonym table can settle SPELLING. It cannot settle QUANTITY. <c>Pack</c>, <c>Package</c>,
/// <c>Pallet</c> and <c>Pipe</c> are packaging or form factor: "1 Pallet" is not "1 each", and
/// nothing in the string says whether the pallet holds 10 items or 10,000. <c>length</c> is a
/// shape, not a measure. <c>ST</c> means piece in SAP, set elsewhere, and short ton in US
/// trade — a 2,000× error if guessed wrong. Those are returned as
/// <see cref="UomResolution.NeedsReview"/> with the source wording intact, because a line a
/// human must confirm is cheap and a line silently quoted at the wrong quantity is not. That
/// refusal is the point of this class, not a gap in it.
///
/// <see cref="UomResolution.Absent"/> is likewise load-bearing: a missing unit stays missing.
/// Defaulting a blank to "EA" would manufacture agreement with a document that stated none.
/// </summary>
public static partial class UomCanonicalizer
{
    // ── Canonical vocabulary ────────────────────────────────────────────────────────────
    // Codes are short, upper-case and stable; they are what lands in LeadItem.UnitOfMeasure,
    // Rfqitem.UnitOfMeasure and the duplicate-detection fingerprint. Every entry below is a
    // pure SPELLING of the same commercial unit — quantity is never rescaled, so no entry may
    // imply a multiplier (that is why "dozen" maps to its own DZ and never to EA).
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        // Count. The whole `each`/`EA`/`pcs`/`piece`/`NOS` family observed in production.
        ["EACH"] = "EA", ["EACHES"] = "EA", ["EA"] = "EA", ["EAS"] = "EA",
        ["PC"] = "EA", ["PCS"] = "EA", ["PCE"] = "EA", ["PCES"] = "EA",
        ["PIECE"] = "EA", ["PIECES"] = "EA",
        ["NO"] = "EA", ["NOS"] = "EA", ["NR"] = "EA",
        ["NUMBER"] = "EA", ["NUMBERS"] = "EA",
        ["ITEM"] = "EA", ["ITEMS"] = "EA",
        ["UNIT"] = "EA", ["UNITS"] = "EA",

        // Assemblies quoted as one line. A kit is priced as a kit, exactly like a set.
        ["SET"] = "SET", ["SETS"] = "SET", ["KIT"] = "SET", ["KITS"] = "SET",

        // Fixed-count groupings that are units in their own right. Kept SEPARATE from EA
        // precisely because they carry an implied multiplier we must not silently apply.
        ["PAIR"] = "PR", ["PAIRS"] = "PR", ["PR"] = "PR", ["PRS"] = "PR",
        ["DOZEN"] = "DZ", ["DOZENS"] = "DZ", ["DOZ"] = "DZ", ["DZ"] = "DZ",

        // SAP activity unit ("Activ.unit", 4 rows in production). A defined unit with a
        // stable meaning — a unit of ACTIVITY, not a physical each — so it gets its own code
        // rather than being folded into EA.
        ["ACTIVUNIT"] = "AU", ["ACTIVITYUNIT"] = "AU", ["ACTIVITYUNITS"] = "AU", ["AU"] = "AU",

        // Whole-scope pricing (the BOQ prompt's fallback unit).
        ["LOT"] = "LOT", ["LOTS"] = "LOT", ["LS"] = "LOT", ["LUMPSUM"] = "LOT",

        // Length. "Linear"/"running" metre is a metre.
        ["MM"] = "MM", ["MILLIMETER"] = "MM", ["MILLIMETRE"] = "MM", ["MILLIMETERS"] = "MM", ["MILLIMETRES"] = "MM",
        ["CM"] = "CM", ["CENTIMETER"] = "CM", ["CENTIMETRE"] = "CM", ["CENTIMETERS"] = "CM", ["CENTIMETRES"] = "CM",
        ["M"] = "M", ["MTR"] = "M", ["MTRS"] = "M", ["MTS"] = "M",
        ["METER"] = "M", ["METRE"] = "M", ["METERS"] = "M", ["METRES"] = "M",
        ["LM"] = "M", ["RM"] = "M", ["LINEARMETER"] = "M", ["LINEARMETRE"] = "M",
        ["RUNNINGMETER"] = "M", ["RUNNINGMETRE"] = "M",
        ["KM"] = "KM", ["KILOMETER"] = "KM", ["KILOMETRE"] = "KM", ["KILOMETERS"] = "KM", ["KILOMETRES"] = "KM",
        ["IN"] = "IN", ["INCH"] = "IN", ["INCHES"] = "IN",
        ["FT"] = "FT", ["FOOT"] = "FT", ["FEET"] = "FT",
        ["YD"] = "YD", ["YARD"] = "YD", ["YARDS"] = "YD",

        // Area / volume. NFKD folds "m²"/"m³" to "M2"/"M3" before lookup.
        ["M2"] = "M2", ["SQM"] = "M2", ["SQMTR"] = "M2", ["SQUAREMETER"] = "M2", ["SQUAREMETRE"] = "M2",
        ["FT2"] = "FT2", ["SQFT"] = "FT2", ["SQUAREFOOT"] = "FT2", ["SQUAREFEET"] = "FT2",
        ["M3"] = "M3", ["CBM"] = "M3", ["CUM"] = "M3", ["CUBICMETER"] = "M3", ["CUBICMETRE"] = "M3",
        ["L"] = "L", ["LTR"] = "L", ["LTRS"] = "L", ["LITER"] = "L", ["LITRE"] = "L", ["LITERS"] = "L", ["LITRES"] = "L",
        ["ML"] = "ML", ["MILLILITER"] = "ML", ["MILLILITRE"] = "ML",

        // Mass. "Ton" alone is refused below — metric/short/long differ by up to 12%.
        ["KG"] = "KG", ["KGS"] = "KG", ["KGM"] = "KG", ["KILO"] = "KG", ["KILOS"] = "KG",
        ["KILOGRAM"] = "KG", ["KILOGRAMS"] = "KG", ["KILOGRAMME"] = "KG",
        ["G"] = "G", ["GM"] = "G", ["GMS"] = "G", ["GRAM"] = "G", ["GRAMS"] = "G",
        ["MT"] = "MT", ["TONNE"] = "MT", ["TONNES"] = "MT", ["METRICTON"] = "MT", ["METRICTONS"] = "MT",
        ["LB"] = "LB", ["LBS"] = "LB", ["POUND"] = "LB", ["POUNDS"] = "LB",

        // Effort.
        ["HR"] = "HR", ["HRS"] = "HR", ["HOUR"] = "HR", ["HOURS"] = "HR", ["MANHOUR"] = "HR", ["MANHOURS"] = "HR",
        ["DAY"] = "DAY", ["DAYS"] = "DAY", ["MANDAY"] = "DAY", ["MANDAYS"] = "DAY",
        ["WK"] = "WK", ["WKS"] = "WK", ["WEEK"] = "WK", ["WEEKS"] = "WK",
        ["MTH"] = "MTH", ["MONTH"] = "MTH", ["MONTHS"] = "MTH",
        ["YR"] = "YR", ["YRS"] = "YR", ["YEAR"] = "YR", ["YEARS"] = "YR"
    };

    // ── Deliberate refusals ─────────────────────────────────────────────────────────────
    // These ARE units in the sense that a document writes them in the unit column. They are
    // NOT convertible to a count without information the document did not supply. Mapping any
    // of them to EA is the single most expensive mistake available here: it turns "25 Packs"
    // into "25 pieces" on a quote to the customer. Every one is returned verbatim, flagged.
    private static readonly Dictionary<string, UomReviewReason> Refusals = new(StringComparer.Ordinal)
    {
        // Packaging — observed in production: Pack (25), Package (4), Pallet (1).
        ["PACK"] = UomReviewReason.Packaging, ["PACKS"] = UomReviewReason.Packaging,
        ["PK"] = UomReviewReason.Packaging, ["PKS"] = UomReviewReason.Packaging,
        ["PKT"] = UomReviewReason.Packaging, ["PACKET"] = UomReviewReason.Packaging,
        ["PACKETS"] = UomReviewReason.Packaging, ["PACKAGE"] = UomReviewReason.Packaging,
        ["PACKAGES"] = UomReviewReason.Packaging, ["PKG"] = UomReviewReason.Packaging,
        ["PALLET"] = UomReviewReason.Packaging, ["PALLETS"] = UomReviewReason.Packaging,
        ["PLT"] = UomReviewReason.Packaging,
        ["BOX"] = UomReviewReason.Packaging, ["BOXES"] = UomReviewReason.Packaging,
        ["CARTON"] = UomReviewReason.Packaging, ["CARTONS"] = UomReviewReason.Packaging,
        ["CTN"] = UomReviewReason.Packaging, ["CASE"] = UomReviewReason.Packaging,
        ["CASES"] = UomReviewReason.Packaging, ["CRATE"] = UomReviewReason.Packaging,
        ["CRATES"] = UomReviewReason.Packaging, ["DRUM"] = UomReviewReason.Packaging,
        ["DRUMS"] = UomReviewReason.Packaging, ["BAG"] = UomReviewReason.Packaging,
        ["BAGS"] = UomReviewReason.Packaging, ["SACK"] = UomReviewReason.Packaging,
        ["SACKS"] = UomReviewReason.Packaging, ["BUNDLE"] = UomReviewReason.Packaging,
        ["BUNDLES"] = UomReviewReason.Packaging, ["BDL"] = UomReviewReason.Packaging,
        ["CONTAINER"] = UomReviewReason.Packaging, ["CONTAINERS"] = UomReviewReason.Packaging,
        ["BOTTLE"] = UomReviewReason.Packaging, ["BOTTLES"] = UomReviewReason.Packaging,
        ["CAN"] = UomReviewReason.Packaging, ["CANS"] = UomReviewReason.Packaging,
        ["TIN"] = UomReviewReason.Packaging, ["TINS"] = UomReviewReason.Packaging,

        // Form factor — observed in production: length (1), Pipe (1).
        ["LENGTH"] = UomReviewReason.FormFactor, ["LENGTHS"] = UomReviewReason.FormFactor,
        ["PIPE"] = UomReviewReason.FormFactor, ["PIPES"] = UomReviewReason.FormFactor,
        ["ROLL"] = UomReviewReason.FormFactor, ["ROLLS"] = UomReviewReason.FormFactor,
        ["REEL"] = UomReviewReason.FormFactor, ["REELS"] = UomReviewReason.FormFactor,
        ["COIL"] = UomReviewReason.FormFactor, ["COILS"] = UomReviewReason.FormFactor,
        ["SPOOL"] = UomReviewReason.FormFactor, ["SPOOLS"] = UomReviewReason.FormFactor,
        ["ROD"] = UomReviewReason.FormFactor, ["RODS"] = UomReviewReason.FormFactor,
        ["BAR"] = UomReviewReason.FormFactor, ["BARS"] = UomReviewReason.FormFactor,
        ["SHEET"] = UomReviewReason.FormFactor, ["SHEETS"] = UomReviewReason.FormFactor,

        // Genuinely ambiguous — observed in production: ST (1).
        ["ST"] = UomReviewReason.Ambiguous,
        ["TON"] = UomReviewReason.Ambiguous, ["TONS"] = UomReviewReason.Ambiguous,
        ["T"] = UomReviewReason.Ambiguous,
        ["GAL"] = UomReviewReason.Ambiguous, ["GALLON"] = UomReviewReason.Ambiguous,
        ["GALLONS"] = UomReviewReason.Ambiguous,
        ["OZ"] = UomReviewReason.Ambiguous, ["OUNCE"] = UomReviewReason.Ambiguous,
        ["OUNCES"] = UomReviewReason.Ambiguous
    };

    /// <summary>
    /// Canonicalises one raw unit string.
    /// </summary>
    /// <param name="vocabulary">
    /// Optional tenant UoM master data. It can attach a foreign key, and it can resolve a unit
    /// this vocabulary has never heard of (the tenant demonstrably transacts in it). It can
    /// NEVER overturn a refusal: that the tenant stocks pallets still does not say how many
    /// items are on one.
    /// </param>
    public static UomCanonicalization Canonicalize(string? raw, IUomVocabulary? vocabulary = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UomCanonicalization.Nothing;

        // "10 EA" is a quantity that leaked into the unit column; the unit is "EA".
        var cleaned = LeadingQuantity().Replace(raw, string.Empty).Trim().TrimEnd('.', ',', ';', ':').Trim();
        var key = cleaned.Length == 0 ? string.Empty : Fold(cleaned);
        if (key.Length == 0)
        {
            // Non-empty but carrying no unit at all ("10", "-", "n/a"). The source wording is
            // preserved rather than nulled: a reviewer needs to see that the extractor put
            // junk here, and null would read as "the document stated no unit".
            var verbatim = raw.Trim();
            return new UomCanonicalization(verbatim, null, verbatim, null,
                UomResolution.NeedsReview, UomReviewReason.Unknown);
        }

        if (Synonyms.TryGetValue(key, out var code))
            return new UomCanonicalization(code, code, cleaned, TenantId(vocabulary, code, key),
                UomResolution.Canonical, UomReviewReason.None);

        if (Refusals.TryGetValue(key, out var reason))
            return new UomCanonicalization(cleaned, null, cleaned, TenantId(vocabulary, key),
                UomResolution.NeedsReview, reason);

        // Unknown to us, but present in the tenant's own master data: they transact in it, so
        // it is a unit — adopt their code verbatim rather than inventing a mapping.
        if (vocabulary is not null && vocabulary.TryLookup(key, out var tenantCode, out var tenantId))
            return new UomCanonicalization(tenantCode, tenantCode, cleaned, tenantId,
                UomResolution.Canonical, UomReviewReason.None);

        return new UomCanonicalization(cleaned, null, cleaned, null,
            UomResolution.NeedsReview, UomReviewReason.Unknown);
    }

    /// <summary>
    /// Write-path convenience: the value to persist. Null stays null; a refused unit keeps the
    /// customer's own wording.
    /// </summary>
    public static string? CanonicalizeForStorage(string? raw, IUomVocabulary? vocabulary = null)
        => Canonicalize(raw, vocabulary).Value;

    /// <summary>
    /// Stable key for equality between two units — duplicate detection, line matching. Two
    /// spellings of one unit produce one key; two units we cannot equate never do. Null when
    /// the source stated no unit, so "no unit" never collides with a real one.
    /// </summary>
    public static string? EquivalenceKey(string? raw)
    {
        var result = Canonicalize(raw);
        return result.Resolution switch
        {
            UomResolution.Absent => null,
            UomResolution.Canonical => result.CanonicalCode,
            _ => Fold(result.SourceText!) is { Length: > 0 } folded ? folded : null
        };
    }

    /// <summary>True when <paramref name="key"/> is a folded spelling we can map.</summary>
    internal static bool TryCanonicalCode(string key, out string code)
    {
        if (Synonyms.TryGetValue(key, out var found)) { code = found; return true; }
        code = string.Empty;
        return false;
    }

    /// <summary>One sentence a reviewer can act on. Empty for a resolved unit.</summary>
    public static string Explain(UomReviewReason reason) => reason switch
    {
        UomReviewReason.Packaging =>
            "packaging unit — confirm how many items it contains before quoting",
        UomReviewReason.FormFactor =>
            "form factor, not a measure — confirm the size or count it stands for",
        UomReviewReason.Ambiguous =>
            "unit has more than one accepted meaning — confirm which the customer means",
        UomReviewReason.Unknown =>
            "unit not recognised — confirm it against the customer's document",
        _ => string.Empty
    };

    private static int? TenantId(IUomVocabulary? vocabulary, params string[] probes)
    {
        if (vocabulary is null) return null;
        foreach (var probe in probes)
            if (vocabulary.TryLookup(probe, out _, out var id))
                return id;
        return null;
    }

    /// <summary>
    /// Case-, punctuation- and whitespace-insensitive fold. NFKD first, so "m²" folds to "M2"
    /// and full-width forms collapse; then letters and digits only, upper-invariant. That is
    /// what makes "Activ.unit", "ACTIV UNIT" and "activunit" one key.
    /// </summary>
    internal static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"^[\d\s.,]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingQuantity();
}

/// <summary>
/// <see cref="IUomVocabulary"/> over a tenant's <c>SetUom</c> rows. Indexed by folded code,
/// folded name AND the canonical code each of those maps to, so a tenant whose master data
/// spells the unit "Nos" is still found when the canonicaliser asks for "EA".
/// </summary>
public sealed class SetUomVocabulary : IUomVocabulary
{
    private readonly Dictionary<string, (string Code, int Id)> _index;

    private SetUomVocabulary(Dictionary<string, (string Code, int Id)> index) => _index = index;

    public static SetUomVocabulary From(IEnumerable<SetUom> rows)
    {
        var index = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Index(index, row.UomCode, row);
            Index(index, row.UomName, row);
        }
        return new SetUomVocabulary(index);
    }

    /// <summary>Adapter for call sites that already built a code/name-keyed dictionary.</summary>
    public static SetUomVocabulary From(IReadOnlyDictionary<string, SetUom> lookup)
        => From(lookup.Values.Distinct());

    public bool TryLookup(string probe, out string code, out int uomId)
    {
        if (!string.IsNullOrWhiteSpace(probe)
            && _index.TryGetValue(UomCanonicalizer.Fold(probe), out var hit))
        {
            (code, uomId) = hit;
            return true;
        }
        (code, uomId) = (string.Empty, 0);
        return false;
    }

    private static void Index(Dictionary<string, (string, int)> index, string? text, SetUom row)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var folded = UomCanonicalizer.Fold(text);
        if (folded.Length == 0) return;
        index.TryAdd(folded, (row.UomCode.Trim(), row.UomId));
        if (UomCanonicalizer.TryCanonicalCode(folded, out var canonical))
            index.TryAdd(canonical, (row.UomCode.Trim(), row.UomId));
    }
}

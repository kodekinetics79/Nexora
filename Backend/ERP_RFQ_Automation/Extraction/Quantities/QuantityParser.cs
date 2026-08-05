using System.Globalization;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction.Quantities;

/// <summary>
/// How a quantity value was arrived at. This travels with the value so a reviewer
/// can see whether the number was read or guessed, and so nothing downstream has to
/// re-derive that from the value itself (it cannot: a defaulted 1 and a read 1 are
/// indistinguishable once the provenance is dropped).
/// </summary>
public enum QuantityOrigin
{
    /// <summary>Parsed cleanly from the source text.</summary>
    Parsed,

    /// <summary>Parsed after discarding a trailing unit token (e.g. "500 EA" -> 500, "EA").</summary>
    ParsedWithUnitSuffix,

    /// <summary>Source was present but could not be read as a number. Value is null.</summary>
    Unreadable,

    /// <summary>Source was blank or absent. Value is null.</summary>
    Absent,

    /// <summary>
    /// Ambiguous grouping/decimal separators — e.g. "1.234" is 1234 under EU convention
    /// and 1.234 under US. Value is null; a human must decide.
    /// </summary>
    Ambiguous
}

/// <param name="Value">The quantity, or null when it could not be established. NEVER a substituted default.</param>
/// <param name="Origin">How <paramref name="Value"/> was arrived at.</param>
/// <param name="UnitToken">A unit token found trailing the number ("500 EA" -> "EA"), else null.</param>
/// <param name="SourceText">The original text, preserved verbatim for the review screen.</param>
public readonly record struct QuantityReading(
    decimal? Value,
    QuantityOrigin Origin,
    string? UnitToken,
    string? SourceText)
{
    /// <summary>True when a number was established and may be used downstream.</summary>
    public bool HasValue => Value.HasValue;

    /// <summary>True when a human must supply or confirm the quantity before it can be quoted.</summary>
    public bool RequiresReview => !Value.HasValue;
}

/// <summary>
/// Parses quantity text out of spreadsheet cells, OCR spans and model output.
///
/// WHY THIS EXISTS. The previous implementation was, at four separate call sites:
///
///     Quantity = int.TryParse(cell.Text, out var qty) ? qty : 1
///
/// <c>int.TryParse</c> with default <see cref="NumberStyles"/> rejects thousands
/// separators, any decimal point, and any trailing unit. So every one of these
/// silently became the number 1:
///
///     "1,000"      -> 1      (customer asked for one thousand)
///     "2,500 PCS"  -> 1      (customer asked for two and a half thousand)
///     "12.00"      -> 1      (customer asked for twelve)
///     "2.5"        -> 1      (customer asked for two and a half)
///     "1 000"      -> 1
///
/// A wrong quantity is worse than no quantity: a missing value stops and asks, while
/// a fabricated 1 looks plausible, passes review, and is quoted to the customer. In
/// production 875 of 2,966 extracted lines carried quantity 1.
///
/// THE RULE THIS CLASS ENFORCES: never invent a number. When the text cannot be read
/// with confidence the result is null with an <see cref="QuantityOrigin"/> explaining
/// why, and the line is routed to a human. Ambiguity is surfaced, not resolved by
/// guessing — "1.234" could be 1234 or 1.234 depending on locale, and picking one
/// silently is how you ship a thousand-fold error.
/// </summary>
public static class QuantityParser
{
    // Leading currency/no-op noise some exporters prefix to numeric cells.
    private static readonly Regex LeadingNoise = new(@"^[\s'`~]+", RegexOptions.Compiled);

    // A number followed by an optional unit word. Deliberately anchored: we will not
    // dig a number out of the middle of a sentence here, because "Part 4032 gasket"
    // must not yield quantity 4032. Prose quantities are the conversational
    // extractor's job, and it carries its own evidence span.
    private static readonly Regex NumberThenUnit = new(
        @"^(?<num>[+-]?[\d][\d\s.,'  ]*?)\s*(?<unit>[\p{L}][\p{L}./]{0,19})?\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Unicode minus / en-dash used as a negative sign by some ERP exports.
    private static readonly Regex UnicodeMinus = new(@"^[−–—]", RegexOptions.Compiled);

    /// <summary>
    /// Reads a quantity from raw source text. Returns a null <see cref="QuantityReading.Value"/>
    /// rather than substituting a default whenever the text cannot be read unambiguously.
    /// </summary>
    /// <param name="raw">Cell text, OCR span or model-returned token. May be null.</param>
    /// <param name="allowFractional">
    /// When false, a fractional result is reported as <see cref="QuantityOrigin.Unreadable"/>
    /// rather than being truncated. Truncating 2.5 to 2 is a silent 20% under-quote.
    /// </param>
    public static QuantityReading Parse(string? raw, bool allowFractional = true)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new QuantityReading(null, QuantityOrigin.Absent, null, raw);

        var text = LeadingNoise.Replace(raw, string.Empty).Trim();
        text = UnicodeMinus.Replace(text, "-");

        if (text.Length == 0)
            return new QuantityReading(null, QuantityOrigin.Absent, null, raw);

        var match = NumberThenUnit.Match(text);
        if (!match.Success)
            return new QuantityReading(null, QuantityOrigin.Unreadable, null, raw);

        var unit = match.Groups["unit"].Success ? match.Groups["unit"].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(unit)) unit = null;

        var numeric = match.Groups["num"].Value;

        // Strip spacing separators (ASCII space, NBSP, narrow NBSP, apostrophe — the
        // Swiss/French thousands conventions) before separator analysis.
        numeric = numeric.Replace(" ", string.Empty)
                         .Replace(" ", string.Empty)
                         .Replace(" ", string.Empty)
                         .Replace("'", string.Empty);

        var interpreted = InterpretSeparators(numeric);
        if (interpreted is null)
            return new QuantityReading(null, QuantityOrigin.Ambiguous, unit, raw);

        if (!decimal.TryParse(interpreted, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new QuantityReading(null, QuantityOrigin.Unreadable, unit, raw);

        // A negative or zero demand quantity is not a quantity. Surface it rather than
        // clamping — a "-5" in a quantity column usually means the row is a credit or
        // a return and the line needs a human, not a sign flip.
        if (value <= 0m)
            return new QuantityReading(null, QuantityOrigin.Unreadable, unit, raw);

        if (!allowFractional && value != decimal.Truncate(value))
            return new QuantityReading(null, QuantityOrigin.Unreadable, unit, raw);

        var origin = unit is null ? QuantityOrigin.Parsed : QuantityOrigin.ParsedWithUnitSuffix;
        return new QuantityReading(value, origin, unit, raw);
    }

    /// <summary>
    /// Resolves '.' and ',' into an invariant-parsable string, or returns null when the
    /// text is genuinely ambiguous between EU and US convention.
    ///
    /// The hard case is a single separator with exactly three trailing digits: "1.234"
    /// is one thousand two hundred thirty-four in Germany and one-point-two-three-four
    /// in the US. There is no way to tell from the token alone, and the two readings
    /// differ by 1000x — so we refuse and ask. Everything else is decidable.
    /// </summary>
    private static string? InterpretSeparators(string numeric)
    {
        var lastDot = numeric.LastIndexOf('.');
        var lastComma = numeric.LastIndexOf(',');
        var dots = numeric.Count(c => c == '.');
        var commas = numeric.Count(c => c == ',');

        // Both present: the RIGHTMOST is the decimal separator, the other groups.
        if (lastDot >= 0 && lastComma >= 0)
        {
            return lastDot > lastComma
                ? numeric.Replace(",", string.Empty)
                : numeric.Replace(".", string.Empty).Replace(',', '.');
        }

        // Exactly one kind of separator present.
        if (dots + commas == 0)
            return numeric;

        var sep = lastDot >= 0 ? '.' : ',';
        var count = lastDot >= 0 ? dots : commas;
        var lastIndex = lastDot >= 0 ? lastDot : lastComma;
        var trailing = numeric.Length - lastIndex - 1;

        // Repeated separator can only be grouping: "1.234.567".
        if (count > 1)
            return numeric.Replace(sep.ToString(), string.Empty);

        // Not three trailing digits -> unambiguously a decimal point: "2.5", "10.75".
        if (trailing != 3)
            return numeric.Replace(sep, '.');

        // Exactly three trailing digits. A leading zero settles it ("0.500" is decimal).
        var head = numeric[..lastIndex].TrimStart('+', '-');
        if (head == "0")
            return numeric.Replace(sep, '.');

        // A comma with three trailing digits is grouping in every locale that uses
        // comma-as-group, and locales using comma-as-decimal do not write three
        // decimal places for a purchase quantity. Treat as thousands.
        if (sep == ',')
            return numeric.Replace(",", string.Empty);

        // A dot with three trailing digits and a non-zero head: "1.234". Genuinely
        // ambiguous. Refuse.
        return null;
    }
}

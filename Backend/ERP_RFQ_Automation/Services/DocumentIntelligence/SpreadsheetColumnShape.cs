using System.Globalization;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>What a single column's VALUES look like, independent of what its heading claims.</summary>
public sealed record SpreadsheetColumnProfile(
    int Column,
    string? Header,
    int ValueCount,
    int NumericCount,
    int DateCount,
    int DistinctCount,
    int TwoDecimalCount,
    int CurrencyMarkedCount,
    int MedianTextLength,
    decimal? MedianNumber,
    bool AnyFraction,
    bool AnyNegative,
    bool AllShortAlphabetic,
    bool LooksSequential)
{
    public double NumericShare => ValueCount == 0 ? 0 : (double)NumericCount / ValueCount;
    public double DateShare => ValueCount == 0 ? 0 : (double)DateCount / ValueCount;
    public double DistinctShare => ValueCount == 0 ? 0 : (double)DistinctCount / ValueCount;
}

/// <summary>
/// The single place that decides what a column's contents look like.
///
/// <para>Both the non-bid floor (<see cref="SpreadsheetBidEvidence"/>) and the field inference
/// that reads a sheet without recognising its headers ask these same questions, and they must
/// never answer them differently. Two components carrying their own copy of "what a quantity
/// looks like" is how one of them starts admitting a column the other rejects, and neither is
/// obviously wrong when it happens.</para>
/// </summary>
public static class SpreadsheetColumnShape
{
    /// <summary>A column is only judged once it has this many non-empty values to judge from.</summary>
    public const int MinimumValuesToProfile = 3;

    /// <summary>
    /// Above this, an integer column is a reference rather than a count. Aramco material numbers
    /// run to ten digits and SAP group codes to five (53000, 61100); genuine order quantities sit
    /// orders of magnitude below both. Compared on the MEDIAN so one outlier line cannot move it.
    /// </summary>
    public const decimal LargestPlausibleQuantityMedian = 10_000m;

    /// <summary>Unit tokens are short. Longer than this and it is prose, not a unit.</summary>
    public const int LongestUnitToken = 6;

    /// <summary>Shortest text that reads as a description rather than a code.</summary>
    public const int ShortestDescriptionLength = 15;

    public static bool IsJudgeable(SpreadsheetColumnProfile p) => p.ValueCount >= MinimumValuesToProfile;

    public static bool IsDate(SpreadsheetColumnProfile p) => p.DateShare >= 0.5;

    /// <summary>
    /// Short, alphabetic, and drawn from a tiny vocabulary — "EA" repeated four hundred times.
    /// The low-cardinality test is what keeps a column of short product codes out.
    /// </summary>
    public static bool IsUnit(SpreadsheetColumnProfile p)
        => p.AllShortAlphabetic
           && p.DistinctCount <= Math.Max(2, p.ValueCount / 10);

    /// <summary>
    /// Money: explicitly marked with a currency symbol or code, or written in the two-decimal
    /// shape prices take. Never inferred from magnitude — a large integer is far more often a
    /// reference number than a price.
    /// </summary>
    public static bool IsPrice(SpreadsheetColumnProfile p)
        => p.CurrencyMarkedCount * 2 >= p.ValueCount
           || (IsMostlyNumeric(p) && p.TwoDecimalCount * 2 >= p.NumericCount && p.AnyFraction);

    /// <summary>
    /// A count: numeric, positive, and of a magnitude a person would order. A column carrying
    /// fractions is a quantity whatever its magnitude, since reference codes are never fractional.
    /// </summary>
    public static bool IsQuantity(SpreadsheetColumnProfile p)
        => IsMostlyNumeric(p)
           && !p.AnyNegative
           && (p.AnyFraction || (p.MedianNumber is { } median && median <= LargestPlausibleQuantityMedian));

    /// <summary>
    /// A reference: near-unique per row, and either long digits or a mixed code. This is what a
    /// material number, a part number or a stock code looks like from the data alone.
    /// </summary>
    public static bool IsIdentifier(SpreadsheetColumnProfile p)
        => p.DistinctShare >= 0.9
           && p.MedianTextLength is >= 4 and <= 24
           && !IsDate(p)
           && !p.AnyFraction;

    /// <summary>Free text: long enough to be prose and varied enough not to be a category.</summary>
    public static bool IsDescription(SpreadsheetColumnProfile p)
        => p.NumericShare < 0.5
           && p.MedianTextLength >= ShortestDescriptionLength
           && p.DistinctShare >= 0.2;

    public static bool IsMostlyNumeric(SpreadsheetColumnProfile p) => p.NumericCount * 10 >= p.ValueCount * 9;

    /// <summary>
    /// A line-number column — 1, 2, 3, … or 10, 20, 30 — profiles exactly like a small quantity:
    /// numeric, positive, modest median. Reading one AS a quantity would put the row's position
    /// into the customer's quote as the amount ordered, on every line, silently. Nothing else in
    /// the profile distinguishes them, so the running sequence itself is the signal.
    /// </summary>
    private static bool IsRunningSequence(List<decimal> numbers)
    {
        if (numbers.Count < MinimumValuesToProfile) return false;
        if (numbers.Any(n => n != decimal.Truncate(n))) return false;
        var ordered = numbers.OrderBy(n => n).ToList();
        if (ordered.Distinct().Count() != ordered.Count) return false;
        var step = ordered[1] - ordered[0];
        if (step <= 0) return false;
        for (var i = 2; i < ordered.Count; i++)
            if (ordered[i] - ordered[i - 1] != step) return false;
        return true;
    }

    // ---- profiling ------------------------------------------------------------------

    public static IReadOnlyList<SpreadsheetColumnProfile> Profile(
        IReadOnlyList<string[]> body, IReadOnlyList<string>? header)
    {
        var width = body.Count == 0 ? 0 : body.Max(row => row.Length);
        if (header is not null) width = Math.Max(width, header.Count);

        var profiles = new List<SpreadsheetColumnProfile>();
        for (var index = 0; index < width; index++)
        {
            var values = body
                .Where(row => index < row.Length)
                .Select(row => row[index])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();

            var numbers = new List<decimal>();
            var twoDecimal = 0;
            var currencyMarked = 0;
            var dates = 0;
            foreach (var value in values)
            {
                if (TryNumber(value, out var number))
                {
                    numbers.Add(number);
                    if (Scale(number) == 2) twoDecimal++;
                }
                if (LooksLikeCurrency(value)) currencyMarked++;
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) dates++;
            }

            profiles.Add(new SpreadsheetColumnProfile(
                Column: index + 1,
                Header: header is not null && index < header.Count ? header[index] : null,
                ValueCount: values.Count,
                NumericCount: numbers.Count,
                DateCount: dates,
                DistinctCount: values.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TwoDecimalCount: twoDecimal,
                CurrencyMarkedCount: currencyMarked,
                MedianTextLength: values.Count == 0 ? 0 : Median(values.Select(v => (decimal)v.Length).ToList()) is var m ? (int)m : 0,
                MedianNumber: numbers.Count == 0 ? null : Median(numbers),
                AnyFraction: numbers.Any(HasFraction),
                AnyNegative: numbers.Any(n => n < 0),
                AllShortAlphabetic: values.Count > 0
                    && values.All(v => v.Length is > 0 and <= LongestUnitToken && v.All(char.IsLetter)),
                LooksSequential: IsRunningSequence(numbers)));
        }
        return profiles;
    }

    /// <summary>Splits the rendered sheet text into rows of cells, dropping worksheet banners.</summary>
    public static List<string[]> ParseGrid(string renderedText)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(renderedText))
            return rows;
        foreach (var line in renderedText.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith("[Worksheet:", StringComparison.Ordinal))
                continue;
            rows.Add(line.Split('\t'));
        }
        return rows;
    }

    /// <summary>
    /// A header row is the one whose cells are all non-numeric. Profiling a header string as a
    /// value would put a text cell in every numeric column and suppress every signal.
    /// </summary>
    public static bool IsHeaderLike(string[] row)
        => row.Any(cell => !string.IsNullOrWhiteSpace(cell))
           && row.Where(cell => !string.IsNullOrWhiteSpace(cell))
                 .All(cell => !TryNumber(cell, out _));

    public static bool TryNumber(string value, out decimal number)
        => decimal.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal).Trim(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    private static bool HasFraction(decimal value) => value != decimal.Truncate(value);

    private static int Scale(decimal value) => decimal.GetBits(value)[3] >> 16 & 0xFF;

    private static bool LooksLikeCurrency(string value)
        => value.IndexOfAny(['$', '€', '£', '¥', '﷼']) >= 0
           || value.Contains("SAR", StringComparison.OrdinalIgnoreCase)
           || value.Contains("USD", StringComparison.OrdinalIgnoreCase)
           || value.Contains("PKR", StringComparison.OrdinalIgnoreCase)
           || value.Contains("AED", StringComparison.OrdinalIgnoreCase);

    private static decimal Median(List<decimal> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        return ordered[ordered.Count / 2];
    }
}

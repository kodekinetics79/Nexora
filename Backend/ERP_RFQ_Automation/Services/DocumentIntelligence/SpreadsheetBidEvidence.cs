using System.Globalization;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Decides, from CELL CONTENT alone, whether a spreadsheet holds anything that could be quoted.
///
/// <para>Exists because the deterministic column mapper answers a different question. It asks
/// "do I recognise these header spellings?", and when it does not, the document was routed to a
/// model — which is how a 2,241-row material cross-reference (ASMO item, Aramco item, group
/// description, group code) reached the chunked extraction path, was refused by the cost ceiling
/// as too large, retried five times and dead-lettered as though the product had failed to read
/// it. It read perfectly. It simply is not a bid: no line states a quantity, a price, a unit or
/// a date, so there is nothing in it to quote at any price.</para>
///
/// <para>Reading CONTENT rather than headers is what makes this survive the things headers do
/// not: a client who renames their columns, a client who reorders them, two clients who share a
/// header row and mean different things by it, a sheet with no header row at all, and headers in
/// a language nobody added aliases for. A 10-digit code is an identifier in every language.</para>
///
/// <para>DELIBERATELY ASYMMETRIC. Asserting "this column IS the quantity" is a guess that ends up
/// in a customer's quote, and a wrong one is wrong by an order of magnitude. Asserting "no column
/// anywhere carries commercial shape" is a much safer claim, and it is the only claim made here.
/// Four independent signals must ALL be absent before this reports "not a bid", because wrongly
/// rejecting a genuine enquiry costs an order, while wrongly admitting one costs a review.</para>
/// </summary>
public static class SpreadsheetBidEvidence
{
    /// <summary>A column is only judged once it has this many non-empty values to judge from.</summary>
    private const int MinimumValuesToProfile = 3;

    /// <summary>
    /// Above this, an integer column is a reference rather than a count. Aramco material numbers
    /// run to ten digits and the ASMO group codes to five (53000, 61100); genuine order
    /// quantities sit orders of magnitude below both. Compared on the MEDIAN so a single outlier
    /// line cannot move the verdict.
    /// </summary>
    private const decimal LargestPlausibleQuantityMedian = 10_000m;

    /// <summary>Unit tokens are short. Longer than this and it is prose, not a unit.</summary>
    private const int LongestUnitToken = 6;

    /// <summary>
    /// Below this many data rows the floor DECLINES TO JUDGE, and the document takes the normal
    /// path regardless of what the profile says.
    ///
    /// <para>Two reasons, and the first is the one that matters. A genuine two-line enquiry —
    /// "Gate valve DN80, EA, 6" — carries too few values for any column profile to mean anything,
    /// and refusing it as a non-bid would reject real business on no evidence. Absence of
    /// evidence is not evidence of absence. Second, the floor exists to stop waste, and there is
    /// no waste to stop here: a handful of lines is a single chunk, a few thousand tokens. The
    /// saving only becomes real on the large documents, which are exactly the ones with enough
    /// rows to judge confidently.</para>
    /// </summary>
    private const int MinimumRowsToJudge = 5;

    public sealed record Evidence(
        bool HasQuantityShapedColumn,
        bool HasPriceShapedColumn,
        bool HasDateColumn,
        bool HasUnitColumn,
        int RowCount,
        int ColumnCount)
    {
        /// <summary>
        /// True when at least one commercial signal was found. Any single one is enough — this is
        /// a floor, not a classifier.
        /// </summary>
        public bool LooksQuotable =>
            HasQuantityShapedColumn || HasPriceShapedColumn || HasDateColumn || HasUnitColumn;

        /// <summary>Enough rows for a column profile to carry any weight.</summary>
        public bool HasEnoughRowsToJudge => RowCount >= MinimumRowsToJudge;

        /// <summary>
        /// THE DECISION, and it is deliberately narrow: refuse only when there were enough rows to
        /// form a view AND not one of the four signals appeared. Everything else — a short sheet,
        /// an empty one, anything carrying a single commercial signal — goes down the normal path
        /// untouched.
        /// </summary>
        public bool ShouldRefuseAsNonBid => HasEnoughRowsToJudge && !LooksQuotable;
    }

    /// <summary>
    /// Profiles the rendered sheet text produced by <see cref="NativeSpreadsheetParser"/>
    /// (a bracketed worksheet line, then tab-joined rows). Taking the RENDERED form rather than a
    /// workbook means one implementation serves xlsx, xls and csv alike.
    /// </summary>
    public static Evidence Assess(string renderedText)
    {
        var grid = ParseGrid(renderedText);
        if (grid.Count == 0)
            return new Evidence(false, false, false, false, 0, 0);

        var width = grid.Max(row => row.Length);

        // The first row is skipped when it looks like a header rather than data: profiling a
        // header string as a value would put a text cell in every numeric column and suppress
        // every signal. Cheap test — a header row is the one whose cells are all non-numeric.
        var body = grid.Count > 1 && IsHeaderLike(grid[0]) ? grid.Skip(1).ToList() : grid;

        var quantity = false;
        var price = false;
        var date = false;
        var unit = false;

        for (var column = 0; column < width; column++)
        {
            var values = body
                .Where(row => column < row.Length)
                .Select(row => row[column])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (values.Count < MinimumValuesToProfile)
                continue;

            if (IsDateColumn(values)) { date = true; continue; }
            if (IsUnitColumn(values)) { unit = true; continue; }
            if (IsPriceColumn(values)) { price = true; continue; }
            if (IsQuantityColumn(values)) quantity = true;
        }

        return new Evidence(quantity, price, date, unit, body.Count, width);
    }

    private static bool IsHeaderLike(string[] row)
        => row.Any(cell => !string.IsNullOrWhiteSpace(cell))
           && row.Where(cell => !string.IsNullOrWhiteSpace(cell))
                 .All(cell => !TryNumber(cell, out _));

    /// <summary>
    /// A count: numeric, positive, and of a magnitude a person would order. The magnitude test is
    /// what separates a quantity from a material number — both are "numeric columns", and only
    /// the median tells them apart. A column carrying fractions is a quantity regardless of
    /// magnitude, since reference codes are never fractional.
    /// </summary>
    private static bool IsQuantityColumn(IReadOnlyList<string> values)
    {
        var numbers = Numbers(values);
        if (!MostlyNumeric(numbers.Count, values.Count) || numbers.Any(n => n < 0))
            return false;
        return numbers.Any(HasFraction) || Median(numbers) <= LargestPlausibleQuantityMedian;
    }

    /// <summary>
    /// Money: either explicitly marked with a currency symbol or code, or the two-decimal shape
    /// prices are written in. Not inferred from magnitude — a large integer is far more often a
    /// reference number than a price.
    /// </summary>
    private static bool IsPriceColumn(IReadOnlyList<string> values)
    {
        if (values.Count(LooksLikeCurrency) * 2 >= values.Count)
            return true;
        var numbers = Numbers(values);
        return MostlyNumeric(numbers.Count, values.Count)
               && numbers.Count(TwoDecimalPlaces) * 2 >= numbers.Count
               && numbers.Any(HasFraction);
    }

    private static bool IsDateColumn(IReadOnlyList<string> values)
        => values.Count(value => DateTime.TryParse(
               value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) * 2 >= values.Count;

    /// <summary>
    /// A unit column is short, alphabetic and drawn from a tiny vocabulary — "EA" repeated four
    /// hundred times. The low-cardinality test is what keeps a column of short product codes out.
    /// </summary>
    private static bool IsUnitColumn(IReadOnlyList<string> values)
    {
        var trimmed = values.Select(v => v.Trim()).ToList();
        if (!trimmed.All(v => v.Length is > 0 and <= LongestUnitToken && v.All(char.IsLetter)))
            return false;
        return trimmed.Distinct(StringComparer.OrdinalIgnoreCase).Count() <= Math.Max(2, trimmed.Count / 10);
    }

    private static bool MostlyNumeric(int numericCount, int total) => numericCount * 10 >= total * 9;

    private static List<decimal> Numbers(IEnumerable<string> values)
    {
        var numbers = new List<decimal>();
        foreach (var value in values)
            if (TryNumber(value, out var number))
                numbers.Add(number);
        return numbers;
    }

    private static bool TryNumber(string value, out decimal number)
        => decimal.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal).Trim(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    private static bool HasFraction(decimal value) => value != decimal.Truncate(value);

    private static bool TwoDecimalPlaces(decimal value) => (decimal.GetBits(value)[3] >> 16 & 0xFF) == 2;

    private static bool LooksLikeCurrency(string value)
        => value.IndexOfAny(['$', '€', '£', '¥', '﷼']) >= 0
           || value.Contains("SAR", StringComparison.OrdinalIgnoreCase)
           || value.Contains("USD", StringComparison.OrdinalIgnoreCase)
           || value.Contains("PKR", StringComparison.OrdinalIgnoreCase)
           || value.Contains("AED", StringComparison.OrdinalIgnoreCase);

    private static decimal Median(List<decimal> numbers)
    {
        var ordered = numbers.OrderBy(n => n).ToList();
        return ordered[ordered.Count / 2];
    }

    private static List<string[]> ParseGrid(string renderedText)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(renderedText))
            return rows;
        foreach (var line in renderedText.Split('\n'))
        {
            if (line.StartsWith("[Worksheet:", StringComparison.Ordinal) || line.Length == 0)
                continue;
            rows.Add(line.Split('\t'));
        }
        return rows;
    }
}

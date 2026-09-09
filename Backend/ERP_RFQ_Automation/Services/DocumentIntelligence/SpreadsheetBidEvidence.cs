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
///
/// <para>The shape rules themselves live in <see cref="SpreadsheetColumnShape"/>, shared with the
/// field inference that reads a sheet whose headers we do not recognise. The two must never
/// disagree about what a quantity looks like.</para>
/// </summary>
public static class SpreadsheetBidEvidence
{
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
    public const int MinimumRowsToJudge = 5;

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
        var reading = SpreadsheetReading.Of(renderedText);
        if (reading.Body.Count == 0)
            return new Evidence(false, false, false, false, 0, 0);

        var quantity = false;
        var price = false;
        var date = false;
        var unit = false;

        foreach (var profile in reading.Profiles)
        {
            if (!SpreadsheetColumnShape.IsJudgeable(profile)) continue;

            // Precedence matters: a date is not also considered as a quantity, and a unit column
            // is not also considered as a price. First match wins, most specific first.
            if (SpreadsheetColumnShape.IsDate(profile)) { date = true; continue; }
            if (SpreadsheetColumnShape.IsUnit(profile)) { unit = true; continue; }
            if (SpreadsheetColumnShape.IsPrice(profile)) { price = true; continue; }
            if (SpreadsheetColumnShape.IsQuantity(profile)) quantity = true;
        }

        return new Evidence(quantity, price, date, unit, reading.Body.Count, reading.Width);
    }
}

/// <summary>One rendered sheet, split into an optional header row, its data rows and their profiles.</summary>
public sealed class SpreadsheetReading
{
    public IReadOnlyList<string>? Header { get; private init; }
    public IReadOnlyList<string[]> Body { get; private init; } = Array.Empty<string[]>();
    public IReadOnlyList<SpreadsheetColumnProfile> Profiles { get; private init; } = Array.Empty<SpreadsheetColumnProfile>();

    /// <summary>Row number (1-based, within the rendered sheet) of the header, when there is one.</summary>
    public int HeaderRowNumber { get; private init; }

    public int Width => Profiles.Count;

    public static SpreadsheetReading Of(string renderedText)
    {
        var grid = SpreadsheetColumnShape.ParseGrid(renderedText);
        if (grid.Count == 0)
            return new SpreadsheetReading();

        var hasHeader = grid.Count > 1 && SpreadsheetColumnShape.IsHeaderLike(grid[0]);
        var header = hasHeader ? grid[0] : null;
        var body = hasHeader ? grid.Skip(1).ToList() : grid;

        return new SpreadsheetReading
        {
            Header = header,
            HeaderRowNumber = hasHeader ? 1 : 0,
            Body = body,
            Profiles = SpreadsheetColumnShape.Profile(body, header)
        };
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Groups the lines of an unstructured document into ONE REGION PER LINE ITEM.
///
/// <para><b>The defect this removes.</b> Both unstructured readers used to do this:</para>
/// <code>var regions = lines.Skip(headerCount).ToList();</code>
/// <para>— every LINE of text became a "line item region". On a document whose items are a
/// code followed by a specification block, that is catastrophically wrong in two directions
/// at once, and it was measured on a real customer RFQ:</para>
///
/// <list type="bullet">
/// <item>The expected count became a LINE count. A Saudi Aramco bid list reported
/// "90 item(s)" when it held a handful of real items, so the completeness ratio the
/// operator reads ("2/90 items") described nothing at all and always looked like loss.</item>
/// <item>Worse, chunking then sliced every 23 LINES. A chunk therefore held the middle of one
/// item's specification — no code, no quantity, just "CONTACT RATING:" and "SILVER PLATED;" —
/// and the model correctly found no whole item in it. Three real documents yielded 2/90, 5/137
/// and 2/32 items: 9 lines recovered out of 259 slices, with every chunk reported as
/// succeeding.</item>
/// </list>
///
/// <para><b>Why boundary detection rather than bigger chunks.</b> Enlarging the chunk would
/// have hidden the smaller documents and still cut the larger ones, because the cut is
/// arbitrary wherever it lands. An item is a semantic unit; the only safe place to divide a
/// document is between items.</para>
///
/// <para><b>Why it is conservative.</b> When the document shows no recognisable item
/// boundaries, this returns the input unchanged rather than guessing a different wrong answer.
/// A document class this cannot read keeps exactly today's behaviour; nothing regresses on the
/// strength of a heuristic.</para>
/// </summary>
public static class LineItemRegionGrouper
{
    /// <summary>
    /// A line that is nothing but a long numeric code. This is the Aramco/SAP material number
    /// shape (<c>906002718</c>) and the single most reliable item boundary in the corpus:
    /// specification lines never look like this, because they carry a label or a unit.
    /// </summary>
    private static readonly Regex StandaloneCode = new(
        @"^\s*\d{6,}\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>An explicitly numbered item: "Item 3", "Line No. 12", "Item#4".</summary>
    private static readonly Regex LabelledItem = new(
        @"^\s*(?:item|line|lot|pos(?:ition)?)\s*(?:no\.?|number|#)?\s*[:.\-]?\s*\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// An ordinal prefix followed by real text: "1. RELAY", "12) BREAKER, CIRCUIT".
    /// Bounded to three digits so a year or a quantity cannot open an item.
    /// </summary>
    private static readonly Regex OrdinalPrefix = new(
        @"^\s*\d{1,3}\s*[.)\]]\s+\S{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Minimum boundaries before grouping is trusted. One boundary is indistinguishable from a
    /// stray code inside a specification; two or more is a pattern.
    /// </summary>
    private const int MinimumBoundaries = 2;

    /// <summary>
    /// How much of a document's ordinals the longest increasing run must cover before the
    /// ordinals are believed to be an item list. See <see cref="SequentialOrdinalStarts"/>.
    /// </summary>
    private const int DominantRunPercent = 80;

    /// <summary>
    /// Returns one entry per detected line item, each carrying that item's full text. Returns
    /// <paramref name="lines"/> unchanged when no item structure is recognisable.
    /// </summary>
    public static IReadOnlyList<string> Group(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0) return Array.Empty<string>();

        // HIGH-PRECISION SHAPES FIRST. A standalone material code and an explicitly labelled
        // item are unambiguous: specification prose never looks like either. An ordinal prefix
        // is different — "1." opens a line item on a bid list and a numbered CLAUSE in a
        // contract, and a 54-page Word RFQ is full of the latter.
        var starts = new List<int>();
        for (var i = 0; i < lines.Count; i++)
            if (StandaloneCode.IsMatch(lines[i]) || LabelledItem.IsMatch(lines[i])) starts.Add(i);

        // Only when the precise shapes find nothing is the ordinal considered, and then only
        // if the ordinals RUN — a real item list numbers 1, 2, 3, 4 in order, while numbered
        // clauses scattered through prose restart and repeat.
        if (starts.Count < MinimumBoundaries)
            starts = SequentialOrdinalStarts(lines);

        // Not enough structure to be sure. Keep today's behaviour rather than trade one wrong
        // answer for another.
        if (starts.Count < MinimumBoundaries) return lines;

        // OVER-DETECTION GUARD, and it is a COST control as much as a correctness one.
        //
        // The chunk plan is derived from this count, and every chunk resends the full
        // extraction prompt — so an inflated region count multiplies the bill directly. A real
        // line item carries a description, a quantity and a unit, so it occupies several lines;
        // a document claiming an item on more than every other line is being mis-read.
        //
        // Measured: a 54-page .docx reported 1,603 items and planned 70 chunks. It consumed an
        // entire monthly token budget, was refused partway at chunk 49, and returned 24 real
        // line items. Almost every "item" was a numbered paragraph.
        if (starts.Count * 2 > lines.Count) return lines;

        var regions = new List<string>(starts.Count + 1);

        // Anything before the first boundary is preamble that belongs to no item. It is kept as
        // its own region rather than dropped: on some documents it carries a delivery term or a
        // currency the items are quoted in, and losing it silently is how a quote goes out in
        // the wrong currency.
        if (starts[0] > 0)
            regions.Add(Join(lines, 0, starts[0]));

        for (var s = 0; s < starts.Count; s++)
        {
            var from = starts[s];
            var to = s + 1 < starts.Count ? starts[s + 1] : lines.Count;
            regions.Add(Join(lines, from, to));
        }

        return regions;
    }


    /// <summary>
    /// Ordinal starts, but only when they form a genuine run.
    ///
    /// <para>A bid list numbers its items 1, 2, 3, 4 in order. A contract numbers clauses too,
    /// and restarts per section — so an ordinal that does not increase on the previous one is
    /// evidence of prose, not of an item list. Requiring a mostly-increasing sequence keeps the
    /// rule useful on real item lists and silent on documents full of numbered paragraphs.</para>
    /// </summary>
    private static List<int> SequentialOrdinalStarts(IReadOnlyList<string> lines)
    {
        var candidates = new List<(int Line, int Ordinal)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var match = OrdinalPrefix.Match(lines[i]);
            if (!match.Success) continue;
            var digits = new string(lines[i].SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var ordinal)) candidates.Add((i, ordinal));
        }
        if (candidates.Count < MinimumBoundaries) return new List<int>();

        // The longest strictly-increasing run wins. Anything outside it is prose that happens
        // to be numbered.
        var best = new List<int>();
        var run = new List<int> { 0 };
        for (var i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].Ordinal > candidates[i - 1].Ordinal) run.Add(i);
            else { if (run.Count > best.Count) best = run; run = new List<int> { i }; }
        }
        if (run.Count > best.Count) best = run;

        // THE RUN MUST DOMINATE, not merely exist.
        //
        // A real item list numbers 1..n once, so its longest increasing run IS essentially every
        // ordinal in the document. Contract prose numbers clauses and restarts them per section,
        // so its longest run is a small fraction of the total — four sections of six clauses
        // yields a run of six out of twenty-four candidates. Requiring the run to cover most of
        // the candidates is what separates the two, and a bare "is there a run of two or more"
        // did not: it grouped a contract on its first six clauses.
        var dominates = best.Count * 100 >= candidates.Count * DominantRunPercent;

        return best.Count >= MinimumBoundaries && dominates
            ? best.Select(index => candidates[index].Line).ToList()
            : new List<int>();
    }

    private static bool StartsItem(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return StandaloneCode.IsMatch(line)
            || LabelledItem.IsMatch(line)
            || OrdinalPrefix.IsMatch(line);
    }

    private static string Join(IReadOnlyList<string> lines, int from, int toExclusive)
    {
        var sb = new StringBuilder();
        for (var i = from; i < toExclusive; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }
}

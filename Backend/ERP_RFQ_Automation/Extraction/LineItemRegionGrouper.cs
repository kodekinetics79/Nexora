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
    /// Returns one entry per detected line item, each carrying that item's full text. Returns
    /// <paramref name="lines"/> unchanged when no item structure is recognisable.
    /// </summary>
    public static IReadOnlyList<string> Group(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0) return Array.Empty<string>();

        var starts = new List<int>();
        for (var i = 0; i < lines.Count; i++)
            if (StartsItem(lines[i])) starts.Add(i);

        // Not enough structure to be sure. Keep today's behaviour rather than trade one wrong
        // answer for another.
        if (starts.Count < MinimumBoundaries) return lines;

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

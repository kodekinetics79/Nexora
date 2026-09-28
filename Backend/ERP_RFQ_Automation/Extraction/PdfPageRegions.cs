using System.Text;
using ERP_RFQ_Automation.Extraction.Anchoring;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Splits a PDF's text into one region per PAGE for the model path.
///
/// <para><b>Why pages.</b> The reader used to emit each page as one run-on line, so every
/// downstream rule that assumes lines (the 20-line header, item-boundary detection, the header
/// cut at the first item) silently read the first 20 PAGES as the header and every page as one
/// "item". With real lines those rules would now run on SAP and Ariba prints — and there they
/// misfire: the numbered LCPGA clauses ("1. Vendor must classify …", "2. …") read as a run of
/// item starts and would push the one real line into the header, where the model is told not to
/// look. A page is a boundary the document itself draws, it is never inside a number, and it is
/// what a reviewer is pointed at ("Page 3, line 12").</para>
///
/// <para><b>Nothing is hidden in the header.</b> The regions together are the whole document from
/// its first line; the header context is only a COPY of the top lines, sent for reference. An item
/// printed above line 20 is therefore in the first region, where the first call reads it, and a
/// later call that repeats it from the reference copy is recognised as an echo and dropped.</para>
/// </summary>
public static class PdfPageRegions
{
    /// <summary>Largest region sent as one piece; a page longer than this is split at line breaks.</summary>
    internal const int MaxRegionCharacters = 8_000;

    /// <summary>True when the text carries the reader's page markers.</summary>
    public static bool HasPageMarkers(IReadOnlyList<string> lines)
        => lines.Any(line => AnchorText.IsPageMarker(line.Trim(), out _));

    /// <summary>
    /// The header (a copy of the first <paramref name="headerLineCap"/> text lines) and the regions:
    /// whatever precedes the first page (an e-mail body) grouped as the line-item grouper groups any
    /// text, then one region per page, each starting with its page marker.
    /// </summary>
    public static (string Header, List<string> Regions) Split(IReadOnlyList<string> lines, int headerLineCap)
    {
        var header = string.Join('\n', lines
            .Where(line => !AnchorText.IsPageMarker(line.Trim(), out _))
            .Take(Math.Max(0, headerLineCap)));

        var regions = new List<string>();
        var preamble = new List<string>();
        var index = 0;
        while (index < lines.Count && !AnchorText.IsPageMarker(lines[index].Trim(), out _))
            preamble.Add(lines[index++]);
        if (preamble.Count > 0)
            regions.AddRange(LineItemRegionGrouper.Group(preamble).SelectMany(Bounded));

        var page = new List<string>();
        for (; index < lines.Count; index++)
        {
            if (AnchorText.IsPageMarker(lines[index].Trim(), out _) && page.Count > 0)
            {
                regions.AddRange(Bounded(string.Join('\n', page)));
                page.Clear();
            }
            page.Add(lines[index]);
        }
        if (page.Count > 0)
            regions.AddRange(Bounded(string.Join('\n', page)));
        return (header, regions);
    }

    /// <summary>A region no longer than <see cref="MaxRegionCharacters"/>, cut only at line breaks.</summary>
    private static IEnumerable<string> Bounded(string region)
    {
        if (region.Length <= MaxRegionCharacters)
        {
            yield return region;
            yield break;
        }
        var piece = new StringBuilder();
        foreach (var line in region.Split('\n'))
        {
            if (piece.Length > 0 && piece.Length + line.Length + 1 > MaxRegionCharacters)
            {
                yield return piece.ToString();
                piece.Clear();
            }
            if (piece.Length > 0) piece.Append('\n');
            piece.Append(line);
        }
        if (piece.Length > 0) yield return piece.ToString();
    }
}

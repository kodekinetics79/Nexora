using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction.Anchoring;

/// <summary>
/// A stretch of document text — the regions one extraction call was given — that remembers the
/// page and line every character came from, so a value found in it can be cited ("Page 3,
/// line 12") and a reviewer can go and look.
///
/// <para>Pages are known from the <c>[[page N]]</c> markers the PDF reader writes at the top of
/// every page (<see cref="PageMarker"/>). Text without markers — an e-mail body, a document read
/// as plain text — is numbered by line alone. Blank lines are not counted: the reader drops them
/// before the text reaches the extractor.</para>
/// </summary>
public sealed partial class AnchorText
{
    private readonly int[] _lineStarts;
    private readonly int[] _linePages;
    private readonly int[] _lineNumbers;
    private string? _canonical;
    private int[]? _canonicalMap;

    /// <summary>The page marker the PDF reader writes on its own line at the start of every page.</summary>
    public static string PageMarker(int page) => $"[[page {page}]]";

    /// <summary>True when <paramref name="line"/> is a page marker; <paramref name="page"/> is its number.</summary>
    public static bool IsPageMarker(string line, out int page)
    {
        page = 0;
        var match = PageMarkerLine().Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, out page);
    }

    /// <summary>Removes page-marker lines, for readers that work on the document as printed.</summary>
    public static string WithoutPageMarkers(string text)
        => text.Contains("[[page ", StringComparison.Ordinal)
            ? PageMarkerLineWithBreak().Replace(text, string.Empty)
            : text;

    public string Text { get; }

    private AnchorText(string text, int[] lineStarts, int[] linePages, int[] lineNumbers)
    {
        Text = text;
        _lineStarts = lineStarts;
        _linePages = linePages;
        _lineNumbers = lineNumbers;
    }

    /// <summary>
    /// Joins <paramref name="regions"/> with line breaks, the way they were sent to the model.
    /// <paramref name="startPage"/>/<paramref name="startLine"/> are the page and line number of the
    /// first line (0 page = no page known yet; lines count from 1).
    /// </summary>
    public static AnchorText Build(IEnumerable<string> regions, int startPage = 0, int startLine = 1)
    {
        var text = string.Join('\n', regions.Select(r => r ?? string.Empty));
        var starts = new List<int>();
        var pages = new List<int>();
        var numbers = new List<int>();
        var page = startPage;
        var number = startLine - 1;
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            starts.Add(offset);
            if (IsPageMarker(line.Trim(), out var marked))
            {
                page = marked;
                number = 0;
                pages.Add(page);
                numbers.Add(0);
            }
            else
            {
                number++;
                pages.Add(page);
                numbers.Add(number);
            }
            offset += line.Length + 1;
        }
        return new AnchorText(text, starts.ToArray(), pages.ToArray(), numbers.ToArray());
    }

    /// <summary>
    /// The page and line number reached at the END of <paramref name="regions"/>, i.e. the position
    /// the next region starts from. Used to give every region of a document its starting position.
    /// </summary>
    public static (int Page, int Line) Advance(string region, int page, int line)
    {
        foreach (var raw in (region ?? string.Empty).Split('\n'))
        {
            if (IsPageMarker(raw.Trim(), out var marked)) { page = marked; line = 0; }
            else line++;
        }
        return (page, line);
    }

    /// <summary>Index of the line holding <paramref name="offset"/>.</summary>
    public int LineIndexOf(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, Math.Clamp(offset, 0, Math.Max(0, Text.Length)));
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    public int LineCount => _lineStarts.Length;

    public int LineStart(int lineIndex) => _lineStarts[Math.Clamp(lineIndex, 0, _lineStarts.Length - 1)];

    public int LineEnd(int lineIndex)
    {
        var index = Math.Clamp(lineIndex, 0, _lineStarts.Length - 1);
        return index + 1 < _lineStarts.Length ? _lineStarts[index + 1] - 1 : Text.Length;
    }

    public string Line(int lineIndex) => Text[LineStart(lineIndex)..LineEnd(lineIndex)];

    /// <summary>"Page 3, line 12" when the page is known, otherwise "Line 12".</summary>
    public string Address(int offset)
    {
        var line = LineIndexOf(offset);
        var page = _linePages[line];
        var number = Math.Max(1, _lineNumbers[line]);
        return page > 0 ? $"Page {page}, line {number}" : $"Line {number}";
    }

    /// <summary>
    /// The text reduced to upper-case letters and digits, with a map back to the original offsets.
    /// Matching a description on this form ignores the spacing and punctuation the reader and the
    /// model disagree on ("40KVAPRIMARY" / "40KVA PRIMARY", "STEP UP ,400" / "STEP UP, 400").
    /// </summary>
    public (string Canonical, int[] Map) Canonical()
    {
        if (_canonical is not null) return (_canonical, _canonicalMap!);
        var builder = new StringBuilder(Text.Length);
        var map = new List<int>(Text.Length);
        for (var i = 0; i < Text.Length; i++)
        {
            var c = Text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            builder.Append(char.ToUpperInvariant(c));
            map.Add(i);
        }
        _canonical = builder.ToString();
        _canonicalMap = map.ToArray();
        return (_canonical, _canonicalMap);
    }

    /// <summary>Upper-case letters and digits of <paramref name="value"/>.</summary>
    public static string CanonicalOf(string? value)
        => value is null ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    [GeneratedRegex(@"^\[\[page (\d{1,5})\]\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PageMarkerLine();

    [GeneratedRegex(@"(?m)^[ \t]*\[\[page \d{1,5}\]\][ \t]*(?:\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PageMarkerLineWithBreak();
}

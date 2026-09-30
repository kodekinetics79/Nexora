using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// The rows of a large Word file around the line a rep is checking, read on the server, so the
/// "check against the document" pane shows the line in a second instead of drawing the whole file.
///
/// <para><b>Why.</b> The browser drew a Word file whole: the 6.8 MB Aramco RFP is a 224 MB
/// <c>document.xml</c> with 102,066 table rows, which took 54 s on a quiet laptop and 183 s under
/// load, to show one line. A line read from a Word table already says where it came from
/// (<c>'Table 7'!R23823</c>), so the pane needs only that table's rows around that row.</para>
///
/// <para><b>Numbering is the parser's.</b> Tables are the TOP-LEVEL tables of the body in document
/// order, from 1 (<see cref="DocxTableParser.Parse(byte[], string)"/>); rows are every top-level
/// row of the table, from 1, as the grid and form readers count them. A merged cell keeps the
/// columns after it in place, as <c>BuildGrid</c> does.</para>
///
/// <para>Streamed: the file is read once, and only kept rows collect their text.</para>
/// </summary>
public static class WordTableWindowReader
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// The names this reader looks for, atomized in the reader's own name table, so each of the
    /// ~20 million nodes of a large file is compared by reference, not letter by letter.
    /// </summary>
    private sealed class Names
    {
        public readonly string Ns, Body, Tbl, Tr, Tc, P, T, GridSpan, Val;
        public Names(XmlNameTable table)
        {
            Ns = table.Add(W); Body = table.Add("body"); Tbl = table.Add("tbl"); Tr = table.Add("tr");
            Tc = table.Add("tc"); P = table.Add("p"); T = table.Add("t"); GridSpan = table.Add("gridSpan"); Val = table.Add("val");
        }
    }

    /// <summary>
    /// A document whose text part is at most this size is drawn whole in the browser, as before
    /// (about 2 s at the measured ~4 MB/s). Larger ones are shown as rows.
    /// </summary>
    public const long PageViewMaxXmlBytes = 8L * 1024 * 1024;

    /// <summary>Rows shown before and after the line being checked (an Aramco item is ~34 rows).</summary>
    public const int RowsBefore = 5;
    public const int RowsAfter = 60;

    /// <summary>First rows of every other table, so the rep can still look at them.</summary>
    public const int LeadingRows = 30;

    /// <summary>First rows of every table when no line is being checked.</summary>
    public const int LeadingRowsWithoutFocus = 200;

    /// <summary>The uncompressed size of the text part, or null when the bytes are not a Word file.</summary>
    public static long? DocumentXmlLength(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            return zip.GetEntry("word/document.xml")?.Length;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The top-level tables up to the line's, each as a sheet named "Table N": the line's table as its
    /// heading row and the rows around <paramref name="focusRow"/>, the tables before it by their
    /// first <see cref="LeadingRows"/> rows. Reading stops there. With no line, the first
    /// <see cref="LeadingRowsWithoutFocus"/> rows of every table.
    /// </summary>
    public static SourceGrid Read(byte[] bytes, int? focusTable, int? focusRow)
    {
        var focused = focusTable is > 0 && focusRow is > 0;
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The file has no Word document part.");
        var nameTable = new NameTable();
        var n = new Names(nameTable);
        using var xml = XmlReader.Create(new BufferedStream(entry.Open(), 1 << 20), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false, IgnoreComments = true,
            IgnoreProcessingInstructions = true, CheckCharacters = false, NameTable = nameTable
        });

        var sheets = new List<SourceGridSheet>();
        var bodyDepth = -1;
        var ordinal = 0;
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || !ReferenceEquals(xml.NamespaceURI, n.Ns)) continue;
            if (ReferenceEquals(xml.LocalName, n.Body)) { bodyDepth = xml.Depth; continue; }
            if (!ReferenceEquals(xml.LocalName, n.Tbl) || bodyDepth < 0 || xml.Depth != bodyDepth + 1) continue;

            ordinal++;
            Func<int, bool> keep = !focused
                ? row => row <= LeadingRowsWithoutFocus
                : ordinal == focusTable
                    ? row => row == 1 || (row >= focusRow!.Value - RowsBefore && row <= focusRow.Value + RowsAfter)
                    : row => row <= LeadingRows;
            var lastWanted = !focused ? LeadingRowsWithoutFocus
                : ordinal == focusTable ? focusRow!.Value + RowsAfter : LeadingRows;

            // The line's table is the last one read: the rows after its window, and the tables after
            // it, are most of a large file (the 224 MB Aramco RFP took 2–4 s to read to the end).
            var isFocus = focused && ordinal == focusTable;
            var (rows, total, stopped) = ReadTable(xml, n, keep, lastWanted, stopAfterLastWanted: isFocus);
            if (rows.Count > 0)
                sheets.Add(new SourceGridSheet($"Table {ordinal}", HeaderRowNumber: 1, rows, Truncated: stopped || rows.Count < total));
            if (isFocus) break;
        }
        return new SourceGrid(sheets);
    }

    /// <summary>
    /// One table's kept rows. Stops collecting after <paramref name="lastWanted"/>; counts the rest
    /// (so <c>Truncated</c> is true only when rows were left out) unless told to stop reading there.
    /// </summary>
    private static (List<SourceGridRow> Rows, int Total, bool Stopped) ReadTable(
        XmlReader xml, Names n, Func<int, bool> keep, int lastWanted, bool stopAfterLastWanted)
    {
        var rows = new List<SourceGridRow>();
        var number = 0;
        // The table is read on the document's own reader, not a subtree reader: closing a subtree
        // reader reads on to the table's end, which made "stop here" read the whole 135 MB table.
        var table = xml;
        var tableDepth = table.Depth;
        if (table.IsEmptyElement) return (rows, 0, false);
        List<string>? cells = null;
        StringBuilder? cell = null;
        var paragraphs = 0;
        var span = 1;
        // Reading a text node's content already moves to the next node, so the loop reads on only
        // when nothing else has; otherwise the second text of a run would be skipped.
        var more = table.Read();
        while (more && table.Depth > tableDepth)
        {
            if (ReferenceEquals(table.NamespaceURI, n.Ns) && table.NodeType == XmlNodeType.Element)
            {
                // Rows and cells of THIS table only; a nested table's text belongs to its cell.
                if (ReferenceEquals(table.LocalName, n.Tr) && table.Depth == tableDepth + 1)
                {
                    if (stopAfterLastWanted && number >= lastWanted) return (rows, number, true);
                    number++;
                    cells = number <= lastWanted && keep(number) ? new List<string>() : null;
                }
                else if (cells is not null && ReferenceEquals(table.LocalName, n.Tc) && table.Depth == tableDepth + 2)
                {
                    cell = new StringBuilder(); paragraphs = 0; span = 1;
                }
                else if (cells is not null && cell is not null && ReferenceEquals(table.LocalName, n.GridSpan) && table.Depth == tableDepth + 4)
                {
                    span = int.TryParse(table.GetAttribute(n.Val, n.Ns), out var value) && value > 1
                        ? Math.Min(value, SpreadsheetGridReader.MaxColumns) : 1;
                }
                else if (cells is not null && cell is not null && ReferenceEquals(table.LocalName, n.P))
                {
                    if (paragraphs++ > 0) cell.Append(' ');
                }
                else if (cells is not null && cell is not null && ReferenceEquals(table.LocalName, n.T))
                {
                    cell.Append(table.ReadElementContentAsString());
                    more = !table.EOF;
                    continue;
                }
            }
            else if (ReferenceEquals(table.NamespaceURI, n.Ns) && table.NodeType == XmlNodeType.EndElement && cells is not null)
            {
                if (ReferenceEquals(table.LocalName, n.Tc) && cell is not null && table.Depth == tableDepth + 2)
                {
                    cells.Add(cell.ToString().Replace('\u00A0', ' ').Trim());
                    // A merged cell holds its text in its first column; the rest keep their places.
                    for (var padding = 1; padding < span; padding++) cells.Add(string.Empty);
                    cell = null;
                }
                else if (ReferenceEquals(table.LocalName, n.Tr) && table.Depth == tableDepth + 1)
                {
                    var last = cells.FindLastIndex(text => text.Length > 0);
                    if (last >= 0)
                        rows.Add(new SourceGridRow(number, cells.Take(Math.Min(last + 1, SpreadsheetGridReader.MaxColumns)).ToArray()));
                    cells = null;
                }
            }
            more = table.Read();
        }
        return (rows, number, false);
    }
}

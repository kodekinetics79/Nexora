using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The "check against the document" pane shows a large Word file's rows around the line being
/// checked instead of the browser drawing the whole file (54–183 s for the 6.8 MB Aramco RFP).
/// The row it marks must be the row the parser read the line from.
/// </summary>
public sealed class WordTableWindowReaderTests
{
    [Fact]
    public void The_marked_row_is_the_row_the_parser_read_the_line_from()
    {
        // The Aramco/ASMO form shape: a heading row, then a block of labels per item.
        var rows = new List<string[]> { new[] { "Name", "Alternative", "Value" } };
        foreach (var (heading, description, qty, material) in new[]
                 {
                     ("8 BATTERY: LEAD ACID", "BATTERY: LEAD ACID, 12 V, 6 CELLS", "1 each", "000000002000008534"),
                     ("9 BEARING Shell", "BEARING Shell two halves for DE and NDE", "4 each", "000000002000010473"),
                     ("10 BEARING SLEEVE", "BEARING SLEEVE, SHELL", "2 set", "000000002000010961")
                 })
        {
            rows.Add(new[] { heading, string.Empty, string.Empty });
            rows.Add(new[] { description, string.Empty, string.Empty });
            rows.Add(new[] { "Quantity", string.Empty, qty });
            rows.Add(new[] { "Material Number", string.Empty, material });
        }
        var bytes = Document(body =>
        {
            body.AppendChild(Table(new[] { new[] { "Timing Rules" }, new[] { "Due date", "10/8/2026 3:00 PM" } }));
            body.AppendChild(Table(rows));
        });

        var parsed = new DocxTableParser(new NativeSpreadsheetParser()).Parse(bytes, "RFP 6000000003.docx");
        var third = parsed[2];
        var address = third.SourceAddress(RfqSpreadsheetFields.ProductName, "row");
        var match = Regex.Match(address, @"^'Table (\d+)'!R(\d+)$");
        Assert.True(match.Success, address);
        var (table, row) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));

        var grid = WordTableWindowReader.Read(bytes, table, row);

        var sheet = Assert.Single(grid.Sheets, s => s.Name == $"Table {table}");
        // The marked row is the row the parser addressed. A form block addresses its name at the
        // block's first label row, with the name itself just above it, inside the rows shown.
        Assert.Single(sheet.Rows, r => r.Number == row);
        Assert.Contains(sheet.Rows, r => r.Number >= row - WordTableWindowReader.RowsBefore && r.Number <= row
            && r.Cells.Contains(third.ProductName!));
        var quantityRow = third.SourceAddress(RfqSpreadsheetFields.Quantity, "row");
        var quantity = sheet.Rows.Single(r => $"'Table {table}'!R{r.Number}" == quantityRow);
        Assert.Equal(new[] { "Quantity", "", "2 set" }, quantity.Cells);
        // The heading row stays on top so the columns keep their names.
        Assert.Equal(1, sheet.HeaderRowNumber);
        Assert.Equal(new[] { "Name", "Alternative", "Value" }, sheet.Rows[0].Cells);
    }

    [Fact]
    public void A_large_table_is_shown_around_the_line_and_other_tables_by_their_first_rows()
    {
        var bytes = Document(body =>
        {
            body.AppendChild(Table(Enumerable.Range(1, 40).Select(i => new[] { $"terms {i}", "value" })));
            body.AppendChild(Table(Enumerable.Range(1, 500).Select(i => new[] { $"line {i}", $"{i} each" })));
        });

        var grid = WordTableWindowReader.Read(bytes, focusTable: 2, focusRow: 300);

        Assert.Equal(new[] { "Table 1", "Table 2" }, grid.Sheets.Select(s => s.Name));
        var terms = grid.Sheets[0];
        Assert.Equal(Enumerable.Range(1, WordTableWindowReader.LeadingRows), terms.Rows.Select(r => r.Number));
        Assert.True(terms.Truncated);
        var lines = grid.Sheets[1];
        var expected = new[] { 1 }.Concat(Enumerable.Range(300 - WordTableWindowReader.RowsBefore,
            WordTableWindowReader.RowsBefore + WordTableWindowReader.RowsAfter + 1));
        Assert.Equal(expected, lines.Rows.Select(r => r.Number));
        Assert.Equal(new[] { "line 300", "300 each" }, lines.Rows.Single(r => r.Number == 300).Cells);
        Assert.True(lines.Truncated);
    }

    [Fact]
    public void Without_a_line_every_table_shows_its_first_rows()
    {
        var bytes = Document(body =>
        {
            body.AppendChild(Table(Enumerable.Range(1, 3).Select(i => new[] { $"a {i}" })));
            body.AppendChild(Table(Enumerable.Range(1, 250).Select(i => new[] { $"b {i}" })));
        });

        var grid = WordTableWindowReader.Read(bytes, focusTable: null, focusRow: null);

        Assert.False(grid.Sheets[0].Truncated);
        Assert.Equal(3, grid.Sheets[0].Rows.Count);
        Assert.Equal(WordTableWindowReader.LeadingRowsWithoutFocus, grid.Sheets[1].Rows.Count);
        Assert.True(grid.Sheets[1].Truncated);
    }

    [Fact]
    public void A_merged_cell_keeps_the_columns_after_it_and_every_run_of_text_is_read()
    {
        var merged = new TableCell(
            new TableCellProperties(new GridSpan { Val = 2 }),
            new Paragraph(new Run(new Text("MODULE,") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve },
                new Text(" 16 CHANNEL"))));
        var bytes = Document(body =>
        {
            var table = Table(new[] { new[] { "Item", "Description", "Detail", "Quantity" } });
            table.AppendChild(new TableRow(Cell("1"), merged, Cell("4")));
            body.AppendChild(table);
        });

        var row = WordTableWindowReader.Read(bytes, 1, 2).Sheets[0].Rows.Single(r => r.Number == 2);

        Assert.Equal(new[] { "1", "MODULE, 16 CHANNEL", "", "4" }, row.Cells);
    }

    [Fact]
    public void Only_a_word_file_has_a_document_part_size()
    {
        var bytes = Document(body => body.AppendChild(new Paragraph(new Run(new Text("Dear supplier")))));

        Assert.InRange(WordTableWindowReader.DocumentXmlLength(bytes)!.Value, 1, WordTableWindowReader.PageViewMaxXmlBytes);
        Assert.Null(WordTableWindowReader.DocumentXmlLength("not a zip"u8.ToArray()));
    }

    private static Table Table(IEnumerable<string[]> rows)
    {
        var table = new Table();
        foreach (var cells in rows)
            table.AppendChild(new TableRow(cells.Select(Cell)));
        return table;
    }

    private static TableCell Cell(string text) => new(new Paragraph(new Run(new Text(text))));

    private static byte[] Document(Action<Body> populate)
    {
        using var stream = new MemoryStream();
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                   stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = main.Document.AppendChild(new Body());
            populate(body);
            main.Document.Save();
        }
        return stream.ToArray();
    }
}

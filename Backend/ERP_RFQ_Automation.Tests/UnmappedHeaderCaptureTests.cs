using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A column the vocabulary does not recognise used to be DROPPED on the deterministic path;
/// the reviewer could not see what the document said and nothing could learn from the
/// correction. It now travels with the line (ExtraFields) or the inquiry (unmapped headers).
/// </summary>
public sealed class UnmappedHeaderCaptureTests
{
    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    [Fact]
    public void An_unrecognised_column_travels_with_the_line_to_its_extra_fields()
    {
        var rows = new NativeSpreadsheetParser().ParseCsv(Csv(
            "Description,Qty,Cut-off,Zone,Buyer Note",
            "Relay module,4,10/08/2026,East,",
            "Display,2,10/08/2026,,urgent"), "bid.csv");

        Assert.Equal(new Dictionary<string, string> { ["Cut-off"] = "10/08/2026", ["Zone"] = "East" }, rows[0].UnmappedColumns);
        Assert.Equal(new Dictionary<string, string> { ["Cut-off"] = "10/08/2026", ["Buyer Note"] = "urgent" }, rows[1].UnmappedColumns);

        var import = new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, businessUnitId: 1);
        var line = Assert.Single(import.Documents).LineItems[0];
        Assert.Equal("10/08/2026", line.ExtraFields!["Cut-off"]);
    }

    [Fact]
    public void A_recognised_column_is_never_duplicated_into_extra_fields()
    {
        var rows = new NativeSpreadsheetParser().ParseCsv(Csv("Description,Qty,Bid Closing Date", "Relay,4,2026-08-10"), "bid.csv");
        Assert.Empty(Assert.Single(rows).UnmappedColumns);
        Assert.Null(Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, 1).Documents).LineItems[0].ExtraFields);
    }

    [Fact]
    public void Unrecognised_document_labels_travel_with_the_inquiry()
    {
        var bytes = WordDocument(
            paragraphs: new[] { "Cut-off: 10/8/2026 3:00 PM", "Please quote: as per the attached specification and terms.", "RFQ Number: RFQ-9" },
            metadata: new[] { ("Currency", "US Dollar"), ("Region", "Eastern") },
            table: (new[] { "Part No", "Description", "Qty" }, new[] { "P-1", "Valve", "5" }));

        var rows = new DocxTableParser(new NativeSpreadsheetParser()).Parse(bytes, "event.docx");
        var row = Assert.Single(rows);

        Assert.Equal("RFQ-9", row.RfqNo);
        Assert.Equal("10/8/2026 3:00 PM", row.UnmappedHeaderLabels["Cut-off"]);
        Assert.Equal("US Dollar", row.UnmappedHeaderLabels["Currency"]);
        Assert.Equal("Eastern", row.UnmappedHeaderLabels["Region"]);
        // A sentence with a colon is prose, not a label.
        Assert.DoesNotContain("Please quote", row.UnmappedHeaderLabels.Keys);

        var document = Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, 1).Documents);
        Assert.Equal("10/8/2026 3:00 PM", document.UnmappedHeaders["Cut-off"]);
    }

    private static byte[] WordDocument(string[] paragraphs, (string[] Header, string[] Row) table, (string Label, string Value)[]? metadata = null)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());
            foreach (var text in paragraphs) body.AppendChild(Paragraph(text));
            if (metadata is not null)
            {
                var meta = new Table();
                foreach (var (label, value) in metadata) meta.AppendChild(Row(new[] { label, value }));
                body.AppendChild(meta);
            }
            var grid = new Table();
            grid.AppendChild(Row(table.Header));
            grid.AppendChild(Row(table.Row));
            body.AppendChild(grid);
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph();
        paragraph.AppendChild(new Run()).AppendChild(new Text(text));
        return paragraph;
    }

    private static TableRow Row(string[] cells)
    {
        var row = new TableRow();
        foreach (var value in cells)
        {
            var cell = new TableCell();
            cell.AppendChild(Paragraph(value));
            row.AppendChild(cell);
        }
        return row;
    }
}

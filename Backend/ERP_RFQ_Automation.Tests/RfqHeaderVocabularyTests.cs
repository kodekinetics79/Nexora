using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Every customer heads the same field differently, and every customer's export is the same every
/// time — so a spelling that misses once misses on every file that customer sends. There were
/// three separate spelling lists (spreadsheet columns, Word header block, legacy CSV) and they
/// disagreed; "BCD" was in none of them. One vocabulary now serves all three doors.
/// </summary>
public sealed class RfqHeaderVocabularyTests
{
    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    private static readonly NativeSpreadsheetParser Grid = new();
    private static readonly DocxTableParser Word = new(Grid);

    // ------------------------------------------------------------------ closing date spellings

    [Theory]
    [InlineData("BCD")]
    [InlineData("Bid Close Date")]
    [InlineData("Bid Close")]
    [InlineData("Response Date")]
    [InlineData("Response Due Date")]
    [InlineData("Last Date for Submission")]
    [InlineData("Quotation Due Date")]
    [InlineData("RFQ Due Date")]
    [InlineData("Closing Date & Time")]
    [InlineData("Tender Deadline")]
    [InlineData("Reply By")]
    [InlineData("Bid Closing Date")]
    public void A_closing_date_column_is_read_under_every_customer_spelling(string header)
    {
        var rows = Grid.ParseCsv(Csv($"Description,Qty,{header}", "Relay module,4,2026-10-08"), "bid.csv");
        Assert.Equal("2026-10-08", Assert.Single(rows).BidClosingDate);
    }

    [Theory]
    [InlineData("BCD")]
    [InlineData("Bid Close Date")]
    [InlineData("Response Date")]
    [InlineData("Last Date for Submission")]
    public void A_closing_date_label_in_a_word_header_block_is_read_under_the_same_spellings(string label)
    {
        var bytes = WordDocument(
            paragraphs: new[] { $"{label}: 2026-10-08" },
            table: (new[] { "Part No", "Description", "Qty" }, new[] { "P-1", "Valve", "5" }));

        Assert.Equal("2026-10-08", Assert.Single(Word.Parse(bytes, "bid.docx")).BidClosingDate);
    }

    [Theory]
    [InlineData("BCD")]
    [InlineData("Response date")]
    public void A_closing_date_in_a_portal_metadata_table_is_read_under_the_same_spellings(string label)
    {
        var bytes = WordDocument(
            paragraphs: Array.Empty<string>(),
            metadata: new[] { (label, "10/8/2026 3:00 PM"), ("Currency", "US Dollar") },
            table: (new[] { "Part No", "Description", "Qty" }, new[] { "P-1", "Valve", "5" }));

        Assert.Equal("10/8/2026 3:00 PM", Assert.Single(Word.Parse(bytes, "event.docx")).BidClosingDate);
    }

    [Theory]
    [InlineData("BCD")]
    [InlineData("Response Date")]
    public void The_legacy_csv_door_reads_the_same_spellings_as_the_production_parser(string header)
    {
        var rows = DefaultExtractionDocumentReader.ParseCsv(
            new List<string> { $"Product Name,Quantity,{header}", "Relay module,4,2026-10-08" }, "bid.csv");

        Assert.Equal("2026-10-08", Assert.Single(rows).BidClosingDate);
    }

    // --------------------------------------------------------- manufacturer and part number

    [Theory]
    [InlineData("Manufacturer")]
    [InlineData("Make")]
    [InlineData("Brand")]
    [InlineData("OEM")]
    [InlineData("Make / Brand")]
    [InlineData("Mfr Name")]
    public void A_manufacturer_column_is_read_under_every_customer_spelling(string header)
    {
        var rows = Grid.ParseCsv(Csv($"Description,Qty,{header}", "Contactor,4,Siemens"), "bid.csv");
        Assert.Equal("Siemens", Assert.Single(rows).ManufacturerName);
    }

    [Theory]
    [InlineData("Part Number")]
    [InlineData("P/N")]
    [InlineData("Mfr P/N")]
    [InlineData("OEM Part No")]
    [InlineData("Catalogue No.")]
    [InlineData("Article Number")]
    [InlineData("SKU")]
    public void A_part_number_column_is_read_under_every_customer_spelling(string header)
    {
        var rows = Grid.ParseCsv(Csv($"Description,Qty,{header}", "Contactor,4,3RT2015-1BB41"), "bid.csv");
        Assert.Equal("3RT2015-1BB41", Assert.Single(rows).ManufacturerPartNumber);
    }

    // ------------------------------------------------------------------------ list hygiene

    [Fact]
    public void No_spelling_names_two_fields()
    {
        foreach (var view in new[] { RfqHeaderVocabulary.Builtin.ColumnAliases, RfqHeaderVocabulary.Builtin.LabelAliases })
        {
            var duplicates = view
                .SelectMany(pair => pair.Value.Select(spelling => (spelling, field: pair.Key)))
                .GroupBy(entry => entry.spelling, StringComparer.Ordinal)
                .Where(group => group.Select(e => e.field).Distinct().Count() > 1)
                .Select(group => $"{group.Key}: {string.Join(", ", group.Select(e => e.field))}")
                .ToList();

            Assert.Empty(duplicates);
        }
    }

    [Fact]
    public void Every_spelling_is_already_normalised()
    {
        var offenders = RfqHeaderVocabulary.Builtin.ColumnAliases.Values
            .Concat(RfqHeaderVocabulary.Builtin.LabelAliases.Values)
            .SelectMany(list => list)
            .Where(spelling => RfqHeaderVocabulary.Normalize(spelling) != spelling)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_document_label_knows_every_column_spelling_of_its_field()
    {
        foreach (var field in RfqHeaderVocabulary.InquiryLevelFields)
        {
            var columns = RfqHeaderVocabulary.Builtin.ColumnAliases[field];
            var labels = RfqHeaderVocabulary.Builtin.LabelAliases[field];
            Assert.True(columns.All(labels.Contains), $"{field} label list is missing a column spelling.");
        }
    }

    [Fact]
    public void A_reference_column_on_a_line_grid_is_not_the_rfq_number()
    {
        // "Reference: RFQ-1" above the table names the inquiry; a "Reference" COLUMN is the
        // buyer's per-line reference, and reading it as the RFQ number would split every line
        // into its own inquiry.
        var rows = Grid.ParseCsv(Csv("Reference,Description,Qty", "L-1,Valve,5"), "bid.csv");
        Assert.Null(Assert.Single(rows).RfqNo);

        Assert.Equal(RfqSpreadsheetFields.RfqNo, RfqHeaderVocabulary.Builtin.FieldForLabel("Reference"));
        // A sourcing event's "Owner" label names the buyer's person; an "Owner" column does not.
        Assert.Equal(RfqSpreadsheetFields.BuyerName, RfqHeaderVocabulary.Builtin.FieldForLabel("Owner"));
        Assert.Null(RfqHeaderVocabulary.Builtin.FieldForColumn("Owner"));
        Assert.Null(RfqHeaderVocabulary.Builtin.FieldForColumn("Reference"));
    }

    // ------------------------------------------------------------------- learned spellings

    [Fact]
    public void A_learned_spelling_is_read_as_a_column_and_as_a_label()
    {
        var taught = RfqHeaderVocabulary.Builtin.WithLearned(new[]
        {
            new LearnedHeaderSpelling("Fecha límite", RfqSpreadsheetFields.BidClosingDate),
        });

        Assert.Equal(RfqSpreadsheetFields.BidClosingDate, taught.FieldForColumn("Fecha Límite"));
        Assert.Equal(RfqSpreadsheetFields.BidClosingDate, taught.FieldForLabel("fecha_limite"));
        Assert.Null(RfqHeaderVocabulary.Builtin.FieldForColumn("Fecha límite"));

        var rows = new NativeSpreadsheetParser(taught)
            .ParseCsv(Csv("Description,Qty,Fecha límite", "Valve,5,2026-10-08"), "bid.csv");
        Assert.Equal("2026-10-08", Assert.Single(rows).BidClosingDate);
    }

    [Fact]
    public void A_learned_spelling_can_never_override_a_built_in_one()
    {
        // One mistaken confirmation must not re-route a field the parser already reads correctly.
        var taught = RfqHeaderVocabulary.Builtin.WithLearned(new[]
        {
            new LearnedHeaderSpelling("Due Date", RfqSpreadsheetFields.ReceivedDate),
            new LearnedHeaderSpelling("Whatever", "NotAField"),
        });

        Assert.Equal(RfqSpreadsheetFields.BidClosingDate, taught.FieldForColumn("Due Date"));
        Assert.Null(taught.FieldForColumn("Whatever"));
        Assert.Empty(taught.Learned);
    }

    [Fact]
    public void The_first_confirmation_of_a_learned_spelling_wins()
    {
        var taught = RfqHeaderVocabulary.Builtin
            .WithLearned(new[] { new LearnedHeaderSpelling("Fecha", RfqSpreadsheetFields.BidClosingDate) })
            .WithLearned(new[] { new LearnedHeaderSpelling("Fecha", RfqSpreadsheetFields.ReceivedDate) });

        Assert.Equal(RfqSpreadsheetFields.BidClosingDate, taught.FieldForColumn("Fecha"));
        Assert.Single(taught.Learned);
    }

    // --------------------------------------------------------------------------- helpers

    private static byte[] WordDocument(
        string[] paragraphs,
        (string[] Header, string[] Row) table,
        (string Label, string Value)[]? metadata = null)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            foreach (var text in paragraphs)
                body.AppendChild(Paragraph(text));

            if (metadata is not null)
            {
                var meta = new Table();
                foreach (var (label, value) in metadata)
                    meta.AppendChild(Row(new[] { label, value }));
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

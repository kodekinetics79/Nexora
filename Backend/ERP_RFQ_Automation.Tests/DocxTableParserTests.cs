using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A Word RFQ that states its lines in a table is structured data. Reading it deterministically
/// costs nothing, cannot hallucinate, and does not depend on an AI provider the tenant may not be
/// authorized to use — which in the current deployment is every tenant.
///
/// <para>The fixtures are two documents from the 120-document sample set, kept verbatim so the
/// parser is tested against the real shape rather than one invented to suit it.</para>
/// </summary>
public sealed class DocxTableParserTests
{
    private static readonly DocxTableParser Parser = new(new NativeSpreadsheetParser());

    private static IReadOnlyList<RfqSpreadsheetRow> Parse(string fixture)
        => Parser.Parse(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)),
            fixture);

    [Fact]
    public void Every_line_of_a_word_table_rfq_is_read()
    {
        var rows = Parse("rfq-table.docx");

        Assert.Equal(8, rows.Count);

        var first = rows[0];
        Assert.Equal("SKU-2244", first.ManufacturerPartNumber);
        Assert.Equal("Safety Relay", first.ProductName);
        Assert.Equal("57", first.Quantity);

        var last = rows[^1];
        Assert.Equal("SKU-4816", last.ManufacturerPartNumber);
        Assert.Equal("Siemens PLC S7-1200 CPU 1214C", last.ProductName);
        Assert.Equal("33", last.Quantity);
    }

    [Fact]
    public void The_header_block_above_the_table_is_applied_to_every_line()
    {
        // "RFQ Number: RFQ-260011Customer: Omega OilRFQ Date: 2026-05-26" — several labelled
        // values concatenated into one paragraph with no separator, which is why each value has
        // to run to the next known label rather than to the end of the line.
        var rows = Parse("rfq-table.docx");

        Assert.All(rows, row =>
        {
            Assert.Equal("RFQ-260011", row.RfqNo);
            Assert.Equal("Omega Oil", row.BuyerName);
            Assert.Equal("2026-05-26", row.ReceivedDate);
        });
    }

    [Fact]
    public void A_second_document_from_the_same_set_reads_with_its_own_identity()
    {
        var rows = Parse("rfq-table-2.docx");

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal("RFQ-260049", row.RfqNo);
            Assert.Equal("Titan Industries", row.BuyerName);
        });
    }

    [Fact]
    public void No_unit_column_means_no_unit_rather_than_an_assumed_one()
    {
        // This sample set states no unit. A quantity with no unit is honest; a quantity silently
        // labelled "each" is a commercial error waiting to be quoted.
        Assert.All(Parse("rfq-table.docx"), row => Assert.Null(row.UnitOfMeasure));
    }

    [Fact]
    public void The_buyers_note_against_each_line_is_captured()
    {
        // "OEM only" and "Equivalent accepted" change what may legitimately be quoted. Dropping
        // them silently is a commercial error, not a cosmetic one.
        var rows = Parse("rfq-table.docx");

        Assert.Equal("Urgent requirement", rows[0].ItemText);
        Assert.Contains(rows, r => r.ItemText == "OEM only");
        Assert.Contains(rows, r => r.ItemText == "Equivalent accepted");
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.ItemText)));
    }

    [Fact]
    public void A_document_with_no_table_yields_no_rows_and_falls_through()
    {
        var bytes = BuildDocx("Dear supplier,", "Please quote for two centrifugal pumps.", "Regards");
        Assert.Empty(Parser.Parse(bytes, "prose.docx"));
    }

    [Fact]
    public void A_table_whose_columns_mean_nothing_yields_no_rows()
    {
        var bytes = BuildDocxWithTable(
            new[] { "Section", "Narrative", "Owner" },
            new[] { "1", "Site mobilisation", "Ops" });

        Assert.Empty(Parser.Parse(bytes, "minutes.docx"));
    }

    [Fact]
    public void A_line_level_column_wins_over_the_document_header_block()
    {
        var bytes = BuildDocxWithTable(
            new[] { "Part No", "Description", "Qty", "RFQ No" },
            new[] { "P-1", "Valve", "5", "LINE-RFQ" },
            headerBlock: "RFQ Number: DOC-RFQ");

        var row = Assert.Single(Parser.Parse(bytes, "mixed.docx"));
        Assert.Equal("LINE-RFQ", row.RfqNo);
    }

    // ------------------------------------------------------------------------------ helpers

    private static byte[] BuildDocx(params string[] paragraphs)
        => BuildDocument(body =>
        {
            foreach (var text in paragraphs)
                body.AppendChild(Paragraph(text));
        });

    private static byte[] BuildDocxWithTable(string[] header, string[] row, string? headerBlock = null)
        => BuildDocument(body =>
        {
            if (headerBlock is not null)
                body.AppendChild(Paragraph(headerBlock));

            var table = new DocumentFormat.OpenXml.Wordprocessing.Table();
            table.AppendChild(Row(header));
            table.AppendChild(Row(row));
            body.AppendChild(table);
        });

    private static byte[] BuildDocument(Action<DocumentFormat.OpenXml.Wordprocessing.Body> populate)
    {
        using var stream = new MemoryStream();
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                   stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = main.Document.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Body());
            populate(body);
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static DocumentFormat.OpenXml.Wordprocessing.Paragraph Paragraph(string text)
    {
        var paragraph = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
        var run = paragraph.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Run());
        run.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text(text));
        return paragraph;
    }

    private static DocumentFormat.OpenXml.Wordprocessing.TableRow Row(string[] cells)
    {
        var row = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
        foreach (var value in cells)
        {
            var cell = new DocumentFormat.OpenXml.Wordprocessing.TableCell();
            cell.AppendChild(Paragraph(value));
            row.AppendChild(cell);
        }
        return row;
    }
}

/// <summary>
/// Sweeps the full 120-document sample set when it is present on the machine, and is skipped
/// otherwise so CI is not tied to a folder outside the repository. Point
/// NEXORA_RFQ_CORPUS at a directory of .docx files to run it.
/// </summary>
public sealed class DocxCorpusSweepTests
{
    private static readonly DocxTableParser Parser = new(new NativeSpreadsheetParser());

    private static string? CorpusPath()
    {
        var configured = Environment.GetEnvironmentVariable("NEXORA_RFQ_CORPUS");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        return null;
    }

    [Fact]
    public void Every_document_in_the_sample_set_is_read_deterministically()
    {
        var path = CorpusPath();
        if (path is null) return;   // corpus not present on this machine

        var files = Directory.GetFiles(path, "*.docx");
        if (files.Length == 0) return;

        var failures = new List<string>();
        var totalLines = 0;

        foreach (var file in files)
        {
            var rows = Parser.Parse(File.ReadAllBytes(file), Path.GetFileName(file));
            if (rows.Count == 0) { failures.Add($"{Path.GetFileName(file)}: no rows"); continue; }
            totalLines += rows.Count;

            var bad = rows.Where(r => string.IsNullOrWhiteSpace(r.RfqNo)
                                   || string.IsNullOrWhiteSpace(r.BuyerName)
                                   || string.IsNullOrWhiteSpace(r.ProductName)
                                   || string.IsNullOrWhiteSpace(r.Quantity)).ToList();
            if (bad.Count > 0)
                failures.Add($"{Path.GetFileName(file)}: {bad.Count} incomplete line(s)");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {files.Length} documents did not read cleanly ({totalLines} lines read):\n"
            + string.Join("\n", failures.Take(15)));

        // Guards against a parser that "succeeds" by reading one line per document.
        Assert.True(totalLines >= files.Length * 3,
            $"Only {totalLines} lines across {files.Length} documents — suspiciously few.");
    }
}

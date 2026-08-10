using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

/// <summary>
/// A Word RFQ corpus written in code rather than checked in as binary.
///
/// <para><b>Why this exists.</b> The 120-document sample set on the reviewer's machine is ONE
/// template rendered 120 times: one table each, an identical header shape, 8 of the 16 canonical
/// fields, every quantity a bare ASCII integer between 1 and 75, and zero merged cells, zero
/// second tables, zero nested tables, zero Arabic, zero Hijri dates, no closing date, no unit of
/// measure, no currency and no price. It proves the pipeline RUNS. It cannot prove the pipeline
/// READS, and every defect this corpus covers passed it.</para>
///
/// <para>Each document below is the smallest one that states a shape the sample set never states.
/// They are generated so a reviewer can read the document's content as source and diff a change
/// to it — a .docx in the repository is an opaque zip that no review can inspect.</para>
/// </summary>
internal static class SyntheticRfqCorpus
{
    // ---------------------------------------------------------------- header-block spellings

    /// <summary>
    /// One line table under a single header paragraph. The paragraph is the whole point: every
    /// spelling a buyer uses for the date a bid closes has to reach the closing-date field, and
    /// the bare "date" alias must not swallow the label it is the tail of.
    /// </summary>
    public static byte[] HeaderBlock(string headerLine) => Build(body =>
    {
        body.AppendChild(Paragraph(headerLine));
        body.AppendChild(LineItemTable());
    });

    /// <summary>The minimal, unambiguous line table these header documents hang off.</summary>
    private static Table LineItemTable() => TableOf(
        new[] { "Part No", "Description", "Qty", "UOM" },
        new[] { "P-1", "Ball Valve 2in 150#", "250", "EA" });

    // ---------------------------------------------------------------- merged cells

    /// <summary>
    /// A horizontally merged cell. Word states it as ONE <c>w:tc</c> carrying
    /// <c>w:gridSpan="2"</c>, so a positional read of the row's elements shifts every column to
    /// its right by one: the quantity lands in the unit column and the unit falls off the end.
    /// </summary>
    public static byte[] HorizontallyMergedCell() => Build(body =>
    {
        var table = new Table();
        table.AppendChild(Row(new[] { "Item Code", "Description", "Qty", "UOM" }));
        table.AppendChild(Row(new[] { "P-1", "Ball Valve 2in", "250", "EA" }));

        var merged = new TableRow();
        merged.AppendChild(SpanningCell("P-2 Gate Valve 4in", 2));
        merged.AppendChild(Cell("40"));
        merged.AppendChild(Cell("EA"));
        table.AppendChild(merged);

        body.AppendChild(table);
    });

    /// <summary>
    /// A vertically merged cell. The owning cell carries <c>w:vMerge w:val="restart"</c> and the
    /// rows below carry an EMPTY continuation cell, so a positional read gives the value to the
    /// first line and null to every line that shares it.
    /// </summary>
    public static byte[] VerticallyMergedCell() => Build(body =>
    {
        var table = new Table();
        table.AppendChild(Row(new[] { "Part No", "Description", "Qty" }));

        var first = new TableRow();
        first.AppendChild(VerticalMergeCell("P-9", restart: true));
        first.AppendChild(Cell("Flange 2in"));
        first.AppendChild(Cell("10"));
        table.AppendChild(first);

        foreach (var (description, quantity) in new[] { ("Flange 3in", "20"), ("Flange 4in", "30") })
        {
            var continuation = new TableRow();
            continuation.AppendChild(VerticalMergeCell(null, restart: false));
            continuation.AppendChild(Cell(description));
            continuation.AppendChild(Cell(quantity));
            table.AppendChild(continuation);
        }

        body.AppendChild(table);
    });

    // ---------------------------------------------------------------- second / nested tables

    /// <summary>
    /// A line table followed by a commercial-terms table. The terms table maps exactly one
    /// column — its left column reads "Description" — so every row of it looks like a line item
    /// called "Payment Terms", "Incoterms" or "Validity".
    /// </summary>
    public static byte[] LineTableThenCommercialTerms() => Build(body =>
    {
        body.AppendChild(TableOf(
            new[] { "Part No", "Description", "Qty", "UOM" },
            new[] { "P-1", "Ball Valve 2in", "250", "EA" },
            new[] { "P-2", "Gate Valve 4in", "40", "EA" }));

        body.AppendChild(TableOf(
            new[] { "Description", "Value" },
            new[] { "Payment Terms", "30 days net" },
            new[] { "Incoterms", "DDP Dammam" },
            new[] { "Validity", "60 days" }));
    });

    /// <summary>
    /// A line table whose description cell holds a nested specification table. The nested table
    /// is a table in its own right, and its text belongs to it — not folded into the product name
    /// of the cell that contains it.
    /// </summary>
    public static byte[] NestedSpecificationTable() => Build(body =>
    {
        var table = new Table();
        table.AppendChild(Row(new[] { "Part No", "Description", "Qty" }));

        var withNested = new TableRow();
        withNested.AppendChild(Cell("P-7"));

        var host = new TableCell();
        host.AppendChild(Paragraph("Gate Valve 4in"));
        host.AppendChild(TableOf(
            new[] { "Qty", "Description" },
            new[] { "999", "Body material SS316" },
            new[] { "888", "Pressure rating 150#" }));
        // Word requires a paragraph after a nested table inside a cell.
        host.AppendChild(Paragraph(string.Empty));
        withNested.AppendChild(host);

        withNested.AppendChild(Cell("12"));
        table.AppendChild(withNested);

        table.AppendChild(Row(new[] { "P-8", "Check Valve 2in", "5" }));
        body.AppendChild(table);
    });

    // ---------------------------------------------------------------- quantity shapes

    /// <summary>
    /// Every quantity spelling the sample set never contains, on one document: a thousands
    /// separator, a unit inside the cell, a decimal that is whole, a space-grouped number, a
    /// range, Arabic-Indic digits, and the genuinely ambiguous "1.234".
    /// </summary>
    public static byte[] QuantityShapes() => Build(body =>
    {
        body.AppendChild(Paragraph("RFQ Number: RFQ-Q1 Customer: Aramco RFQ Date: 2026-05-26"));
        body.AppendChild(TableOf(
            new[] { "Part No", "Description", "Qty", "UOM" },
            new[] { "P-1", "Cable 3 core 95mm", "2,500", "M" },
            new[] { "P-2", "Gasket spiral wound", "500 PCS", "" },
            new[] { "P-3", "Bolt M20", "12.00", "EA" },
            new[] { "P-4", "Nut M20", "1 000", "EA" },
            new[] { "P-5", "Washer M20", "10-20", "EA" },
            new[] { "P-6", "صمام كروي", "٥٠٠", "EA" },
            new[] { "P-7", "Butterfly Valve", "1.234", "EA" },
            new[] { "P-8", "Centrifugal Pump", "2.5", "EA" }));
    });

    // ---------------------------------------------------------------- dates

    /// <summary>A tender that closes at a stated time of day, not at the end of a day.</summary>
    public static byte[] ClosingTime() => Build(body =>
    {
        body.AppendChild(Paragraph(
            "RFQ Number: RFQ-T1 Customer: Aramco RFQ Date: 2026-05-26 Bid Closing Date: 2026-09-01 14:00"));
        body.AppendChild(LineItemTable());
    });

    /// <summary>A numeric date whose day and month are both 12 or lower.</summary>
    public static byte[] AmbiguousDayMonth() => Build(body =>
    {
        body.AppendChild(Paragraph("RFQ Number: RFQ-A1 Customer: Aramco RFQ Date: 03/04/2026"));
        body.AppendChild(LineItemTable());
    });

    /// <summary>A Saudi government pack stating its closing date in the Umm al-Qura calendar.</summary>
    public static byte[] HijriClosingDate() => Build(body =>
    {
        body.AppendChild(Paragraph(
            "RFQ Number: RFQ-H1 Customer: Aramco RFQ Date: 2026-05-26 Bid Closing Date: 15/03/1447"));
        body.AppendChild(LineItemTable());
    });

    // ---------------------------------------------------------------- OpenXML plumbing

    private static byte[] Build(Action<Body> populate)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            populate(main.Document.AppendChild(new Body()));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static Table TableOf(params string[][] rows)
    {
        var table = new Table();
        foreach (var row in rows)
            table.AppendChild(Row(row));
        return table;
    }

    private static TableRow Row(string[] cells)
    {
        var row = new TableRow();
        foreach (var value in cells)
            row.AppendChild(Cell(value));
        return row;
    }

    private static TableCell Cell(string? text)
    {
        var cell = new TableCell();
        cell.AppendChild(Paragraph(text ?? string.Empty));
        return cell;
    }

    /// <summary>A cell occupying <paramref name="columns"/> grid columns (<c>w:gridSpan</c>).</summary>
    private static TableCell SpanningCell(string text, int columns)
    {
        var cell = new TableCell();
        cell.AppendChild(new TableCellProperties(new GridSpan { Val = columns }));
        cell.AppendChild(Paragraph(text));
        return cell;
    }

    /// <summary>
    /// A cell taking part in a vertical merge: the owner carries <c>w:val="restart"</c> and every
    /// row below carries a bare <c>w:vMerge</c> with no text at all.
    /// </summary>
    private static TableCell VerticalMergeCell(string? text, bool restart)
    {
        var cell = new TableCell();
        cell.AppendChild(new TableCellProperties(restart
            ? new VerticalMerge { Val = MergedCellValues.Restart }
            : new VerticalMerge()));
        cell.AppendChild(Paragraph(text ?? string.Empty));
        return cell;
    }

    private static Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph();
        var run = paragraph.AppendChild(new Run());
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return paragraph;
    }
}

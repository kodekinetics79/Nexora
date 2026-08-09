using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Quantities;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake;

/// <summary>
/// FR-COM-01. Turns an already-read customer purchase-order document into the lines and header
/// values a human then confirms.
///
/// <para><b>This is not a new parser and must not become one.</b> A customer PO is a tabular
/// commercial document with exactly the shape of an RFQ, so every piece of reading is delegated:
/// bytes to text/rows by <see cref="IExtractionDocumentReader"/> (PdfPig text layer with a
/// Tesseract OCR fallback for scans, OpenXML for Word, EPPlus/ExcelDataReader for workbooks);
/// column recognition and header-row location by
/// <see cref="NativeSpreadsheetParser.ParseGrid"/>; quantities and money by
/// <see cref="QuantityParser"/>; dates by <see cref="RfqDateParser"/>. What is written here is the
/// part that genuinely differs between an RFQ and a PO: the document-level labels
/// ("Purchase Order No.", "PO Date"), and the decision about which identity key a buyer's code
/// column actually is.</para>
///
/// <para><b>Nothing is guessed.</b> A quantity, price or date that cannot be read is returned as
/// null with a reason attached, because a fabricated value on a purchase order is a value that
/// looks reviewed, passes the quote-versus-PO comparison, and is invoiced.</para>
/// </summary>
public sealed class CustomerPurchaseOrderDocumentExtractor
{
    /// <summary>Lines of a text document scanned for document-level labels.</summary>
    private const int HeaderScanLineLimit = 200;

    /// <summary>Upper bound on preview lines, so one pathological file cannot become an unbounded response.</summary>
    private const int MaximumLines = 2_000;

    private const string PoNumberField = "PoNumber";
    private const string PoDateField = "PoDate";

    /// <summary>
    /// Document-level labels, compared with case, spacing and punctuation removed so "P.O. No.:",
    /// "PO NUMBER" and "purchase_order_no" all land on the same field.
    /// </summary>
    private static readonly Dictionary<string, string[]> HeaderLabels = new(StringComparer.Ordinal)
    {
        [PoNumberField] =
        [
            "purchaseordernumber", "purchaseorderno", "purchaseorderref", "purchaseorder",
            "customerponumber", "customerpono", "clientponumber", "clientpono",
            "localpurchaseorderno", "lpronumber", "lponumber", "lpono", "lpo",
            "ponumber", "poref", "porefno", "pono", "ordernumber", "orderno", "po"
        ],
        [PoDateField] =
        [
            "purchaseorderdate", "poissuedate", "issuedate", "dateofissue", "documentdate",
            "orderdate", "podate", "date"
        ],
    };

    /// <summary>
    /// Labels too generic to be trusted as a bare table heading. "Date" as a column above a
    /// delivery-date column would otherwise silently become the PO date. These are honoured only
    /// in the explicit "Label: value" form.
    /// </summary>
    private static readonly HashSet<string> ColonOnlyLabels = new(StringComparer.Ordinal)
    {
        // "PURCHASE ORDER" is the TITLE of nearly every purchase order and almost never a column
        // heading, so honouring it bare would read the first label underneath as the PO number.
        "po", "purchaseorder", "date", "orderno", "ordernumber"
    };

    /// <summary>
    /// Header spellings that mean "the BUYER's code for this item" rather than "the manufacturer's
    /// part number". The two are different keys and matching is specified on both, so a column
    /// headed "Material Code" must not be recorded as a manufacturer part number.
    /// </summary>
    private static readonly string[] BuyerItemCodeHeaderMarkers =
        ["item", "material", "customer", "client", "sku", "stockcode", "articleno", "articlenumber"];

    private readonly NativeSpreadsheetParser _grid;

    public CustomerPurchaseOrderDocumentExtractor(NativeSpreadsheetParser grid) => _grid = grid;

    public CustomerPurchaseOrderDocumentExtractor() : this(new NativeSpreadsheetParser())
    {
    }

    /// <param name="document">What the shared document reader made of the file.</param>
    /// <param name="bytes">The stored bytes, used only to render a workbook/Word header block as text.</param>
    /// <param name="extension">Lower-case extension without the dot, as inspection established it.</param>
    public CustomerPurchaseOrderDocumentReading Read(
        DocumentExtractionInput document, byte[] bytes, string extension)
    {
        ArgumentNullException.ThrowIfNull(document);

        var rows = ReadRows(document);
        var text = ReadText(document, bytes, extension);

        if (rows.Count == 0)
        {
            throw new CustomerPurchaseOrderDocumentException(
                CustomerPurchaseOrderDocumentErrorCodes.NoLineItems,
                string.IsNullOrWhiteSpace(text)
                    ? "Nexora accepted this file but could not read any text or rows out of it, so no purchase-order "
                      + "lines were extracted. If it is a scan, upload a clearer copy; otherwise send the PO as a PDF, "
                      + "Word or Excel file, or enter the lines manually."
                    : "Nexora read this document but could not identify a line-item table in it, so no purchase-order "
                      + "lines were extracted. Check that the item table has column headings such as description, "
                      + "quantity and unit price, or enter the lines manually.");
        }

        var lines = rows.Take(MaximumLines)
            .Select((row, index) => MapLine(index + 1, row))
            .ToList();

        var reviewReasons = new List<string>();
        if (rows.Count > MaximumLines)
        {
            reviewReasons.Add($"The document states {rows.Count} lines; the first {MaximumLines} are shown. "
                + "Split the purchase order or enter the remainder manually.");
        }

        var poNumber = FindLabelledValue(text, PoNumberField);
        if (string.IsNullOrWhiteSpace(poNumber))
        {
            poNumber = null;
            reviewReasons.Add("The purchase-order number could not be found in the document. Enter it before saving.");
        }
        else if (poNumber.Length > 200)
        {
            poNumber = null;
            reviewReasons.Add("The text found next to the purchase-order number label was too long to be a PO number. "
                + "Enter it before saving.");
        }

        var dateText = FindLabelledValue(text, PoDateField);
        var dateReading = RfqDateParser.Read(dateText);
        if (!dateReading.HasValue)
        {
            reviewReasons.Add(string.IsNullOrWhiteSpace(dateText)
                ? "The purchase-order date could not be found in the document. Enter it before saving."
                : $"\"{dateText}\" was found next to the purchase-order date label but could not be read as a date. "
                  + "Enter it before saving.");
        }
        else if (dateReading.IsDayMonthAmbiguous)
        {
            reviewReasons.Add($"\"{dateText}\" is ambiguous — both parts are 12 or lower, so it could be either "
                + "day/month or month/day. It has been read day-first; confirm it before saving.");
        }

        return new CustomerPurchaseOrderDocumentReading(
            poNumber?.Trim(),
            dateReading.Value,
            dateText,
            dateReading.IsDayMonthAmbiguous,
            lines,
            reviewReasons);
    }

    // ---- rows ------------------------------------------------------------

    /// <summary>
    /// A workbook, CSV or Word table already arrived as mapped rows on the deterministic path. An
    /// unstructured document (PDF text layer, OCR of a scan, prose Word, image) arrives as text
    /// lines, which are split into cells and put through the SAME column mapper — so one set of
    /// buyer column spellings serves every format.
    /// </summary>
    private IReadOnlyList<RfqSpreadsheetRow> ReadRows(DocumentExtractionInput document)
    {
        if (document.IsStructured && document.StructuredRows is { Count: > 0 } structured)
            return structured;

        var lines = TextLines(document);
        if (lines.Count < 2)
            return [];

        var grid = lines.Select(SplitCells).ToList();
        return _grid.ParseGrid(grid, document.SourceDocumentName, "Document");
    }

    /// <summary>
    /// Splits one text line into cells on a tab, or failing that on a run of two or more spaces —
    /// which is how a PDF text layer and an OCR pass render a column gap. A line with neither is
    /// a single cell, which the header locator will simply not recognise.
    /// </summary>
    private static IReadOnlyList<string?> SplitCells(string line)
    {
        var cells = line.Contains('\t') ? line.Split('\t') : ColumnGap.Split(line);
        return cells.Select(cell => string.IsNullOrWhiteSpace(cell) ? null : cell.Trim()).ToList();
    }

    private static readonly Regex ColumnGap = new(@"\s{2,}", RegexOptions.Compiled);

    // ---- text ------------------------------------------------------------

    /// <summary>
    /// The text used for the document-level label scan. Unstructured documents already are text.
    /// A workbook read on the deterministic path is rendered by the spreadsheet parser's own
    /// renderers; a Word document by its paragraph text — in both cases so that "PO Number: …"
    /// stated ABOVE the item table is still visible, which is where buyers put it.
    /// </summary>
    private string ReadText(DocumentExtractionInput document, byte[] bytes, string extension)
    {
        if (!document.IsStructured)
            return string.Join('\n', TextLines(document));

        try
        {
            return extension switch
            {
                "xlsx" or "xlsm" => _grid.RenderXlsxText(bytes),
                "xls" => _grid.RenderXlsText(bytes),
                "csv" => _grid.RenderCsvText(bytes),
                "docx" => DocxParagraphText(bytes),
                _ => string.Join('\n', TextLines(document)),
            };
        }
        catch (Exception)
        {
            // The rows are already in hand; a header block we cannot render costs a review flag,
            // never the whole document.
            return string.Empty;
        }
    }

    private static List<string> TextLines(DocumentExtractionInput document)
    {
        var header = (document.HeaderText ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.Trim().Length > 0)
            .ToList();
        var regions = document.LineItemRegions.Where(line => line.Trim().Length > 0).ToList();

        // The reader repeats the whole document as regions when the body is short; do not
        // duplicate the header block in that case.
        if (regions.Count >= header.Count && header.Count > 0
            && regions.Take(header.Count).SequenceEqual(header, StringComparer.Ordinal))
            return regions;

        return [.. header, .. regions];
    }

    /// <summary>
    /// Paragraph text from a .docx, excluding paragraphs inside tables.
    ///
    /// <para>A Word purchase order whose lines are in a table is read structurally by
    /// <see cref="DocxTableParser"/>, which deliberately returns only line rows — the "Purchase
    /// Order No: …" block above the table is paragraphs, not cells, and is not part of that
    /// contract. This reads exactly those paragraphs and nothing else.</para>
    /// </summary>
    private static string DocxParagraphText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var word = WordprocessingDocument.Open(stream, isEditable: false);
        var body = word.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var text = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            if (paragraph.Ancestors<Table>().Any()) continue;
            var value = paragraph.InnerText.Replace(' ', ' ').Trim();
            if (value.Length > 0) text.Append(value).Append('\n');
        }
        return text.ToString();
    }

    // ---- line mapping ----------------------------------------------------

    private static CustomerPurchaseOrderDocumentLineView MapLine(int ordinal, RfqSpreadsheetRow row)
    {
        var reasons = new List<string>();

        var description = Trimmed(row.ProductName) ?? Trimmed(row.ItemText);
        if (description is null)
            reasons.Add("The document states no description for this line.");

        var quantity = QuantityParser.Parse(row.Quantity);
        if (!quantity.HasValue)
            reasons.Add(QuantityReason("quantity", quantity));

        var unitPrice = QuantityParser.Parse(row.UnitPrice);
        if (!unitPrice.HasValue)
            reasons.Add(QuantityReason("unit price", unitPrice));

        var (itemCode, partNumber) = SplitIdentityKeys(row);

        return new CustomerPurchaseOrderDocumentLineView(
            ordinal,
            ordinal.ToString(),
            description,
            quantity.Value,
            Trimmed(row.Quantity),
            Trimmed(row.UnitOfMeasure) ?? quantity.UnitToken,
            unitPrice.Value,
            Trimmed(row.UnitPrice),
            quantity.Value.HasValue && unitPrice.Value.HasValue
                ? decimal.Round(quantity.Value.Value * unitPrice.Value.Value, 2, MidpointRounding.AwayFromZero)
                : null,
            itemCode,
            Trimmed(row.ManufacturerName),
            partNumber,
            row.SourceAddress(RfqSpreadsheetFields.ProductName, "row"),
            reasons);
    }

    private static string QuantityReason(string field, QuantityReading reading) => reading.Origin switch
    {
        QuantityOrigin.Absent => $"The document states no {field} for this line.",
        QuantityOrigin.Ambiguous => $"The {field} \"{reading.SourceText}\" is ambiguous — \".\" could be a decimal "
            + "point or a thousands separator, and the two readings differ a thousandfold. Confirm it.",
        _ => $"The {field} \"{reading.SourceText}\" could not be read as a number. Confirm it.",
    };

    /// <summary>
    /// The shared column mapper folds "Item Code", "Material Code" and "Part No." into one field,
    /// because for an RFQ they are all just "the code the buyer typed". For a purchase order they
    /// are not interchangeable: matching a PO line back to its quotation is specified on the
    /// buyer's item code AND the manufacturer part number, and recording one under the other's
    /// name makes the match wrong rather than merely absent.
    ///
    /// <para>The decision is made from the header the mapper actually matched — which the row
    /// publishes — not from the value, and defaults to the manufacturer part number when the
    /// header is unknown (a Word table, an OCR pass) rather than inventing a buyer code.</para>
    /// </summary>
    private static (string? ItemCode, string? PartNumber) SplitIdentityKeys(RfqSpreadsheetRow row)
    {
        var value = Trimmed(row.ManufacturerPartNumber);
        if (value is null) return (null, null);

        if (!row.FieldColumnNumbers.TryGetValue(RfqSpreadsheetFields.ManufacturerPartNumber, out var column)
            || !row.HeadersByColumn.TryGetValue(column, out var header))
            return (null, value);

        var normalized = NormalizeLabel(header);
        return BuyerItemCodeHeaderMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal))
            ? (value, null)
            : (null, value);
    }

    // ---- document-level labels -------------------------------------------

    /// <summary>
    /// Finds a document-level value stated either as "Label: value" on one line, or as a heading
    /// cell with its value in the next cell along or in the cell directly below — which are the
    /// three ways a purchase order actually states its own number and date.
    /// </summary>
    private static string? FindLabelledValue(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0)
            .Take(HeaderScanLineLimit)
            .ToList();

        var aliases = HeaderLabels[field].OrderByDescending(alias => alias.Length).ToArray();
        var cellsByLine = lines.Select(line => line.Split('\t').Select(cell => cell.Trim()).ToArray()).ToList();

        for (var lineIndex = 0; lineIndex < cellsByLine.Count; lineIndex++)
        {
            var cells = cellsByLine[lineIndex];
            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                var cell = cells[cellIndex];
                if (cell.Length == 0) continue;

                foreach (var alias in aliases)
                {
                    // "Label: value" inside the cell.
                    var colon = LabelWithColon(cell, alias);
                    if (colon >= 0)
                    {
                        var inline = TrimValue(cell[colon..]);
                        if (inline.Length > 0) return inline;

                        var following = NextNonEmpty(cells, cellIndex);
                        if (following is not null) return following;

                        var below = Below(cellsByLine, lineIndex, cellIndex);
                        if (below is not null) return below;
                        continue;
                    }

                    // A bare heading cell, value alongside or beneath it.
                    if (ColonOnlyLabels.Contains(alias)) continue;
                    if (!string.Equals(NormalizeLabel(cell), alias, StringComparison.Ordinal)) continue;

                    var alongside = NextNonEmpty(cells, cellIndex);
                    if (alongside is not null) return alongside;
                    var beneath = Below(cellsByLine, lineIndex, cellIndex);
                    if (beneath is not null) return beneath;
                }
            }
        }

        return null;
    }

    private static string? NextNonEmpty(string[] cells, int from)
    {
        for (var index = from + 1; index < cells.Length; index++)
        {
            var value = TrimValue(cells[index]);
            if (value.Length > 0 && !IsAnotherLabel(value)) return value;
        }
        return null;
    }

    private static string? Below(List<string[]> cellsByLine, int lineIndex, int cellIndex)
    {
        if (lineIndex + 1 >= cellsByLine.Count) return null;
        var next = cellsByLine[lineIndex + 1];
        if (cellIndex >= next.Length) return null;
        var value = TrimValue(next[cellIndex]);
        return value.Length > 0 && !IsAnotherLabel(value) ? value : null;
    }

    /// <summary>
    /// A cell that is itself a label is never the value of the previous one. Without this, a
    /// document whose title sits above its identity block reads "PO Number" as its PO number.
    /// </summary>
    private static bool IsAnotherLabel(string value)
    {
        var normalized = NormalizeLabel(value);
        return normalized.Length > 0
            && HeaderLabels.Values.Any(aliases => aliases.Contains(normalized, StringComparer.Ordinal));
    }

    private static string TrimValue(string value)
    {
        var trimmed = value.Trim().TrimStart(':').Trim();
        // Buyers routinely run several labelled values into one cell; stop at the next label.
        var stop = trimmed.Length;
        foreach (var aliases in HeaderLabels.Values)
        {
            foreach (var alias in aliases)
            {
                var at = LabelStart(trimmed, alias);
                if (at > 0 && at < stop) stop = at;
            }
        }
        return trimmed[..stop].Trim().Trim('-', '–').Trim();
    }

    /// <summary>Index just past "alias:" in <paramref name="text"/>, or -1.</summary>
    private static int LabelWithColon(string text, string alias)
    {
        var start = LabelStart(text, alias);
        if (start < 0) return -1;
        var colon = text.IndexOf(':', start);
        return colon < 0 ? -1 : colon + 1;
    }

    /// <summary>
    /// Locates a label FOLLOWED BY A COLON, ignoring case, spacing and punctuation inside the
    /// label itself, so "P.O. No.:", "PO_NUMBER :" and "Purchase Order No:" all match the same
    /// alias. Returns the index the label starts at, or -1.
    /// </summary>
    private static int LabelStart(string text, string alias)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue;

            var matched = 0;
            var j = i;
            while (j < text.Length && matched < alias.Length)
            {
                var c = text[j];
                if (char.IsLetterOrDigit(c))
                {
                    if (char.ToLowerInvariant(c) != alias[matched]) break;
                    matched++;
                }
                else if (c is not (' ' or '_' or '.' or '-' or ' ' or '/'))
                {
                    break;
                }
                j++;
            }

            if (matched != alias.Length) continue;
            if (j < text.Length && char.IsLetterOrDigit(text[j])) continue;

            var k = j;
            while (k < text.Length && (text[k] is ' ' or ' ' or '.' or '#')) k++;
            if (k < text.Length && text[k] == ':') return i;
        }

        return -1;
    }

    private static string NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

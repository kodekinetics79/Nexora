using System.Globalization;
using System.Text;
using ExcelDataReader;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

public sealed class NativeSpreadsheetParser
{
    private const string CsvWorksheetName = "CSV";

    static NativeSpreadsheetParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<RfqSpreadsheetRow> ParseXlsx(byte[] bytes, string sourceDocumentName)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var stream = new MemoryStream(bytes, writable: false);
        using var package = new ExcelPackage(stream);
        var rows = new List<RfqSpreadsheetRow>();

        foreach (var worksheet in package.Workbook.Worksheets.Where(sheet => sheet.Dimension != null))
        {
            var dimension = worksheet.Dimension!;
            var located = LocateHeader(
                dimension.Start.Row,
                dimension.End.Row,
                row => ReadHeaders(
                    dimension.Start.Column,
                    dimension.End.Column,
                    column => worksheet.Cells[row, column].Text));
            var headerRow = located.Row;
            var headers = located.Headers;
            var fieldColumns = located.FieldColumns;

            for (var rowNumber = headerRow + 1; rowNumber <= dimension.End.Row; rowNumber++)
            {
                string? Cell(string field) => ReadCell(
                    fieldColumns,
                    field,
                    column => worksheet.Cells[rowNumber, column].Text);

                var row = CreateRow(
                    sourceDocumentName,
                    worksheet.Name,
                    headerRow,
                    rowNumber,
                    headers,
                    fieldColumns,
                    field => Cell(field));

                if (IsMaterial(row))
                    rows.Add(row);
            }
        }

        return rows;
    }

    public IReadOnlyList<RfqSpreadsheetRow> ParseCsv(byte[] bytes, string sourceDocumentName)
    {
        var records = ParseCsvRecords(DecodeUtf8(bytes));
        if (records.Count <= 1)
            return Array.Empty<RfqSpreadsheetRow>();

        var located = LocateHeader(1, records.Count, row => ReadHeaders(
            1, records[row - 1].Values.Count, column => records[row - 1].Values[column - 1]));
        var headerRecord = records[located.Row - 1];
        var headers = located.Headers;
        var fieldColumns = located.FieldColumns;
        var rows = new List<RfqSpreadsheetRow>();

        foreach (var record in records.Skip(located.Row))
        {
            string? Cell(string field) => ReadCell(
                fieldColumns,
                field,
                column => column <= record.Values.Count ? record.Values[column - 1] : null);

            var row = CreateRow(
                sourceDocumentName,
                CsvWorksheetName,
                headerRecord.StartLine,
                record.StartLine,
                headers,
                fieldColumns,
                field => Cell(field));

            if (IsMaterial(row))
                rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Renders every worksheet of an OOXML workbook as plain text — a bracketed sheet-name
    /// line followed by each non-empty row with its cells tab-joined (the same shape the
    /// DOCX reader produces for tables). Used as the unstructured fallback when
    /// <see cref="ParseXlsx"/> recognizes none of the RFQ column headers.
    /// </summary>
    public string RenderXlsxText(byte[] bytes)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var stream = new MemoryStream(bytes, writable: false);
        using var package = new ExcelPackage(stream);
        var text = new StringBuilder();

        foreach (var worksheet in package.Workbook.Worksheets.Where(sheet => sheet.Dimension != null))
        {
            var dimension = worksheet.Dimension!;
            var rows = new List<string>();
            for (var rowNumber = dimension.Start.Row; rowNumber <= dimension.End.Row; rowNumber++)
            {
                var cells = new List<string?>();
                for (var column = dimension.Start.Column; column <= dimension.End.Column; column++)
                    cells.Add(worksheet.Cells[rowNumber, column].Text);
                AppendRenderedRow(rows, cells);
            }
            AppendRenderedSheet(text, worksheet.Name, rows);
        }

        return text.ToString();
    }

    /// <summary>
    /// Renders every worksheet of a legacy BIFF workbook as plain text (sheet name +
    /// tab-joined rows). Used as the unstructured fallback when <see cref="ParseXls"/>
    /// recognizes none of the RFQ column headers.
    /// </summary>
    public string RenderXlsText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream, new ExcelReaderConfiguration
        {
            FallbackEncoding = Encoding.GetEncoding(1252),
            LeaveOpen = false
        });
        var text = new StringBuilder();

        do
        {
            var rows = new List<string>();
            while (reader.Read())
            {
                var cells = new List<string?>();
                for (var column = 0; column < reader.FieldCount; column++)
                    cells.Add(CellText(reader.GetValue(column)));
                AppendRenderedRow(rows, cells);
            }
            AppendRenderedSheet(text, string.IsNullOrWhiteSpace(reader.Name) ? "Worksheet" : reader.Name, rows);
        } while (reader.NextResult());

        return text.ToString();
    }

    /// <summary>
    /// Renders a CSV document as plain text (constant sheet name + tab-joined records).
    /// Used as the unstructured fallback when <see cref="ParseCsv"/> recognizes none of
    /// the RFQ column headers.
    /// </summary>
    public string RenderCsvText(byte[] bytes)
    {
        var text = new StringBuilder();
        var rows = new List<string>();
        foreach (var record in ParseCsvRecords(DecodeUtf8(bytes)))
            AppendRenderedRow(rows, record.Values.ToList());
        AppendRenderedSheet(text, CsvWorksheetName, rows);
        return text.ToString();
    }

    private static void AppendRenderedRow(List<string> rows, List<string?> cells)
    {
        // Trim trailing empty cells; keep interior blanks so column alignment survives.
        while (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]))
            cells.RemoveAt(cells.Count - 1);
        if (cells.Count == 0)
            return;
        rows.Add(string.Join('\t', cells.Select(cell => (cell ?? string.Empty).Trim())));
    }

    private static void AppendRenderedSheet(StringBuilder text, string worksheetName, List<string> rows)
    {
        if (rows.Count == 0)
            return;
        text.Append("[Worksheet: ").Append(worksheetName).Append("]\n");
        foreach (var row in rows)
            text.Append(row).Append('\n');
    }

    public IReadOnlyList<RfqSpreadsheetRow> ParseXls(byte[] bytes, string sourceDocumentName)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream, new ExcelReaderConfiguration
        {
            FallbackEncoding = Encoding.GetEncoding(1252),
            LeaveOpen = false
        });
        var rows = new List<RfqSpreadsheetRow>();

        do
        {
            // This reader is forward-only, so the header scan needs the top of the sheet in
            // hand. Only the scan window is buffered; the rest of the sheet still streams.
            var buffered = new List<string?[]>();
            while (buffered.Count < HeaderScanWindow && reader.Read())
            {
                if (reader.FieldCount == 0)
                    continue;
                var values = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = CellText(reader.GetValue(i));
                buffered.Add(values);
            }

            if (buffered.Count == 0)
                continue;

            var located = LocateHeader(1, buffered.Count, row => ReadHeaders(
                1, buffered[row - 1].Length, column => buffered[row - 1][column - 1]));
            var headerRow = located.Row;
            var headers = located.Headers;
            var fieldColumns = located.FieldColumns;
            var worksheetName = string.IsNullOrWhiteSpace(reader.Name) ? "Worksheet" : reader.Name;
            var rowNumber = headerRow;

            void Emit(Func<int, string?> valueAt)
            {
                string? Cell(string field) => ReadCell(fieldColumns, field, valueAt);
                var row = CreateRow(sourceDocumentName, worksheetName, headerRow, rowNumber,
                    headers, fieldColumns, Cell);
                if (IsMaterial(row))
                    rows.Add(row);
            }

            for (var i = headerRow; i < buffered.Count; i++)
            {
                var values = buffered[i];
                rowNumber++;
                Emit(column => column <= values.Length ? values[column - 1] : null);
            }

            while (reader.Read())
            {
                rowNumber++;
                Emit(column => column <= reader.FieldCount ? CellText(reader.GetValue(column - 1)) : null);
            }
        } while (reader.NextResult());

        return rows;
    }

    private static string? CellText(object? value) => value switch
    {
        null => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static RfqSpreadsheetRow CreateRow(
        string sourceDocumentName,
        string worksheetName,
        int headerRowNumber,
        int rowNumber,
        Dictionary<int, string> headers,
        Dictionary<string, int> fieldColumns,
        Func<string, string?> cell)
    {
        var addresses = fieldColumns.ToDictionary(
            pair => pair.Key,
            pair => QualifyAddress(worksheetName, pair.Value, rowNumber),
            StringComparer.Ordinal);

        return new RfqSpreadsheetRow
        {
            RowNumber = rowNumber,
            SourceDocumentName = sourceDocumentName,
            WorksheetName = worksheetName,
            HeaderRowNumber = headerRowNumber,
            HeadersByColumn = new Dictionary<int, string>(headers),
            FieldColumnNumbers = new Dictionary<string, int>(fieldColumns, StringComparer.Ordinal),
            FieldSourceAddresses = addresses,
            RfqNo = cell(RfqSpreadsheetFields.RfqNo),
            BuyerName = cell(RfqSpreadsheetFields.BuyerName),
            ReceivedDate = cell(RfqSpreadsheetFields.ReceivedDate),
            BidClosingDate = cell(RfqSpreadsheetFields.BidClosingDate),
            ProductName = cell(RfqSpreadsheetFields.ProductName),
            Quantity = cell(RfqSpreadsheetFields.Quantity),
            UnitOfMeasure = cell(RfqSpreadsheetFields.UnitOfMeasure),
            UnitPrice = cell(RfqSpreadsheetFields.UnitPrice),
            Currency = cell(RfqSpreadsheetFields.Currency),
            ManufacturerName = cell(RfqSpreadsheetFields.ManufacturerName),
            ManufacturerPartNumber = cell(RfqSpreadsheetFields.ManufacturerPartNumber),
            LeadTimeDays = cell(RfqSpreadsheetFields.LeadTimeDays),
            ItemText = cell(RfqSpreadsheetFields.ItemText),
            DeliveryLocation = cell(RfqSpreadsheetFields.DeliveryLocation),
            RequiredDeliveryDate = cell(RfqSpreadsheetFields.RequiredDeliveryDate),
            AgreementReference = cell(RfqSpreadsheetFields.AgreementReference)
        };
    }

    /// <summary>
    /// Maps an already-extracted grid of cells onto RFQ rows, using the same header location and
    /// column-alias rules as every spreadsheet format.
    ///
    /// <para>Exists so a Word table can reach the deterministic path instead of being flattened
    /// to prose and handed to a model. A tabular RFQ is a tabular RFQ whether it arrived as a
    /// workbook or as a .docx, and the column spellings buyers use are the same either way.</para>
    /// </summary>
    /// <param name="grid">Row-major cells. Ragged rows are tolerated.</param>
    public IReadOnlyList<RfqSpreadsheetRow> ParseGrid(
        IReadOnlyList<IReadOnlyList<string?>> grid, string sourceDocumentName, string worksheetName)
    {
        if (grid.Count < 2)
            return Array.Empty<RfqSpreadsheetRow>();

        var width = grid.Max(r => r.Count);
        var located = LocateHeader(1, grid.Count, row => ReadHeaders(
            1, width, column => column <= grid[row - 1].Count ? grid[row - 1][column - 1] : null));

        var rows = new List<RfqSpreadsheetRow>();
        for (var index = located.Row; index < grid.Count; index++)
        {
            var cells = grid[index];
            var rowNumber = index + 1;
            string? Cell(string field) => ReadCell(located.FieldColumns, field,
                column => column <= cells.Count ? cells[column - 1] : null);

            var row = CreateRow(sourceDocumentName, worksheetName, located.Row, rowNumber,
                located.Headers, located.FieldColumns, Cell);
            if (IsMaterial(row))
                rows.Add(row);
        }

        return rows;
    }

    /// <summary>Rows examined from the top of a sheet when looking for the header.</summary>
    private const int HeaderScanWindow = 25;

    /// <summary>
    /// Recognised columns a row needs before it is believed to be the header. Two keeps a stray
    /// title cell ("Quantity Summary") from being mistaken for a real header row.
    /// </summary>
    private const int MinimumHeaderFieldMatches = 2;

    /// <summary>
    /// Finds the header row.
    ///
    /// <para>The header used to be assumed to be the first row of the used range. Customer
    /// workbooks routinely open with a logo, a title block, a reference block or a covering note
    /// above the table, and for every one of those the assumption mapped zero columns — which
    /// made <c>IsMaterial</c> false for every subsequent row and discarded the entire RFQ with no
    /// diagnostic anywhere. Scanning a bounded window and taking the row that recognises the most
    /// fields turns that whole class of document from "silently empty" into "read correctly".</para>
    ///
    /// <para>When nothing in the window looks like a header the first row is returned, which is
    /// the previous behaviour: the caller maps no columns and the document falls through to the
    /// unstructured text path exactly as it did before.</para>
    /// </summary>
    private static (int Row, Dictionary<int, string> Headers, Dictionary<string, int> FieldColumns) LocateHeader(
        int firstRow, int lastRow, Func<int, Dictionary<int, string>> readHeadersAt)
    {
        var bestRow = firstRow;
        var bestMatches = -1;
        Dictionary<int, string>? bestHeaders = null;
        Dictionary<string, int>? bestFieldColumns = null;

        var limit = Math.Min(lastRow, firstRow + HeaderScanWindow - 1);
        for (var row = firstRow; row <= limit; row++)
        {
            var headers = readHeadersAt(row);
            var fieldColumns = BuildFieldColumnMap(headers);
            if (fieldColumns.Count <= bestMatches)
                continue;

            bestRow = row;
            bestMatches = fieldColumns.Count;
            bestHeaders = headers;
            bestFieldColumns = fieldColumns;
        }

        if (bestMatches >= MinimumHeaderFieldMatches && bestHeaders is not null && bestFieldColumns is not null)
            return (bestRow, bestHeaders, bestFieldColumns);

        var fallback = readHeadersAt(firstRow);
        return (firstRow, fallback, BuildFieldColumnMap(fallback));
    }

    private static Dictionary<int, string> ReadHeaders(int firstColumn, int lastColumn, Func<int, string?> value)
    {
        var headers = new Dictionary<int, string>();
        for (var column = firstColumn; column <= lastColumn; column++)
            headers[column] = (value(column) ?? string.Empty).Trim();
        return headers;
    }

    /// <summary>
    /// Header spellings are compared with punctuation and spacing removed, so "Qty.", "QTY",
    /// "Unit of Measure", "U/M" and "Part No." all land on the field a buyer meant. Matching is
    /// still exact after normalisation — substring matching would let a "Total Price" column
    /// capture "Price".
    /// </summary>
    private static readonly Dictionary<string, string[]> FieldAliases = new(StringComparer.Ordinal)
    {
        [RfqSpreadsheetFields.RfqNo] = new[] { "rfqno", "rfq", "rfqnumber", "rfqref", "rfqreference", "enquiryno", "inquiryno", "tenderno", "bidno" },
        [RfqSpreadsheetFields.BuyerName] = new[] { "buyername", "buyer", "customer", "customername", "client", "clientname" },
        [RfqSpreadsheetFields.ReceivedDate] = new[] { "receiveddate", "datereceived", "rfqdate", "enquirydate" },
        [RfqSpreadsheetFields.BidClosingDate] = new[] { "bidclosingdate", "closingdate", "duedate", "deadline", "submissiondate", "bidduedate" },
        // "item" is deliberately absent — it is ambiguous and resolved below.
        [RfqSpreadsheetFields.ProductName] = new[] { "productname", "product", "description", "itemdescription", "materialdescription", "materialname", "particulars" },
        [RfqSpreadsheetFields.Quantity] = new[] { "quantity", "qty", "qtyrequired", "quantityrequired", "reqqty", "requiredqty" },
        [RfqSpreadsheetFields.UnitOfMeasure] = new[] { "unitofmeasure", "uom", "unit", "um", "units", "measure", "unitofmeasurement", "unitmeasure" },
        [RfqSpreadsheetFields.UnitPrice] = new[] { "unitprice", "price", "rate", "unitrate" },
        [RfqSpreadsheetFields.Currency] = new[] { "currency", "curr", "ccy" },
        [RfqSpreadsheetFields.ManufacturerName] = new[] { "manufacturername", "manufacturer", "make", "brand", "mfr", "mfg" },
        [RfqSpreadsheetFields.ManufacturerPartNumber] = new[] { "manufacturerpartnumber", "mpn", "partnumber", "partno", "partcode", "modelno", "modelnumber", "materialcode", "itemcode" },
        [RfqSpreadsheetFields.LeadTimeDays] = new[] { "leadtimedays", "leadtime", "delivery", "deliverytime", "deliveryperiod" },
        [RfqSpreadsheetFields.ItemText] = new[] { "notes", "note", "remarks", "remark", "comments", "comment", "itemtext", "specification", "spec" },
        [RfqSpreadsheetFields.DeliveryLocation] = new[] { "deliverylocation", "deliveryto", "shipto", "destination", "deliveryaddress", "deliverypoint", "site", "plant" },
        [RfqSpreadsheetFields.RequiredDeliveryDate] = new[] { "requireddeliverydate", "requesteddeliverydate", "deliverydate", "requiredby", "neededby", "wanteddate" },
        [RfqSpreadsheetFields.AgreementReference] = new[] { "agreementreference", "agreementno", "contractno", "contractreference", "framecontract", "agreement" },
    };

    private static Dictionary<string, int> BuildFieldColumnMap(IReadOnlyDictionary<int, string> headers)
    {
        var normalizedHeaders = headers.ToDictionary(
            pair => pair.Key,
            pair => NormalizeHeader(pair.Value));
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var field in FieldAliases)
        {
            // Lowest column wins when a sheet repeats a spelling, so the mapping is stable
            // rather than dependent on dictionary enumeration order.
            var match = normalizedHeaders
                .Where(pair => pair.Value.Length > 0 && field.Value.Contains(pair.Value, StringComparer.Ordinal))
                .OrderBy(pair => pair.Key)
                .Select(pair => (int?)pair.Key)
                .FirstOrDefault();
            if (match is { } column)
                result[field.Key] = column;
        }

        ResolveAmbiguousItemColumn(normalizedHeaders, result);
        return result;
    }

    /// <summary>
    /// A column headed exactly "Item" means one of two different things, and which one depends on
    /// the rest of the table. Alongside a description column — "Item | Description | Qty" — it is
    /// the buyer's item CODE. On its own — "Item | Qty" — it is the description. Guessing either
    /// way unconditionally mis-reads half of all RFQ tables, so the decision is made from the
    /// columns actually present.
    /// </summary>
    private static void ResolveAmbiguousItemColumn(
        IReadOnlyDictionary<int, string> normalizedHeaders, Dictionary<string, int> result)
    {
        var itemColumn = normalizedHeaders
            .Where(pair => pair.Value is "item" or "items")
            .OrderBy(pair => pair.Key)
            .Select(pair => (int?)pair.Key)
            .FirstOrDefault();

        if (itemColumn is not { } column || result.ContainsValue(column))
            return;

        if (!result.ContainsKey(RfqSpreadsheetFields.ProductName))
            result[RfqSpreadsheetFields.ProductName] = column;
        else if (!result.ContainsKey(RfqSpreadsheetFields.ManufacturerPartNumber))
            result[RfqSpreadsheetFields.ManufacturerPartNumber] = column;
    }

    /// <summary>Lowercase and strip everything that is not a letter or digit.</summary>
    private static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;
        var sb = new StringBuilder(header.Length);
        foreach (var c in header)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string? ReadCell(
        IReadOnlyDictionary<string, int> fieldColumns,
        string field,
        Func<int, string?> value)
    {
        if (!fieldColumns.TryGetValue(field, out var column))
            return null;
        var raw = value(column);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static bool IsMaterial(RfqSpreadsheetRow row)
        => new[] { row.RfqNo, row.BuyerName, row.ProductName, row.Quantity, row.UnitOfMeasure,
                row.UnitPrice, row.Currency, row.ManufacturerName, row.ManufacturerPartNumber,
                row.LeadTimeDays }
            .Any(value => !string.IsNullOrWhiteSpace(value));

    private static string QualifyAddress(string worksheetName, int column, int row)
        => $"'{worksheetName.Replace("'", "''", StringComparison.Ordinal)}'!{ColumnName(column)}{row}";

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static List<CsvRecord> ParseCsvRecords(string text)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordStartLine = 1;
        var physicalLine = 1;

        void FinishField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void FinishRecord()
        {
            FinishField();
            if (fields.Any(value => !string.IsNullOrWhiteSpace(value)))
                records.Add(new CsvRecord(recordStartLine, fields.ToArray()));
            fields.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(current);
                    if (current == '\n')
                        physicalLine++;
                    else if (current == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'))
                        physicalLine++;
                }
                continue;
            }

            if (current == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (current == ',')
            {
                FinishField();
            }
            else if (current == '\r' || current == '\n')
            {
                FinishRecord();
                if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                physicalLine++;
                recordStartLine = physicalLine;
            }
            else
            {
                field.Append(current);
            }
        }

        if (inQuotes)
            throw new FormatException("CSV contains an unterminated quoted field.");
        if (field.Length > 0 || fields.Count > 0)
            FinishRecord();

        return records;
    }

    private sealed record CsvRecord(int StartLine, IReadOnlyList<string> Values);
}

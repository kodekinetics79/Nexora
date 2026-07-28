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
            var headerRow = dimension.Start.Row;
            var headers = ReadHeaders(
                dimension.Start.Column,
                dimension.End.Column,
                column => worksheet.Cells[headerRow, column].Text);
            var fieldColumns = BuildFieldColumnMap(headers);

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

        var headerRecord = records[0];
        var headers = ReadHeaders(1, headerRecord.Values.Count, column => headerRecord.Values[column - 1]);
        var fieldColumns = BuildFieldColumnMap(headers);
        var rows = new List<RfqSpreadsheetRow>();

        foreach (var record in records.Skip(1))
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
            if (!reader.Read() || reader.FieldCount == 0)
                continue;

            const int headerRow = 1;
            var headers = ReadHeaders(1, reader.FieldCount, column => CellText(reader.GetValue(column - 1)));
            var fieldColumns = BuildFieldColumnMap(headers);
            var rowNumber = headerRow;

            while (reader.Read())
            {
                rowNumber++;
                string? Cell(string field) => ReadCell(
                    fieldColumns,
                    field,
                    column => column <= reader.FieldCount ? CellText(reader.GetValue(column - 1)) : null);

                var row = CreateRow(
                    sourceDocumentName,
                    string.IsNullOrWhiteSpace(reader.Name) ? "Worksheet" : reader.Name,
                    headerRow,
                    rowNumber,
                    headers,
                    fieldColumns,
                    Cell);

                if (IsMaterial(row))
                    rows.Add(row);
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
            UnitPrice = cell(RfqSpreadsheetFields.UnitPrice),
            Currency = cell(RfqSpreadsheetFields.Currency),
            ManufacturerName = cell(RfqSpreadsheetFields.ManufacturerName),
            ManufacturerPartNumber = cell(RfqSpreadsheetFields.ManufacturerPartNumber),
            LeadTimeDays = cell(RfqSpreadsheetFields.LeadTimeDays)
        };
    }

    private static Dictionary<int, string> ReadHeaders(int firstColumn, int lastColumn, Func<int, string?> value)
    {
        var headers = new Dictionary<int, string>();
        for (var column = firstColumn; column <= lastColumn; column++)
            headers[column] = (value(column) ?? string.Empty).Trim();
        return headers;
    }

    private static Dictionary<string, int> BuildFieldColumnMap(IReadOnlyDictionary<int, string> headers)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [RfqSpreadsheetFields.RfqNo] = new[] { "rfqno", "rfq no", "rfq" },
            [RfqSpreadsheetFields.BuyerName] = new[] { "buyername", "buyer name", "buyer" },
            [RfqSpreadsheetFields.ReceivedDate] = new[] { "receiveddate", "received date" },
            [RfqSpreadsheetFields.BidClosingDate] = new[] { "bidclosingdate", "bid closing date" },
            [RfqSpreadsheetFields.ProductName] = new[] { "productname", "product name", "product", "description" },
            [RfqSpreadsheetFields.Quantity] = new[] { "quantity", "qty" },
            [RfqSpreadsheetFields.UnitPrice] = new[] { "unitprice", "unit price", "price" },
            [RfqSpreadsheetFields.Currency] = new[] { "currency" },
            [RfqSpreadsheetFields.ManufacturerName] = new[] { "manufacturername", "manufacturer" },
            [RfqSpreadsheetFields.ManufacturerPartNumber] = new[] { "manufacturerpartnumber", "mpn", "part number" },
            [RfqSpreadsheetFields.LeadTimeDays] = new[] { "leadtimedays", "lead time", "leadtime" }
        };

        var normalizedHeaders = headers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Trim().ToLowerInvariant());
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var field in aliases)
        {
            var match = normalizedHeaders.FirstOrDefault(pair => field.Value.Contains(pair.Value, StringComparer.Ordinal));
            if (match.Key > 0)
                result[field.Key] = match.Key;
        }

        return result;
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
        => new[] { row.RfqNo, row.BuyerName, row.ProductName, row.Quantity, row.UnitPrice,
                row.Currency, row.ManufacturerName, row.ManufacturerPartNumber, row.LeadTimeDays }
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

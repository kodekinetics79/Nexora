using System.Globalization;
using System.Text;
using ExcelDataReader;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>One row of a source spreadsheet, numbered as the spreadsheet numbers it.</summary>
public sealed record SourceGridRow(int Number, IReadOnlyList<string> Cells);

/// <summary>One sheet: its rows, and the row the parser read the column headings from.</summary>
public sealed record SourceGridSheet(string Name, int? HeaderRowNumber, IReadOnlyList<SourceGridRow> Rows, bool Truncated);

/// <summary>The cells of a retained spreadsheet source, as the parser read them.</summary>
public sealed record SourceGrid(IReadOnlyList<SourceGridSheet> Sheets);

/// <summary>
/// Reads a retained spreadsheet (.xlsx, .xls, .csv) back into plain rows so the decision screen
/// can show the document beside the lines it produced.
///
/// <para>A browser can frame a PDF but cannot draw a workbook, so the "check against the
/// document" pane was empty for the most common shape an RFQ arrives in — the rep confirmed six
/// lines against nothing. The parser already has every cell; this reads the same bytes with the
/// same libraries and hands them over as text. Row numbers are the spreadsheet's own, so the
/// <c>'Sheet1'!B12</c> a line's evidence names is row 12 here. The header row is the one the
/// parser located, so the table's headings are the columns the lines were read from.</para>
/// </summary>
public static class SpreadsheetGridReader
{
    /// <summary>Rows per sheet handed to the browser; a 1,500-line bid list fits, a ledger does not.</summary>
    public const int MaxRows = 2000;

    /// <summary>Columns per row handed to the browser.</summary>
    public const int MaxColumns = 64;

    static SpreadsheetGridReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private enum Kind { None, Xlsx, Xls, Csv }

    public static bool IsSpreadsheet(string? fileName, string? mimeType) => KindOf(fileName, mimeType) != Kind.None;

    /// <summary>The grid, or null when the file is not a spreadsheet this reader knows.</summary>
    public static SourceGrid? Read(byte[] bytes, string? fileName, string? mimeType)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "document" : fileName;
        switch (KindOf(fileName, mimeType))
        {
            case Kind.Xlsx: return ReadXlsx(bytes, name);
            case Kind.Xls: return ReadXls(bytes, name);
            case Kind.Csv: return ReadCsv(bytes, name);
            default: return null;
        }
    }

    private static Kind KindOf(string? fileName, string? mimeType)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        var type = (mimeType ?? string.Empty).ToLowerInvariant();
        if (extension is "xlsx" or "xlsm" || type.Contains("spreadsheetml")) return Kind.Xlsx;
        if (extension is "xls" || type.Contains("ms-excel")) return Kind.Xls;
        if (extension is "csv" || type.EndsWith("/csv", StringComparison.Ordinal)) return Kind.Csv;
        return Kind.None;
    }

    private static SourceGrid ReadXlsx(byte[] bytes, string name)
    {
        var headerRows = HeaderRows(() => new NativeSpreadsheetParser().ParseXlsx(bytes, name));
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var stream = new MemoryStream(bytes, writable: false);
        using var package = new ExcelPackage(stream);
        var sheets = new List<SourceGridSheet>();
        foreach (var worksheet in package.Workbook.Worksheets.Where(sheet => sheet.Dimension != null))
        {
            var dimension = worksheet.Dimension!;
            var lastColumn = Math.Min(dimension.End.Column, dimension.Start.Column + MaxColumns - 1);
            var rows = new List<SourceGridRow>();
            var truncated = dimension.End.Column > lastColumn;
            for (var rowNumber = dimension.Start.Row; rowNumber <= dimension.End.Row; rowNumber++)
            {
                if (rows.Count >= MaxRows) { truncated = true; break; }
                var cells = new List<string>();
                for (var column = dimension.Start.Column; column <= lastColumn; column++)
                    cells.Add(worksheet.Cells[rowNumber, column].Text ?? string.Empty);
                Add(rows, rowNumber, cells);
            }
            sheets.Add(new SourceGridSheet(worksheet.Name, headerRows.GetValueOrDefault(worksheet.Name), rows, truncated));
        }
        return new SourceGrid(sheets);
    }

    private static SourceGrid ReadXls(byte[] bytes, string name)
    {
        var headerRows = HeaderRows(() => new NativeSpreadsheetParser().ParseXls(bytes, name));
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream, new ExcelReaderConfiguration
        {
            FallbackEncoding = Encoding.GetEncoding(1252),
            LeaveOpen = false
        });
        var sheets = new List<SourceGridSheet>();
        do
        {
            var sheetName = string.IsNullOrWhiteSpace(reader.Name) ? "Worksheet" : reader.Name;
            var rows = new List<SourceGridRow>();
            var truncated = false;
            var rowNumber = 0;
            while (reader.Read())
            {
                rowNumber++;
                if (rows.Count >= MaxRows) { truncated = true; break; }
                var width = Math.Min(reader.FieldCount, MaxColumns);
                truncated |= reader.FieldCount > width;
                var cells = new List<string>(width);
                for (var i = 0; i < width; i++) cells.Add(CellText(reader.GetValue(i)));
                Add(rows, rowNumber, cells);
            }
            if (rows.Count > 0)
                sheets.Add(new SourceGridSheet(sheetName, headerRows.GetValueOrDefault(sheetName), rows, truncated));
        } while (reader.NextResult());
        return new SourceGrid(sheets);
    }

    private static SourceGrid ReadCsv(byte[] bytes, string name)
    {
        var headerRows = HeaderRows(() => new NativeSpreadsheetParser().ParseCsv(bytes, name));
        var rows = new List<SourceGridRow>();
        var truncated = false;
        foreach (var record in NativeSpreadsheetParser.ParseCsvRecords(NativeSpreadsheetParser.DecodeUtf8(bytes)))
        {
            if (rows.Count >= MaxRows) { truncated = true; break; }
            var cells = record.Values.Take(MaxColumns).ToList();
            truncated |= record.Values.Count > cells.Count;
            Add(rows, record.StartLine, cells);
        }
        var sheetName = NativeSpreadsheetParser.CsvWorksheetName;
        return new SourceGrid([new SourceGridSheet(sheetName, headerRows.GetValueOrDefault(sheetName), rows, truncated)]);
    }

    /// <summary>Rows with nothing in them are left out; the numbering keeps the gaps.</summary>
    private static void Add(List<SourceGridRow> rows, int number, List<string> cells)
    {
        if (cells.All(string.IsNullOrWhiteSpace)) return;
        var last = cells.FindLastIndex(cell => !string.IsNullOrWhiteSpace(cell));
        rows.Add(new SourceGridRow(number, cells.Take(last + 1).Select(cell => cell.Trim()).ToArray()));
    }

    /// <summary>
    /// The header row the parser located on each sheet. Best effort: a sheet the parser could not
    /// read as an RFQ still shows its rows, just without a heading row picked out.
    /// </summary>
    private static Dictionary<string, int?> HeaderRows(Func<IReadOnlyList<RfqSpreadsheetRow>> parse)
    {
        try
        {
            return parse()
                .GroupBy(row => row.WorksheetName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (int?)group.First().HeaderRowNumber, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, int?>(StringComparer.Ordinal);
        }
    }

    private static string CellText(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}

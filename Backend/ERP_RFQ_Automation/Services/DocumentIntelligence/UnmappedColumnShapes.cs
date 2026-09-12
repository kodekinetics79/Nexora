using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Reads a column the vocabulary did not recognise by what its VALUES look like, with the
/// heading as a hint rather than a key.
///
/// <para><b>Why.</b> A spelling list only knows what it has seen, and every customer heads the
/// same column differently: "Bid #", "RFQ Ref.", "Inquiry No", "Event ID". What does not vary is
/// the shape. A bid number is the same value on every line of the sheet; an item number is a
/// different small integer on every line, counting up; a part number is a different code on
/// every line; a currency is one code repeated; a maker is one of a handful of names repeated.
/// Those shapes are read here, for the fields the recognised headings left empty, and the
/// heading is only asked whether it points the same way — "bid", "rfq", "part", "make" — so a
/// constant plant code is never mistaken for a request number.</para>
///
/// <para>Every value filled this way carries a provenance note and a confidence below a
/// deterministic read, so the reviewer sees where it came from; the column stays with the
/// line's extra fields only when nothing was read from it.</para>
/// </summary>
public static class UnmappedColumnShapes
{
    public const decimal ShapeConfidence = 0.9m;
    private const int MinimumRows = 3;

    private static readonly Regex RequestHint = new(@"rfq|rfp|bid|tender|enquir|inquir|request|quot|event|ref|\bpr\b|case|docno|document", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PartHint = new(@"part|mpn|model|material|catalog|article|sku|code|item|product|number|no\b|ref|id\b|type", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LineHint = new(@"item|line|sl|sr|serial|pos|seq|no\b|number|#", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MakerHint = new(@"make|manuf|brand|oem|mfr|mfg|maker|vendor", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClosingHint = new(@"due|clos|deadline|submi|bcd|respon|offer|quot|last", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeliveryHint = new(@"deliver|need|requir|want|eta", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IssueHint = new(@"issue|publish|receiv|creat|rfqdate|enquirydate|inquirydate|date$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Apply(IList<RfqSpreadsheetRow> rows)
    {
        foreach (var sheet in rows.GroupBy(r => r.WorksheetName, StringComparer.Ordinal))
            ApplyToSheet(sheet.ToList());
    }

    private static void ApplyToSheet(List<RfqSpreadsheetRow> rows)
    {
        if (rows.Count < MinimumRows) return;
        var headings = rows.SelectMany(r => r.UnmappedColumns.Keys).Distinct(StringComparer.Ordinal).ToList();

        foreach (var heading in headings)
        {
            var values = rows.Select(r => r.UnmappedColumns.TryGetValue(heading, out var v) ? v.Trim() : null).ToList();
            var present = values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
            if (present.Count * 5 < rows.Count * 4) continue; // a column most lines leave blank says nothing about the document
            var distinct = present.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var hint = heading.Trim();

            if (distinct == 1)
            {
                var value = present[0];
                if (Missing(rows, r => r.RfqNo) && RequestHint.IsMatch(hint) && LooksLikeReference(value))
                    Fill(rows, heading, RfqSpreadsheetFields.RfqNo, (r, v) => r.RfqNo = v, "the same value on every line, under a heading that names the request");
                else if (Missing(rows, r => r.Currency) && CurrencyNames.ToIsoCode(value) is not null)
                    Fill(rows, heading, RfqSpreadsheetFields.Currency, (r, v) => r.Currency = v, "the same currency on every line");
                else if (RfqDateParser.Read(value).HasValue)
                {
                    if (Missing(rows, r => r.BidClosingDate) && ClosingHint.IsMatch(hint))
                        Fill(rows, heading, RfqSpreadsheetFields.BidClosingDate, (r, v) => r.BidClosingDate = v, "the same date on every line, under a heading that speaks of closing");
                    else if (Missing(rows, r => r.RequiredDeliveryDate) && DeliveryHint.IsMatch(hint))
                        Fill(rows, heading, RfqSpreadsheetFields.RequiredDeliveryDate, (r, v) => r.RequiredDeliveryDate = v, "the same date on every line, under a heading that speaks of delivery");
                    else if (Missing(rows, r => r.ReceivedDate) && IssueHint.IsMatch(hint))
                        Fill(rows, heading, RfqSpreadsheetFields.ReceivedDate, (r, v) => r.ReceivedDate = v, "the same date on every line, under a heading that speaks of issue");
                }
                continue;
            }

            var unique = distinct * 10 >= present.Count * 9;
            if (unique && Missing(rows, r => r.CustomerLineNumber) && LineHint.IsMatch(hint) && IsCountingUp(values))
            {
                foreach (var row in rows)
                    if (row.UnmappedColumns.Remove(heading, out var v) && !string.IsNullOrWhiteSpace(v)) row.CustomerLineNumber = v.Trim();
                continue;
            }
            if (unique && Missing(rows, r => r.ManufacturerPartNumber) && PartHint.IsMatch(hint) && present.Count(LooksLikeCode) * 10 >= present.Count * 9)
            {
                Fill(rows, heading, RfqSpreadsheetFields.ManufacturerPartNumber, (r, v) => r.ManufacturerPartNumber = v, "a different code on every line, under a heading that names a part");
                continue;
            }
            if (!unique && Missing(rows, r => r.ManufacturerName) && MakerHint.IsMatch(hint)
                && present.All(v => v.Length is >= 2 and <= 60 && v.Any(char.IsLetter)))
                Fill(rows, heading, RfqSpreadsheetFields.ManufacturerName, (r, v) => r.ManufacturerName = v, "a name repeated across lines, under a heading that names the maker");
        }
    }

    private static bool Missing(List<RfqSpreadsheetRow> rows, Func<RfqSpreadsheetRow, string?> field)
        => rows.All(r => string.IsNullOrWhiteSpace(field(r)));

    private static void Fill(List<RfqSpreadsheetRow> rows, string heading, string field, Action<RfqSpreadsheetRow, string> set, string why)
    {
        var note = $"read_from_column_shape: column \"{heading}\" — {why}";
        foreach (var row in rows)
        {
            if (!row.UnmappedColumns.Remove(heading, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            set(row, value.Trim());
            row.FieldProvenance[field] = new RowFieldProvenance(ShapeConfidence, note);
            var column = row.HeadersByColumn.FirstOrDefault(pair => string.Equals(pair.Value?.Trim(), heading, StringComparison.Ordinal)).Key;
            if (column > 0)
            {
                row.FieldColumnNumbers[field] = column;
                row.FieldSourceAddresses[field] = NativeSpreadsheetParser.QualifyAddress(row.WorksheetName, column, row.RowNumber);
            }
        }
    }

    /// <summary>A request number: has a digit, is not a date, and is one token or nearly so.</summary>
    private static bool LooksLikeReference(string value)
        => value.Length is >= 3 and <= 40 && value.Any(char.IsDigit) && value.Count(char.IsWhiteSpace) <= 2 && !RfqDateParser.Read(value).HasValue;

    /// <summary>A part or material code: a digit somewhere, no prose.</summary>
    private static bool LooksLikeCode(string value)
        => value.Length is >= 3 and <= 40 && value.Any(char.IsDigit) && value.Count(char.IsWhiteSpace) <= 1 && !RfqDateParser.Read(value).HasValue
           && !(decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) && value.Contains('.'));

    /// <summary>Integers that increase down the sheet, gaps allowed: 1, 2, 3 or 10, 20, 30.</summary>
    private static bool IsCountingUp(List<string?> values)
    {
        long? previous = null;
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (!long.TryParse(raw.Trim(), out var n) || n <= 0) return false;
            if (previous is { } p && n <= p) return false;
            previous = n;
        }
        return previous is not null;
    }
}

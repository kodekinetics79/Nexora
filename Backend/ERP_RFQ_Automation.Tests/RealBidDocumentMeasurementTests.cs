using System.Text;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A measuring harness, not a regression test: reads the client's REAL bid documents through the
/// same deterministic readers the extraction worker uses (xlsx grid, Word table, HTML-named-.doc
/// grid) and the canonical normaliser, and reports lines / makers / part numbers per file.
///
/// <para>The documents are the customer's and are not committed. The harness runs only when
/// <c>NEXORA_REAL_DOCS</c> names a directory holding them (files absent there are skipped), and
/// writes its table to <c>NEXORA_REAL_DOCS_REPORT</c> when set. No database is touched.</para>
/// </summary>
public sealed class RealBidDocumentMeasurementTests
{
    private static readonly string[] Documents =
    {
        "ARAMCO Enquiry 6031.xlsx",
        "ARAMCO Enquiry 6028.xlsx",
        "Aramco_4203207346_LAMP CADMIUM.xlsx",
        "RFP 6000000003 - Motors and Generators.docx",
        "RFP 6000000003 - Motors and Generators.doc",
    };

    [Fact]
    public void Measure_real_bid_documents_when_available()
    {
        var root = Environment.GetEnvironmentVariable("NEXORA_REAL_DOCS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var report = new StringBuilder();
        report.AppendLine("file | rows | lines | invalid | review | maker | part no | approved-maker list | bad currency | extras lost (null) | extras > 2KB | largest extras (chars) | PO text > 2000");
        foreach (var name in Documents)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var rows = Read(bytes, name);
            var result = new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, businessUnitId: 1,
                receivedOn: new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));
            var lines = result.Documents.SelectMany(d => d.LineItems).ToList();

            var extrasNull = 0;
            var extrasOver2k = 0;
            foreach (var line in lines.Where(l => l.ExtraFields is { Count: > 0 }))
            {
                var stored = ExtraFieldsJson.Serialize(line.ExtraFields!);
                if (stored is null) extrasNull++;
                else if (stored.Length > 2048) extrasOver2k++;
            }

            report.AppendLine(string.Join(" | ",
                name,
                rows.Count,
                lines.Count,
                lines.Count(l => l.ValidationStatus == ValidationStatus.Invalid),
                lines.Count(l => l.ValidationStatus == ValidationStatus.NeedsReview),
                lines.Count(l => !string.IsNullOrWhiteSpace(l.ManufacturerName.Value)),
                lines.Count(l => !string.IsNullOrWhiteSpace(l.ManufacturerPartNumber.Value)),
                lines.Count(l => l.ExtraFields?.ContainsKey("Approved manufacturers") == true),
                lines.Count(l => l.Currency.Value is { } c && (c.Length != 3 || c.Any(ch => !char.IsAsciiLetterUpper(ch)))),
                extrasNull,
                extrasOver2k,
                lines.Where(l => l.ExtraFields is { Count: > 0 }).Select(l => System.Text.Json.JsonSerializer.Serialize(l.ExtraFields).Length).DefaultIfEmpty(0).Max(),
                lines.Count(l => (l.MaterialPoText.Value?.Length ?? 0) > 2000)));
            foreach (var line in lines.Take(3))
                report.AppendLine($"    #{line.LineItemNo.Value} name={Clip(line.ProductName.Value)} qty={line.Quantity.OriginalValue} uom={line.UnitOfMeasure.Value} cur={line.Currency.Value} maker={Clip(line.ManufacturerName.Value)} pn={Clip(line.ManufacturerPartNumber.Value)} mat={line.CustomerMaterialCode.Value} extras=[{string.Join(", ", line.ExtraFields?.Keys ?? Enumerable.Empty<string>())}]");
        }

        // Buyer terms from every document the terms reader can reach (SEC prints are HTML .doc).
        report.AppendLine();
        report.AppendLine("buyer terms:");
        foreach (var path in Directory.GetFiles(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (!ERP_RFQ_Automation.Extraction.Templates.BuyerTerms.CanRead(name, null)) continue;
            var terms = ERP_RFQ_Automation.Extraction.Templates.BuyerTerms.ReadDocument(File.ReadAllBytes(path), name);
            report.AppendLine($"{name} | {terms.Count} | " + string.Join(" · ", terms.Select(t => $"{t.Label}: {t.Value}")));
        }

        var output = Environment.GetEnvironmentVariable("NEXORA_REAL_DOCS_REPORT");
        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output, report.ToString());
    }

    private static string? Clip(string? value) => value is null ? null : value.Length <= 60 ? value : value[..60] + "…";

    /// <summary>The worker's own dispatch for these formats (ProductionDocumentReader.ReadAsync).</summary>
    private static IReadOnlyList<RfqSpreadsheetRow> Read(byte[] bytes, string name)
    {
        var grid = new NativeSpreadsheetParser();
        var tables = new DocxTableParser(grid);
        if (HtmlDocumentTextExtractor.HasHtmlSignature(bytes))
        {
            var reading = HtmlTableGrids.Read(bytes);
            return tables.ParseGrids(reading.Grids, reading.Paragraphs, name);
        }
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".xlsx" => grid.ParseXlsx(bytes, name),
            ".docx" => tables.Parse(bytes, name),
            _ => Array.Empty<RfqSpreadsheetRow>(),
        };
    }
}

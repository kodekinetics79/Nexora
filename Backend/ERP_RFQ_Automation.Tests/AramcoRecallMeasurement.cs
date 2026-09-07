using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// LOCAL MEASUREMENT, not a committed regression test — it reads a directory of real customer
/// documents that must never enter this repository, and skips silently when that directory is
/// absent so it can never fail anybody else's build.
///
/// <para>Set NEXORA_ARAMCO_TXT to a directory of .txt files converted from genuine Aramco bid
/// lists, and NEXORA_ARAMCO_TRUTH to a TSV of "filename\texpectedLineCount".</para>
/// </summary>
public sealed class AramcoRecallMeasurement
{
    [Fact]
    public void Measure_deterministic_recall_over_genuine_bid_lists()
    {
        var dir = Environment.GetEnvironmentVariable("NEXORA_ARAMCO_TXT");
        var truthFile = Environment.GetEnvironmentVariable("NEXORA_ARAMCO_TRUTH");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)
            || string.IsNullOrWhiteSpace(truthFile) || !File.Exists(truthFile))
        {
            return; // not configured on this machine
        }

        var truth = File.ReadAllLines(truthFile)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 2 && int.TryParse(p[1], out _))
            .GroupBy(p => Path.GetFileNameWithoutExtension(p[0]))
            // The same bid number appears in both sample folders; take the larger count.
            .ToDictionary(g => g.Key, g => g.Max(p => int.Parse(p[1])));

        int docs = 0, templateHit = 0, expectedTotal = 0, gotTotal = 0, exact = 0;
        var misses = new List<string>();

        foreach (var path in Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!truth.TryGetValue(name, out var expected) || expected == 0) continue;
            docs++;
            expectedTotal += expected;

            var outcome = AramcoBidListExtraction.TryExtract(
                File.ReadAllText(path), $"{name}.doc", out var rejection);

            if (outcome is null)
            {
                misses.Add($"{name}: expected {expected}, TEMPLATE DID NOT MATCH ({rejection ?? "no reason"})");
                continue;
            }

            templateHit++;
            var got = outcome.Result?.Items?.Count ?? 0;
            if (name == "C001046140")
            {
                Console.WriteLine($"--- VALUE CHECK {name} (first 5 of {got}) ---");
                foreach (var it in (outcome.Result?.Items ?? new()).Take(5))
                    Console.WriteLine($"  line={it.LineItemNo} mpn={it.ManufacturerPartNumber} code={it.ItemMaterialCode} qty={it.Quantity} uom={it.UnitOfMeasure} name={(it.ProductShortName ?? "").Substring(0, Math.Min(40, (it.ProductShortName ?? "").Length))}");
            }
            gotTotal += got;
            if (got == expected) exact++;
            else misses.Add($"{name}: expected {expected}, got {got}");
        }

        var recall = expectedTotal == 0 ? 0 : (double)gotTotal / expectedTotal;
        Console.WriteLine($"=== GENUINE ARAMCO BID LISTS ===");
        Console.WriteLine($"documents with line items : {docs}");
        Console.WriteLine($"template matched          : {templateHit}  ({(docs == 0 ? 0 : 100.0 * templateHit / docs):0.0}%)");
        Console.WriteLine($"exact line-count match    : {exact}  ({(docs == 0 ? 0 : 100.0 * exact / docs):0.0}%)");
        Console.WriteLine($"line items expected       : {expectedTotal}");
        Console.WriteLine($"line items extracted      : {gotTotal}");
        Console.WriteLine($"RECALL                    : {recall:P1}");
        Console.WriteLine();
        foreach (var m in misses.Take(25)) Console.WriteLine("  " + m);
        if (misses.Count > 25) Console.WriteLine($"  … and {misses.Count - 25} more");

        Assert.True(docs > 0, "no documents measured");
    }
}

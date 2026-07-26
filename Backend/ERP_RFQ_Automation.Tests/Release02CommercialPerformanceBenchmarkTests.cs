using System.Collections.Concurrent;
using System.Diagnostics;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.CommercialLearning;
using Xunit.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02CommercialPerformanceBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Local_commercial_algorithms_report_observed_cost_without_external_calls()
    {
        var measurements = new[]
        {
            ClassifyTenThousandLines(),
            MatchTenThousandSupplierQuoteLines(),
            CompareOffers(10), CompareOffers(20), CompareOffers(50),
            AggregateCommercialMemory(),
            CoalesceConcurrentRetries()
        };

        foreach (var measurement in measurements)
            output.WriteLine("RELEASE02_BENCHMARK scope={0} operations={1} elapsed_ms={2:F3} external_calls=0 note={3}",
                measurement.Scope, measurement.Operations, measurement.Elapsed.TotalMilliseconds, measurement.Note);

        Assert.All(measurements, measurement => Assert.True(measurement.Operations > 0));
    }

    private static Measurement ClassifyTenThousandLines()
    {
        const int count = 10_000;
        var classifier = new DeterministicCommercialDocumentClassifier();
        var started = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
        {
            var decision = classifier.Classify(new CommercialDocumentClassificationSignals(
                $"supplier-quote-{index}.csv", $"Quotation No SQ-{index}", "Supplier", "Unit price and quote validity"),
                new CommercialDocumentMatchReferences(SupplierRfqId: index + 1));
            Assert.Equal(CommercialDocumentType.SupplierQuote, decision.DocumentType);
        }
        started.Stop();
        return new("deterministic_supplier_quote_classification_10000_lines", count, started.Elapsed,
            "Local classification path only; excludes file IO, OCR, database, HTTP, and UI latency.");
    }

    private static Measurement MatchTenThousandSupplierQuoteLines()
    {
        const int count = 10_000;
        var known = Enumerable.Range(1, 1_000).ToDictionary(index => $"PART-{index:0000}", index => (long)index,
            StringComparer.OrdinalIgnoreCase);
        var matched = 0;
        var started = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
            if (known.ContainsKey($"PART-{(index % 1_000) + 1:0000}")) matched++;
        started.Stop();
        Assert.Equal(count, matched);
        return new("exact_supplier_quote_line_matching_10000", count, started.Elapsed,
            "Normalized indexed exact-match algorithm only; excludes persistence and document extraction.");
    }

    private static Measurement CompareOffers(int supplierCount)
    {
        const int runs = 1_000;
        var offers = Enumerable.Range(1, supplierCount).Select(index => new Offer(index,
            95m + index * 1.25m, 2 + index % 11, 99m - index / 2m, index % 7 != 0)).ToArray();
        var started = Stopwatch.StartNew();
        for (var run = 0; run < runs; run++)
        {
            var selected = offers.Where(x => x.Eligible).OrderBy(x => x.LandedCost)
                .ThenBy(x => x.LeadTimeDays).ThenByDescending(x => x.Reliability).First();
            Assert.True(selected.Eligible);
        }
        started.Stop();
        return new($"landed_cost_offer_comparison_{supplierCount}_suppliers", runs, started.Elapsed,
            "In-memory deterministic ranking only; currency normalization is assumed completed and database/UI latency is excluded.");
    }

    private static Measurement AggregateCommercialMemory()
    {
        const int count = 10_000;
        var rows = Enumerable.Range(0, count).Select(index => new Outcome(index % 3 == 0 ? "WON" :
            index % 3 == 1 ? "LOST" : "PENDING", index % 2 == 0 ? "USD" : "EUR", 100m + index % 250)).ToArray();
        var started = Stopwatch.StartNew();
        var decided = rows.Where(x => x.Status != "PENDING").ToArray();
        var won = decided.Count(x => x.Status == "WON");
        var summaries = rows.Where(x => x.Status == "WON").GroupBy(x => x.Currency)
            .Select(x => new { x.Key, Count = x.Count(), Median = x.OrderBy(v => v.Value).ElementAt(x.Count() / 2).Value })
            .ToArray();
        var eligible = CommercialLearningRules.CanRecommendStocking(decided.Length, won);
        started.Stop();
        Assert.Equal(2, summaries.Length);
        Assert.True(eligible);
        return new("commercial_memory_aggregation_10000_outcome_rows", count, started.Elapsed,
            "In-memory outcome/currency aggregation only; excludes PostgreSQL query and serialization latency.");
    }

    private static Measurement CoalesceConcurrentRetries()
    {
        const int attempts = 100;
        var persisted = new ConcurrentDictionary<string, Lazy<long>>(StringComparer.Ordinal);
        var creates = 0;
        var started = Stopwatch.StartNew();
        Parallel.For(0, attempts, _ =>
        {
            var value = persisted.GetOrAdd("supplier-quote:retry:1", _ => new Lazy<long>(() =>
            {
                Interlocked.Increment(ref creates);
                return 42;
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            Assert.Equal(42, value);
        });
        started.Stop();
        Assert.Equal(1, creates);
        return new("concurrent_supplier_quote_retry_key_coalescing_100_attempts", attempts, started.Elapsed,
            "In-memory concurrency primitive only; PostgreSQL serializable/idempotency behavior is covered by integration tests.");
    }

    private sealed record Measurement(string Scope, int Operations, TimeSpan Elapsed, string Note);
    private sealed record Offer(int SupplierId, decimal LandedCost, int LeadTimeDays, decimal Reliability, bool Eligible);
    private sealed record Outcome(string Status, string Currency, decimal Value);
}

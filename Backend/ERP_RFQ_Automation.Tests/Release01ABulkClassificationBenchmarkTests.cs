using System.Diagnostics;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release01ABulkClassificationBenchmarkTests
{
    private readonly ITestOutputHelper _output;
    public Release01ABulkClassificationBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Synthetic_local_classification_reports_distribution_latency_allocations_and_zero_external_calls()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(75);
        Seed.BusinessUnit(context, 75); Seed.EmailConfig(context, 7501, 75); Seed.EmailIngest(context, 7601, 7501, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);
        var results = new List<LeadReconciliationResult>();
        var latencies = new List<double>();
        var allocationStart = GC.GetTotalAllocatedBytes(true);

        for (var customer = 1; customer <= 25; customer++)
        {
            var email = $"buyer{customer}@benchmark.test";
            await Measure(Candidate($"RFQ-{customer}", email, 10), $"new-{customer}", $"hash-new-{customer}");
            await Measure(Candidate($"RFQ-{customer}", email, 10), $"dup-{customer}", $"hash-new-{customer}");
            await Measure(Candidate($"RFQ-{customer}", email, 11), $"rev-{customer}", $"hash-rev-{customer}");
            await Measure(Candidate($"SEPARATE-{customer}", email, 10), $"separate-{customer}", $"hash-separate-{customer}");
        }
        for (var ambiguous = 1; ambiguous <= 10; ambiguous++)
            await Measure(Candidate(null, null, 10), $"ambiguous-{ambiguous}", $"hash-ambiguous-{ambiguous}");

        latencies.Sort();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocationStart;
        var distribution = results.GroupBy(x => x.Classification).ToDictionary(x => x.Key, x => x.Count());
        _output.WriteLine($"occurrences={results.Count}; distribution={string.Join(',', distribution.Select(x => $"{x.Key}:{x.Value}"))}; p50_ms={Percentile(.50):F2}; p95_ms={Percentile(.95):F2}; allocated_bytes={allocated}; duplicate_race=covered_by_postgresql_lane; external_calls=0; external_cost=0");

        Assert.Equal(110, results.Count);
        Assert.Equal(50, distribution[LeadOccurrenceClassification.New]);
        Assert.Equal(25, distribution[LeadOccurrenceClassification.ExactDuplicate]);
        Assert.Equal(25, distribution[LeadOccurrenceClassification.Revision]);
        Assert.Equal(10, distribution[LeadOccurrenceClassification.PossibleMatchReviewRequired]);
        Assert.False(await context.Set<LeadIngestionOccurrence>().AnyAsync(x => x.ExternalAiUsed));

        async Task Measure(Lead candidate, string key, string hash)
        {
            context.ChangeTracker.Clear();
            var timer = Stopwatch.StartNew();
            results.Add(await service.ReconcileAsync(candidate, new LeadIntakeDescriptor(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "Benchmark", key, null, null, "synthetic", candidate.Clientemail, null, $"{key}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                100, hash.PadRight(64, '0')[..64], null, null, null, DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic, false, 0,
                "Test", "benchmark", $"benchmark:{key}")));
            timer.Stop(); latencies.Add(timer.Elapsed.TotalMilliseconds);
        }
        double Percentile(double percentile) => latencies[(int)Math.Ceiling(latencies.Count * percentile) - 1];
    }

    private static Lead Candidate(string? rfq, string? email, int quantity)
    {
        var lead = new Lead { Rfqno = rfq, BuyersName = email is null ? null : "Buyer", RecDate = DateTime.UtcNow,
            LeadSource = "Benchmark", CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = 75,
            EmailIngestsId = 7601, Clientemail = email, RequiresCommercialReview = true };
        lead.LeadItems.Add(new LeadItem { LineItemNo = "1", ManufacturerPartNumber = "BENCH-PART", Quantity = quantity, UnitOfMeasure = "EA" });
        return lead;
    }
}

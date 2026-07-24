using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using System.Diagnostics;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// ChunkedExtractionService is the safety net that guarantees a large RFQ is never
/// silently truncated: line items are split into bounded chunks, extracted independently,
/// unioned in order, and the item count is asserted (Σ chunk items == parsed rows). Any
/// mismatch, partial chunk failure, or low confidence must route the document to
/// NeedsReview rather than being saved as "complete". These tests drive that logic with a
/// scripted LLM stub (one scripted response per chunk).
/// </summary>
public class ChunkedExtractionServiceTests
{
    private static ChunkedExtractionService NewService(StubLlm llm)
        => new(llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

    private static DocumentExtractionInput Doc(IReadOnlyList<string> regions, string header = "buyer: Acme")
        => new() { BusinessUnitId = 1, LineItemRegions = regions, HeaderText = header };

    private static List<string> Rows(int n) => Enumerable.Range(0, n).Select(i => $"row {i}").ToList();

    [Fact]
    public async Task Conservation_AllItemsExtracted_HighConfidence_IsOk()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(3, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(3, outcome.ExpectedItemCount);
        Assert.Equal(3, outcome.ExtractedItemCount);
        Assert.Null(outcome.ReviewReason);
    }

    [Fact]
    public async Task ItemCountMismatch_RoutesToNeedsReview()
    {
        // 3 parsed rows but the LLM only returns 2 items -> a silent-loss guard must fire.
        var llm = new StubLlm(Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Equal(3, outcome.ExpectedItemCount);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Contains("mismatch", outcome.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LowOverallConfidence_RoutesToNeedsReview_EvenWhenCountsMatch()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(3, 0.30), 0.30));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Equal(3, outcome.ExtractedItemCount); // nothing lost...
        Assert.Contains("confidence", outcome.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PartialChunkFailure_RoutesToNeedsReview_AndReportsFailedChunk()
    {
        // 250 rows -> two chunks (200 + 50). Second chunk fails (null) -> its 50 items are
        // absent from the union and the failure is surfaced, not swallowed.
        var llm = new StubLlm(Ext.Result(Ext.Items(200, 0.9), 0.9), null);
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(250)));

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Equal(250, outcome.ExpectedItemCount);
        Assert.Equal(200, outcome.ExtractedItemCount);
        Assert.Contains("chunk", outcome.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("2 chunk"));
    }

    [Fact]
    public async Task AllChunksFail_ProducesFailed()
    {
        var llm = new StubLlm((LeadExtractionResult?)null);
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, outcome.ExtractedItemCount);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task ManyRows_SplitByItemCap_ConserveAcrossChunks()
    {
        // 250 rows -> 200 + 50; both chunks succeed and the union conserves the count.
        var llm = new StubLlm(
            Ext.Result(Ext.Items(200, 0.9), 0.9),
            Ext.Result(Ext.Items(50, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(250)));

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(250, outcome.ExpectedItemCount);
        Assert.Equal(250, outcome.ExtractedItemCount);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("2 chunk(s)"));
    }

    [Fact]
    public async Task LargeRows_SplitByCharBudget_EvenWithFewItems()
    {
        // Two ~13k-char rows exceed the 24k char budget together -> forced into two chunks.
        var big = new string('x', 13_000);
        var llm = new StubLlm(
            Ext.Result(Ext.Items(1, 0.9), 0.9),
            Ext.Result(Ext.Items(1, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(new List<string> { big, big }));

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.ExtractedItemCount);
    }

    [Fact]
    public async Task NoDetectedRows_UsesSingleWholeDocumentPass()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(new List<string>()));

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.ExtractedItemCount);
    }

    [Fact]
    public async Task NoDetectedRows_LowConfidence_RoutesToNeedsReview()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(1, 0.2), 0.2));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(new List<string>()));

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
    }

    [Fact]
    public async Task NoDetectedRows_LlmReturnsNull_IsFailed()
    {
        var llm = new StubLlm((LeadExtractionResult?)null);
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(new List<string>()));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task ExtractAsync_StructuredInput_BypassesLlm_AndUsesDeterministicNormalizer()
    {
        // Routing: a structured spreadsheet must NOT touch the LLM (biggest cost lever).
        var llm = new StubLlm(); // no scripted responses -> a call would strand the pipeline
        var rows = new[]
        {
            SpreadsheetRow(2, "RFQ-9", "Contactor 40A", qty: "5", price: "120.50"),
            SpreadsheetRow(3, "RFQ-9", "Relay 24V", qty: "10", price: "8.00"),
        };
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = 7,
            IsStructured = true,
            StructuredRows = rows,
            SourceDocumentName = "sheet.xlsx"
        };

        var outcome = await NewService(llm).ExtractAsync(input);

        Assert.Equal(0, llm.CallCount); // deterministic path, no LLM
        Assert.NotEqual(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.NotNull(outcome.CanonicalImport);
        Assert.Equal(2, outcome.CanonicalImport.Documents.Single().LineItems.Count);
    }

    [Fact]
    public async Task ExtractAsync_UnstructuredInput_RoutesThroughLlm()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(1, 0.9), 0.9));
        var input = Doc(Rows(1));
        var outcome = await NewService(llm).ExtractAsync(input);

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
    }

    [Fact]
    [Trait("Category", "LocalProcessingBenchmark")]
    public async Task StructuredLocalPath_ProcessesTenThousandRowsWithoutExternalCalls()
    {
        var llm = new StubLlm();
        var service = NewService(llm);
        var rows = Enumerable.Range(2, 10_000)
            .Select(row => SpreadsheetRow(row, "RFQ-BENCH", $"Part {row}", "1", "10.00"))
            .ToArray();
        var samples = new List<double>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);

        for (var run = 0; run < 5; run++)
        {
            var timer = Stopwatch.StartNew();
            var outcome = await service.ExtractStructuredAsync(rows, 7, "benchmark.xlsx");
            timer.Stop();
            Assert.Equal(10_000, outcome.ExtractedItemCount);
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        Console.WriteLine(
            $"LOCAL_PROCESSING_BENCHMARK rows=10000 runs=5 p50_ms={samples[2]:F2} p95_ms={samples[4]:F2} allocated_bytes={allocated} external_calls={llm.CallCount}");
        Assert.Equal(0, llm.CallCount);
        Assert.True(samples[4] < 10_000, $"p95 local parse took {samples[4]:F2} ms");
    }

    private static RfqSpreadsheetRow SpreadsheetRow(int row, string rfqNo, string product, string qty, string price)
        => new()
        {
            RowNumber = row,
            SourceDocumentName = "sheet.xlsx",
            RfqNo = rfqNo,
            BuyerName = "Acme",
            ReceivedDate = "2026-07-14",
            BidClosingDate = "2026-07-30",
            ProductName = product,
            Quantity = qty,
            UnitPrice = price,
            Currency = "USD",
            ManufacturerName = "Maker",
            ManufacturerPartNumber = "MPN-1",
            LeadTimeDays = "10"
        };
}

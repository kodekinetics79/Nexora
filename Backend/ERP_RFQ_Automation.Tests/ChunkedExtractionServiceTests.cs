using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.AI;
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
    private static ChunkedExtractionService NewService(ILLMService llm)
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

    /// <summary>Items the service will pack into one chunk for a stub with this ceiling.</summary>
    private static int PerChunk(StubLlm llm) => ExtractionOutputBudget.MaxItemsPerChunk(llm.MaxOutputTokens);

    [Fact]
    public async Task PartialChunkFailure_RoutesToNeedsReview_AndReportsFailedChunk()
    {
        // One full chunk + a remainder. The second chunk fails (null) -> its items are absent
        // from the union and the failure is surfaced, not swallowed.
        var probe = new StubLlm();
        var full = PerChunk(probe);
        var remainder = 3;
        var llm = new StubLlm(Ext.Result(Ext.Items(full, 0.9), 0.9), null);
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(full + remainder)));

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Equal(full + remainder, outcome.ExpectedItemCount);
        Assert.Equal(full, outcome.ExtractedItemCount);
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
        // One full chunk + a remainder; both chunks succeed and the union conserves the count.
        var probe = new StubLlm();
        var full = PerChunk(probe);
        var remainder = 3;
        var llm = new StubLlm(
            Ext.Result(Ext.Items(full, 0.9), 0.9),
            Ext.Result(Ext.Items(remainder, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(full + remainder)));

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(full + remainder, outcome.ExpectedItemCount);
        Assert.Equal(full + remainder, outcome.ExtractedItemCount);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("2 chunk(s)"));
    }

    [Fact]
    public async Task ChunkSize_NeverProjectsMoreOutputThanTheModelCanEmit()
    {
        // THE REGRESSION THIS SUITE EXISTS FOR (2026-08-05). Chunking used to be sized by
        // input characters alone, so a 200-item chunk was planned against a 4,096-token
        // completion ceiling that it needed ~90,000 tokens to satisfy. Every real RFQ came
        // back cut mid-JSON and the whole document dead-lettered. Whatever the ceiling is,
        // the planned chunk must be projected to FIT it.
        const int ceiling = 4096;
        var perChunk = ExtractionOutputBudget.MaxItemsPerChunk(ceiling);
        var responses = Enumerable.Repeat<LeadExtractionResult?>(
            Ext.Result(Ext.Items(perChunk, 0.9), 0.9), 3).ToArray();
        var llm = new StubLlm(AiProviderClass.Local, responses) { MaxOutputTokens = ceiling };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(perChunk * 3)));

        Assert.Equal(3, llm.CallCount);
        Assert.All(llm.RequestedItemCounts, count =>
        {
            Assert.NotNull(count);
            Assert.True(
                ExtractionOutputBudget.FitsBudget(count!.Value, 4096),
                $"A chunk of {count} item(s) projects "
                + $"{ExtractionOutputBudget.ProjectedOutputTokens(count.Value)} output tokens, which does not "
                + "fit a 4096-token ceiling with margin.");
        });
        Assert.Equal(perChunk * 3, outcome.ExtractedItemCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
    }

    [Fact]
    public void ChunkSize_ScalesWithTheCeiling_AndNeverFallsBelowOneItem()
    {
        // The budget is arithmetic, not a magic number: bigger ceiling -> bigger chunk, and
        // the projection always stays under the ceiling with the safety margin applied.
        foreach (var ceiling in new[] { 1024, 2048, 4096, 8192, 16384 })
        {
            var items = ExtractionOutputBudget.MaxItemsPerChunk(ceiling);
            Assert.True(items >= 1, $"ceiling {ceiling} produced {items} items per chunk");
            if (items > 1)
                Assert.True(ExtractionOutputBudget.FitsBudget(items, ceiling),
                    $"ceiling {ceiling}: {items} items project "
                    + $"{ExtractionOutputBudget.ProjectedOutputTokens(items)} tokens");
            Assert.False(ExtractionOutputBudget.FitsBudget(items + 1, ceiling),
                $"ceiling {ceiling}: {items + 1} items should NOT fit — the budget is leaving room unused");
        }

        Assert.True(ExtractionOutputBudget.MaxItemsPerChunk(8192)
                    > ExtractionOutputBudget.MaxItemsPerChunk(4096));
        // A ceiling too small for even one item still yields one: an item is indivisible,
        // and the caller fails it honestly instead of looping.
        Assert.Equal(1, ExtractionOutputBudget.MaxItemsPerChunk(64));
    }

    [Fact]
    public async Task OutputTruncation_RetriesWithASmallerChunk_NeverTheSameRequestTwice()
    {
        // The document plans 12 items per chunk against the advertised 8,192-token ceiling,
        // but this document is verbose enough that the model truncates above 4 items. The
        // extractor must respond by asking for LESS — halving, floor 1 — not by replaying
        // the identical failing request.
        var llm = new BudgetedStubLlm(maxOutputTokens: 8192, truncateAboveItems: 4);
        var planned = ExtractionOutputBudget.MaxItemsPerChunk(8192);
        Assert.Equal(12, planned); // guards the documented arithmetic

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(planned)));

        // 12 truncates -> 6 + 6; each 6 truncates -> 3 + 3; every 3 succeeds.
        Assert.Equal(new[] { 12, 6, 3, 3, 6, 3, 3 }, llm.RequestedItemCounts);
        // No truncated size is ever re-issued at that same size.
        Assert.Single(llm.RequestedItemCounts.Where(c => c == 12));
        Assert.Equal(2, llm.RequestedItemCounts.Count(c => c == 6));
        // Conservation still holds: every parsed row came back.
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(planned, outcome.ExpectedItemCount);
        Assert.Equal(planned, outcome.ExtractedItemCount);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("truncated") && d.Contains("retrying"));
    }

    [Fact]
    public async Task OutputTruncation_OfASingleItem_FailsThatItemHonestly_AndDoesNotLoop()
    {
        // Nothing left to halve. The item must fail — visibly, with the count mismatch
        // guard firing — rather than the extractor looping forever trying to shrink it.
        var llm = new BudgetedStubLlm(maxOutputTokens: 8192, truncateAboveItems: 0);

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, outcome.ExtractedItemCount);
        Assert.True(llm.CallCount <= 8, $"re-splitting did not terminate promptly: {llm.CallCount} calls");
        Assert.All(llm.RequestedItemCounts, c => Assert.True(c >= 1));
    }

    [Fact]
    public async Task OutputTruncation_PartialDocument_StillRefusesToDropItemsSilently()
    {
        // One pathological line item overflows on its own; the other three extract cleanly.
        // The document must NOT be reported complete — conservation is the whole point of
        // this service, and truncation is not allowed to become a silent item drop.
        var llm = new BudgetedStubLlm(maxOutputTokens: 8192, truncateAboveItems: 12)
        {
            AlwaysTruncatesMarker = "OVERSIZED"
        };
        var rows = new List<string> { "row 0", "row 1", "OVERSIZED row", "row 3" };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(rows));

        Assert.Equal(4, outcome.ExpectedItemCount);
        Assert.Equal(3, outcome.ExtractedItemCount);
        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Contains(outcome.Diagnostics,
            d => d.Contains("one line item alone exceeds", StringComparison.OrdinalIgnoreCase));
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
    public async Task EmptyParserOrOcrResult_FailsBeforeAnyModelCall()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(1, 0.9), 0.9));

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Array.Empty<string>(), "  "));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains("no readable content", outcome.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TruncatedOcr_CannotReportComplete()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(1, 0.95), 0.95));
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            HeaderText = "buyer: Acme",
            LineItemRegions = Array.Empty<string>(),
            OcrStatus = ExtractionOcrStatus.Partial,
            OcrTruncated = true
        };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Contains("OCR was incomplete", outcome.ReviewReason);
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
    public async Task NoDetectedRows_ExternalProviderCannotReceiveWholeDocument()
    {
        var llm = new StubLlm(AiProviderClass.External, Ext.Result(Ext.Items(1, 0.9), 0.9));

        var outcome = await NewService(llm).ExtractUnstructuredAsync(
            Doc(Array.Empty<string>(), "confidential whole document"));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains("blocked for unstructured documents", outcome.ReviewReason);
        Assert.Contains("human review", outcome.ReviewReason);
    }

    [Fact]
    public async Task PopulatedRegions_ExternalProviderFailsClosedWithoutReceivingDocumentContent()
    {
        var llm = new StubLlm(AiProviderClass.External, Ext.Result(Ext.Items(2, 0.9), 0.9));
        var input = Doc(
            new[] { "CONFIDENTIAL-PART-001 qty 10", "CONFIDENTIAL-PART-002 qty 20" },
            "confidential buyer and RFQ reference");

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(2, outcome.ExpectedItemCount);
        Assert.Equal(0, outcome.ExtractedItemCount);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains("locally reduced, redacted field/row payload", outcome.ReviewReason);
        Assert.Contains("human review", outcome.ReviewReason);
    }

    [Fact]
    public async Task PopulatedRegions_LocalProviderStillUsesChunkedExtraction()
    {
        var llm = new StubLlm(AiProviderClass.Local, Ext.Result(Ext.Items(2, 0.9), 0.9));

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(2)));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal(2, outcome.ExpectedItemCount);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Equal(AiProviderClass.Local, outcome.AiProviderClass);
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

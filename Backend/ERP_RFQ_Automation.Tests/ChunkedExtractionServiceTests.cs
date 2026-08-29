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
/// silently truncated: parsed text regions are split into bounded chunks, extracted
/// independently, and unioned in order. Conservation is enforced where it is real — a
/// failed chunk, incomplete OCR, or a populated body that produced zero items routes the
/// document to NeedsReview rather than being saved as "complete". What must NOT flag
/// review on the unstructured path is "fewer items than text lines": lines are not items,
/// and that comparison used to stamp a false "Item count mismatch" on effectively every
/// unstructured document (and thereby disabled multi-inquiry auto-split). These tests
/// drive that logic with a scripted LLM stub (one scripted response per chunk).
/// </summary>
public class ChunkedExtractionServiceTests
{
    private static ChunkedExtractionService NewService(ILLMService llm)
        => new(llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

    private static DocumentExtractionInput Doc(IReadOnlyList<string> regions, string header = "buyer: Acme")
        => new() { BusinessUnitId = 1, LineItemRegions = regions, HeaderText = header };

    private static List<string> Rows(int n) => Enumerable.Range(0, n).Select(i => $"row {i}").ToList();

    [Fact]
    public async Task Every_header_field_survives_chunking_including_the_ones_declared_last()
    {
        // REGRESSION. WithItems used to rebuild LeadExtractionResult POSITIONALLY and stop at
        // `items`, so every header field declared after it was silently dropped on every
        // chunked document — InquiryType/InquiryTypeConfidence had been lost that way since
        // the BOQ work landed, and the client-organisation fields would have been lost the
        // same way. Chunking starts at 11 items and SEC bids run 12+ lines, so this hit
        // exactly the documents that matter.
        var perChunk = ExtractionOutputBudget.MaxItemsPerChunk(4096);
        var header = Ext.Result(Ext.Items(perChunk, 0.9), 0.9) with
        {
            InquiryType = "service",
            InquiryTypeConfidence = 0.88,
            CustomerCompanyName = "Saudi Electricity Company",
            CustomerCompanyNameConfidence = 0.91,
            CustomerCompanyEvidence = "As SEC is implementing the new distribution standard",
            CustomerBuyerEmail = "57322@se.com.sa",
            CustomerBuyerEmailConfidence = 0.97,
            CustomerPortalName = "MATERIALS E-BIDDING SYSTEM",
            CustomerPortalNameConfidence = 0.93,
            SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL",
            SupplierAccountRefOnDocument = "2004414",
            SupplierAccountRefOnDocumentConfidence = 0.99
        };
        var llm = new StubLlm(header, Ext.Result(Ext.Items(1, 0.9), 0.9));

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(perChunk + 1)));

        Assert.True(llm.CallCount > 1, "the document must actually be chunked for this to prove anything");
        Assert.NotNull(outcome.Result);
        Assert.Equal("service", outcome.Result!.InquiryType);
        Assert.Equal(0.88, outcome.Result.InquiryTypeConfidence);
        Assert.Equal("Saudi Electricity Company", outcome.Result.CustomerCompanyName);
        Assert.Equal("As SEC is implementing the new distribution standard", outcome.Result.CustomerCompanyEvidence);
        Assert.Equal("57322@se.com.sa", outcome.Result.CustomerBuyerEmail);
        Assert.Equal("MATERIALS E-BIDDING SYSTEM", outcome.Result.CustomerPortalName);
        Assert.Equal("ALI ZAID AL-QURAISHI&PARTNERS EL", outcome.Result.SupplierNameOnDocument);
        Assert.Equal("2004414", outcome.Result.SupplierAccountRefOnDocument);
        // The merge still does its own job: all items unioned, confidence recomputed.
        Assert.Equal(perChunk + 1, outcome.Result.Items.Count);
    }

    [Fact]
    public async Task ChunkMerge_PreservesEveryHeaderProperty_IncludingOnesAddedInTheFuture()
    {
        // REGRESSION NET for the WithItems positional drop, reflection-based so it cannot
        // rot: every public property of LeadExtractionResult except Items gets a distinct
        // probe value, and after chunked extraction every one of them must survive onto the
        // merged result. A header field added to the record in the future is covered
        // automatically — if reconstruction ever goes positional again and drops it, this
        // fails without anyone having to remember to extend the hand-written test above.
        var perChunk = ExtractionOutputBudget.MaxItemsPerChunk(4096);
        var header = Ext.Result(Ext.Items(perChunk, 0.9), 0.9);
        var headerProperties = typeof(LeadExtractionResult).GetProperties()
            .Where(p => p.Name != nameof(LeadExtractionResult.Items))
            .ToArray();
        for (var i = 0; i < headerProperties.Length; i++)
        {
            var property = headerProperties[i];
            if (property.PropertyType == typeof(string))
                property.SetValue(header, $"probe-{property.Name}");
            else if (property.PropertyType == typeof(double?))
                property.SetValue(header, 0.70 + (i * 0.001)); // distinct, and high enough not to trip review
            else
                Assert.Fail(
                    $"LeadExtractionResult.{property.Name} is a {property.PropertyType.Name}; teach this "
                    + "regression test how to probe that type so reconstruction coverage stays total.");
        }
        var llm = new StubLlm(header, Ext.Result(Ext.Items(1, 0.9), 0.9));

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(perChunk + 1)));

        Assert.True(llm.CallCount > 1, "the document must actually be chunked for this to prove anything");
        Assert.NotNull(outcome.Result);
        foreach (var property in headerProperties)
        {
            if (property.Name == nameof(LeadExtractionResult.OverallConfidence))
                continue; // recomputed from the merged items BY DESIGN
            Assert.True(
                Equals(property.GetValue(header), property.GetValue(outcome.Result)),
                $"LeadExtractionResult.{property.Name} was dropped or altered by the chunk-merge "
                + $"reconstruction (expected '{property.GetValue(header)}', got '{property.GetValue(outcome.Result)}').");
        }
        Assert.Equal(perChunk + 1, outcome.Result!.Items.Count);
    }

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
    public async Task FewerItemsThanTextLines_IsNotAMismatch_OnTheUnstructuredPath()
    {
        // PANEL ITEM 3. "expected" counts parsed TEXT LINES, not items — on a real PDF
        // several lines form one item (wrapped descriptions, banners, footers), so 3 lines
        // -> 2 items is the NORMAL case, not loss. This used to stamp a false "Item count
        // mismatch: expected N, extracted M" on effectively every unstructured document
        // (production: a 6-item SEC bid flagged as "expected 174") and, because only an Ok
        // outcome may auto-split, silently disabled multi-inquiry splitting for all of them.
        var llm = new StubLlm(Ext.Result(Ext.Items(2, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(3, outcome.ExpectedItemCount);   // still reported — as a diagnostic
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Null(outcome.ReviewReason);
        Assert.DoesNotContain(outcome.Diagnostics,
            d => d.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ZeroItemsFromAPopulatedBody_StillRoutesToNeedsReview()
    {
        // The one line-count signal that IS real on any document: the body had parsed
        // regions and the model extracted NOTHING. Dropping the false mismatch alarm must
        // not also drop this.
        var llm = new StubLlm(Ext.Result(Ext.Items(0, 0.9), 0.9));
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Equal(0, outcome.ExtractedItemCount);
        Assert.Contains("No line items were extracted", outcome.ReviewReason);
    }

    [Fact]
    public async Task MultiInquirySplit_RunsForUnstructuredDocuments_OnItsOwnSignals()
    {
        // The false mismatch forced NeedsReview, and a NeedsReview document is never
        // split — so auto-split was silently OFF for every unstructured document. With
        // the text-line expectation gone, the splitter's own evidence (InquiryGroup
        // labels + grouping confidence) decides.
        var items = new List<LeadItemData>
        {
            Ext.Item(0.9, "Breaker") with { InquiryGroup = "RFQ-A", InquiryGroupConfidence = 0.9 },
            Ext.Item(0.9, "Relay") with { InquiryGroup = "RFQ-A", InquiryGroupConfidence = 0.9 },
            Ext.Item(0.9, "Cable") with { InquiryGroup = "RFQ-B", InquiryGroupConfidence = 0.9 },
        };
        var llm = new StubLlm(Ext.Result(items, 0.9));
        // 8 text lines -> 3 items: the exact shape production sees on every PDF.
        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(8)));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.NotNull(outcome.SplitResults);
        Assert.Equal(2, outcome.SplitResults!.Count);
        Assert.Equal(new[] { 2, 1 }, outcome.SplitResults.Select(r => r.Items.Count).ToArray());
        Assert.Contains(outcome.Diagnostics, d => d.Contains("split into 2 inquiry group(s)"));
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

    /// <summary>Input pinned to a stable job identity and an explicit lease attempt, the way
    /// the worker's document readers build it.</summary>
    private static DocumentExtractionInput Attempt(int attempt, IReadOnlyList<string> regions)
        => new()
        {
            BusinessUnitId = 1,
            SourceId = "job:33",
            AttemptNumber = attempt,
            HeaderText = "buyer: Acme",
            LineItemRegions = regions
        };

    [Fact]
    public async Task RetryAttempt_IssuesADistinctIdempotencyKey_AndASameAttemptReplayDoesNot()
    {
        // THE dead-letter root cause: the key omitted the lease attempt, so a retried job
        // replayed attempt one's keys and the governance ledger refused every call as a
        // duplicate before any model call. A retry must be a NEW governed request; an
        // identical (job, chunk, attempt) replay must still produce the identical key.
        var first = new StubLlm(Ext.Result(Ext.Items(2, 0.9), 0.9));
        var second = new StubLlm(Ext.Result(Ext.Items(2, 0.9), 0.9));
        var replay = new StubLlm(Ext.Result(Ext.Items(2, 0.9), 0.9));

        await NewService(first).ExtractUnstructuredAsync(Attempt(1, Rows(2)));
        await NewService(second).ExtractUnstructuredAsync(Attempt(2, Rows(2)));
        await NewService(replay).ExtractUnstructuredAsync(Attempt(1, Rows(2)));

        var attemptOneKey = Assert.Single(first.IdempotencyKeys);
        var attemptTwoKey = Assert.Single(second.IdempotencyKeys);
        var replayedKey = Assert.Single(replay.IdempotencyKeys);
        Assert.Equal("extraction:job:33:a1:chunk:1:2", attemptOneKey);
        Assert.Equal("extraction:job:33:a2:chunk:1:2", attemptTwoKey);
        Assert.NotEqual(attemptOneKey, attemptTwoKey); // retry N is a NEW governed request
        Assert.Equal(attemptOneKey, replayedKey);      // same attempt still dedups downstream
    }

    [Fact]
    public async Task WholeDocumentPass_KeyIsAlsoAttemptScoped()
    {
        var llm = new StubLlm(Ext.Result(Ext.Items(1, 0.9), 0.9));

        await NewService(llm).ExtractUnstructuredAsync(Attempt(4, Array.Empty<string>()));

        Assert.Equal("extraction:job:33:a4:whole", Assert.Single(llm.IdempotencyKeys));
    }

    /// <summary>An LLM whose governance layer refuses every reservation — the exact shape of
    /// the production duplicate-key refusal, thrown before any provider call.</summary>
    // ------------------------------------------------- the template must be REACHABLE

    [Fact]
    public async Task The_model_path_consults_the_template_before_spending_anything()
    {
        // THE DEFECT THIS EXISTS TO PREVENT, and it survived a whole suite of green tests.
        //
        // Every other Aramco test calls AramcoBidListExtraction.TryExtract directly. They prove
        // the parser WORKS; none proved it was REACHABLE. The guard sat in ExtractAsync, while
        // ExtractionWorker — the path every real document takes — calls ExtractUnstructuredAsync.
        // So the template was never consulted in production: zero hits since it shipped, while
        // two Aramco bid lists uploaded on 2026-08-19 went to an external model and came back
        // with 2 of 3 and 11 of 42 line items.
        //
        // This test asserts the wiring rather than the parsing: hand the PAID entry point a
        // document the template can read, and no model may be called at all.
        var llm = new ExplodingLlm();
        var service = NewService(llm);

        var outcome = await service.ExtractUnstructuredAsync(new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            SourceDocumentName = "C001046164.doc",
            HeaderText = AramcoFixture,
            LineItemRegions = Array.Empty<string>(),
        });

        Assert.False(llm.WasCalled, "the template was skipped and the document went to the model");
        Assert.Equal(1, outcome.ExtractedItemCount);
        Assert.Null(outcome.AiProviderClass);   // nothing external was involved
    }

    /// <summary>A minimal but genuine bid list: masthead, the six column headers, one record.</summary>
    private const string AramcoFixture = """
        MATERIALS E-BIDDING SYSTEM
        Bid Materials List (Low Value Bid)
        Vendor Code
        Vendname
        Bidno
        Bid Date
        Bid Close
        2004414
        ALI ZAID AL-QURAISHI&PARTNERS EL
        C001046164
        2/12/2021
        2/24/2021
        Address
        Buyer
        Buyer Tel
        Saudi Arabia
        2GH-MEYASSAR MARAKSHI
        012-6537914-
        Bid Line
        Item No
        Ship To
        Req Unit
        Req Qty
        Resp Qty
        10
        902017274
        3801
        EA
        176
        KEY:SHAFT,SQUARE,10 MM X 10 MM LG
        """;

    /// <summary>Fails loudly if anything asks it to think. The point of the test is that
    /// nothing should.</summary>
    private sealed class ExplodingLlm : ILLMService
    {
        public bool WasCalled { get; private set; }
        public AiProviderClass ProviderClass => AiProviderClass.External;

        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException(
                "The model must not be called for a document the template can read.");
        }

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }

    private sealed class GovernanceRefusingLlm : ILLMService
    {
        private readonly string _code;
        public GovernanceRefusingLlm(string code) => _code = code;
        public AiProviderClass ProviderClass => AiProviderClass.Local;

        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
            => throw new AiPolicyDeniedException(_code);

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }

    [Fact]
    public async Task GovernanceRefusal_SurfacesItsOwnCode_NeverMasqueradesAsAllChunksFailed()
    {
        // REGRESSION (job 33): every governed call was refused as duplicate_request before
        // any model call, and the outcome still said "All chunks failed; no data
        // extracted" — a governance refusal reported as a model failure. The refusal must
        // keep its own code all the way into the failure reason and the diagnostics.
        var llm = new GovernanceRefusingLlm("duplicate_request");

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.ReviewReason);
        Assert.Contains("AI governance refused", outcome.ReviewReason);
        Assert.Contains("duplicate_request", outcome.ReviewReason);
        Assert.Contains("before any model call", outcome.ReviewReason);
        Assert.DoesNotContain("All chunks failed", outcome.ReviewReason);
        Assert.Contains(outcome.Diagnostics,
            d => d.Contains("refused by AI governance") && d.Contains("duplicate_request"));
    }

    [Fact]
    public async Task GovernanceRefusal_OnTheWholeDocumentPass_SurfacesItsOwnCode()
    {
        var llm = new GovernanceRefusingLlm("document_budget_exceeded");

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(new List<string>()));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("AI governance refused", outcome.ReviewReason);
        Assert.Contains("document_budget_exceeded", outcome.ReviewReason);
    }

    [Fact]
    public async Task AllChunksFailed_KeepsCollectedDiagnosticsOnTheFailedOutcome()
    {
        // The per-chunk diagnostics used to be discarded when every chunk failed — the
        // outcome carried only the flattened reason, so the dead-letter LastError could
        // not say which chunk failed or why. They must survive onto the Failed outcome.
        var llm = new StubLlm((LeadExtractionResult?)null);

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(3)));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("split into 1 chunk(s)"));
        Assert.Contains(outcome.Diagnostics, d => d.Contains("Chunk 1/1 failed"));
        Assert.Contains(outcome.Diagnostics, d => d == outcome.ReviewReason);
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
        // The document plans 22 items per chunk against the advertised 8,192-token ceiling,
        // but this document is verbose enough that the model truncates above 4 items. The
        // extractor must respond by asking for LESS — halving, floor 1 — not by replaying
        // the identical failing request.
        //
        // 22, not 11: rfq-extraction-v2 stopped asking for a "<Field>Confidence" number
        // beside each of the 24 per-item value fields. Those numbers were parsed and then
        // discarded (LeadItem has ONE Aiconfidence column, fed from ItemConfidence), so half
        // of every item's output budget was being spent on output nobody read.
        // EstimatedOutputTokensPerItem went 450 -> 225 and the planned chunk doubled.
        var llm = new BudgetedStubLlm(maxOutputTokens: 8192, truncateAboveItems: 4);
        var planned = ExtractionOutputBudget.MaxItemsPerChunk(8192);
        Assert.Equal(22, planned); // guards the documented arithmetic

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(Rows(planned)));

        // Depth-first halving, left half reprocessed first: 22 -> 11 + 11; each 11 -> 5 + 6;
        // 5 -> 2 + 3 and 6 -> 3 + 3. Anything <= 4 succeeds.
        Assert.Equal(
            new[] { 22, 11, 5, 2, 3, 6, 3, 3, 11, 5, 2, 3, 6, 3, 3 },
            llm.RequestedItemCounts);
        // No truncated request is ever replayed at the same size: a truncation is always
        // followed immediately by a STRICTLY SMALLER request (the left half).
        var counts = llm.RequestedItemCounts;
        for (var i = 0; i < counts.Count - 1; i++)
            if (counts[i] > 4) // > truncateAboveItems, i.e. this request truncated
                Assert.True(counts[i + 1] < counts[i],
                    $"request {i} truncated at {counts[i]} item(s) and the next request asked "
                    + $"for {counts[i + 1]} — a truncated size must never be re-issued.");
        // Conservation still holds: every parsed row came back.
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(planned, outcome.ExpectedItemCount);
        Assert.Equal(planned, outcome.ExtractedItemCount);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("truncated") && d.Contains("retrying"));
    }

    [Fact]
    public async Task OutputTruncation_OfASingleItem_FailsThatItemHonestly_AndDoesNotLoop()
    {
        // Nothing left to halve. The item must fail — visibly, as a failed chunk —
        // rather than the extractor looping forever trying to shrink it.
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

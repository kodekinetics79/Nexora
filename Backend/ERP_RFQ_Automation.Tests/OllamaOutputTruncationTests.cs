using System.Net;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// PROD ROOT CAUSE (2026-08-05). Ollama reports WHY generation stopped in
/// <c>done_reason</c>. The client ignored it, so a response that was cut off at the
/// completion ceiling ("length") was indistinguishable from a model that wrote bad JSON:
/// both parsed to null and both were filed as <c>invalid_output</c>. Measured live against
/// ollama.com/deepseek-v4-pro on a 40-line RFQ, num_predict 4096 AND 8192 both returned
/// done_reason="length" with eval_count pinned at the ceiling — every chunk "failed" and
/// the document dead-lettered as "All chunks failed; no data extracted", while the model
/// was answering a 3-item document perfectly.
///
/// These tests pin the distinction: truncation is named <c>output_truncated</c>, it is
/// never replayed unchanged, and a genuinely bad-but-complete response is still
/// <c>invalid_output</c>.
/// </summary>
public sealed class OllamaOutputTruncationTests
{
    private const string TruncatedJson = "{\"Rfqno\":\"RFQ-1\",\"Items\":[{\"ProductShortName\":\"Cont";

    [Fact]
    public async Task DoneReasonLength_IsReportedAsOutputTruncated_NotInvalidOutput()
    {
        var handler = new RecordingHandler(_ => Reply(TruncatedJson, doneReason: "length", evalCount: 8192));
        var governance = new PermissiveGovernance();
        var service = CreateService(handler, governance);

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        Assert.Null(outcome.Result);
        Assert.Equal(AiErrorCodes.OutputTruncated, outcome.ErrorCode);
        Assert.True(outcome.OutputTruncated);
        Assert.Equal(AiErrorCodes.OutputTruncated, Assert.Single(governance.Attempts).ErrorCode);
        Assert.Equal(AiErrorCodes.OutputTruncated, governance.CompletedErrorCode);
        Assert.NotEqual(AiErrorCodes.InvalidOutput, governance.CompletedErrorCode);
    }

    [Fact]
    public async Task DoneReasonLength_IsRetryable_ButIsNeverReplayedAsTheIdenticalRequest()
    {
        // Retryable, yes — by a caller that can make the request SMALLER. Re-sending this
        // exact payload would re-truncate at the identical token and burn the retry budget,
        // so the client stops after one attempt and hands the retryable code back up.
        var handler = new RecordingHandler(_ => Reply(TruncatedJson, doneReason: "length", evalCount: 4096));
        var governance = new PermissiveGovernance();
        var service = CreateService(handler, governance);

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        Assert.True(outcome.OutputTruncated);
        Assert.Single(handler.RequestBodies);
        Assert.Single(governance.Attempts);
    }

    [Fact]
    public async Task DoneReasonLength_LogsTheEvalCountAndTheCeiling_WithoutDocumentContent()
    {
        var handler = new RecordingHandler(_ => Reply(TruncatedJson, doneReason: "length", evalCount: 4096));
        var logger = new CapturingLogger<OllamaLlmService>();
        var service = CreateService(handler, new PermissiveGovernance(), logger);

        await service.ExtractLeadDataDetailedAsync("CONFIDENTIAL-PART-1 quantity 10", Context());

        var truncationLog = Assert.Single(logger.Messages.Where(m => m.Contains("TRUNCATED")));
        Assert.Contains("4096", truncationLog, StringComparison.Ordinal);
        Assert.Contains("done_reason=length", truncationLog, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIDENTIAL-PART-1", string.Join('\n', logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoneReasonLength_CarriesTheItemsInTheChunkIntoTheLog()
    {
        // The chunker declares how many line items it packed in; that number is what tells an
        // operator the ask was too big rather than the model being broken.
        var handler = new RecordingHandler(_ => Reply(TruncatedJson, doneReason: "length", evalCount: 4096));
        var logger = new CapturingLogger<OllamaLlmService>();
        var service = CreateService(handler, new PermissiveGovernance(), logger);

        await service.ExtractLeadDataDetailedAsync(
            "rows", Context() with { ItemsInPayload = 37 });

        Assert.Contains(logger.Messages, m => m.Contains("TRUNCATED") && m.Contains("37"));
    }

    [Fact]
    public async Task CompleteButUnparseableOutput_IsStillInvalidOutput()
    {
        // The guard on the other side: mislabelling a genuine model failure as truncation
        // would send the chunker into a pointless shrink loop.
        var handler = new RecordingHandler(_ => Reply("not json at all", doneReason: "stop", evalCount: 40));
        var governance = new PermissiveGovernance();
        var service = CreateService(handler, governance);

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        Assert.Null(outcome.Result);
        Assert.Equal(AiErrorCodes.InvalidOutput, outcome.ErrorCode);
        Assert.False(outcome.OutputTruncated);
    }

    [Fact]
    public async Task EmptyContentAtTheCeiling_IsTruncation_NotAnEmptyModel()
    {
        var handler = new RecordingHandler(_ => Reply("", doneReason: "length", evalCount: 4096));
        var service = CreateService(handler, new PermissiveGovernance());

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        Assert.Equal(AiErrorCodes.OutputTruncated, outcome.ErrorCode);
    }

    [Fact]
    public async Task BoqDraft_DoneReasonLength_IsReportedAsOutputTruncated()
    {
        // Same request shape, same failure mode. The BOQ path has no chunking to fall back
        // on, which makes naming the failure honestly more important, not less.
        var handler = new RecordingHandler(_ => Reply(
            "{\"ServiceCategory\":\"mechanical\",\"OverallConfidence\":0.8,\"Sections\":[{\"Title\":\"Wo",
            doneReason: "length", evalCount: 4096));
        var governance = new PermissiveGovernance();
        var service = CreateService(handler, governance);

        var result = await service.DraftServiceBoqAsync("overhaul the pump", BoqContext());

        Assert.Null(result);
        Assert.Equal(AiErrorCodes.OutputTruncated, Assert.Single(governance.Attempts).ErrorCode);
        Assert.Equal(AiErrorCodes.OutputTruncated, governance.CompletedErrorCode);
        Assert.Single(handler.RequestBodies); // not replayed unchanged
    }

    [Fact]
    public async Task BoqDraft_TruncatedButParseableOutput_IsStillRefused()
    {
        // A BOQ that happens to close its JSON early is a bill of quantities missing its
        // tail. Returning it would be the exact fabrication this path refuses everywhere.
        var handler = new RecordingHandler(_ => Reply(
            "{\"ServiceCategory\":\"mechanical\",\"OverallConfidence\":0.8,\"Sections\":"
            + "[{\"Title\":\"Works\",\"Items\":[{\"Description\":\"Strip pump\",\"Unit\":\"lot\","
            + "\"Quantity\":1,\"ItemType\":\"Labor\",\"Confidence\":0.9,\"Tbd\":false}]}],"
            + "\"Assumptions\":[]}",
            doneReason: "length", evalCount: 4096));
        var governance = new PermissiveGovernance();
        var service = CreateService(handler, governance);

        var result = await service.DraftServiceBoqAsync("overhaul the pump", BoqContext());

        Assert.Null(result);
        Assert.Equal(AiErrorCodes.OutputTruncated, governance.CompletedErrorCode);
    }

    [Theory]
    // Unset is 8,192, the value appsettings.json ships: 4,096 truncated nearly every
    // spec-heavy chunk (a single Aramco or Marafiq line costs 450–650 output tokens).
    [InlineData(null, 8192)]
    [InlineData("8192", 8192)]
    [InlineData("32768", 32768)]
    // ollama.com rejects num_predict above 65,536 for deepseek-v4-pro (ollama/ollama#16890),
    // so the clamp must refuse to ask for more — a ceiling above the provider's real limit
    // only trades truncation for HTTP 400.
    [InlineData("1048576", OllamaLlmService.PROVIDER_MAX_OUTPUT_TOKENS)]
    public async Task ConfiguredOutputCeiling_IsHonouredUpToTheProviderLimit(
        string? configured, int expected)
    {
        // A context window big enough that the output ceiling is the only limit in play.
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance(), maxOutputTokens: configured, numCtx: "131072");

        Assert.Equal(expected, service.MaxOutputTokens);

        await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(expected,
            request.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    // ---- context window + keep-alive (P0 #13) ----------------------------
    // Ollama picked a VRAM-based 262,144-token context when the request named none: 24.6 GB
    // of KV cache beside 17.5 GB of weights, the laptop in 51 GB of swap, for prompts of
    // 4,000–6,600 tokens. And it unloaded the model after 5 idle minutes, costing 13–27 s a
    // reload. Every request now names both.

    [Fact]
    public async Task EveryExtractionRequest_NamesTheContextWindowAndTheKeepAlive_ByDefault()
    {
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance());

        await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(16384, request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(8192, request.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("30m", request.RootElement.GetProperty("keep_alive").GetString());
        Assert.Equal(16384, service.ContextWindowTokens);
    }

    [Fact]
    public async Task ConfiguredContextWindowAndKeepAlive_AreSentAsConfigured()
    {
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance(), numCtx: "32768", keepAlive: "2h");

        await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(32768, request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("2h", request.RootElement.GetProperty("keep_alive").GetString());
    }

    [Fact]
    public async Task ZeroContextWindowAndBlankKeepAlive_SendNeither()
    {
        // The escape hatch for a provider that refuses the fields: configuration can turn them off.
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance(), numCtx: "0", keepAlive: "");

        await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.False(request.RootElement.GetProperty("options").TryGetProperty("num_ctx", out _));
        Assert.False(request.RootElement.TryGetProperty("keep_alive", out _));
        Assert.Null(service.ContextWindowTokens);
        Assert.Null(service.MaxDocumentInputTokens);
    }

    [Fact]
    public async Task HeaderCompletionAndBoqRequests_AlsoNameTheContextWindowAndKeepAlive()
    {
        var handler = new RecordingHandler(_ => Reply("{}", doneReason: "stop", evalCount: 4));
        var service = CreateService(handler, new PermissiveGovernance());

        await service.CompleteHeaderAsync("RFQ No: 1234567890", new AiCallContext(
            1, AiPurposes.RfqExtraction, Guid.NewGuid().ToString("N"), AiPromptVersions.HeaderCompletion));
        await service.DraftServiceBoqAsync("overhaul the pump", BoqContext());

        Assert.True(handler.RequestBodies.Count >= 2);
        foreach (var body in handler.RequestBodies)
        {
            using var request = JsonDocument.Parse(body);
            Assert.Equal(16384, request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal("30m", request.RootElement.GetProperty("keep_alive").GetString());
        }
    }

    [Fact]
    public async Task AnOutputCeilingThatLeavesNoRoomForThePrompt_GivesWayToTheContextWindow()
    {
        // Prompt + output must fit num_ctx. Ollama silently drops the START of a prompt that does
        // not fit — the instructions — so the ceiling gives way and the chunker re-splits instead.
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance(), maxOutputTokens: "32768", numCtx: "16384");

        Assert.Equal(16384 - OllamaLlmService.MINIMUM_INPUT_RESERVE_TOKENS, service.MaxOutputTokens);
        Assert.NotNull(service.MaxDocumentInputTokens);
        Assert.True(service.MaxDocumentInputTokens < 16384 - service.MaxOutputTokens,
            "the document budget must leave room for the instructions as well");

        await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());
        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var options = request.RootElement.GetProperty("options");
        Assert.True(options.GetProperty("num_predict").GetInt32() < options.GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task TheDocumentReachesTheModelLineByLine_NotFlattenedIntoOneRunOnLine()
    {
        // Every line break used to be collapsed to a space before the model saw the text, which
        // re-glued the table the PDF reader had just laid out ("… BOARD 6 SAR" / "978596 EA 1").
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance());

        await service.ExtractLeadDataDetailedAsync(
            "00010 201195514 TRANSFORMER:STEP UP,400  TO 480VAC,40KVA\n2     Assembley\n\n\n\nnext", Context());

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var user = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("00010 201195514 TRANSFORMER:STEP UP,400 TO 480VAC,40KVA\n2 Assembley\n\nnext", user,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaterChunk_IsAskedForItsItemsOnly_WithoutTheHeaderSchema()
    {
        var handler = new RecordingHandler(_ => Reply("{\"Items\":[]}", doneReason: "stop", evalCount: 12));
        var service = CreateService(handler, new PermissiveGovernance());

        await service.ExtractLeadDataDetailedAsync("rows", new AiCallContext(
            1, AiPurposes.RfqExtraction, Guid.NewGuid().ToString("N"), AiPromptVersions.StructuredRfqItemsOnly));
        await service.ExtractLeadDataDetailedAsync("rows", new AiCallContext(
            1, AiPurposes.RfqExtraction, Guid.NewGuid().ToString("N"), AiPromptVersions.StructuredRfqExtraction));

        string System(int i)
        {
            using var request = JsonDocument.Parse(handler.RequestBodies[i]);
            return request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        }
        var itemsOnly = System(0);
        var full = System(1);
        Assert.DoesNotContain("\"RfqnoConfidence\"", itemsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("\"OverallConfidence\": number", itemsOnly, StringComparison.Ordinal);
        Assert.Contains("\"ItemMaterialCode\"", itemsOnly, StringComparison.Ordinal);
        Assert.Contains("LINE ITEMS ONLY", itemsOnly, StringComparison.Ordinal);
        Assert.Contains("\"RfqnoConfidence\"", full, StringComparison.Ordinal);
        Assert.True(itemsOnly.Length < full.Length);
    }

    // ---- quantity tolerance (ship-before-pilot ②) ------------------------
    // Two whole-document killers lived in the response handling, both candidate causes
    // of the "unparseable output" / "no result" dead-letters. KILLER 1: Quantity is
    // int? and the schema called it "number", so one line writing 2.0 (or "2") failed
    // the ENTIRE document's deserialization. KILLER 2: validation rejected the whole
    // extraction result when ANY single line carried a non-positive quantity. These
    // tests now pin the decimal contract: integral and fractional spellings parse exactly,
    // invalid/out-of-range shapes degrade that LINE to null (needs review), and only genuinely
    // document-level defects may still reject the document.

    [Theory]
    [InlineData("2")]      // the honest integer
    [InlineData("2.0")]    // the float the "number" schema used to invite
    [InlineData("2e0")]    // integral value in exponent clothing
    [InlineData("\"2\"")]  // quantity as a JSON string
    [InlineData("\"2.0\"")]
    public async Task IntegralQuantityInAnyDisguise_ParsesToTheInteger_AndTheDocumentSurvives(string token)
    {
        var handler = new RecordingHandler(_ => Reply(
            ItemsJson($"{{\"ProductShortName\":\"Contactor\",\"Quantity\":{token},\"ItemConfidence\":0.9}}"),
            doneReason: "stop", evalCount: 60));
        var governance = new PermissiveGovernance();
        var service = CreateService(handler, governance);

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 2", Context());

        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.Result);
        Assert.Equal(2m, Assert.Single(outcome.Result!.Items).Quantity);
        Assert.Null(governance.CompletedErrorCode);
    }

    [Fact]
    public async Task QuantityWithARealFraction_IsPreservedExactly()
    {
        // 2.5 truncated to 2 is a silent under-quote; rounding to 3 is an invention.
        var handler = new RecordingHandler(_ => Reply(
            ItemsJson("{\"ProductShortName\":\"Gasket\",\"Quantity\":2.5,\"ItemConfidence\":0.9}"),
            doneReason: "stop", evalCount: 60));
        var service = CreateService(handler, new PermissiveGovernance());

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 2.5", Context());

        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.Result);
        Assert.Equal(2.5m, Assert.Single(outcome.Result!.Items).Quantity);
    }

    [Fact]
    public async Task FractionalQuantityInAJsonString_IsPreservedExactly()
    {
        var handler = new RecordingHandler(_ => Reply(
            ItemsJson("{\"ProductShortName\":\"Gasket\",\"Quantity\":\"2.750125\",\"ItemConfidence\":0.9}"),
            doneReason: "stop", evalCount: 60));
        var service = CreateService(handler, new PermissiveGovernance());

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 2.750125", Context());

        Assert.Equal(2.750125m, Assert.Single(outcome.Result!.Items).Quantity);
    }

    [Theory]
    [InlineData("\"ten\"")]     // words are not quantities
    [InlineData("\"12,000\"")]  // group separators are locale-ambiguous ("2,5" is 2.5)
    [InlineData("100000000000000")] // beyond numeric(20,6)'s integral range
    [InlineData("2.1234567")]    // would require rounding beyond scale 6
    [InlineData("true")]
    [InlineData("{\"value\":2}")]
    [InlineData("[2]")]
    [InlineData("null")]
    public async Task UnusableQuantityToken_DegradesTheLineToNull_InsteadOfKillingTheDocument(string token)
    {
        var handler = new RecordingHandler(_ => Reply(
            ItemsJson($"{{\"ProductShortName\":\"Contactor\",\"Quantity\":{token},\"ItemConfidence\":0.9}}"),
            doneReason: "stop", evalCount: 60));
        var service = CreateService(handler, new PermissiveGovernance());

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1", Context());

        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.Result);
        Assert.Null(Assert.Single(outcome.Result!.Items).Quantity);
    }

    [Fact]
    public async Task OneZeroQuantityLine_IsQuarantinedToNull_WhileEveryOtherLineSurvives()
    {
        // KILLER 2. A 174-line production document must not die for one zero cell: the
        // bad LINE degrades to a null quantity (needs review) and the rest persist.
        var handler = new RecordingHandler(_ => Reply(ItemsJson(
                "{\"ProductShortName\":\"Breaker\",\"Quantity\":10,\"ItemConfidence\":0.9}",
                "{\"ProductShortName\":\"Contactor\",\"Quantity\":0,\"ItemConfidence\":0.8}",
                "{\"ProductShortName\":\"Relay\",\"Quantity\":-3,\"ItemConfidence\":0.7}",
                "{\"ProductShortName\":\"Fuse\",\"Quantity\":3,\"ItemConfidence\":0.9}"),
            doneReason: "stop", evalCount: 200));
        var governance = new PermissiveGovernance();
        var logger = new CapturingLogger<OllamaLlmService>();
        var service = CreateService(handler, governance, logger);

        var outcome = await service.ExtractLeadDataDetailedAsync("four lines", Context());

        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.Result);
        var items = outcome.Result!.Items;
        Assert.Equal(4, items.Count);
        Assert.Equal(10m, items[0].Quantity);
        Assert.Null(items[1].Quantity);
        Assert.Null(items[2].Quantity);
        Assert.Equal(3m, items[3].Quantity);
        Assert.Null(governance.CompletedErrorCode);

        // The quarantine is diagnosed — count and line positions, no document content.
        var diagnostic = Assert.Single(logger.Messages.Where(m => m.Contains("Quarantined")));
        Assert.Contains("2 of 4", diagnostic, StringComparison.Ordinal);
        Assert.Contains("2,3", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Contactor", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentLevelDefects_StillRejectTheWholeResult()
    {
        // The preserved other side: an envelope whose OverallConfidence is out of range
        // is untrustworthy evidence about every line, not a line problem — the whole
        // result is still refused as invalid_output.
        var handler = new RecordingHandler(_ => Reply(
            "{\"Rfqno\":\"RFQ-1\",\"OverallConfidence\":40,\"Items\":"
            + "[{\"ProductShortName\":\"Breaker\",\"Quantity\":10,\"ItemConfidence\":0.9}]}",
            doneReason: "stop", evalCount: 60));
        var service = CreateService(handler, new PermissiveGovernance());

        var outcome = await service.ExtractLeadDataDetailedAsync("PART-1 quantity 10", Context());

        Assert.Null(outcome.Result);
        Assert.Equal(AiErrorCodes.InvalidOutput, outcome.ErrorCode);
    }

    // ---- harness ---------------------------------------------------------

    /// <summary>A complete, well-formed extraction envelope around the given item objects.</summary>
    private static string ItemsJson(params string[] items)
        => $"{{\"Rfqno\":\"RFQ-1\",\"OverallConfidence\":0.9,\"Items\":[{string.Join(",", items)}]}}";

    private static OllamaLlmService CreateService(
        HttpMessageHandler handler, IAiGovernanceService governance,
        ILogger<OllamaLlmService>? logger = null, string? maxOutputTokens = null,
        string? numCtx = null, string? keepAlive = null)
        => new(new HttpClient(handler), logger ?? new CapturingLogger<OllamaLlmService>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://127.0.0.1:11434/",
                ["Ollama:Model"] = "test-model",
                ["Ollama:MaxOutputTokens"] = maxOutputTokens,
                ["Ollama:NumCtx"] = numCtx,
                ["Ollama:KeepAlive"] = keepAlive
            }).Build(), governance);

    private static AiCallContext Context()
        => new(1, AiPurposes.RfqExtraction, Guid.NewGuid().ToString("N"), "test-v1");

    private static AiCallContext BoqContext()
        => new(1, AiPurposes.BoqDraft, Guid.NewGuid().ToString("N"), "test-v1");

    private static HttpResponseMessage Reply(string content, string doneReason, long evalCount)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                message = new { role = "assistant", content },
                done = true,
                done_reason = doneReason,
                prompt_eval_count = 1200,
                eval_count = evalCount,
                total_duration = 9_000_000
            }), Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return response(request);
        }
    }

    private sealed class PermissiveGovernance : IAiGovernanceService
    {
        public List<AiAttemptCompletion> Attempts { get; } = new();
        public string? CompletedErrorCode { get; private set; }

        public Task<AiReservation> ReserveAsync(AiCallContext context, string provider, string model,
            string input, int maximumInputBytes, int maximumOutputTokens, int maximumAttempts, CancellationToken ct)
            => Task.FromResult(new AiReservation(Guid.NewGuid(), context.BusinessUnitId,
                maximumOutputTokens, AiGovernanceService.EstimateTokens(input.Length), 1));

        public Task RecordAttemptAsync(AiReservation reservation, AiAttemptCompletion attempt, CancellationToken ct)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(AiReservation reservation, string status, long inputTokens,
            long outputTokens, string tokenSource, string? output, string? errorCode, CancellationToken ct)
        {
            CompletedErrorCode = errorCode;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

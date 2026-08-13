using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Extraction;

public enum ExtractionOutcomeStatus
{
    /// <summary>All items accounted for and confidence acceptable.</summary>
    Ok,
    /// <summary>Persist, but flag for a human: a partial chunk failure, an empty extraction
    /// from a populated body, incomplete OCR, low confidence, or a structured import with
    /// validation issues.</summary>
    NeedsReview,
    /// <summary>Nothing usable was produced (every chunk failed / empty document).</summary>
    Failed
}

public enum ExtractionProcessingPath
{
    LegacyUnknown,
    NativeParser,
    DeterministicRules,
    LocalOcr,
    LocalModel,
    ExternalFallback
}

public enum ExtractionOcrStatus
{
    NotRequired,
    Completed,
    Partial,
    Failed
}

/// <summary>
/// Parsed, ready-to-extract view of ONE document. Produced by the document reader and
/// consumed by the extraction service. <see cref="LineItemRegions"/> carries the parsed
/// body text, one region per entry, and is what the LLM calls are chunked over. For
/// structured sources a region IS a row; for unstructured sources it is merely a text
/// line, so its count is a chunk-planning input, NOT an item count. Structured sources
/// also carry <see cref="StructuredRows"/> so they can bypass the LLM entirely via the
/// deterministic normalizer.
/// </summary>
public sealed class DocumentExtractionInput
{
    public long BusinessUnitId { get; init; }
    public string SourceDocumentName { get; init; } = "RFQ document";
    public string SourceId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The queue lease attempt (<see cref="ExtractionJob.Attempts"/>) this pass runs
    /// under. Monotonic for the life of the job — every claim increments it and
    /// dead-letter recovery extends MaxAttempts without ever resetting it — and it is
    /// baked into every AI idempotency key this pass issues. Without it a retry (lease
    /// reclaim, worker restart, manual re-drive) replayed the FIRST attempt's keys and
    /// the governance ledger refused every call as a duplicate before a single model
    /// call was made, so re-driving a document was guaranteed to fail. Within one
    /// attempt the keys are still deterministic, so a replay of the same
    /// (job, chunk, attempt) is still deduplicated.
    /// </summary>
    public int AttemptNumber { get; init; } = 1;

    public long? ExtractionJobId { get; init; }
    public long? SourceDocumentOccurrenceId { get; init; }
    public ExtractionProcessingPath ProcessingPath { get; init; } = ExtractionProcessingPath.NativeParser;
    public ExtractionOcrStatus OcrStatus { get; init; } = ExtractionOcrStatus.NotRequired;
    public int OcrPageCount { get; init; }
    public int PageCount { get; init; } = 1;
    public bool PageCountAuthoritative { get; init; }
    public bool OcrTruncated { get; init; }

    /// <summary>Header/context text extracted once (buyer, RFQ no, dates, terms).</summary>
    public string HeaderText { get; init; } = "";

    /// <summary>
    /// One entry per parsed body region. For STRUCTURED sources (spreadsheet/CSV) a region
    /// is a real row, so the count is a real item count. For UNSTRUCTURED sources the
    /// reader produces one region per non-empty TEXT LINE — several lines routinely form
    /// one real item (wrapped descriptions, section banners, footers), so the count is a
    /// chunking bound only and must never be treated as a ground-truth item count. It was
    /// once, and the resulting "Item count mismatch: expected 174, extracted 4"-style alarm
    /// fired on effectively every unstructured document while gating multi-inquiry
    /// auto-split off for all of them.
    /// </summary>
    public IReadOnlyList<string> LineItemRegions { get; init; } = Array.Empty<string>();

    /// <summary>True when the document is a structured spreadsheet/CSV and can skip the LLM.</summary>
    public bool IsStructured { get; init; }

    /// <summary>Deterministic-path rows (spreadsheet/CSV). Required when <see cref="IsStructured"/>.</summary>
    public IReadOnlyList<RfqSpreadsheetRow>? StructuredRows { get; init; }

    /// <summary>
    /// Honest, user-facing context set by the reader when a spreadsheet was READ
    /// successfully but its column layout was not recognized by the deterministic
    /// mapper, so the document fell back to the unstructured text path. The worker
    /// prefixes failure/hold reasons with it so operators see the document was
    /// readable and WHY an AI path (or a review hold) followed.
    /// </summary>
    public string? StructuredFallbackNote { get; init; }

    /// <summary>
    /// The document's own prose OUTSIDE the parsed table — the instruction block, the
    /// warranty and validity terms, the country-of-origin and Incoterms requirements, the
    /// submission method, and the "as per attached specification" that turns a line into a
    /// different quotation.
    ///
    /// <para>
    /// The structured input used to be built with an empty header and the line-item regions
    /// alone, so on the deterministic path this text was read and thrown away for every
    /// document — 120 of 120 in the pilot corpus, each of which ends with exactly such a
    /// block. The original file is retained as immutable evidence, so it was recoverable
    /// only by a human opening the source by hand.
    /// </para>
    /// <para>
    /// It is NOT parsed into fields here. Making it visible beside the extracted lines is
    /// most of the value, and inventing fields from prose is how a specification reference
    /// becomes a wrong commercial fact. It is also never sent to a model: the deterministic
    /// path makes no provider call at all.
    /// </para>
    /// </summary>
    public string? DocumentNarrative { get; init; }
}

public sealed class ChunkedExtractionOutcome
{
    public ExtractionOutcomeStatus Status { get; init; }
    public LeadExtractionResult? Result { get; init; }

    /// <summary>
    /// Diagnostic only. For structured sources this is the real row/item count; for
    /// unstructured sources it is the parsed text-REGION count (the chunk-planning bound),
    /// which legitimately exceeds the item count. It is never used to gate review on the
    /// unstructured path.
    /// </summary>
    public int ExpectedItemCount { get; init; }
    public int ExtractedItemCount { get; init; }
    public string? ReviewReason { get; init; }
    public List<string> Diagnostics { get; init; } = new();
    public AiProviderClass? AiProviderClass { get; init; }
    public ExtractionProcessingPath ProcessingPath { get; init; } = ExtractionProcessingPath.NativeParser;
    public ExtractionOcrStatus OcrStatus { get; init; } = ExtractionOcrStatus.NotRequired;
    public int OcrPageCount { get; init; }
    public int PageCount { get; init; } = 1;
    public bool PageCountAuthoritative { get; init; }
    public bool OcrTruncated { get; init; }

    /// <summary>
    /// Multi-inquiry auto-split (see <see cref="MultiInquirySplitter"/>): when the
    /// document verifiably contains N distinct inquiries, one result per inquiry group
    /// (2..MaxGroups entries, a strict partition of <see cref="Result"/>'s items). Null
    /// when the document is a single inquiry or the grouping was ambiguous/low-confidence
    /// (fall back to single-lead behavior). <see cref="Result"/> always stays populated
    /// with the merged view for consumers that don't split.
    /// </summary>
    public List<LeadExtractionResult>? SplitResults { get; init; }

    /// <summary>
    /// Authoritative deterministic representation for structured sources. Persistence
    /// consumes this graph to retain validation state and source-cell evidence instead
    /// of attempting to reconstruct it from the flattened commercial projection.
    /// </summary>
    public CanonicalRfqImportResult? CanonicalImport { get; init; }

    /// <summary>
    /// The document's own prose outside the parsed table, carried from
    /// <see cref="DocumentExtractionInput.DocumentNarrative"/> so persistence can retain it
    /// as evidence and the reviewer can read it beside the lines.
    /// </summary>
    public string? DocumentNarrative { get; init; }

    /// <summary>
    /// TRUE when retrying this document cannot change the answer, so the worker must
    /// dead-letter it on the first attempt instead of spending five attempts on
    /// exponential backoff against a deterministic refusal.
    ///
    /// <para>
    /// Set only where the cause is a decision rather than a condition: today, an external
    /// AI provider the tenant has not authorized. A timeout, a truncated response, a
    /// provider outage and a transient storage error are all still retryable — the default
    /// is false, so a new failure mode is retryable until someone proves otherwise.
    /// </para>
    /// </summary>
    public bool PermanentFailure { get; init; }
}

public interface IChunkedExtractionService
{
    /// <summary>
    /// Route a parsed document to the correct extractor: structured -> deterministic
    /// normalizer (no LLM); otherwise chunked map/reduce over the LLM.
    /// </summary>
    Task<ChunkedExtractionOutcome> ExtractAsync(DocumentExtractionInput input, CancellationToken ct = default);

    /// <summary>Chunked map/reduce extraction for unstructured documents (never truncates).</summary>
    Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(DocumentExtractionInput input, CancellationToken ct = default);

    /// <summary>
    /// Deterministic hook for structured spreadsheets/CSV: parses via
    /// <see cref="ICanonicalRfqNormalizer"/> with full per-field confidence + evidence and
    /// bypasses the LLM entirely. This is the single biggest throughput/cost lever.
    /// </summary>
    /// <param name="documentNarrative">
    /// The document's own prose outside the table, retained for the reviewer. Optional and
    /// null by default: the deterministic parse does not depend on it, and a caller that has
    /// none simply passes nothing.
    /// </param>
    Task<ChunkedExtractionOutcome> ExtractStructuredAsync(IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName, CancellationToken ct = default, string? documentNarrative = null);
}

/// <summary>
/// Chunked map/reduce extraction. Parsed text regions are split into bounded chunks —
/// sized first by the model's OUTPUT-token budget (<see cref="ExtractionOutputBudget"/>)
/// and then by the ~24k-character input budget, whichever binds first — each extracted
/// independently via <see cref="ILLMService"/>, then unioned in order.
///
/// Conservation is enforced where it is REAL, and only there:
///   * CHUNK level — a failed chunk's regions were never extracted; the document routes to
///     NeedsReview, never saved as "complete". A truncated chunk is halved and re-issued
///     (<see cref="AiErrorCodes.OutputTruncated"/>), never replayed unchanged, and a single
///     item that cannot fit fails honestly.
///   * STRUCTURED sources — rows are rows, so row-count conservation holds by construction
///     on the deterministic path.
/// What is deliberately NOT asserted: "extracted items == parsed text lines" on the
/// unstructured path. Text lines are not items (several lines form one item), so that
/// comparison flagged effectively every unstructured document with a false "Item count
/// mismatch" and thereby disabled multi-inquiry auto-split entirely.
/// Per-field confidence is preserved end-to-end.
/// </summary>
public sealed class ChunkedExtractionService : IChunkedExtractionService
{
    /// <summary>
    /// The refusal an unauthorized external provider still receives, unchanged. Kept as a
    /// constant so the fail-closed wording can never drift away from the contract tests
    /// that assert it.
    /// </summary>
    internal const string ExternalUnstructuredRefusal =
        "External processing is blocked for unstructured documents until a locally reduced, " +
        "redacted field/row payload is available; send this document to human review or " +
        "configure a local model.";

    /// <summary>
    /// The machine-readable marker that makes the refusal ABOVE reportable.
    ///
    /// <para>
    /// The gate is correct and unchanged — nothing here weakens it. What was broken is that
    /// its refusal was indistinguishable from a model timeout by the time it reached anyone:
    /// the tenant-facing category function matched on integrity / malware / unsupported /
    /// timeout / provider, and neither the refusal text nor any denial code matched a single
    /// one, so it surfaced as generic EXTRACTION_FAILURE with the underlying error stripped
    /// from the DTO. The marker is a closed token, carried in the stored failure reason and
    /// matched by <c>ExtractionDeadLetterService.ClassifyFailure</c>, so the refusal has one
    /// name from the extractor to the operator's screen.
    /// </para>
    /// </summary>
    internal const string AiNotAuthorizedCode = "EXTRACTION_AI_NOT_AUTHORIZED";

    /// <summary>
    /// What an operator must actually DO. A category alone is a diagnosis; this is the
    /// prescription, and it names the two switches rather than describing a state.
    /// </summary>
    internal const string AiNotAuthorizedOperatorAction =
        "This document needs AI reading, and external AI processing is not authorized for this "
        + "tenant. Nothing was sent to any provider. To process documents of this kind, either "
        + "authorize the configured inference endpoint for this tenant in the AI trust centre "
        + "(a platform owner must grant it; it is off by default and deliberately so), or point "
        + "the deployment at a local model on a loopback address. Until one of those is done, "
        + "only spreadsheets and Word documents whose lines are in a table can be read.";

    private readonly ILLMService _llm;
    private readonly ICanonicalRfqNormalizer _normalizer;
    private readonly ILogger<ChunkedExtractionService> _log;
    private readonly IAiExternalProviderTrust? _externalProviderTrust;
    private readonly ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? _metrics;

    // Chunk bounds. A chunk must satisfy ALL THREE constraints:
    //   1. OUTPUT-token budget (ExtractionOutputBudget) — the binding one in practice, and
    //      the one whose absence caused the 2026-08-05 outage. The extraction schema costs
    //      ~225 output tokens per line item, so 200 items would demand ~45,000 output
    //      tokens against a 4,096–8,192 ceiling: every real multi-line RFQ came back cut
    //      mid-JSON, unparseable, and the whole document dead-lettered.
    //   2. Character budget — keeps the chunk inside the model context and the request
    //      timeout, and bounds the blast radius of one failed chunk.
    //   3. This absolute item ceiling, unchanged.
    private const int MaxItemsPerChunk = 200;
    private const int MaxChunkChars = 24_000;
    private const int HeaderContextBudget = 6_000;
    private const double MinAcceptableConfidence = 0.60;

    /// <summary>
    /// Ceiling on provider calls per document while truncation-driven re-splitting is in
    /// play. Splitting already terminates on its own — every split strictly halves and a
    /// 1-item chunk is never split again, so the worst case is bounded by (2 × items) − 1
    /// calls — but the budget makes that guarantee explicit instead of emergent, and it is
    /// set ABOVE the worst legitimate case so it can only ever catch pathology, never a
    /// document that was going to finish.
    /// </summary>
    private static int TruncationCallBudget(int expectedItems, int plannedChunks)
        => (2 * Math.Max(1, expectedItems)) + Math.Max(1, plannedChunks);

    /// <param name="externalProviderTrust">
    /// Per-tenant external-provider allow-list. Optional, and its ABSENCE IS A REFUSAL:
    /// when the gate is not wired up, every external provider is refused exactly as
    /// before. There is no configuration, and no missing registration, that turns
    /// unstructured external processing on by accident.
    /// </param>
    public ChunkedExtractionService(
        ILLMService llm,
        ICanonicalRfqNormalizer normalizer,
        ILogger<ChunkedExtractionService> log,
        IAiExternalProviderTrust? externalProviderTrust = null,
        ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? metrics = null)
    {
        _llm = llm;
        _normalizer = normalizer;
        _log = log;
        _externalProviderTrust = externalProviderTrust;
        _metrics = metrics;
    }

    /// <summary>
    /// Emits nexora.llm.calls + nexora.llm.latency for exactly one provider call.
    /// Latency is measured around the call itself and is recorded on EVERY outcome —
    /// a refusal or a timeout is the outcome an operator most needs the latency for, and
    /// recording only successes is how a provider that has started failing slowly looks
    /// like a provider that has simply gone quiet.
    /// </summary>
    private void RecordLlmCall(long businessUnitId, long startTimestamp, string outcome)
    {
        if (_metrics is null) return;
        var descriptor = _llm.ProviderDescriptor;
        _metrics.LlmCall(
            System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            model: string.IsNullOrEmpty(descriptor.Model) ? null : descriptor.Model,
            businessUnitId: businessUnitId,
            provider: descriptor.Provider,
            outcome: outcome);
    }

    public Task<ChunkedExtractionOutcome> ExtractAsync(DocumentExtractionInput input, CancellationToken ct = default)
    {
        if (input.IsStructured && input.StructuredRows is { Count: > 0 })
            return ExtractStructuredAsync(input.StructuredRows, input.BusinessUnitId, input.SourceDocumentName, ct,
                input.DocumentNarrative);
        return ExtractUnstructuredAsync(input, ct);
    }

    public async Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(DocumentExtractionInput input, CancellationToken ct = default)
    {
        var expected = input.LineItemRegions.Count;
        var diagnostics = new List<string>();

        // ---- external-provider allow-list -----------------------------------
        // Unstructured extraction sends whole document text to the model, so this is the
        // single highest-risk egress in the product. It used to be governed by one global
        // bit: External => always refuse. That is not a governance position, it is an
        // outage with a comment. The decision is now a per-tenant, attributed, revocable
        // authorization for one exact endpoint (AI/AiExternalProviderTrustService.cs).
        // Everything not on that list is refused with the identical message and the same
        // zero bytes of egress; everything on it still goes through the unchanged token
        // ledger, budget caps, redaction, injection boundary and count conservation below.
        if (_llm.ProviderClass == AiProviderClass.External)
        {
            var descriptor = _llm.ProviderDescriptor;
            var decision = _externalProviderTrust is null
                ? AiExternalProviderDecision.Deny(
                    AiExternalProviderTrustReasons.GateUnavailable, descriptor)
                : await _externalProviderTrust.EvaluateAsync(
                    input.BusinessUnitId, descriptor, AiPurposes.RfqExtraction,
                    unstructuredPayload: true, ct);

            if (!decision.Allowed)
            {
                // A closed gate is a DETERMINISTIC answer, so this is reported as permanent
                // on the first attempt. It used to return a retryable failure, and the job
                // then re-asked the same closed gate five times on exponential backoff —
                // about an hour of pointless retries before dead-lettering, with a reason
                // nobody could act on when it got there. Fail closed on the processing;
                // never fail silently on the reporting.
                _log.LogError(
                    "Unstructured extraction REFUSED for tenant {Tenant}: external provider is not authorized. "
                    + "{Descriptor} reason={Reason} document={Document}. {Action}",
                    input.BusinessUnitId, descriptor, decision.Reason, input.SourceDocumentName,
                    AiNotAuthorizedOperatorAction);
                return Failed(
                    expected,
                    $"[{AiNotAuthorizedCode}] {ExternalUnstructuredRefusal} [denial: {decision.Reason}]",
                    input,
                    permanent: true);
            }

            // This line used to say ALLOWED, and it was wrong twice. It describes the
            // allow-list gate only: the governance reservation below re-tests the policy row's
            // AllowedProvider and AllowedModel in a different layer and can still kill the
            // document with provider_denied/model_denied — which triages as a generic
            // extraction failure, not an authorization one, because this gate was reached. It
            // also promised redaction, and nothing in this build redacts; a control named in a
            // log line but absent from the code is the one an auditor stops looking for. The
            // whole chain is now reportable up front: GET /api/platform/tenants/{id}/ai-readiness.
            _log.LogWarning(
                "Unstructured extraction PASSED THE ALLOW-LIST GATE for tenant {Tenant} under authorization "
                + "{AuthorizationId}. {Descriptor} document={Document}. Nothing has egressed yet: the governance "
                + "reservation still re-tests AllowedProvider (case-insensitive) and AllowedModel (ORDINAL, "
                + "case-sensitive) and can still refuse this document with provider_denied/model_denied. "
                + "Token budget, injection boundary and count conservation still apply; nothing in this build redacts.",
                input.BusinessUnitId, decision.AuthorizationId, descriptor, input.SourceDocumentName);
            diagnostics.Add(
                $"External provider authorized for unstructured extraction (authorization #{decision.AuthorizationId}, "
                + $"endpoint {decision.Endpoint}, model {decision.Model}). "
                + "The governance reservation has not run yet.");
        }

        if (expected == 0)
        {
            if (string.IsNullOrWhiteSpace(input.HeaderText))
                return Failed(0, "The local parser/OCR produced no readable content.", input);

            // No detected line-item rows: a single whole-document pass (header + any body).
            // There is nothing to chunk here — the parser found no rows to split on — so the
            // only correction available on truncation is an honest failure reason.
            // The key is scoped to the LEASE ATTEMPT so a retried job is a NEW governed
            // request rather than a replay of a refused one (see AttemptNumber).
            LlmExtractionOutcome wholeDocument;
            var wholeDocumentStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                wholeDocument = await _llm.ExtractLeadDataDetailedAsync(
                    Clip(input.HeaderText, MaxChunkChars),
                    new AiCallContext(input.BusinessUnitId, AiPurposes.RfqExtraction,
                        $"extraction:{input.SourceId}:a{input.AttemptNumber}:whole",
                        AiPromptVersions.StructuredRfqExtraction,
                        ExtractionJobId: input.ExtractionJobId,
                        SourceDocumentOccurrenceId: input.SourceDocumentOccurrenceId), ct);
                RecordLlmCall(input.BusinessUnitId, wholeDocumentStartedAt,
                    wholeDocument.Result is not null ? "ok"
                        : wholeDocument.OutputTruncated ? "output_truncated" : "no_result");
            }
            catch (AiPolicyDeniedException ex)
            {
                RecordLlmCall(input.BusinessUnitId, wholeDocumentStartedAt, "policy_denied");
                _log.LogWarning(ex,
                    "Whole-document extraction for {Document} was refused by AI governance ({Code}) "
                    + "before any model call.", input.SourceDocumentName, ex.Code);
                return Failed(0,
                    $"AI governance refused this request before any model call was made ({ex.Code}).",
                    input, diagnostics);
            }
            var single = wholeDocument.Result;
            if (single is null)
                return Failed(0, wholeDocument.OutputTruncated
                    ? $"The model ran out of output budget ({_llm.MaxOutputTokens} tokens) before it finished "
                      + "this document, and no line-item rows were detected to split it on."
                    : "LLM returned no result for the document.", input, diagnostics);
            var items0 = single.Items ?? new List<LeadItemData>();
            var incompleteOcr = input.OcrTruncated
                                || input.OcrStatus is ExtractionOcrStatus.Partial or ExtractionOcrStatus.Failed;
            var status0 = single.OverallConfidence is < MinAcceptableConfidence || incompleteOcr
                ? ExtractionOutcomeStatus.NeedsReview
                : ExtractionOutcomeStatus.Ok;

            // Multi-inquiry auto-split — only from a clean (Ok) extraction; an uncertain
            // document is never guess-split (it stays a single NeedsReview lead).
            List<LeadExtractionResult>? split0 = null;
            if (status0 == ExtractionOutcomeStatus.Ok)
            {
                split0 = MultiInquirySplitter.TrySplitByItemGroups(single);
                if (split0 != null)
                    diagnostics.Add($"Multi-inquiry document: split into {split0.Count} inquiry group(s).");
            }

            return new ChunkedExtractionOutcome
            {
                Status = status0,
                Result = single,
                ExpectedItemCount = items0.Count,
                ExtractedItemCount = items0.Count,
                ReviewReason = incompleteOcr
                    ? "OCR was incomplete; omitted content requires review."
                    : status0 == ExtractionOutcomeStatus.NeedsReview
                        ? "Overall confidence below threshold."
                        : null,
                Diagnostics = diagnostics,
                SplitResults = split0,
                AiProviderClass = _llm.ProviderClass,
                ProcessingPath = EffectivePath(input, _llm.ProviderClass),
                OcrStatus = input.OcrStatus,
                OcrPageCount = input.OcrPageCount,
                PageCount = input.PageCount,
                PageCountAuthoritative = input.PageCountAuthoritative,
                OcrTruncated = input.OcrTruncated
            };
        }

        var itemsPerChunk = ItemsPerChunk();
        var chunks = BuildChunks(input.LineItemRegions, itemsPerChunk);
        diagnostics.Add($"Document split into {chunks.Count} chunk(s) for {expected} line item(s).");
        _log.LogInformation(
            "Chunk plan for {Document}: {Chunks} chunk(s), {Expected} item(s), <={ItemsPerChunk} item(s) per chunk "
            + "(projected <={ProjectedTokens} output tokens against a {MaxOutputTokens}-token ceiling).",
            input.SourceDocumentName, chunks.Count, expected, itemsPerChunk,
            ExtractionOutputBudget.ProjectedOutputTokens(itemsPerChunk), _llm.MaxOutputTokens);

        var headerContext = Clip(input.HeaderText, HeaderContextBudget);
        var mergedItems = new List<LeadItemData>(expected);
        LeadExtractionResult? headerSource = null;
        var failedChunks = 0;

        // MAP: extract each chunk independently. A failed chunk is recorded (its items are
        // "missing" from the union) rather than silently dropped — the count assert catches it.
        //
        // `pending` starts as the planned chunks and may GROW: when the provider reports it
        // ran out of output budget (AiErrorCodes.OutputTruncated) the chunk is replaced
        // in-place by its two halves and reprocessed, preserving document order. The planned
        // size is derived from an ESTIMATE, so an unusually verbose document can still
        // overflow it; this is the honest correction, and it re-issues a SMALLER request
        // rather than replaying the identical failing one.
        var pending = new List<List<string>>(chunks);
        var attemptedCalls = 0;
        var callBudget = TruncationCallBudget(expected, chunks.Count);
        var governanceRefusalCodes = new List<string>();

        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pending[i];
            var prompt = BuildChunkText(headerContext, chunk);
            LlmExtractionOutcome outcome;
            var chunkStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                attemptedCalls++;
                // The key is scoped to the LEASE ATTEMPT (a{n}) so that a retried job is a
                // NEW governed request instead of a replay the ledger refuses as a
                // duplicate; within one attempt the (position, size) pair never repeats
                // (every re-split shrinks the chunk at its position), so a replay of the
                // same attempt still deduplicates exactly as it should.
                outcome = await _llm.ExtractLeadDataDetailedAsync(prompt,
                    new AiCallContext(input.BusinessUnitId, AiPurposes.RfqExtraction,
                        $"extraction:{input.SourceId}:a{input.AttemptNumber}:chunk:{i + 1}:{chunk.Count}",
                        AiPromptVersions.StructuredRfqExtraction,
                        ExtractionJobId: input.ExtractionJobId,
                        SourceDocumentOccurrenceId: input.SourceDocumentOccurrenceId,
                        ItemsInPayload: chunk.Count), ct);
                RecordLlmCall(input.BusinessUnitId, chunkStartedAt,
                    outcome.Result is not null ? "ok"
                        : outcome.OutputTruncated ? "output_truncated" : "no_result");
            }
            catch (OperationCanceledException)
            {
                throw; // lease loss / shutdown is not a chunk failure — never record it as one
            }
            catch (AiPolicyDeniedException ex)
            {
                RecordLlmCall(input.BusinessUnitId, chunkStartedAt, "policy_denied");
                // The governance ledger refused this request BEFORE any model call
                // (duplicate key, policy denial, budget ceiling). That is a different fact
                // from a model failure and it must stay legible all the way to the
                // dead-letter row — this exact refusal used to be flattened into
                // attempts_exhausted and then into "All chunks failed", which is how a job
                // whose 12 calls all succeeded dead-lettered as a model problem.
                failedChunks++;
                governanceRefusalCodes.Add(ex.Code);
                diagnostics.Add(
                    $"Chunk {i + 1}/{pending.Count} refused by AI governance before any model call "
                    + $"({ex.Code}); {chunk.Count} item(s) not extracted.");
                _log.LogWarning(ex,
                    "Chunk {Index}/{Total} for {Document} was refused by AI governance ({Code}).",
                    i + 1, pending.Count, input.SourceDocumentName, ex.Code);
                continue;
            }
            catch (Exception ex)
            {
                RecordLlmCall(input.BusinessUnitId, chunkStartedAt, "error");
                _log.LogWarning(ex, "Chunk {Index}/{Total} extraction threw.", i + 1, pending.Count);
                outcome = new LlmExtractionOutcome(null, AiErrorCodes.AttemptsExhausted);
            }

            if (outcome.Result is null && outcome.OutputTruncated)
            {
                // A single line item is indivisible. If even that overflows the ceiling,
                // fail THAT item honestly — never loop, never silently drop it.
                if (chunk.Count <= 1)
                {
                    failedChunks++;
                    var reason =
                        $"Chunk {i + 1}/{pending.Count} failed: one line item alone exceeds the model's "
                        + $"{_llm.MaxOutputTokens}-token output budget (1 item not extracted).";
                    diagnostics.Add(reason);
                    _log.LogWarning(
                        "Single line item exceeded the output budget for {Document}; failing the item rather "
                        + "than retrying. Code={Code}.", input.SourceDocumentName,
                        AiErrorCodes.SingleItemExceedsOutputBudget);
                    continue;
                }

                if (attemptedCalls >= callBudget)
                {
                    failedChunks++;
                    diagnostics.Add(
                        $"Chunk {i + 1}/{pending.Count} failed: output truncated and the re-split budget "
                        + $"is exhausted ({chunk.Count} item(s) not extracted).");
                    continue;
                }

                var half = chunk.Count / 2;
                pending[i] = chunk.Skip(half).ToList();
                pending.Insert(i, chunk.Take(half).ToList());
                diagnostics.Add(
                    $"Chunk {i + 1} output was truncated at {chunk.Count} item(s); retrying as "
                    + $"{half} + {chunk.Count - half} item(s).");
                _log.LogWarning(
                    "Output truncated for {Document} chunk {Index} at {Items} item(s); halving and retrying "
                    + "({First} + {Second}).", input.SourceDocumentName, i + 1, chunk.Count,
                    half, chunk.Count - half);
                i--; // reprocess this position, which now holds the first half
                continue;
            }

            if (outcome.Result is null)
            {
                failedChunks++;
                diagnostics.Add(
                    $"Chunk {i + 1}/{pending.Count} failed ({chunk.Count} item(s) not extracted)."
                    + (outcome.ErrorCode is null ? "" : $" [{outcome.ErrorCode}]"));
                continue;
            }

            headerSource ??= outcome.Result; // header fields come from the first successful chunk
            if (outcome.Result.Items is { Count: > 0 })
                mergedItems.AddRange(outcome.Result.Items); // REDUCE: union in order
        }

        if (headerSource is null)
        {
            // Tell the truth about WHICH layer stopped the document. "All chunks failed"
            // is only honest when the model was actually asked and could not answer.
            var refusalCodes = string.Join(", ", governanceRefusalCodes.Distinct());
            var reason = governanceRefusalCodes.Count == failedChunks && failedChunks > 0
                ? $"AI governance refused every request before any model call was made ({refusalCodes}). "
                  + "No chunk was extracted."
                : governanceRefusalCodes.Count > 0
                    ? $"All chunks failed; no data extracted ({governanceRefusalCodes.Count} of {failedChunks} "
                      + $"chunk(s) were refused by AI governance: {refusalCodes})."
                    : "All chunks failed; no data extracted.";
            return Failed(expected, reason, input, diagnostics);
        }

        // Merge accounting. `expected` counts parsed text REGIONS (lines), not items: on
        // an unstructured document several lines routinely form ONE real item (wrapped
        // descriptions, section banners, footers), so `extracted != expected` is not
        // evidence of loss and must not flag review. It used to — stamping a false
        // "Item count mismatch: expected N, extracted M" on effectively every unstructured
        // document (production example: a 6-item SEC bid flagged as "expected 174") and,
        // because only an Ok outcome may auto-split, silently disabling multi-inquiry
        // splitting for all of them. The conservation that IS real stays below: a FAILED
        // chunk's regions were genuinely never extracted, incomplete OCR genuinely omitted
        // content, and a populated body that produced ZERO items is a real signal on any
        // document. Row-level conservation lives on the structured path, where rows are rows.
        var extracted = mergedItems.Count;
        var overall = ComputeOverallConfidence(headerSource, mergedItems);
        var merged = WithItems(headerSource, mergedItems, overall);
        diagnostics.Add($"Extracted {extracted} item(s) from {expected} parsed text region(s).");

        string? reviewReason = null;
        if (input.OcrTruncated || input.OcrStatus is ExtractionOcrStatus.Partial or ExtractionOcrStatus.Failed)
            reviewReason = "OCR was incomplete; omitted content requires review.";
        else if (failedChunks > 0)
            reviewReason = $"{failedChunks} chunk(s) failed to extract.";
        else if (extracted == 0)
            reviewReason = $"No line items were extracted from {expected} parsed text region(s).";
        else if (overall < MinAcceptableConfidence)
            reviewReason = $"Overall confidence {overall:F2} below threshold {MinAcceptableConfidence:F2}.";

        var status = reviewReason is null ? ExtractionOutcomeStatus.Ok : ExtractionOutcomeStatus.NeedsReview;
        if (reviewReason is not null) diagnostics.Add(reviewReason);

        // Multi-inquiry auto-split — only when the extraction is fully clean (all chunks
        // succeeded, OCR complete, items present, confidence acceptable). A NeedsReview
        // document is never guess-split; it keeps today's single flagged lead. The split
        // decides on its OWN signals (InquiryGroup labels + grouping confidence, see
        // MultiInquirySplitter) — never on the text-line count.
        List<LeadExtractionResult>? splitResults = null;
        if (status == ExtractionOutcomeStatus.Ok)
        {
            splitResults = MultiInquirySplitter.TrySplitByItemGroups(merged);
            if (splitResults != null)
                diagnostics.Add($"Multi-inquiry document: split into {splitResults.Count} inquiry group(s).");
        }

        return new ChunkedExtractionOutcome
        {
            Status = status,
            Result = merged,
            ExpectedItemCount = expected,
            ExtractedItemCount = extracted,
            ReviewReason = reviewReason,
            Diagnostics = diagnostics,
            SplitResults = splitResults,
            AiProviderClass = _llm.ProviderClass,
            ProcessingPath = EffectivePath(input, _llm.ProviderClass),
            OcrStatus = input.OcrStatus,
            OcrPageCount = input.OcrPageCount,
            PageCount = input.PageCount,
            PageCountAuthoritative = input.PageCountAuthoritative,
            OcrTruncated = input.OcrTruncated
        };
    }

    public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
        IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName, CancellationToken ct = default,
        string? documentNarrative = null)
    {
        // Deterministic parse — runs in milliseconds for 10k rows, no LLM call.
        var import = _normalizer.NormalizeSpreadsheetRows(rows, businessUnitId);
        var diagnostics = new List<string>
        {
            $"Structured parse produced {import.Documents.Count} RFQ group(s) from {rows.Count} row(s)."
        };

        var allItems = import.Documents.SelectMany(d => d.LineItems).ToList();
        var expected = allItems.Count;

        if (expected == 0)
            return Task.FromResult(Failed(0, "Structured file contained no valid line items."));

        var primary = import.Documents.First();
        var items = allItems.Select(MapCanonicalItem).ToList();
        var overall = ComputeOverallConfidence(items, header: primary);
        var result = BuildStructuredResult(primary, items, overall);

        var anyNeedsReview = import.Documents.Any(d => d.ValidationStatus != ValidationStatus.Valid)
                             || allItems.Any(i => i.ValidationStatus != ValidationStatus.Valid);

        string? reviewReason = null;
        List<LeadExtractionResult>? splitResults = null;
        if (import.Documents.Count > 1)
        {
            // Multi-inquiry auto-split: only clean (fully valid, confident), fully
            // identified groups are split into per-inquiry results. Anything ambiguous
            // keeps the previous single-lead + NeedsReview behavior — never guess-split.
            if (!anyNeedsReview
                && overall >= MinAcceptableConfidence
                && MultiInquirySplitter.ShouldSplitStructured(import.Documents))
            {
                splitResults = import.Documents
                    .Select(d =>
                    {
                        var groupItems = d.LineItems.Select(MapCanonicalItem).ToList();
                        return BuildStructuredResult(d, groupItems, ComputeOverallConfidence(groupItems, d));
                    })
                    .ToList();
                diagnostics.Add($"Multi-inquiry document: split into {splitResults.Count} RFQ group(s).");
            }
            else
            {
                reviewReason = $"File contains {import.Documents.Count} distinct RFQ groups; review before splitting.";
            }
        }
        else if (anyNeedsReview)
            reviewReason = DescribeCanonicalReview(import);
        else if (overall < MinAcceptableConfidence)
            reviewReason = $"Overall confidence {overall:F2} below threshold.";

        var status = reviewReason is null ? ExtractionOutcomeStatus.Ok : ExtractionOutcomeStatus.NeedsReview;
        if (reviewReason is not null) diagnostics.Add(reviewReason);

        return Task.FromResult(new ChunkedExtractionOutcome
        {
            Status = status,
            Result = result,
            ExpectedItemCount = expected,
            ExtractedItemCount = items.Count,
            ReviewReason = reviewReason,
            Diagnostics = diagnostics,
            SplitResults = splitResults,
            CanonicalImport = import,
            DocumentNarrative = documentNarrative,
            ProcessingPath = ExtractionProcessingPath.DeterministicRules
        });
    }

    // ---- chunking --------------------------------------------------------

    /// <summary>
    /// Items per chunk for the currently bound provider: the OUTPUT-token budget and the
    /// absolute ceiling, whichever is smaller.
    /// </summary>
    internal int ItemsPerChunk()
        => Math.Min(MaxItemsPerChunk, ExtractionOutputBudget.MaxItemsPerChunk(_llm.MaxOutputTokens));

    internal static List<List<string>> BuildChunks(IReadOnlyList<string> regions, int maxItemsPerChunk)
    {
        var itemCap = Math.Clamp(maxItemsPerChunk, 1, MaxItemsPerChunk);
        var chunks = new List<List<string>>();
        var current = new List<string>();
        var currentChars = 0;

        foreach (var region in regions)
        {
            var len = region?.Length ?? 0;
            if (current.Count > 0 && (current.Count >= itemCap || currentChars + len > MaxChunkChars))
            {
                chunks.Add(current);
                current = new List<string>();
                currentChars = 0;
            }
            current.Add(region ?? "");
            currentChars += len;
        }
        if (current.Count > 0)
            chunks.Add(current);
        return chunks;
    }

    private static string BuildChunkText(string headerContext, List<string> regions)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(headerContext))
            sb.Append("[DOCUMENT HEADER / CONTEXT]\n").Append(headerContext).Append("\n\n");
        sb.Append("[LINE ITEMS — extract EVERY item below, do not skip any]\n");
        for (var i = 0; i < regions.Count; i++)
            sb.Append(regions[i]).Append('\n');
        return sb.ToString();
    }

    private static string Clip(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max));

    // ---- confidence + merge ---------------------------------------------

    private static double ComputeOverallConfidence(LeadExtractionResult header, List<LeadItemData> items)
    {
        var headerConf = new[]
        {
            header.RfqnoConfidence, header.BuyersNameConfidence, header.RecDateConfidence,
            header.BidClosingDateConfidence
        }.Where(c => c.HasValue).Select(c => c!.Value).DefaultIfEmpty(0).Average();
        var itemConf = items.Select(i => i.ItemConfidence ?? 0).DefaultIfEmpty(0).Average();
        return items.Count > 0 ? (headerConf * 0.4) + (itemConf * 0.6) : headerConf;
    }

    /// <summary>
    /// Names WHICH fields need review, instead of "one or more fields need review (see
    /// canonical validation issues)" — a sentence that pointed at a ledger the reviewer
    /// cannot open and that read identically whether one closing date was unstated or every
    /// line was unreadable. Bounded: distinct messages only, first three, then a count.
    /// </summary>
    private static string DescribeCanonicalReview(CanonicalRfqImportResult import)
    {
        var reasons = import.Documents
            .SelectMany(d => d.Issues)
            .Where(i => i.Severity != ValidationSeverity.Info)
            .Select(i => i.Message)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var linesNeedingReview = import.Documents
            .SelectMany(d => d.LineItems)
            .Count(l => l.ValidationStatus != ValidationStatus.Valid);
        if (linesNeedingReview > 0)
        {
            var totalLines = import.Documents.Sum(d => d.LineItems.Count);
            reasons.Add($"{linesNeedingReview} of {totalLines} line(s) have a field the document states elsewhere but not here.");
        }

        if (reasons.Count == 0)
            return "One or more fields need review.";

        var head = string.Join(" ", reasons.Take(3));
        return reasons.Count > 3 ? $"{head} (+{reasons.Count - 3} more)" : head;
    }

    /// <summary>
    /// Deterministic-path document confidence. Same rule as
    /// <see cref="AverageConfidence(CanonicalRfqLineItem)"/>: a header field the document
    /// states nowhere is excluded, because confidence is a reading measure and there was
    /// nothing there to read. It is NOT excused from review — the closing-date issue in
    /// <c>CanonicalRfqNormalizer.CollectValueIssues</c> still fires, because a date the buyer
    /// normally states is a commercial gap a human must resolve even when we read the
    /// document perfectly.
    /// </summary>
    private static double ComputeOverallConfidence(List<LeadItemData> items, CanonicalRfqDocument header)
    {
        var headerConf = Stated(
            (header.RfqNo.StatedInDocument, header.RfqNo.Confidence),
            (header.BuyerName.StatedInDocument, header.BuyerName.Confidence),
            (header.ReceivedDate.StatedInDocument, header.ReceivedDate.Confidence),
            (header.BidClosingDate.StatedInDocument, header.BidClosingDate.Confidence));
        var itemConf = items.Select(i => i.ItemConfidence ?? 0).DefaultIfEmpty(0).Average();
        return items.Count > 0 ? (headerConf * 0.4) + (itemConf * 0.6) : headerConf;
    }

    /// <summary>
    /// Re-attaches the merged item list to the document header.
    ///
    /// PRE-EXISTING DEFECT FIXED HERE: this reconstruction was positional and stopped at
    /// <c>items</c>, so every header field declared AFTER it on
    /// <see cref="LeadExtractionResult"/> was silently dropped on every chunked document —
    /// InquiryType/InquiryTypeConfidence have been lost this way since the BOQ work landed.
    /// Chunking starts at 11 items and SEC bids are 12+ lines, so this hit exactly the
    /// documents that matter most. Using a <c>with</c> expression instead of a positional
    /// call makes the same mistake impossible for every field added from now on.
    /// </summary>
    private static LeadExtractionResult WithItems(LeadExtractionResult header, List<LeadItemData> items, double overall)
        => header with { OverallConfidence = overall, Items = items };

    /// <summary>
    /// The deterministic spreadsheet/CSV path. It produces no document-level classification
    /// and no client-organisation evidence: a structured price sheet carries neither a
    /// narrative nor a vendor block. Those fields stay null on purpose — an invented client
    /// is worse than an unresolved one.
    /// </summary>
    private static LeadExtractionResult BuildStructuredResult(CanonicalRfqDocument doc, List<LeadItemData> items, double overall)
        => new(
            doc.RfqNo.Value, (double)doc.RfqNo.Confidence,
            doc.BuyerName.Value, (double)doc.BuyerName.Confidence,
            // Kind-guarded for the same reason as RequiredDeliveryDate below and UnitPrice in
            // MapCanonicalItem: CanonicalValue<DateTime>.Value is a non-nullable struct, so a date
            // the document never stated formatted as the sentinel "0001-01-01" and was emitted as
            // though it were read. SanitizeDate happened to discard it downstream; nothing here
            // depended on that, and a diagnostic printing the contract showed a date that exists
            // in no document.
            doc.ReceivedDate.Kind == CanonicalValueKind.Normalized ? FormatDate(doc.ReceivedDate.Value) : null,
            (double)doc.ReceivedDate.Confidence,
            doc.BidClosingDate.Kind == CanonicalValueKind.Normalized ? FormatDate(doc.BidClosingDate.Value) : null,
            (double)doc.BidClosingDate.Confidence,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            overall,
            items,
            // Everything between `items` and the intake block below is a trailing optional
            // parameter, so these are passed by name rather than padding a dozen nulls.
            DeliveryLocation: doc.DeliveryLocation.Value,
            DeliveryLocationConfidence: (double)doc.DeliveryLocation.Confidence,
            RequiredDeliveryDate: doc.RequiredDeliveryDate.Kind == CanonicalValueKind.Normalized
                ? FormatDate(doc.RequiredDeliveryDate.Value) : null,
            RequiredDeliveryDateConfidence: (double)doc.RequiredDeliveryDate.Confidence,
            AgreementReference: doc.AgreementReference.Value,
            AgreementReferenceConfidence: (double)doc.AgreementReference.Confidence);

    private static LeadItemData MapCanonicalItem(CanonicalRfqLineItem line)
        => new(
            CompanyRef: null, CompanyRefConfidence: 0,
            CustomerAccountPortalId: null, CustomerAccountPortalIdConfidence: 0,
            CustomerRfqno: null, CustomerRfqnoConfidence: 0,
            ItemMaterialCode: null, ItemMaterialCodeConfidence: 0,
            CommodityProduct: null, CommodityProductConfidence: 0,
            BuyerName: null, BuyerNameConfidence: 0,
            LineItemNo: line.LineItemNo.Value, LineItemNoConfidence: (double)line.LineItemNo.Confidence,
            ProductShortName: line.ProductName.Value, ProductShortNameConfidence: (double)line.ProductName.Confidence,
            Alternative: null, AlternativeConfidence: 0,
            ProductShortDescription: null, ProductShortDescriptionConfidence: 0,
            Currency: line.Currency.Value, CurrencyConfidence: (double)line.Currency.Confidence,
            UnitOfMeasure: line.UnitOfMeasure.Value, UnitOfMeasureConfidence: (double)line.UnitOfMeasure.Confidence,
            // Only a value the normalizer actually PARSED is emitted. Testing "zero with zero
            // confidence" let an UNPARSEABLE value through as a real 0, because a failed parse
            // still leaves the struct at its default while carrying a non-zero confidence.
            UnitPrice: line.UnitPrice.Kind == CanonicalValueKind.Normalized ? line.UnitPrice.Value : null,
            UnitPriceConfidence: (double)line.UnitPrice.Confidence,
            // Same rule as UnitPrice above, and it was the one field here that did not carry it.
            // A failed quantity parse leaves the struct at 0, so "unreadable" left this method as
            // the number 0 and was quoted to the customer as an order for nothing. A quantity we
            // could not read is null, the line routes to a human, and nothing downstream is told
            // a number the document never stated.
            Quantity: line.Quantity.Kind == CanonicalValueKind.Normalized ? line.Quantity.Value : null,
            QuantityConfidence: (double)line.Quantity.Confidence,
            StorageLocation: null, StorageLocationConfidence: 0,
            ManufacturerName: line.ManufacturerName.Value, ManufacturerNameConfidence: (double)line.ManufacturerName.Confidence,
            ManufacturerPartNumber: line.ManufacturerPartNumber.Value, ManufacturerPartNumberConfidence: (double)line.ManufacturerPartNumber.Confidence,
            AlternateProductName: null, AlternateProductNameConfidence: 0,
            AlternatePartNumber: null, AlternatePartNumberConfidence: 0,
            ItemText: line.ItemText.Value, ItemTextConfidence: (double)line.ItemText.Confidence,
            MaterialPotext: null, MaterialPotextConfidence: 0,
            // Same rule as UnitPrice. A lead time of 0 means "deliver immediately"; emitting it
            // for a value we could not read is a false commercial fact, not a harmless default.
            LeadTime: line.LeadTimeDays.Kind == CanonicalValueKind.Normalized
                ? line.LeadTimeDays.Value.ToString(CultureInfo.InvariantCulture) : null,
            LeadTimeConfidence: (double)line.LeadTimeDays.Confidence,
            ReceivedDate: null, ReceivedDateConfidence: 0,
            BidClosingDateLine: null, BidClosingDateLineConfidence: 0,
            ItemConfidence: AverageConfidence(line));

    /// <summary>
    /// Confidence in how well this line was READ, over the fields the document actually
    /// asserts. A field the document states nowhere (<see cref="CanonicalValue{T}.StatedInDocument"/>)
    /// contributes nothing: there was nothing there to read, so averaging its zero in
    /// measures the document's shape rather than our accuracy. On an inbound RFQ that is
    /// five of these seven fields — price, currency, brand, part number, lead time are what
    /// the supplier is being ASKED for — which held every document in the 120-document pilot
    /// corpus at 0.557, under the 0.60 acceptance threshold, with all 641 lines read
    /// byte-perfectly.
    ///
    /// <para>A field whose source text WAS present and could not be parsed stays Stated and
    /// keeps its 0.2, so a genuine misread still drags the number down.</para>
    /// </summary>
    private static double AverageConfidence(CanonicalRfqLineItem line)
        => Stated(
            (line.ProductName.StatedInDocument, line.ProductName.Confidence),
            (line.Quantity.StatedInDocument, line.Quantity.Confidence),
            (line.UnitPrice.StatedInDocument, line.UnitPrice.Confidence),
            (line.Currency.StatedInDocument, line.Currency.Confidence),
            (line.ManufacturerName.StatedInDocument, line.ManufacturerName.Confidence),
            (line.ManufacturerPartNumber.StatedInDocument, line.ManufacturerPartNumber.Confidence),
            (line.LeadTimeDays.StatedInDocument, line.LeadTimeDays.Confidence));

    /// <summary>
    /// Averages the confidences of the STATED members only. An all-unstated set averages to
    /// 0 rather than throwing — it cannot occur on a real line (product name and quantity are
    /// required), and a silent exception here would take out the whole document.
    /// </summary>
    private static double Stated(params (bool StatedInDocument, decimal Confidence)[] values)
        => values.Where(v => v.StatedInDocument).Select(v => (double)v.Confidence)
            .DefaultIfEmpty(0).Average();

    /// <summary>
    /// Renders a canonical date for the string-typed extraction contract, KEEPING a stated time of
    /// day.
    ///
    /// <para>FR-RFQ-04 requires the bid closing date and its time. The normalizer reads
    /// "2026-09-01 14:00" correctly, and this method used to render it "2026-09-01" — so the
    /// deadline reached the lead as midnight and a tender closing at 14:00 showed as a whole-day
    /// deadline. A quote submitted at 15:00 looked on time and was late.</para>
    ///
    /// <para>The round-trip form is one <c>RfqDateParser</c> already accepts, so the worker that
    /// re-reads this string recovers the same instant. A midnight value still renders date-only,
    /// so nothing that never stated a time changes shape.</para>
    /// </summary>
    private static string? FormatDate(DateTime? value)
        => value is not { } date
            ? null
            : date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static ExtractionProcessingPath EffectivePath(DocumentExtractionInput input, AiProviderClass providerClass)
        => providerClass switch
        {
            AiProviderClass.External => ExtractionProcessingPath.ExternalFallback,
            _ => input.ProcessingPath
        };

    /// <param name="diagnostics">
    /// The per-chunk diagnostics collected before the failure. They used to be DISCARDED
    /// here — the outcome carried only the flattened reason, so the dead-letter LastError
    /// could not say which chunk failed at which stage or why. The reason is appended so
    /// consumers that read only <see cref="ChunkedExtractionOutcome.Diagnostics"/> still
    /// see it.
    /// </param>
    private static ChunkedExtractionOutcome Failed(
        int expected, string reason, DocumentExtractionInput? input = null, List<string>? diagnostics = null,
        bool permanent = false)
    {
        diagnostics ??= new List<string>();
        if (!diagnostics.Contains(reason))
            diagnostics.Add(reason);
        return new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Failed,
            PermanentFailure = permanent,
            Result = null,
            ExpectedItemCount = expected,
            ExtractedItemCount = 0,
            ReviewReason = reason,
            Diagnostics = diagnostics,
            ProcessingPath = input?.ProcessingPath ?? ExtractionProcessingPath.NativeParser,
            OcrStatus = input?.OcrStatus ?? ExtractionOcrStatus.NotRequired,
            OcrPageCount = input?.OcrPageCount ?? 0,
            PageCount = input?.PageCount ?? 0,
            PageCountAuthoritative = input?.PageCountAuthoritative ?? false,
            OcrTruncated = input?.OcrTruncated ?? false
        };
    }
}

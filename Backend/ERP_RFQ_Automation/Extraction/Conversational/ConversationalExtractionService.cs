using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Extraction.Conversational;

public interface IConversationalExtractionService
{
    /// <summary>
    /// Extracts a lead from ONE conversational email body.
    /// </summary>
    /// <param name="threadContinuation">
    /// True when the source message was a reply/forward. A continuation is never returned as
    /// a clean Ok: the request it restates may already be in the system, and a silent
    /// duplicate lead is worse than a flagged one.
    /// </param>
    Task<ChunkedExtractionOutcome> ExtractAsync(
        DocumentExtractionInput input, bool threadContinuation = false, CancellationToken ct = default);
}

/// <summary>
/// The email-body extraction path: free prose in, the SAME <see cref="LeadExtractionResult"/>
/// out, so the normalizer, the persister and every downstream consumer are untouched.
///
/// THREE DELIBERATE DIFFERENCES FROM THE DOCUMENT PATH:
/// <list type="number">
/// <item>It uses <see cref="ConversationalPrompt"/> — a strict subset of the same JSON keys —
/// because the structured prompt cannot describe an email (see that file for the six
/// specific reasons).</item>
/// <item>It DOES NOT GATE ON <c>OverallConfidence</c>. That number is self-reported by the
/// model and is not a measurement; refusing a real enquiry because a model said 0.55 about
/// its own work is not a quality control, it is a coin toss with a lost deal on one side.</item>
/// <item>It gates on ANCHORS instead (<see cref="ProseAnchorVerifier"/>): every item must
/// quote the message verbatim. That is a fact, not a self-assessment.</item>
/// </list>
///
/// Failure posture: an email that yields nothing is <c>NeedsReview</c>, never <c>Failed</c> —
/// there is nothing to retry about a message that simply asked us to call back, and
/// dead-lettering it would hide it from the humans who can judge it.
/// </summary>
public sealed class ConversationalExtractionService : IConversationalExtractionService
{
    private readonly ILLMService _llm;
    private readonly ILogger<ConversationalExtractionService> _log;
    private readonly IAiExternalProviderTrust? _externalProviderTrust;

    /// <summary>Input ceiling for one message. Email bodies are small; anything past this is a
    /// pasted document, and the overflow is reported rather than silently dropped.</summary>
    private const int MaxProseChars = 24_000;

    /// <summary>
    /// THE ONE SENTENCE THAT MEANS "the whole message was read and it asks for nothing".
    ///
    /// <para>A named constant rather than a literal because a DOWNSTREAM DECISION now keys on
    /// it: <c>EmailInquiryLeadAssembler</c> may only tell an operator "read in full, asked for
    /// nothing" about a part whose extractor said exactly this. Left as a literal in two
    /// places, the two would drift, and the drift would be silent and in the dangerous
    /// direction — a message we failed to read being reported as a message that asked for
    /// nothing.</para>
    ///
    /// <para>It is emitted ONLY when the entire submitted body reached the model. A truncated
    /// body gets <see cref="TruncatedBodyFoundNothingReviewReason"/> instead, because "we read
    /// all of it and found nothing" and "we read the first 24,000 characters and found nothing"
    /// are different facts and only one of them justifies closing the subject.</para>
    /// </summary>
    public const string NoRequestableItemsReviewReason =
        "No requestable items found in message body.";

    /// <summary>
    /// The same empty result, reached on a body that did NOT reach the model in full.
    ///
    /// <para>This case used to be invisible. The review-reason chain below is an if/else, the
    /// zero-item branch is first, and the truncation branch is last — so a 30,000-character
    /// enquiry clipped to 24,000 and returning no items reported the clean sentence, and the
    /// fact that 6,000 characters were never looked at was dropped on the floor. The clipped
    /// tail is exactly where a long covering letter puts its schedule.</para>
    /// </summary>
    public const string TruncatedBodyFoundNothingReviewReason =
        "The message body was too long to submit in full, and the part that was read names no "
        + "requestable item. The omitted content requires review.";

    /// <param name="externalProviderTrust">
    /// Per-tenant external-provider allow-list. Optional, and its ABSENCE IS A REFUSAL —
    /// identical to the unstructured document path. An email body is untruncated customer
    /// correspondence; it never leaves for an unauthorized endpoint.
    /// </param>
    public ConversationalExtractionService(
        ILLMService llm,
        ILogger<ConversationalExtractionService> log,
        IAiExternalProviderTrust? externalProviderTrust = null)
    {
        _llm = llm;
        _log = log;
        _externalProviderTrust = externalProviderTrust;
    }

    public async Task<ChunkedExtractionOutcome> ExtractAsync(
        DocumentExtractionInput input, bool threadContinuation = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagnostics = new List<string>();

        var submitted = ComposeSubmittedText(input);
        if (string.IsNullOrWhiteSpace(submitted))
            return Failed(input, "The message body carried no readable text.");

        if (submitted.Length > MaxProseChars)
        {
            diagnostics.Add(
                $"Message body truncated from {submitted.Length} to {MaxProseChars} characters for extraction.");
            submitted = submitted[..MaxProseChars];
        }
        var truncatedInput = diagnostics.Count > 0;

        // ---- external-provider allow-list (same gate as unstructured documents) ----
        if (_llm.ProviderClass == AiProviderClass.External)
        {
            var descriptor = _llm.ProviderDescriptor;
            var decision = _externalProviderTrust is null
                ? AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.GateUnavailable, descriptor)
                : await _externalProviderTrust.EvaluateAsync(
                    input.BusinessUnitId, descriptor, AiPurposes.RfqExtraction,
                    unstructuredPayload: true, ct);
            if (!decision.Allowed)
            {
                // Same closed gate, same deterministic answer, so the same permanent
                // disposition as the document path: retrying an email body against an
                // unauthorized provider cannot produce a different result either.
                _log.LogError(
                    "Conversational extraction REFUSED for tenant {Tenant}: external provider is not "
                    + "authorized. {Descriptor} reason={Reason} message={Document}. {Action}",
                    input.BusinessUnitId, descriptor, decision.Reason, input.SourceDocumentName,
                    ChunkedExtractionService.AiNotAuthorizedOperatorAction);
                return Failed(input,
                    $"[{ChunkedExtractionService.AiNotAuthorizedCode}] "
                    + $"{ChunkedExtractionService.ExternalUnstructuredRefusal} [denial: {decision.Reason}]",
                    diagnostics,
                    permanent: true);
            }
            diagnostics.Add(
                $"External provider authorized for conversational extraction (authorization #{decision.AuthorizationId}).");
        }

        LlmExtractionOutcome call;
        try
        {
            // The key is scoped to the LEASE ATTEMPT (a{n}), exactly like the document
            // path: a retried job must be a NEW governed request, not a replay the
            // governance ledger refuses as a duplicate before any model call.
            call = await _llm.ExtractLeadDataDetailedAsync(submitted,
                new AiCallContext(input.BusinessUnitId, AiPurposes.RfqExtraction,
                    $"conversational:{input.SourceId}:a{input.AttemptNumber}", ConversationalPrompt.PromptVersion,
                    ExtractionJobId: input.ExtractionJobId,
                    SourceDocumentOccurrenceId: input.SourceDocumentOccurrenceId), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiPolicyDeniedException ex)
        {
            // A governance refusal happens BEFORE any model call and must not masquerade
            // as a model failure — its remedy is governance-side, not another retry of
            // the identical request.
            _log.LogWarning(ex,
                "Conversational extraction for {Document} was refused by AI governance ({Code}).",
                input.SourceDocumentName, ex.Code);
            return Failed(input,
                $"AI governance refused this request before any model call was made ({ex.Code}).",
                diagnostics);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conversational extraction call threw for {Document}.", input.SourceDocumentName);
            call = new LlmExtractionOutcome(null, AiErrorCodes.AttemptsExhausted);
        }

        // A provider failure is genuinely retryable — that is a different fact from "this
        // message asks for nothing", and it must stay distinguishable.
        if (call.Result is null)
            return Failed(input, call.OutputTruncated
                ? $"The model ran out of output budget ({_llm.MaxOutputTokens} tokens) on this message body."
                : $"The model returned no usable result for this message body. [{call.ErrorCode ?? AiErrorCodes.AttemptsExhausted}]",
                diagnostics);

        var modelItems = call.Result.Items ?? new List<LeadItemData>();
        var verification = ProseAnchorVerifier.Verify(submitted, modelItems);
        diagnostics.AddRange(verification.Diagnostics);

        var result = call.Result with { Items = verification.Items };
        // A governed redaction placeholder is not an identity: on an external provider the
        // model is shown "[REDACTED_EMAIL]", and echoing it back must not count as knowing
        // who the customer is. (The real sender address still reaches the lead from the
        // message envelope, not from the model.)
        var customerResolved = IsRealValue(result.CustomerCompanyName) || IsRealValue(result.CustomerBuyerEmail);

        string? reviewReason = null;
        if (verification.Items.Count == 0)
        {
            // Deliberately NOT Failed: "this message contains no request" is a complete,
            // correct answer that a human must be able to see and overturn.
            //
            // TRUNCATION OUTRANKS THE EMPTY VERDICT, and the order is the whole point. These
            // two sentences look like the same outcome and are not: one is a fact about the
            // SENDER (they asked for nothing), the other is a fact about US (we only read part
            // of it). Downstream, the first is allowed to dispose of the message and the second
            // is not, so emitting the first for a clipped body would let a real enquiry be
            // closed on evidence nobody ever looked at.
            reviewReason = truncatedInput
                ? TruncatedBodyFoundNothingReviewReason
                : NoRequestableItemsReviewReason;
        }
        else if (!verification.Clean)
        {
            reviewReason =
                $"Line items KEPT but needing confirmation: {verification.UnanchoredItemCount} whose "
                + $"quoted source text could not be located, {verification.CeilingDroppedCount} beyond "
                + $"the {verification.Ceiling} the message obviously supports. Nothing was discarded — "
                + "check these against the original message.";
        }
        else if (!customerResolved)
        {
            reviewReason = "The message names no buying organisation or contact address.";
        }
        else if (threadContinuation)
        {
            reviewReason =
                "This message is a reply/forward in an existing thread; confirm it is a new request "
                + "and not a restatement of one already in the system.";
        }
        else if (truncatedInput)
        {
            reviewReason = "The message body was too long to submit in full; omitted content requires review.";
        }

        if (reviewReason is not null && !diagnostics.Contains(reviewReason))
            diagnostics.Add(reviewReason);

        var status = reviewReason is null
            ? ExtractionOutcomeStatus.Ok
            : ExtractionOutcomeStatus.NeedsReview;

        _log.LogInformation(
            "Conversational extraction for {Document}: {Kept}/{Returned} item(s) anchored "
            + "(ceiling {Ceiling}), customer {CustomerResolved}, status {Status}.",
            input.SourceDocumentName, verification.Items.Count, modelItems.Count,
            verification.Ceiling, customerResolved ? "resolved" : "unresolved", status);

        return new ChunkedExtractionOutcome
        {
            Status = status,
            Result = result,
            ExpectedItemCount = modelItems.Count,
            ExtractedItemCount = verification.Items.Count,
            ReviewReason = reviewReason,
            Diagnostics = diagnostics,
            AiProviderClass = _llm.ProviderClass,
            ProcessingPath = _llm.ProviderClass == AiProviderClass.External
                ? ExtractionProcessingPath.ExternalFallback
                : ExtractionProcessingPath.LocalModel,
            OcrStatus = input.OcrStatus,
            OcrPageCount = input.OcrPageCount,
            OcrTruncated = input.OcrTruncated
        };
    }

    /// <summary>
    /// Rebuilds exactly one copy of the message text from the reader's header/region split.
    /// Anchoring compares against the text we SUBMIT, so this must be the same string — and
    /// it must not duplicate the header lines, which the reader repeats inside the regions
    /// for short documents.
    /// </summary>
    internal static string ComposeSubmittedText(DocumentExtractionInput input)
    {
        var header = input.HeaderText ?? string.Empty;
        var regions = input.LineItemRegions is { Count: > 0 }
            ? string.Join("\n", input.LineItemRegions)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(regions)) return header.Trim();
        if (string.IsNullOrWhiteSpace(header)) return regions.Trim();
        if (regions.StartsWith(header, StringComparison.Ordinal)) return regions.Trim();
        return $"{header}\n{regions}".Trim();
    }

    private static bool IsRealValue(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Contains("[REDACTED_", StringComparison.OrdinalIgnoreCase);

    /// <param name="diagnostics">Diagnostics collected before the failure; preserved (with
    /// the reason appended) so the stored dead-letter error can say what actually happened
    /// instead of only the flattened summary.</param>
    private static ChunkedExtractionOutcome Failed(
        DocumentExtractionInput input, string reason, List<string>? diagnostics = null,
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
            ExpectedItemCount = 0,
            ExtractedItemCount = 0,
            ReviewReason = reason,
            Diagnostics = diagnostics,
            ProcessingPath = input.ProcessingPath,
            OcrStatus = input.OcrStatus,
            OcrPageCount = input.OcrPageCount,
            OcrTruncated = input.OcrTruncated
        };
    }
}

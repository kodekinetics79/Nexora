using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The email-body extraction path. It answers the owner's first ingestion question — can the
/// system read a scattered, conversational enquiry and turn it into a lead with its line
/// items — and it does so WITHOUT trusting the model's self-reported confidence, which is not
/// a measurement. Its only invention guard is deterministic: every item must quote the message.
/// </summary>
public class ConversationalExtractionServiceTests
{
    private const string ProseMessage =
        "Subject: Cable tray requirement\nFrom: Ahmed <ahmed@alnoortrading.ae>\nDate: 2026-08-04\n\n"
        + "Hi, please quote 40 nos cable tray 300mm and 12 nos junction box IP65, "
        + "delivery to Jebel Ali by 20th.\n\nRegards\nAhmed Al Mansoori\nAl Noor Trading LLC";

    // ---------------------------------------------------------------- test doubles

    private sealed class ProseLlm : ILLMService
    {
        private readonly LeadExtractionResult? _result;
        public ProseLlm(LeadExtractionResult? result, AiProviderClass providerClass = AiProviderClass.Local)
        {
            _result = result;
            ProviderClass = providerClass;
        }

        public AiProviderClass ProviderClass { get; }
        public int CallCount { get; private set; }
        public string? LastPromptVersion { get; private set; }
        public string? LastPurpose { get; private set; }
        public string? LastText { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        /// <summary>When set, every call throws it — models the governance layer refusing
        /// the reservation before any provider call is made.</summary>
        public Exception? ThrowOnCall { get; init; }

        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPromptVersion = context.PromptVersion;
            LastPurpose = context.Purpose;
            LastText = fullText;
            LastIdempotencyKey = context.IdempotencyKey;
            if (ThrowOnCall is not null)
                throw ThrowOnCall;
            return Task.FromResult(_result);
        }

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }

    private static ConversationalExtractionService NewService(
        ILLMService llm, IAiExternalProviderTrust? trust = null)
        => new(llm, new NoopLogger<ConversationalExtractionService>(), trust);

    private static DocumentExtractionInput Input(string text = ProseMessage)
        => new() { BusinessUnitId = 1, HeaderText = text, SourceDocumentName = "enquiry_body.txt" };

    private static LeadItemData Item(string name, string span, int? quantity, string? token, double confidence = 0.8)
        => Ext.Item(confidence, name, quantity ?? 0) with
        {
            Quantity = quantity,
            SourceSpan = span,
            QuantityToken = token,
            UnitOfMeasure = token?.Split(' ').Last()
        };

    private static LeadExtractionResult Result(
        List<LeadItemData> items, double overall = 0.8,
        string? company = "Al Noor Trading LLC", string? email = "ahmed@alnoortrading.ae")
        => Ext.Result(items, overall) with
        {
            Rfqno = null,
            CustomerCompanyName = company,
            CustomerCompanyEvidence = company is null ? null : "Al Noor Trading LLC",
            CustomerBuyerEmail = email
        };

    // ------------------------------------------------------ the primary capability

    [Fact]
    public async Task TheIdempotencyKeyIsScopedToTheLeaseAttempt()
    {
        // Same dead-letter root cause as the document path: without the attempt in the
        // key, a retried job replayed attempt one's key and the governance ledger refused
        // it as a duplicate before any model call.
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }));
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            SourceId = "job:33",
            AttemptNumber = 3,
            HeaderText = ProseMessage,
            SourceDocumentName = "enquiry_body.txt"
        };

        await NewService(llm).ExtractAsync(input);

        Assert.Equal("conversational:job:33:a3", llm.LastIdempotencyKey);
    }

    [Fact]
    public async Task GovernanceRefusalSurfacesItsOwnCodeInsteadOfAModelFailure()
    {
        // A refusal issued BEFORE any model call used to be flattened into
        // attempts_exhausted and reported as "the model returned no usable result" —
        // a governance decision masquerading as a model failure.
        var llm = new ProseLlm(null)
        {
            ThrowOnCall = new AiPolicyDeniedException("duplicate_request")
        };

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.ReviewReason);
        Assert.Contains("AI governance refused", outcome.ReviewReason);
        Assert.Contains("duplicate_request", outcome.ReviewReason);
        Assert.DoesNotContain("model returned no usable result", outcome.ReviewReason);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("duplicate_request"));
    }

    [Fact]
    public async Task ProseEnquiryWithNoAttachmentProducesALeadWithItsLineItems()
    {
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos"),
            Item("Junction box IP65", "12 nos junction box IP65", 12, "12 nos")
        }));

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.Equal(2, outcome.Result!.Items.Count);
        Assert.Equal(40, outcome.Result.Items[0].Quantity);
        Assert.Equal(12, outcome.Result.Items[1].Quantity);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Null(outcome.ReviewReason);
    }

    [Fact]
    public async Task TheConversationalPromptIsTheOneActuallyRequested()
    {
        // The governed prompt version is BOTH the ledger label and the switch that selects
        // the instruction set, so the prompt recorded is provably the prompt sent.
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }));

        await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ConversationalPrompt.PromptVersion, llm.LastPromptVersion);
        Assert.Equal(AiPurposes.RfqExtraction, llm.LastPurpose);
        Assert.True(ConversationalPrompt.IsConversational(llm.LastPromptVersion));
    }

    [Fact]
    public async Task SignatureBlockYieldsTheBuyerOrganisation()
    {
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }));

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal("Al Noor Trading LLC", outcome.Result!.CustomerCompanyName);
        Assert.Equal("ahmed@alnoortrading.ae", outcome.Result.CustomerBuyerEmail);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
    }

    [Fact]
    public async Task LowSelfReportedConfidenceDoesNotBlockAnAnchoredEnquiry()
    {
        // 0.42 would have been refused by the document path's MinAcceptableConfidence of
        // 0.60. That number is self-reported by the model and is not evidence of anything;
        // the anchors are.
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos", confidence: 0.42)
        }, overall: 0.42));

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Single(outcome.Result!.Items);
    }

    [Fact]
    public async Task UnstatedQuantityStaysNullInsteadOfBeingInventedAsOne()
    {
        const string message = "Please quote cable tray 300mm for the Jebel Ali site.\nAl Noor Trading LLC";
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "cable tray 300mm", null, null)
        }));

        var outcome = await NewService(llm).ExtractAsync(Input(message));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Null(Assert.Single(outcome.Result!.Items).Quantity);
    }

    // -------------------------------------------------------------- invention guard

    [Fact]
    public async Task AnItemThatDoesNotQuoteTheMessageIsKeptAndTheLeadIsFlagged()
    {
        // The switchgear line quotes text the message never contains. It is KEPT and the lead
        // is flagged for review, rather than deleted: the reviewer is the one who can tell a
        // model's invention from a quote-check false negative, and only one of those two
        // mistakes is recoverable once the line is gone.
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos"),
            Item("Switchgear panel", "2 nos switchgear panel 11kV", 2, "2 nos")
        }));

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Equal(2, outcome.Result!.Items.Count);
        Assert.Equal(2, outcome.ExpectedItemCount);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Contains("confirmation", outcome.ReviewReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- honest outcomes

    [Fact]
    public async Task AMessageThatOnlyPointsAtAnAttachmentIsReviewNotFailure()
    {
        // The attachment is a separate job. "This body asks for nothing" is a complete,
        // correct answer — dead-lettering it would hide it from the humans who can judge it.
        var llm = new ProseLlm(Result(new List<LeadItemData>()) with
        {
            HeaderRemarks = "The message refers to an attached BOQ and states no items itself."
        });

        var outcome = await NewService(llm).ExtractAsync(
            Input("Dear Sir,\n\nPlease find attached our BOQ.\n\nAl Noor Trading LLC"));

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.Empty(outcome.Result!.Items);
        Assert.Contains("No requestable items", outcome.ReviewReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnidentifiedBuyerIsExtractedAndFlaggedNeverDiscarded()
    {
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }, company: null, email: null));

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Single(outcome.Result!.Items);
        Assert.Contains("buying organisation", outcome.ReviewReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARedactedContactPlaceholderDoesNotCountAsAResolvedCustomer()
    {
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }, company: null, email: "[REDACTED_EMAIL]"));

        var outcome = await NewService(llm).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
    }

    [Fact]
    public async Task AThreadContinuationIsFlaggedRatherThanSilentlyDuplicated()
    {
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }));

        var outcome = await NewService(llm).ExtractAsync(Input(), threadContinuation: true);

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Single(outcome.Result!.Items);
        Assert.Contains("reply/forward", outcome.ReviewReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProviderFailureStaysRetryableAndDistinguishable()
    {
        var outcome = await NewService(new ProseLlm(null)).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task AnEmptyBodyIsNeverSentToTheModel()
    {
        var llm = new ProseLlm(Result(new List<LeadItemData>()));

        var outcome = await NewService(llm).ExtractAsync(Input("   "));

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
    }

    // ------------------------------------------------------------------- governance

    [Fact]
    public async Task AnUnauthorizedExternalProviderReceivesZeroBytesOfCustomerCorrespondence()
    {
        // Same fail-closed posture as the unstructured document path: absence of the
        // allow-list gate IS a refusal. An email body is untruncated customer correspondence.
        var llm = new ProseLlm(Result(new List<LeadItemData>
        {
            Item("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos")
        }), AiProviderClass.External);

        var outcome = await NewService(llm, trust: null).ExtractAsync(Input());

        Assert.Equal(ExtractionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains("External processing is blocked", outcome.ReviewReason!);
    }
}

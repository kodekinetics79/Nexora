using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The review signal must carry information.
///
/// <para>
/// It did not. A line was marked NeedsReview when any of unit price, currency, manufacturer,
/// part number or lead time was missing — and on an INBOUND RFQ those are precisely the
/// fields the buyer is asking the supplier to supply. Every line of every correctly-read
/// document was flagged (all 641 in the 120-document sample set), and document confidence,
/// averaged over the whole schema rather than over what the document asserts, sat at 0.557
/// for all 120 — below the 0.60 acceptance threshold, with nothing misread.
/// </para>
/// <para>
/// The rule these tests pin down is a single one, and it is evidence rather than intent:
/// a field is ABSENT FROM THE DOCUMENT when no row of that document carries source text for
/// it, and FAILED TO READ when the document states it elsewhere but not here, or when source
/// text is present and produced no value. The first is not flagged. The second always is —
/// the opposite failure, a signal that never fires, would be just as useless.
/// </para>
/// </summary>
public sealed class ExtractionReviewSignalTests
{
    private static readonly ICanonicalRfqNormalizer Normalizer = new CanonicalRfqNormalizer();

    /// <summary>The shape of every document in the sample set: item code, description, qty, note.</summary>
    private static RfqSpreadsheetRow CorpusRow(int rowNumber, string product, string qty, string part) => new()
    {
        RowNumber = rowNumber,
        SourceDocumentName = "RFQ-260011_Omega_Oil.docx",
        RfqNo = "RFQ-260011",
        BuyerName = "Omega Oil",
        ReceivedDate = "2026-05-26",
        ProductName = product,
        Quantity = qty,
        ManufacturerPartNumber = part,
        ItemText = "Urgent requirement"
    };

    private static List<RfqSpreadsheetRow> CorpusDocument() => new()
    {
        CorpusRow(2, "Safety Relay", "57", "SKU-2244"),
        CorpusRow(3, "Ball Valve 2in", "12", "SKU-2245"),
        CorpusRow(4, "Cable Tray 300mm", "40", "SKU-2246")
    };

    // ---- absent from the document ----------------------------------------

    [Fact]
    public void AFieldTheDocumentNeverStatesIsNotAReadingFailure()
    {
        var import = Normalizer.NormalizeSpreadsheetRows(CorpusDocument(), businessUnitId: 7);

        var document = Assert.Single(import.Documents);
        Assert.Equal(3, document.LineItems.Count);
        Assert.All(document.LineItems, line =>
            Assert.Equal(ValidationStatus.Valid, line.ValidationStatus));

        // ...and it is recorded as absent rather than merely tolerated, so downstream
        // consumers can tell the two apart too.
        var first = document.LineItems[0];
        Assert.False(first.UnitPrice.StatedInDocument);
        Assert.False(first.Currency.StatedInDocument);
        Assert.False(first.LeadTimeDays.StatedInDocument);
        Assert.False(first.UnitOfMeasure.StatedInDocument);
        Assert.False(first.ManufacturerName.StatedInDocument);
        Assert.Contains(first.UnitPrice.Transformations,
            t => t.Contains("not stated anywhere in this document", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredFieldsAreNeverExcusedByTheSameRule()
    {
        // The opposite failure: a signal that never fires. A line with no product name and no
        // quantity is broken however the rest of the document is shaped.
        var rows = CorpusDocument();
        rows.Add(new RfqSpreadsheetRow
        {
            RowNumber = 5,
            SourceDocumentName = "RFQ-260011_Omega_Oil.docx",
            RfqNo = "RFQ-260011",
            BuyerName = "Omega Oil",
            ReceivedDate = "2026-05-26",
            ManufacturerPartNumber = "SKU-2247"
        });

        var document = Assert.Single(Normalizer.NormalizeSpreadsheetRows(rows, 7).Documents);

        Assert.Equal(ValidationStatus.Invalid, document.LineItems[3].ValidationStatus);
        Assert.True(document.LineItems[3].ProductName.StatedInDocument);
        Assert.Equal(ValidationStatus.Invalid, document.ValidationStatus);
    }

    // ---- failed to read ---------------------------------------------------

    [Fact]
    public void AFieldTheDocumentStatesOnAnotherLineStillFlagsWhereItIsMissing()
    {
        // A price sheet that prices two of three lines: the third is a real gap, because the
        // document demonstrably carries the field.
        var rows = CorpusDocument();
        rows[0].UnitPrice = "125.00";
        rows[1].UnitPrice = "48.50";

        var document = Assert.Single(Normalizer.NormalizeSpreadsheetRows(rows, 7).Documents);

        Assert.Equal(ValidationStatus.Valid, document.LineItems[0].ValidationStatus);
        Assert.Equal(ValidationStatus.Valid, document.LineItems[1].ValidationStatus);
        Assert.Equal(ValidationStatus.NeedsReview, document.LineItems[2].ValidationStatus);
        Assert.True(document.LineItems[2].UnitPrice.StatedInDocument);
    }

    [Fact]
    public void SourceTextThatCouldNotBeParsedStillFlags()
    {
        // The document said something here. We could not turn it into a value. That is a
        // reading failure whatever the rest of the document looks like, and the raw text is
        // kept so a human can read what the buyer actually wrote.
        var rows = CorpusDocument();
        rows[1].LeadTimeDays = "nine weeks";

        var document = Assert.Single(Normalizer.NormalizeSpreadsheetRows(rows, 7).Documents);

        var flagged = document.LineItems[1];
        Assert.Equal(ValidationStatus.NeedsReview, flagged.ValidationStatus);
        Assert.True(flagged.LeadTimeDays.StatedInDocument);
        Assert.True(flagged.LeadTimeDays.Confidence < 1m);
        Assert.Equal("nine weeks", flagged.LeadTimeDays.OriginalValue);

        // The other lines of the same document do NOT inherit the flag: the field is stated
        // once, so only the line that carries unreadable text is a human's problem... and the
        // lines that carry nothing at all are, correctly, still flagged as gaps.
        Assert.Equal(ValidationStatus.NeedsReview, document.LineItems[0].ValidationStatus);
    }

    [Fact]
    public void AUnitStatedOnSomeLinesIsExpectedOnAllOfThem()
    {
        var rows = CorpusDocument();
        rows[0].UnitOfMeasure = "M";
        rows[1].UnitOfMeasure = "EA";

        var document = Assert.Single(Normalizer.NormalizeSpreadsheetRows(rows, 7).Documents);

        Assert.Equal(ValidationStatus.Valid, document.LineItems[0].ValidationStatus);
        Assert.Equal(ValidationStatus.NeedsReview, document.LineItems[2].ValidationStatus);
    }

    // ---- confidence -------------------------------------------------------

    [Fact]
    public async Task ConfidenceIsComputedOverTheFieldsTheDocumentAsserts()
    {
        var outcome = await Extractor().ExtractStructuredAsync(CorpusDocument(), businessUnitId: 7, sourceName: "RFQ-260011.docx");

        // 0.4 x header + 0.6 x item, over STATED fields only. Averaging the five solicited
        // line fields and the unstated closing date in produced 0.557 for all 120 sample
        // documents — under the 0.60 threshold, with all 641 lines byte-perfect.
        Assert.NotNull(outcome.Result);
        // OverallConfidence is nullable — "no score" is now distinct from 0.0 — so the presence
        // of a score is asserted before its value is compared.
        Assert.NotNull(outcome.Result!.OverallConfidence);
        Assert.Equal(1.0, outcome.Result!.OverallConfidence!.Value, 3);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Contains("below threshold", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMisreadValueStillDragsConfidenceDown()
    {
        var rows = CorpusDocument();
        rows[1].LeadTimeDays = "nine weeks";

        var outcome = await Extractor().ExtractStructuredAsync(rows, businessUnitId: 7, sourceName: "RFQ-260011.docx");

        Assert.True(outcome.Result!.OverallConfidence < 1.0,
            "a field whose source text could not be parsed must still cost confidence");
        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
    }

    [Fact]
    public async Task TheReviewReasonNamesWhichFieldNeedsTheLook()
    {
        var rows = CorpusDocument();
        rows[0].BidClosingDate = "not a date at all";

        var outcome = await Extractor().ExtractStructuredAsync(rows, businessUnitId: 7, sourceName: "RFQ-260011.docx");

        // "One or more fields need review (see canonical validation issues)" pointed at a
        // ledger the reviewer cannot open and read identically whether one date was unreadable
        // or every line was.
        Assert.NotNull(outcome.ReviewReason);
        Assert.Contains("closing date", outcome.ReviewReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canonical validation issues", outcome.ReviewReason!);
    }

    // ---- the refusal is reportable ---------------------------------------

    [Fact]
    public void AnUnauthorizedProviderRefusalIsPermanentAndNamed()
    {
        const string stored =
            "[EXTRACTION_AI_NOT_AUTHORIZED] External processing is blocked for unstructured "
            + "documents until a locally reduced, redacted field/row payload is available; send "
            + "this document to human review or configure a local model. "
            + "[denial: external_processing_denied]";

        // It used to match none of the category rules and land on generic EXTRACTION_FAILURE,
        // indistinguishable from a model timeout — while the raw error is stripped from the
        // tenant DTO, so nothing else could tell them apart either.
        Assert.Equal(ExtractionDeadLetterService.AiNotAuthorizedCategory,
            ExtractionDeadLetterService.ClassifyFailure(stored));

        // The word "provider" appears in the refusal, and PROCESSING_PROVIDER would have
        // swallowed it. Order matters, so it is asserted.
        Assert.NotEqual("PROCESSING_PROVIDER", ExtractionDeadLetterService.ClassifyFailure(stored));

        var action = ExtractionDeadLetterService.OperatorAction(
            ExtractionDeadLetterService.AiNotAuthorizedCategory);
        Assert.NotNull(action);
        Assert.Contains("AI trust centre", action!);
        Assert.Contains("local model", action!);
    }

    [Fact]
    public void EveryDenialCodeTheGateCanProduceClassifiesTheSameWay()
    {
        // A new denial reason must not silently fall back to EXTRACTION_FAILURE. The marker
        // is on the message, not on the reason, so it holds for every code the gate emits.
        var reasons = new[]
        {
            AiExternalProviderTrustReasons.NotAuthorized,
            AiExternalProviderTrustReasons.EndpointUnresolved,
            AiExternalProviderTrustReasons.Revoked,
            AiExternalProviderTrustReasons.Expired,
            AiExternalProviderTrustReasons.PurposeNotAuthorized,
            AiExternalProviderTrustReasons.UnstructuredNotAuthorized,
            AiExternalProviderTrustReasons.PolicyMissing,
            AiExternalProviderTrustReasons.PolicyDisabled,
            AiExternalProviderTrustReasons.PolicyExternalProcessingDenied,
            AiExternalProviderTrustReasons.TenantMismatch,
            AiExternalProviderTrustReasons.GateUnavailable
        };

        foreach (var reason in reasons)
        {
            var stored = $"[{ChunkedExtractionService.AiNotAuthorizedCode}] "
                + $"{ChunkedExtractionService.ExternalUnstructuredRefusal} [denial: {reason}]";
            Assert.Equal(ExtractionDeadLetterService.AiNotAuthorizedCategory,
                ExtractionDeadLetterService.ClassifyFailure(stored));
        }
    }

    [Fact]
    public void TheRefusalHasAFirstClassOutcomeState()
    {
        // Malware and an unsupported format each had one. The most likely first-day outcome on
        // a stock deploy had none, so the intake record could not say what happened.
        Assert.Equal("AI_NOT_AUTHORIZED", IngestionOutcomeState.AI_NOT_AUTHORIZED.ToString());
        Assert.NotEqual(IngestionOutcomeState.NONE, IngestionOutcomeState.AI_NOT_AUTHORIZED);
    }

    [Fact]
    public void OtherFailuresStayRetryableAndKeepTheirCategories()
    {
        // The permanence is scoped to a DECISION. A timeout, a provider outage and an
        // unrecognised error are all still retryable and still classify as they did.
        Assert.Equal("PROCESSING_TIMEOUT", ExtractionDeadLetterService.ClassifyFailure("model timeout after 180s"));
        Assert.Equal("PROCESSING_PROVIDER", ExtractionDeadLetterService.ClassifyFailure("provider returned 503"));
        Assert.Equal("EXTRACTION_FAILURE", ExtractionDeadLetterService.ClassifyFailure("something else entirely"));
        Assert.Equal("UNCLASSIFIED", ExtractionDeadLetterService.ClassifyFailure(null));
    }

    /// <summary>The deterministic path must make no provider call at all.</summary>
    private static ChunkedExtractionService Extractor() =>
        new(new StubLlm(AiProviderClass.Local), Normalizer, new NoopLogger<ChunkedExtractionService>());
}

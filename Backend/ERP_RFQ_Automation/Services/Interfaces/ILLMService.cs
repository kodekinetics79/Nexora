using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Services.Interfaces
{
    public interface ILLMService
    {
        AiProviderClass ProviderClass => AiProviderClass.External;

        /// <summary>
        /// The canonical identity of the endpoint this client will actually call —
        /// normalized origin, model, resolved provider class and the reason for that
        /// classification. Callers that must decide whether egress to this destination is
        /// authorized (see <c>IAiExternalProviderTrust</c>) match against THIS value, so
        /// the endpoint that is governed is provably the endpoint that is called.
        /// The default is deliberately unresolved-and-External: an implementation that
        /// does not describe itself can never be matched by an allow-list, so it fails closed.
        /// </summary>
        AiProviderDescriptor ProviderDescriptor => AiProviderDescriptor.Unresolved();

        /// <summary>
        /// The hard ceiling on OUTPUT tokens this client will let the provider generate for
        /// one call (Ollama's <c>num_predict</c>). Callers that decide how much work to pack
        /// into a single request MUST size against this — the extraction schema costs
        /// hundreds of output tokens per line item, so an input-sized chunk that "fits the
        /// context window" can still overflow the completion budget and come back as
        /// unparseable, half-written JSON. See
        /// <c>ERP_RFQ_Automation.Extraction.ExtractionOutputBudget</c>.
        /// The default matches the client default so an implementation that does not say
        /// otherwise is assumed to be modest, never generous.
        /// </summary>
        int MaxOutputTokens => 4096;

        Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default);

        /// <summary>
        /// Same call as <see cref="ExtractLeadDataAsync"/> but it also reports WHY a null
        /// result is null. Callers that can change the shape of the request — the chunked
        /// extractor, which can re-issue a chunk with fewer line items — need to tell
        /// "the model wrote bad JSON" (pointless to retry) apart from "the model ran out of
        /// output budget mid-JSON" (retry smaller, and only smaller).
        /// The default implementation preserves every existing implementation unchanged: it
        /// simply reports the failure as non-retryable, which is exactly today's behaviour.
        /// </summary>
        async Task<LlmExtractionOutcome> ExtractLeadDataDetailedAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            var result = await ExtractLeadDataAsync(fullText, context, cancellationToken);
            return new LlmExtractionOutcome(
                result, result is null ? AiErrorCodes.AttemptsExhausted : null);
        }

        /// <summary>
        /// WP-BOQ (additive): reads a service request / scope-of-work text and drafts a
        /// structured bill of quantities (sections + items + explicit TBD lines +
        /// assumptions). Returns null when the model output cannot be trusted — callers
        /// must fall back to an honest TBD-heavy skeleton, never fabricate quantities.
        /// Contracts + lenient converters live in BoqDraftContracts.cs; the Ollama
        /// implementation (same retry/strict-parse shape as ExtractLeadDataAsync) is in
        /// Services/OllamaLlmService.Boq.cs.
        /// </summary>
        Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default);
    }
    /// <summary>
    /// The result of one extraction call plus the reason it failed, if it failed.
    /// <paramref name="ErrorCode"/> is one of <see cref="AiErrorCodes"/> and is null on success.
    /// </summary>
    public sealed record LlmExtractionOutcome(LeadExtractionResult? Result, string? ErrorCode)
    {
        /// <summary>
        /// True when the provider stopped because it hit the output-token ceiling. The only
        /// meaningful retry is a smaller request; replaying the identical request re-truncates
        /// at the identical point.
        /// </summary>
        public bool OutputTruncated => ErrorCode == AiErrorCodes.OutputTruncated;
    }

    public record LeadExtractionResult(
        string? Rfqno, double? RfqnoConfidence,
        string? BuyersName, double? BuyersNameConfidence,
        string? RecDate, double? RecDateConfidence,
        string? BidClosingDate, double? BidClosingDateConfidence,
        string? BiddingDecision, double? BiddingDecisionConfidence,
        string? AcknowledgmentDate, double? AcknowledgmentDateConfidence,
        string? SubDate, double? SubDateConfidence,
        string? HeaderRemarks, double? HeaderRemarksConfidence,
        string? OpportunityNo, double? OpportunityNoConfidence,
        string? Rfqtype, double? RfqtypeConfidence,
        string? DurationAgreement, double? DurationAgreementConfidence,
        double? OverallConfidence,
        List<LeadItemData> Items,
        // Document-level classification: "product" | "service" | "mixed" (foundation for
        // the BOQ engine). Optional + defaulted so every existing positional construction
        // site and model outputs that omit it keep working unchanged.
        string? InquiryType = null,
        double? InquiryTypeConfidence = null,

        // ── CLIENT ORGANISATION IDENTITY ────────────────────────────────────────────
        // Header-level ONLY (a per-item field costs ~225 output tokens PER LINE; a header
        // field is paid once). Every field is a trailing optional parameter so that all
        // existing positional construction sites keep compiling unchanged.
        //
        // DIRECTION OF TRADE is the whole point of this block. On the production corpus —
        // 14 Saudi Electricity Company "MATERIALS E-BIDDING SYSTEM" bids — the ONLY company
        // name printed anywhere is OUR OWN ("Vendname: ALI ZAID AL-QURAISHI&PARTNERS",
        // "Vendor Code 2004414"). A naive "CustomerName" field would make the model return
        // the trading house itself and auto-link every SEC lead to a customer record of us.
        // So the vendor block is captured too — as the thing to EXCLUDE.

        // The BUYING organisation, verbatim. Null unless the document states it.
        string? CustomerCompanyName = null,
        double? CustomerCompanyNameConfidence = null,
        // <=120-character verbatim snippet that names the buyer. No snippet, no name.
        string? CustomerCompanyEvidence = null,
        // Buyer's CR / VAT / commercial registration. Usually null on SEC bids — correct.
        string? CustomerCompanyRegistrationId = null,
        double? CustomerCompanyRegistrationIdConfidence = null,
        // Buyer contact address (57322@se.com.sa) — often the only trace of the real domain.
        string? CustomerBuyerEmail = null,
        double? CustomerBuyerEmailConfidence = null,
        // Buying portal / template name; pairs with the vendor code into a unique key.
        string? CustomerPortalName = null,
        double? CustomerPortalNameConfidence = null,
        // OUR name in the Vendor/Vendname block. Captured to be EXCLUDED, never matched.
        string? SupplierNameOnDocument = null,
        double? SupplierNameOnDocumentConfidence = null,
        // OUR vendor code AT the customer (e.g. 2004414). Identifies us, never them.
        string? SupplierAccountRefOnDocument = null,
        double? SupplierAccountRefOnDocumentConfidence = null,

        // ── FR-RFQ-03 / FR-RFQ-04 INTAKE FIELDS ─────────────────────────────────────
        // Header-level, trailing and optional for the same reason as the block above:
        // every existing positional construction site keeps compiling unchanged.

        // Saudi region or city the buyer wants delivery to, in the buyer's own wording.
        // Normalising it against the city master is a separate, reviewable step.
        string? DeliveryLocation = null,
        double? DeliveryLocationConfidence = null,
        // The delivery date the BUYER is asking for. Not a supplier lead time, and never
        // written into one — that conflation put a lead time of zero on every line once.
        string? RequiredDeliveryDate = null,
        double? RequiredDeliveryDateConfidence = null,
        // Reference of a standing agreement or frame contract this inquiry calls off
        // against, kept distinct from the inquiry's own RFQ number.
        string? AgreementReference = null,
        double? AgreementReferenceConfidence = null);
    /// <summary>
    /// One extracted line item.
    ///
    /// THE PER-FIELD <c>&lt;Field&gt;Confidence</c> SLOTS BELOW ARE NO LONGER ASKED FOR BY
    /// THE LLM (prompt rfq-extraction-v2, 2026-08-05). They cost roughly half of every
    /// item's output budget and were then discarded: a LeadItem row has ONE confidence
    /// column, <c>Aiconfidence</c>, and <c>ExtractionWorker.BuildLead</c> reads exactly one
    /// value — <c>ItemConfidence</c>. From the model path they now arrive null, which the
    /// persister already handles because it never read them.
    ///
    /// They are KEPT as parameters on purpose. Three construction sites pass all 48
    /// value/confidence arguments POSITIONALLY (<c>FolderService</c> ×2,
    /// <c>ManualUploadService.BuildFallbackItems</c>) with interleaved
    /// <c>value, confidence</c> pairs, and most of those values are <c>string?</c>. Deleting
    /// the confidence parameters would require re-threading those lists by hand, where a
    /// single miscount silently shifts every following value into the WRONG FIELD and still
    /// compiles. That is a data-corruption class of bug, traded for zero runtime benefit:
    /// a null field on a record costs nothing, and the saving being banked here is in the
    /// PROMPT, not in this type. The deterministic parser paths
    /// (<c>ChunkedExtractionService.MapCanonicalItem</c>, <c>FolderService</c>) also still
    /// populate them with genuine per-field parser confidence, so the slots are not dead —
    /// only unread at persistence.
    /// </summary>
    public record LeadItemData(
        string? CompanyRef, double? CompanyRefConfidence,
        string? CustomerAccountPortalId, double? CustomerAccountPortalIdConfidence,
        string? CustomerRfqno, double? CustomerRfqnoConfidence,
        string? ItemMaterialCode, double? ItemMaterialCodeConfidence,
        string? CommodityProduct, double? CommodityProductConfidence,
        string? BuyerName, double? BuyerNameConfidence,
        string? LineItemNo, double? LineItemNoConfidence,
        string? ProductShortName, double? ProductShortNameConfidence,
        string? Alternative, double? AlternativeConfidence,
        string? ProductShortDescription, double? ProductShortDescriptionConfidence,
        string? Currency, double? CurrencyConfidence,
        string? UnitOfMeasure, double? UnitOfMeasureConfidence,
        decimal? UnitPrice, double? UnitPriceConfidence,
        // Null means "the document did not state a usable quantity" — the LINE routes to
        // review (the canonical-line store enforces quantity IS NULL OR quantity > 0).
        // The model path reads this int? through OllamaLlmService.LenientQuantityConverter
        // (2 / 2.0 / "2" / "2.0" -> 2; a real fraction like 2.5 -> null, NEVER truncated),
        // and a zero/negative model value is quarantined to null per line — one bad cell
        // must not fail the other 173 lines of the document.
        int? Quantity, double? QuantityConfidence,
        string? StorageLocation, double? StorageLocationConfidence,
        string? ManufacturerName, double? ManufacturerNameConfidence,
        string? ManufacturerPartNumber, double? ManufacturerPartNumberConfidence,
        string? AlternateProductName, double? AlternateProductNameConfidence,
        string? AlternatePartNumber, double? AlternatePartNumberConfidence,
        string? ItemText, double? ItemTextConfidence,
        string? MaterialPotext, double? MaterialPotextConfidence,
        string? LeadTime, double? LeadTimeConfidence,
        string? ReceivedDate, double? ReceivedDateConfidence,
        string? BidClosingDateLine, double? BidClosingDateLineConfidence,
        double? ItemConfidence,
        // Verbatim unrecognized document columns ({"original header": "cell value"}).
        // Optional + defaulted so existing positional construction sites and model
        // outputs that omit it keep working unchanged.
        [property: JsonConverter(typeof(LenientStringDictionaryConverter))]
        Dictionary<string, string>? ExtraFields = null,
        // Multi-inquiry detection: when one document contains several distinct
        // inquiries/RFQs, the model labels each item with its inquiry's RFQ number /
        // section label (identical label for items of the same inquiry). Null when the
        // document is a single inquiry. Optional + defaulted for the same reason as
        // ExtraFields.
        string? InquiryGroup = null,
        double? InquiryGroupConfidence = null,

        // ── CONVERSATIONAL ANCHORS ──────────────────────────────────────────────────
        // Emitted ONLY by the conversational (email-body) prompt, where there is no parsed
        // row count to conserve and therefore no way to tell an extracted item from an
        // invented one. SourceSpan is a <=120-character VERBATIM quote of the sentence that
        // requested this item; ProseAnchorVerifier re-finds it in the submitted text and
        // drops any item whose span is absent, out of order, or overlapping.
        // QuantityToken is the sender's literal quantity wording ("40 nos") — null when the
        // message stated none, which is precisely when Quantity must stay null instead of
        // being invented as 1. Trailing optional parameters: every existing positional
        // construction site and every model output that omits them keeps working unchanged.
        string? SourceSpan = null,
        string? QuantityToken = null);

    /// <summary>
    /// Tolerant reader for the LLM's ExtraFields object: accepts string/number/bool
    /// values (stringifying non-strings), skips nulls and nested structures, and
    /// treats any non-object token as "no extra fields" instead of failing the whole
    /// extraction parse.
    /// </summary>
    public sealed class LenientStringDictionaryConverter : JsonConverter<Dictionary<string, string>?>
    {
        public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip(); // null / array / scalar -> ignore gracefully
                return null;
            }

            var result = new Dictionary<string, string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString() ?? "";
                reader.Read();
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        result[key] = reader.GetString() ?? "";
                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        result[key] = System.Text.Encoding.UTF8.GetString(
                            reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray());
                        break;
                    default:
                        reader.Skip(); // null / object / array values are ignored
                        break;
                }
            }
            return result.Count > 0 ? result : null;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string>? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartObject();
            foreach (var (k, v) in value)
                writer.WriteString(k, v);
            writer.WriteEndObject();
        }
    }
}

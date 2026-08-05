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
        double? InquiryTypeConfidence = null);
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
        double? InquiryGroupConfidence = null);

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

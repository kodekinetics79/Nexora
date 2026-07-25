using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Services.Interfaces
{
    public interface ILLMService
    {
        AiProviderClass ProviderClass => AiProviderClass.External;

        Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default);

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

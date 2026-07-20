using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.Services.Interfaces
{
    // ─── Service-scope → BOQ draft wire contracts (WP-BOQ) ───────────────────
    //
    // Output schema of ILLMService.DraftServiceBoqAsync. Mirrors the
    // LeadExtractionResult conventions: records, per-field confidence, lenient
    // converters so a slightly-off model output degrades a field instead of
    // failing the whole parse. Quantities are decimal? — a null quantity means
    // "the document did not state one" and MUST surface as a TBD line, never a
    // guessed number.

    public record BoqDraftResult(
        string? ServiceCategory,
        double? OverallConfidence,
        List<BoqDraftSection>? Sections,
        [property: JsonConverter(typeof(LenientStringListConverter))]
        List<string>? Assumptions);

    public record BoqDraftSection(
        string? Title,
        List<BoqDraftItem>? Items);

    public record BoqDraftItem(
        string? Description,
        string? Unit,
        decimal? Quantity,
        string? ItemType,
        double? Confidence,
        [property: JsonConverter(typeof(LenientBoolConverter))]
        bool? Tbd = null,
        string? TbdReason = null,
        string? ItemCode = null);

    /// <summary>
    /// Tolerant reader for a JSON array of strings: stringifies numbers/bools,
    /// skips nulls and nested structures, and treats any non-array token as
    /// "no entries" instead of failing the whole parse.
    /// </summary>
    public sealed class LenientStringListConverter : JsonConverter<List<string>?>
    {
        public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip(); // null / object / scalar -> ignore gracefully
                return null;
            }

            var result = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        var s = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        result.Add(System.Text.Encoding.UTF8.GetString(
                            reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray()));
                        break;
                    default:
                        reader.Skip(); // nested objects/arrays and nulls are ignored
                        break;
                }
            }
            return result.Count > 0 ? result : null;
        }

        public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartArray();
            foreach (var v in value)
                writer.WriteStringValue(v);
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Tolerant reader for a nullable bool: accepts true/false, "true"/"false"
    /// (any casing), "yes"/"no", and 0/1; anything else reads as null instead of
    /// failing the whole parse.
    /// </summary>
    public sealed class LenientBoolConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var n)) return n != 0;
                    return null;
                case JsonTokenType.String:
                    var s = reader.GetString()?.Trim().ToLowerInvariant();
                    return s switch
                    {
                        "true" or "yes" or "1" => true,
                        "false" or "no" or "0" => false,
                        _ => null
                    };
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteBooleanValue(value.Value);
        }
    }
}

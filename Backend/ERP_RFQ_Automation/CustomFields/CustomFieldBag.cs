using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ERP_RFQ_Automation.CustomFields;

/// <summary>
/// Read/write hygiene for the jsonb custom-field value bag carried by an owning row
/// (Customer.CustomFieldsJson, Supplier.CustomFieldsJson, LeadItem.CustomFieldsJson).
///
/// Every method here is total: a malformed or non-object payload reads as "no values"
/// rather than throwing. The column is open text at the database level, so the read path
/// has to survive anything that has ever been written into it.
/// </summary>
public static class CustomFieldBag
{
    /// <summary>Defensive cap on how many custom-field values one row may carry.</summary>
    public const int MaximumKeys = 100;

    /// <summary>Defensive cap on serialized bag size (~16 KB).</summary>
    public const int MaximumSerializedChars = 16_384;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses a stored bag into a key → value map. Returns an empty map for null, blank,
    /// malformed, or non-object payloads. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyMap;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return EmptyMap;
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name)) continue;
                result[property.Name] = property.Value.Clone();
                if (result.Count >= MaximumKeys) break;
            }
            return result;
        }
        catch (JsonException)
        {
            return EmptyMap;
        }
    }

    /// <summary>
    /// Serializes a bag back to a jsonb payload, or null when nothing remains. Keys with a
    /// JSON null value are dropped — clearing a custom field removes it from the bag rather
    /// than storing an explicit null, so "unset" has exactly one representation.
    /// </summary>
    public static string? Write(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var node = new JsonObject();
        foreach (var (key, value) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            node[key] = JsonNode.Parse(value.GetRawText());
            if (node.Count >= MaximumKeys) break;
        }
        if (node.Count == 0) return null;

        var json = node.ToJsonString(SerializerOptions);
        if (json.Length > MaximumSerializedChars)
            throw new CustomFieldDomainException(
                $"Custom-field values for this record exceed the {MaximumSerializedChars} character limit.");
        return json;
    }

    /// <summary>
    /// Renders one stored value as the plain string a grid cell shows. Used by list
    /// projections so the client never has to know the underlying JSON shape.
    /// </summary>
    public static string? Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText()
    };

    /// <summary>Builds a JsonElement from a CLR value for the given declared type.</summary>
    internal static JsonElement Element(object? value)
    {
        var json = value switch
        {
            null => "null",
            string s => JsonSerializer.Serialize(s, SerializerOptions),
            bool b => b ? "true" : "false",
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value, SerializerOptions)
        };
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyMap =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

using System.Text.Json;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Closed, server-owned catalogue of boolean product entitlements. Plan JSON is retained as the
/// persistence format for backwards compatibility, but its keys are no longer an arbitrary bag
/// interpreted only by the console. Unknown keys and non-boolean values are rejected on writes;
/// absent or malformed values deny access.
/// </summary>
public static class TypedEntitlementCatalog
{
    public const string Rfq = "module.rfq";
    public const string Quotes = "module.quotes";
    public const string Orders = "module.orders";
    public const string Procurement = "module.procurement";
    public const string Inventory = "module.inventory";
    public const string Ai = "capability.ai";
    public const string Ocr = "capability.ocr";
    public const string Api = "capability.api";
    public const string EmailIntake = "capability.email-intake";
    public const string SupplierSearch = "capability.supplier-search";
    public const string Integrations = "capability.integrations";
    public const string Exports = "capability.exports";
    public const string Automation = "capability.automation";
    public const string Sso = "capability.sso";
    public const string Scim = "capability.scim";
    public const string DedicatedResources = "capability.dedicated-resources";

    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        Rfq, Quotes, Orders, Procurement, Inventory, Ai, Ocr, Api, EmailIntake,
        SupplierSearch, Integrations, Exports, Automation, Sso, Scim, DedicatedResources
    };

    /// <summary>
    /// Capabilities that have a production execution boundary today. A plan may retain a false
    /// packaging flag for a future capability, but setting that flag true cannot make an
    /// unimplemented product surface spring into existence: runtime authorization still denies.
    /// </summary>
    public static readonly IReadOnlySet<string> RuntimeAvailableKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        Rfq, Quotes, Orders, Procurement, Inventory, Ai, Ocr, EmailIntake,
        SupplierSearch, Integrations, Exports
    };

    public static bool IsRuntimeAvailable(string key) => RuntimeAvailableKeys.Contains(key);

    public static string RequireKnown(string key)
        => Keys.Contains(key)
            ? key
            : throw new ArgumentOutOfRangeException(nameof(key), key,
                "Entitlement declarations must use the closed server catalogue.");

    public static bool TryParse(
        string? json,
        out IReadOnlyDictionary<string, bool> values,
        out string? error)
    {
        values = new Dictionary<string, bool>(StringComparer.Ordinal);
        error = null;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Entitlements must be a JSON object.";
                return false;
            }

            var parsed = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Keys.Contains(property.Name))
                {
                    error = $"Unknown entitlement '{property.Name}'. Allowed keys: {string.Join(", ", Keys.Order())}.";
                    return false;
                }

                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"Entitlement '{property.Name}' must be true or false.";
                    return false;
                }

                parsed[property.Name] = property.Value.GetBoolean();
            }

            values = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "Entitlements must be valid JSON.";
            return false;
        }
    }

    /// <summary>Default deny: absent, unknown and malformed values are never enabled.</summary>
    public static bool IsEnabled(string? json, string key)
        => Keys.Contains(key)
           && TryParse(json, out var values, out _)
           && values.TryGetValue(key, out var enabled)
           && enabled;
}

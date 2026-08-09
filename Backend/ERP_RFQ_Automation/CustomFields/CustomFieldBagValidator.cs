using System.Globalization;
using System.Text.Json;

namespace ERP_RFQ_Automation.CustomFields;

/// <summary>
/// Type enforcement for the jsonb custom-field value bag.
///
/// A custom field that accepts anything is a data-quality liability, not a feature: the
/// whole point of asking a tenant to declare a data type is that a Decimal field cannot
/// later contain "ask Ahmed", and a Date field cannot contain "next week". Every write
/// path goes through <see cref="ValidateAndMerge"/>; nothing writes the bag directly.
///
/// The validator works against the ACTIVE version of each definition. A retired definition
/// keeps whatever value is already in the bag (history must not be rewritten by a
/// governance decision) but rejects new writes to that key.
/// </summary>
public static class CustomFieldBagValidator
{
    /// <summary>
    /// Applies <paramref name="updates"/> onto <paramref name="existingJson"/> after
    /// validating each value against its definition's declared type and constraints.
    /// </summary>
    /// <param name="activeDefinitions">
    /// The tenant's ACTIVE custom-field definitions for the owning entity type. Must already
    /// be tenant-filtered by the caller — this method does no data access and therefore
    /// enforces no isolation of its own.
    /// </param>
    /// <param name="existingJson">Current bag contents, or null.</param>
    /// <param name="updates">
    /// Key → value to set. A JSON null (or <see cref="JsonValueKind.Undefined"/>) clears the
    /// key. Keys absent from this map are left untouched.
    /// </param>
    /// <param name="enforceRequired">
    /// When true, a definition marked required must end up with a value. Callers doing a
    /// partial patch of a record that predates the field should pass false and let the
    /// full-record save enforce it, otherwise every unrelated edit fails.
    /// </param>
    /// <returns>The new bag payload, or null when the bag ends up empty.</returns>
    /// <exception cref="CustomFieldDomainException">
    /// Thrown for an unknown/retired key, a value whose JSON kind does not match the declared
    /// data type, a value outside a declared range or length, an option value that is not in
    /// the option list, or a missing required value.
    /// </exception>
    public static string? ValidateAndMerge(
        IReadOnlyList<CustomFieldDefinition> activeDefinitions,
        string? existingJson,
        IReadOnlyDictionary<string, JsonElement> updates,
        bool enforceRequired = true)
    {
        ArgumentNullException.ThrowIfNull(activeDefinitions);
        ArgumentNullException.ThrowIfNull(updates);

        var byKey = new Dictionary<string, (CustomFieldDefinition Definition, CustomFieldVersion Version)>(
            StringComparer.Ordinal);
        foreach (var definition in activeDefinitions)
        {
            if (definition.Status != CustomFieldDefinitionStatus.Active) continue;
            var active = definition.Versions.FirstOrDefault(v => v.VersionNumber == definition.ActiveVersionNumber);
            if (active is null) continue;
            byKey[definition.StableKey] = (definition, active);
        }

        var merged = new Dictionary<string, JsonElement>(CustomFieldBag.Read(existingJson), StringComparer.Ordinal);

        foreach (var (rawKey, value) in updates)
        {
            var key = (rawKey ?? string.Empty).Trim();
            if (key.Length == 0)
                throw new CustomFieldDomainException("A custom-field key is required.");
            if (!byKey.TryGetValue(key, out var target))
                throw new CustomFieldDomainException(
                    $"'{key}' is not an active custom field on this record.");

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (target.Version.IsRequired && enforceRequired)
                    throw new CustomFieldDomainException($"'{target.Version.Label}' is required.");
                merged.Remove(key);
                continue;
            }

            merged[key] = Coerce(target.Version, value);
        }

        if (enforceRequired)
        {
            foreach (var (key, target) in byKey)
            {
                if (!target.Version.IsRequired) continue;
                if (!merged.TryGetValue(key, out var stored) || IsBlank(stored))
                    throw new CustomFieldDomainException($"'{target.Version.Label}' is required.");
            }
        }

        if (merged.Count > CustomFieldBag.MaximumKeys)
            throw new CustomFieldDomainException(
                $"A record cannot carry more than {CustomFieldBag.MaximumKeys} custom-field values.");

        return CustomFieldBag.Write(merged);
    }

    /// <summary>
    /// Validates one value against a declared type and returns the canonical stored form.
    /// Numbers arrive from JSON as numbers; dates and timestamps arrive as ISO-8601 strings
    /// and are re-emitted in a single canonical format so sorting a grid column is stable.
    /// </summary>
    private static JsonElement Coerce(CustomFieldVersion version, JsonElement value)
    {
        switch (version.DataType)
        {
            case CustomFieldDataType.Text:
            {
                var text = RequireString(version, value);
                if (version.MinimumLength is { } min && text.Length < min)
                    throw new CustomFieldDomainException(
                        $"'{version.Label}' must contain at least {min} characters.");
                if (version.MaximumLength is { } max && text.Length > max)
                    throw new CustomFieldDomainException($"'{version.Label}' cannot exceed {max} characters.");
                return CustomFieldBag.Element(text);
            }

            case CustomFieldDataType.Integer:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
                    throw new CustomFieldDomainException($"'{version.Label}' must be a whole number.");
                RequireRange(version, number);
                return CustomFieldBag.Element(number);
            }

            case CustomFieldDataType.Decimal:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                    throw new CustomFieldDomainException($"'{version.Label}' must be a number.");
                RequireRange(version, number);
                return CustomFieldBag.Element(number);
            }

            case CustomFieldDataType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new CustomFieldDomainException($"'{version.Label}' must be true or false.");
                return CustomFieldBag.Element(value.GetBoolean());

            case CustomFieldDataType.Date:
            {
                var text = RequireString(version, value);
                if (!DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new CustomFieldDomainException($"'{version.Label}' must be a date (YYYY-MM-DD).");
                return CustomFieldBag.Element(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            case CustomFieldDataType.Timestamp:
            {
                var text = RequireString(version, value);
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment))
                    throw new CustomFieldDomainException($"'{version.Label}' must be an ISO-8601 timestamp.");
                return CustomFieldBag.Element(moment.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }

            case CustomFieldDataType.Option:
            {
                var text = RequireString(version, value);
                RequireOption(version, text);
                return CustomFieldBag.Element(text);
            }

            case CustomFieldDataType.MultiOption:
            {
                if (value.ValueKind != JsonValueKind.Array)
                    throw new CustomFieldDomainException($"'{version.Label}' must be a list of options.");
                var selected = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new CustomFieldDomainException($"'{version.Label}' must be a list of option keys.");
                    var option = (item.GetString() ?? string.Empty).Trim();
                    RequireOption(version, option);
                    if (selected.Contains(option, StringComparer.OrdinalIgnoreCase))
                        throw new CustomFieldDomainException($"'{version.Label}' cannot repeat an option.");
                    selected.Add(option);
                }
                return CustomFieldBag.Element(selected);
            }

            case CustomFieldDataType.Json:
                // Any well-formed JSON is acceptable here BY DECLARATION — the tenant chose an
                // untyped field. It is still bounded by the bag size cap.
                return value.Clone();

            case CustomFieldDataType.Reference:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0)
                    throw new CustomFieldDomainException(
                        $"'{version.Label}' must reference a persisted record by id.");
                return CustomFieldBag.Element(id);
            }

            default:
                throw new CustomFieldDomainException($"Unsupported custom-field type {version.DataType}.");
        }
    }

    private static string RequireString(CustomFieldVersion version, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new CustomFieldDomainException(
                $"'{version.Label}' must be text (declared type {version.DataType}).");
        var text = value.GetString() ?? string.Empty;
        if (version.IsRequired && string.IsNullOrWhiteSpace(text))
            throw new CustomFieldDomainException($"'{version.Label}' is required.");
        return text;
    }

    private static void RequireRange(CustomFieldVersion version, decimal value)
    {
        if (version.MinimumValue is { } min && value < min)
            throw new CustomFieldDomainException($"'{version.Label}' cannot be less than {min}.");
        if (version.MaximumValue is { } max && value > max)
            throw new CustomFieldDomainException($"'{version.Label}' cannot exceed {max}.");
    }

    private static void RequireOption(CustomFieldVersion version, string option)
    {
        if (version.Options.All(x => !x.StableKey.Equals(option, StringComparison.OrdinalIgnoreCase)))
            throw new CustomFieldDomainException($"'{option}' is not an allowed option for '{version.Label}'.");
    }

    private static bool IsBlank(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false
    };
}

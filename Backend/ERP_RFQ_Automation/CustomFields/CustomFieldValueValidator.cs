using System.Text.Json;

namespace ERP_RFQ_Automation.CustomFields;

public static class CustomFieldValueValidator
{
    public static void Validate(CustomFieldVersion version, CustomFieldValueInput input)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(input);

        if ((input.ReferenceId.HasValue) != !string.IsNullOrWhiteSpace(input.ReferenceType))
            throw new CustomFieldDomainException("Reference type and reference ID must be supplied together.");

        var populatedColumns = CountPopulatedColumns(input);
        if (populatedColumns == 0)
        {
            if (version.IsRequired)
                throw new CustomFieldDomainException($"'{version.Label}' is required.");
            return;
        }
        if (populatedColumns != 1)
            throw new CustomFieldDomainException("Exactly one typed value must be supplied.");

        switch (version.DataType)
        {
            case CustomFieldDataType.Text:
                RequireOnly(input.Text is not null, version);
                ValidateText(version, input.Text!);
                break;
            case CustomFieldDataType.Integer:
                RequireOnly(input.Integer.HasValue, version);
                ValidateNumber(version, input.Integer!.Value);
                break;
            case CustomFieldDataType.Decimal:
                RequireOnly(input.Decimal.HasValue, version);
                ValidateNumber(version, input.Decimal!.Value);
                break;
            case CustomFieldDataType.Boolean:
                RequireOnly(input.Boolean.HasValue, version);
                break;
            case CustomFieldDataType.Date:
                RequireOnly(input.Date.HasValue, version);
                break;
            case CustomFieldDataType.Timestamp:
                RequireOnly(input.Timestamp.HasValue, version);
                if (input.Timestamp!.Value.Kind != DateTimeKind.Utc)
                    throw new CustomFieldDomainException("Timestamp custom-field values must be UTC.");
                break;
            case CustomFieldDataType.Option:
                RequireOnly(input.Text is not null, version);
                ValidateOption(version, input.Text!);
                break;
            case CustomFieldDataType.MultiOption:
                RequireOnly(input.Json is not null, version);
                ValidateMultiOption(version, input.Json!);
                break;
            case CustomFieldDataType.Json:
                RequireOnly(input.Json is not null, version);
                ValidateJson(input.Json!);
                break;
            case CustomFieldDataType.Reference:
                RequireOnly(input.ReferenceId.HasValue, version);
                if (string.IsNullOrWhiteSpace(input.ReferenceType))
                    throw new CustomFieldDomainException("Reference values require a reference type.");
                break;
            default:
                throw new CustomFieldDomainException($"Unsupported custom-field type {version.DataType}.");
        }
    }

    private static int CountPopulatedColumns(CustomFieldValueInput input) =>
        (input.Text is null ? 0 : 1) +
        (input.Integer.HasValue ? 1 : 0) +
        (input.Decimal.HasValue ? 1 : 0) +
        (input.Boolean.HasValue ? 1 : 0) +
        (input.Date.HasValue ? 1 : 0) +
        (input.Timestamp.HasValue ? 1 : 0) +
        (input.Json is null ? 0 : 1) +
        (input.ReferenceId.HasValue ? 1 : 0);

    private static void RequireOnly(bool matchesType, CustomFieldVersion version)
    {
        if (!matchesType)
            throw new CustomFieldDomainException($"Value does not match data type {version.DataType}.");
    }

    private static void ValidateText(CustomFieldVersion version, string value)
    {
        if (version.IsRequired && string.IsNullOrWhiteSpace(value))
            throw new CustomFieldDomainException($"'{version.Label}' is required.");
        if (version.MinimumLength.HasValue && value.Length < version.MinimumLength.Value)
            throw new CustomFieldDomainException($"'{version.Label}' must contain at least {version.MinimumLength} characters.");
        if (version.MaximumLength.HasValue && value.Length > version.MaximumLength.Value)
            throw new CustomFieldDomainException($"'{version.Label}' cannot exceed {version.MaximumLength} characters.");
    }

    private static void ValidateNumber(CustomFieldVersion version, decimal value)
    {
        if (version.MinimumValue.HasValue && value < version.MinimumValue.Value)
            throw new CustomFieldDomainException($"'{version.Label}' cannot be less than {version.MinimumValue}.");
        if (version.MaximumValue.HasValue && value > version.MaximumValue.Value)
            throw new CustomFieldDomainException($"'{version.Label}' cannot exceed {version.MaximumValue}.");
    }

    private static void ValidateOption(CustomFieldVersion version, string option)
    {
        if (version.Options.All(x => !x.StableKey.Equals(option, StringComparison.OrdinalIgnoreCase)))
            throw new CustomFieldDomainException($"'{option}' is not an allowed option for '{version.Label}'.");
    }

    private static void ValidateMultiOption(CustomFieldVersion version, string json)
    {
        try
        {
            var options = JsonSerializer.Deserialize<string[]>(json)
                ?? throw new CustomFieldDomainException("Multi-option values must be a JSON string array.");
            if (options.Length == 0 && version.IsRequired)
                throw new CustomFieldDomainException($"'{version.Label}' is required.");
            if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Length)
                throw new CustomFieldDomainException("Multi-option values cannot contain duplicates.");
            foreach (var option in options) ValidateOption(version, option);
        }
        catch (JsonException exception)
        {
            throw new CustomFieldDomainException($"Multi-option value is invalid JSON: {exception.Message}");
        }
    }

    private static void ValidateJson(string json)
    {
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException exception)
        {
            throw new CustomFieldDomainException($"JSON custom-field value is invalid: {exception.Message}");
        }
    }
}

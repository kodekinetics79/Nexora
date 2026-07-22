using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.CustomFields;

public static partial class CustomFieldGovernance
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "business_unit_id", "businessunitid", "tenant_id", "tenantid",
        "created_on", "createdon", "created_by", "createdby", "updated_on", "updatedon",
        "deleted_on", "deletedon", "row_version", "rowversion", "version",
        "status", "reference", "master_reference", "commercial_case_id", "commercialcaseid"
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{2,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableKeyPattern();

    public static string NormalizeAndValidateStableKey(string stableKey)
    {
        var normalized = (stableKey ?? string.Empty).Trim().ToLowerInvariant();
        if (!StableKeyPattern().IsMatch(normalized))
            throw new CustomFieldDomainException(
                "Stable keys must be 3-63 characters and contain only lowercase letters, numbers, and underscores.");
        if (ReservedNames.Contains(normalized))
            throw new CustomFieldDomainException($"'{normalized}' is reserved by the platform.");
        return normalized;
    }

    public static string ValidateEntityType(string entityType)
    {
        var normalized = (entityType ?? string.Empty).Trim();
        if (normalized.Length is < 2 or > 80)
            throw new CustomFieldDomainException("Entity type must be 2-80 characters.");
        return normalized;
    }
}

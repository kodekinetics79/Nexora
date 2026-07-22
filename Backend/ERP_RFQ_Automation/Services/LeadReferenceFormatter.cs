using System.Globalization;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Services;

public static partial class LeadReferenceFormatter
{
    private static readonly string[] AllowedTokens =
        ["{PREFIX}", "{YEAR}", "{FY}", "{BU}", "{SOURCE}", "{SEQUENCE}"];

    public static string Format(
        LeadReferenceConfiguration configuration,
        string businessUnitCode,
        string? leadSource,
        DateTime createdOn,
        long sequence)
    {
        Validate(configuration);

        var financialYearStart = createdOn.Month < configuration.FinancialYearStartMonth
            ? createdOn.Year - 1
            : createdOn.Year;
        var financialYear = $"{financialYearStart:D4}-{(financialYearStart + 1) % 100:D2}";
        var value = configuration.Format
            .Replace("{PREFIX}", Sanitize(configuration.Prefix), StringComparison.Ordinal)
            .Replace("{YEAR}", createdOn.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{FY}", financialYear, StringComparison.Ordinal)
            .Replace("{BU}", Sanitize(businessUnitCode), StringComparison.Ordinal)
            .Replace("{SOURCE}", Sanitize(leadSource), StringComparison.Ordinal)
            .Replace("{SEQUENCE}", sequence.ToString($"D{configuration.SequencePadding}", CultureInfo.InvariantCulture), StringComparison.Ordinal);

        if (value.Length is 0 or > 100)
            throw new InvalidOperationException("The configured lead-reference format must produce between 1 and 100 characters.");

        return value;
    }

    public static void Validate(LeadReferenceConfiguration configuration)
    {
        if (configuration.SequencePadding is < 1 or > 18)
            throw new InvalidOperationException("Sequence padding must be between 1 and 18.");
        if (configuration.FinancialYearStartMonth is < 1 or > 12)
            throw new InvalidOperationException("Financial-year start month must be between 1 and 12.");
        if (!configuration.Format.Contains("{SEQUENCE}", StringComparison.Ordinal))
            throw new InvalidOperationException("Lead-reference format must contain {SEQUENCE}.");

        var unknownTokens = TokenPattern().Matches(configuration.Format)
            .Select(match => match.Value)
            .Except(AllowedTokens, StringComparer.Ordinal)
            .ToArray();
        if (unknownTokens.Length > 0)
            throw new InvalidOperationException($"Unknown lead-reference token(s): {string.Join(", ", unknownTokens)}.");
    }

    private static string Sanitize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NonReferenceCharacterPattern().Replace(value.Trim().ToUpperInvariant(), string.Empty);

    [GeneratedRegex(@"\{[A-Z_]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[^A-Z0-9_-]", RegexOptions.CultureInvariant)]
    private static partial Regex NonReferenceCharacterPattern();
}

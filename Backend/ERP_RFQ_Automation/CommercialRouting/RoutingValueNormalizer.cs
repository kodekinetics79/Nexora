using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.CommercialRouting;

public static partial class RoutingValueNormalizer
{
    public static string Normalize(CustomerIdentifierType type, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Identifier value is required.", nameof(value));

        var trimmed = value.Trim();
        return type switch
        {
            CustomerIdentifierType.Email => trimmed.ToLowerInvariant(),
            CustomerIdentifierType.Domain => NormalizeDomain(trimmed),
            CustomerIdentifierType.Phone => NonDigits().Replace(trimmed, string.Empty),
            CustomerIdentifierType.CustomerName or CustomerIdentifierType.Alias =>
                Whitespace().Replace(trimmed, " ").ToUpperInvariant(),
            _ => NonAlphaNumeric().Replace(trimmed, string.Empty).ToUpperInvariant()
        };
    }

    public static string? DomainFromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        return at > 0 && at < email.Length - 1 ? NormalizeDomain(email[(at + 1)..]) : null;
    }

    private static string NormalizeDomain(string value)
    {
        var domain = value.Trim().ToLowerInvariant();
        if (domain.StartsWith("http://", StringComparison.Ordinal)) domain = domain[7..];
        if (domain.StartsWith("https://", StringComparison.Ordinal)) domain = domain[8..];
        if (domain.StartsWith("www.", StringComparison.Ordinal)) domain = domain[4..];
        var slash = domain.IndexOf('/');
        if (slash >= 0) domain = domain[..slash];
        return domain.TrimEnd('.');
    }

    [GeneratedRegex("[^0-9]")]
    private static partial Regex NonDigits();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}

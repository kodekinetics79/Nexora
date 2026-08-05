using System.Text.RegularExpressions;
using ERP_RFQ_Automation.CustomerResolution;

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
            // ONE normaliser for organisation names, platform-wide: the routing store and
            // the client resolver must agree about what "the same name" is, or a learned
            // alias would be written under one key and looked up under another.
            CustomerIdentifierType.CustomerName or CustomerIdentifierType.Alias or CustomerIdentifierType.Portal =>
                CustomerNameNormalizer.LooseKey(trimmed),
            // "PORTAL|OUR-VENDOR-CODE": each half is normalised by its own rule, and the
            // separator is preserved because the PAIR is the identity.
            CustomerIdentifierType.PortalAccount => NormalizePortalAccount(trimmed),
            // A generated regex shape. Stripping its punctuation would destroy it, so the
            // value is stored exactly as derived (see RfqNumberPattern.Derive).
            CustomerIdentifierType.RfqNumberPattern => trimmed,
            _ => NonAlphaNumeric().Replace(trimmed, string.Empty).ToUpperInvariant()
        };
    }

    private static string NormalizePortalAccount(string value)
    {
        var separator = value.IndexOf('|');
        if (separator <= 0 || separator == value.Length - 1)
            return CustomerNameNormalizer.LooseKey(value);
        var portal = CustomerNameNormalizer.LooseKey(value[..separator]);
        var account = NonAlphaNumeric().Replace(value[(separator + 1)..], string.Empty).ToUpperInvariant();
        return $"{portal}|{account}";
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
}

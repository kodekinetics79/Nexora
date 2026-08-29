using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Extraction.Quantities;
using ERP_RFQ_Automation.Services.Uom;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

/// <summary>
/// One deterministic interpretation of the quote-critical source fields used by both the
/// workbench and the participation/RFQ enforcement boundary. A verified prose citation may
/// cover all three values only when the same retained span contains an exact commercial
/// identity and an exact quantity/UOM pair. It never turns a generic description into proof.
/// </summary>
internal static partial class CriticalSourceEvidence
{
    internal sealed record Field(string FieldName, string? RawValue, string? NormalizedValue);

    internal sealed record DerivedSpanFields(
        string IdentityFieldName,
        string IdentityRawValue,
        string QuantityRawValue,
        string UnitOfMeasureRawValue);

    internal sealed record Assessment(bool Identity, bool Quantity, bool UnitOfMeasure)
    {
        internal bool Complete => Identity && Quantity && UnitOfMeasure;

        internal IReadOnlyList<string> Missing()
        {
            var missing = new List<string>(3);
            if (!Identity) missing.Add("item identity/description");
            if (!Quantity) missing.Add("quantity");
            if (!UnitOfMeasure) missing.Add("unit of measure");
            return missing;
        }
    }

    internal static Assessment Assess(
        IEnumerable<Field> evidence,
        IEnumerable<string?> identityValues,
        decimal? quantity,
        string? unitOfMeasure)
    {
        var fields = evidence.ToArray();
        var identities = identityValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedUom = UomCanonicalizer.CanonicalizeForStorage(unitOfMeasure);

        var identity = identities.Length > 0 && fields.Any(field =>
            IdentityFieldNames.Contains(CanonicalFieldName(field.FieldName))
            && Values(field).Any(value => identities.Any(expected => SameCommercialText(value, expected))));
        var exactQuantity = quantity.HasValue && fields.Any(field =>
            CanonicalFieldName(field.FieldName) == "QUANTITY"
            && Values(field).Any(value => QuantityParser.Parse(value).Value == quantity.Value));
        var uom = expectedUom is not null && fields.Any(field =>
            CanonicalFieldName(field.FieldName) is "UNITOFMEASURE" or "UOM"
            && Values(field).Any(value => string.Equals(
                UomCanonicalizer.CanonicalizeForStorage(value), expectedUom,
                StringComparison.OrdinalIgnoreCase)));

        // Historical conversational ingestion retained one server-verified verbatim span per
        // line. That citation is admissible as composite evidence only when the SAME span proves
        // identity, quantity and UOM. This is intentionally stronger than treating SourceSpan as
        // a generic identity field, which was the workbench/commit divergence that exposed the
        // production defect.
        var composite = quantity.HasValue && expectedUom is not null && identities.Length > 0
            && fields.Where(field => CanonicalFieldName(field.FieldName) == "SOURCESPAN")
                .SelectMany(Values)
                .Any(span => ContainsIdentity(span, identities)
                    && ContainsQuantityAndUom(span, quantity.Value, expectedUom));

        return composite
            ? new Assessment(true, true, true)
            : new Assessment(identity, exactQuantity, uom);
    }

    internal static DerivedSpanFields? DeriveFromVerifiedSpan(
        string? span,
        IEnumerable<(string FieldName, string? Value)> identityCandidates,
        decimal? quantity,
        string? unitOfMeasure)
    {
        if (string.IsNullOrWhiteSpace(span) || !quantity.HasValue) return null;
        var expectedUom = UomCanonicalizer.CanonicalizeForStorage(unitOfMeasure);
        if (expectedUom is null) return null;

        var identity = identityCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
            .Select(candidate => (candidate.FieldName, Value: candidate.Value!.Trim()))
            .Select(candidate => TryFindBounded(span, candidate.Value, out var exact)
                ? (Found: true, candidate.FieldName, Exact: exact)
                : (Found: false, candidate.FieldName, Exact: string.Empty))
            .FirstOrDefault(candidate => candidate.Found);
        if (!identity.Found) return null;

        foreach (Match match in QuantityUom().Matches(span))
        {
            var reading = QuantityParser.Parse(match.Value);
            if (reading.Value != quantity.Value || string.IsNullOrWhiteSpace(reading.UnitToken)
                || !string.Equals(UomCanonicalizer.CanonicalizeForStorage(reading.UnitToken), expectedUom,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            return new DerivedSpanFields(
                identity.FieldName,
                identity.Exact,
                match.Groups["quantity"].Value,
                match.Groups["uom"].Value);
        }
        return null;
    }

    private static bool ContainsIdentity(string span, IReadOnlyCollection<string> identities)
        => identities.Any(identity => ContainsBounded(span, identity));

    private static bool ContainsQuantityAndUom(string span, decimal expectedQuantity, string expectedUom)
    {
        foreach (Match match in QuantityUom().Matches(span))
        {
            var reading = QuantityParser.Parse(match.Value);
            if (reading.Value != expectedQuantity || string.IsNullOrWhiteSpace(reading.UnitToken))
                continue;
            if (string.Equals(UomCanonicalizer.CanonicalizeForStorage(reading.UnitToken), expectedUom,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsBounded(string source, string expected)
        => TryFindBounded(source, expected, out _);

    private static bool TryFindBounded(string source, string expected, out string exact)
    {
        exact = string.Empty;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(expected)) return false;
        var from = 0;
        while (from < source.Length)
        {
            var index = source.IndexOf(expected, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + expected.Length;
            var leftBounded = index == 0 || !char.IsLetterOrDigit(source[index - 1]);
            var rightBounded = end == source.Length || !char.IsLetterOrDigit(source[end]);
            if (leftBounded && rightBounded)
            {
                exact = source.Substring(index, expected.Length);
                return true;
            }
            from = index + 1;
        }
        return false;
    }

    private static IEnumerable<string> Values(Field field)
        => new[] { field.NormalizedValue, field.RawValue }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());

    private static bool SameCommercialText(string left, string right)
        => string.Equals(CanonicalCommercialText(left), CanonicalCommercialText(right),
            StringComparison.Ordinal);

    private static string CanonicalCommercialText(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string CanonicalFieldName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static readonly HashSet<string> IdentityFieldNames = new(StringComparer.Ordinal)
    {
        "PRODUCTNAME", "PRODUCTSHORTNAME", "PRODUCTSHORTDESCRIPTION", "ITEMMATERIALCODE",
        "MANUFACTURERPARTNUMBER", "ITEMTEXT", "REQUESTEDLINE"
    };

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?<quantity>\d{1,14}(?:[.,]\d{1,6})?)\s*(?<uom>[\p{L}][\p{L}\p{N}²³./-]{0,24})(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex QuantityUom();
}

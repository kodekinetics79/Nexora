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
    internal sealed record Identity(string FieldName, string? Value);

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
        IEnumerable<Identity> identityValues,
        decimal? quantity,
        string? unitOfMeasure)
    {
        var fields = evidence.ToArray();
        var identities = identityValues
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
            .Select(candidate => new Identity(
                CanonicalFieldName(candidate.FieldName), candidate.Value!.Trim()))
            .DistinctBy(candidate => (candidate.FieldName, candidate.Value),
                IdentityComparer.Instance)
            .ToArray();
        var expectedUom = UomCanonicalizer.CanonicalizeForStorage(unitOfMeasure);

        var identity = HasTypedIdentity(fields, identities);
        var exactQuantity = quantity.HasValue && fields.Any(field =>
            CanonicalFieldName(field.FieldName) == "QUANTITY"
            && Values(field).Any(value => QuantityParser.Parse(value).Value == quantity.Value));
        var uom = expectedUom is not null && fields.Any(field =>
            CanonicalFieldName(field.FieldName) is "UNITOFMEASURE" or "UOM"
            && Values(field).Any(value => string.Equals(
                UomCanonicalizer.CanonicalizeForStorage(value), expectedUom,
                StringComparison.OrdinalIgnoreCase)));

        // SourceSpan is a citation, not a typed commercial fact. Newly ingested exact spans are
        // projected into typed fields by DeriveFromVerifiedSpan; historical spans require the
        // governed human-review audit. Keeping that distinction here prevents a generic or
        // multi-item paragraph from silently becoming an RFQ authorization boundary.
        return new Assessment(identity, exactQuantity, uom);
    }

    internal static DerivedSpanFields? DeriveFromVerifiedSpan(
        string? span,
        IEnumerable<Identity> identityCandidates,
        decimal? quantity,
        string? unitOfMeasure)
    {
        if (string.IsNullOrWhiteSpace(span) || !quantity.HasValue
            || span.Contains("[REDACTED_", StringComparison.OrdinalIgnoreCase)) return null;
        var expectedUom = UomCanonicalizer.CanonicalizeForStorage(unitOfMeasure);
        if (expectedUom is null) return null;

        var candidates = identityCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
            .Select(candidate => new Identity(candidate.FieldName, candidate.Value!.Trim()))
            .ToArray();
        var strong = candidates.Where(candidate => StrongIdentityFieldNames.Contains(
            CanonicalFieldName(candidate.FieldName))).ToArray();
        var admissible = strong.Length > 0 ? strong : candidates;
        var identityMatches = admissible.Select(candidate =>
            TryFindCommercialToken(span, candidate.Value!, strong.Length > 0, out var index, out var exact)
                ? (Found: true, candidate.FieldName, Index: index, Exact: exact)
                : (Found: false, candidate.FieldName, Index: -1, Exact: string.Empty))
            .Where(candidate => candidate.Found).ToArray();
        if (identityMatches.Length != 1) return null;

        var quantityMatches = QuantityUom().Matches(span).Cast<Match>().ToArray();
        if (quantityMatches.Length != 1) return null;
        var match = quantityMatches[0];
        var reading = QuantityParser.Parse(match.Value);
        if (reading.Value != quantity.Value || string.IsNullOrWhiteSpace(reading.UnitToken)
            || !string.Equals(UomCanonicalizer.CanonicalizeForStorage(reading.UnitToken), expectedUom,
                StringComparison.OrdinalIgnoreCase))
            return null;

        var identity = identityMatches[0];
        var betweenStart = Math.Min(identity.Index, match.Index);
        var betweenEnd = Math.Max(identity.Index + identity.Exact.Length, match.Index + match.Length);
        if (betweenEnd - betweenStart > 160) return null;
        var between = span[betweenStart..betweenEnd];
        if (between.IndexOfAny(['\n', '\r', ';']) >= 0) return null;

        return new DerivedSpanFields(
            identity.FieldName,
            identity.Exact,
            match.Groups["quantity"].Value,
            match.Groups["uom"].Value);
    }

    private static bool TryFindCommercialToken(
        string source, string expected, bool strong, out int matchIndex, out string exact)
    {
        matchIndex = -1;
        exact = string.Empty;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(expected)) return false;
        var from = 0;
        while (from < source.Length)
        {
            var index = source.IndexOf(expected, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + expected.Length;
            var leftBounded = index == 0 || !IsCommercialTokenContinuation(source[index - 1], strong);
            var rightBounded = end == source.Length || !IsCommercialTokenContinuation(source[end], strong);
            if (leftBounded && rightBounded)
            {
                matchIndex = index;
                exact = source.Substring(index, expected.Length);
                return true;
            }
            from = index + 1;
        }
        return false;
    }

    private static bool IsCommercialTokenContinuation(char value, bool strong)
        => char.IsLetterOrDigit(value) || strong && value is '-' or '_' or '.' or '/' or '#';

    private static bool HasTypedIdentity(IReadOnlyCollection<Field> fields,
        IReadOnlyCollection<Identity> identities)
    {
        if (identities.Count == 0) return false;
        var strong = identities.Where(candidate => StrongIdentityFieldNames.Contains(
            candidate.FieldName)).ToArray();
        if (strong.Length > 0)
        {
            var compared = false;
            var matched = false;
            foreach (var expected in strong)
            {
                var stated = fields.Where(field => CanonicalFieldName(field.FieldName) == expected.FieldName)
                    .SelectMany(Values).ToArray();
                if (stated.Length == 0) continue;
                compared = true;
                if (!stated.Any(value => SameCommercialText(value, expected.Value!))) return false;
                matched = true;
            }
            if (compared) return matched;
            // Historical deterministic parsers stored a strong material/part token under
            // requestedLine while preserving the exact normalized value. It remains admissible
            // only when no explicit strong field contradicts it and the value matches a strong
            // canonical identifier; a generic description can never satisfy this branch.
            return fields.Where(field => IdentityFieldNames.Contains(CanonicalFieldName(field.FieldName))
                    && CanonicalFieldName(field.FieldName) != "SOURCESPAN")
                .SelectMany(Values)
                .Any(value => strong.Any(expected => SameCommercialText(value, expected.Value!)));
        }

        return fields.Any(field => IdentityFieldNames.Contains(CanonicalFieldName(field.FieldName))
            && Values(field).Any(value => identities.Any(expected =>
                SameCommercialText(value, expected.Value!))));
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

    private static readonly HashSet<string> StrongIdentityFieldNames = new(StringComparer.Ordinal)
    {
        "ITEMMATERIALCODE", "MANUFACTURERPARTNUMBER"
    };

    private sealed class IdentityComparer : IEqualityComparer<(string FieldName, string? Value)>
    {
        internal static readonly IdentityComparer Instance = new();
        public bool Equals((string FieldName, string? Value) x, (string FieldName, string? Value) y)
            => string.Equals(x.FieldName, y.FieldName, StringComparison.Ordinal)
                && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string FieldName, string? Value) value)
            => HashCode.Combine(value.FieldName,
                value.Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value));
    }

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?<quantity>\d{1,14}(?:[.,]\d{1,6})?)\s*(?<uom>[\p{L}][\p{L}\p{N}²³./-]{0,24})(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex QuantityUom();
}

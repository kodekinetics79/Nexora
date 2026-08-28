using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Services.Uom;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Finds contradictory commercial values for the same strong line identity across different
/// parts of one email. It never chooses a winner: the body and an attachment are both source
/// evidence, so selecting either quantity would manufacture precedence the sender did not state.
/// </summary>
internal static class EmailInquiryCommercialConflictDetector
{
    internal sealed record ComponentLines(long ComponentId, IReadOnlyList<LeadItemData> Items);

    internal static int Count(IReadOnlyList<ComponentLines> components)
    {
        var candidates = components
            .SelectMany(component => component.Items.Select(item => new
            {
                component.ComponentId,
                Item = item,
                Identity = StableIdentity(item)
            }))
            .Where(candidate => candidate.Identity is not null)
            .ToArray();

        var conflicts = 0;
        foreach (var group in candidates.GroupBy(candidate => candidate.Identity!, StringComparer.Ordinal))
        {
            var lines = group.ToArray();
            var groupConflicts = false;
            for (var left = 0; left < lines.Length && !groupConflicts; left++)
            for (var right = left + 1; right < lines.Length; right++)
            {
                if (lines[left].ComponentId == lines[right].ComponentId) continue;
                if (!CommercialValuesContradict(lines[left].Item, lines[right].Item)) continue;
                groupConflicts = true;
                break;
            }

            if (groupConflicts) conflicts++;
        }

        return conflicts;
    }

    private static string? StableIdentity(LeadItemData item)
    {
        var candidates = new[]
        {
            (Value: item.ManufacturerPartNumber, AllowCodeShape: false),
            (Value: item.ItemMaterialCode, AllowCodeShape: false),
            (Value: item.AlternatePartNumber, AllowCodeShape: false),
            // A terse code is commonly all a prose body supplies (for example QA-FLT-50).
            // Descriptive names are deliberately excluded: "filter" is not a stable identity.
            (Value: item.ProductShortName, AllowCodeShape: true)
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Value)) continue;
            var trimmed = candidate.Value.Trim();
            if (candidate.AllowCodeShape && !LooksLikePartCode(trimmed)) continue;
            var normalized = new string(trimmed.Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant).ToArray());
            // Identity is deliberately field-agnostic. Prose extraction may place QA-FLT-50 in
            // ProductShortName while a CSV parser places the same value in ItemMaterialCode or
            // ManufacturerPartNumber. Prefixing the field name would miss the exact conflict
            // this guard exists to surface.
            if (normalized.Length >= 3) return normalized;
        }

        return null;
    }

    private static bool LooksLikePartCode(string value)
        => value.Length is >= 3 and <= 80
           && !value.Any(char.IsWhiteSpace)
           && value.Any(char.IsLetter)
           && value.Any(char.IsDigit)
           && value.All(character => char.IsLetterOrDigit(character)
                                     || character is '-' or '_' or '.' or '/' or '+');

    private static bool CommercialValuesContradict(LeadItemData left, LeadItemData right)
        => BothDiffer(left.Quantity, right.Quantity)
           || BothDiffer(left.UnitPrice, right.UnitPrice)
           || BothDiffer(CanonicalUom(left.UnitOfMeasure), CanonicalUom(right.UnitOfMeasure))
           || BothDiffer(CanonicalCode(left.Currency), CanonicalCode(right.Currency));

    private static string? CanonicalUom(string? value)
        => CanonicalCode(UomCanonicalizer.CanonicalizeForStorage(value));

    private static string? CanonicalCode(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static bool BothDiffer<T>(T? left, T? right) where T : struct
        => left.HasValue && right.HasValue && !EqualityComparer<T>.Default.Equals(left.Value, right.Value);

    private static bool BothDiffer(string? left, string? right)
        => left is not null && right is not null && !string.Equals(left, right, StringComparison.Ordinal);
}

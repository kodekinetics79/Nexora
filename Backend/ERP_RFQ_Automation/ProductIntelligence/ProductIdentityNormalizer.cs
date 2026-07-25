using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.ProductIntelligence;

public static partial class ProductIdentityNormalizer
{
    private static readonly HashSet<char> MeaningfulSeparators = ['-', '/', '_', '+', '.'];

    public static string? NormalizePartNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        var result = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && result.Length > 0 && !MeaningfulSeparators.Contains(result[^1]))
                    result.Append(' ');
                result.Append(character);
                pendingSpace = false;
                continue;
            }

            if (MeaningfulSeparators.Contains(character))
            {
                while (result.Length > 0 && result[^1] == ' ') result.Length--;
                if (result.Length > 0 && result[^1] != character) result.Append(character);
                pendingSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character) || char.GetUnicodeCategory(character) is
                UnicodeCategory.DashPunctuation or UnicodeCategory.ConnectorPunctuation)
                pendingSpace = result.Length > 0;
        }

        return result.ToString().Trim() is { Length: > 0 } output ? output : null;
    }

    public static string? NormalizeManufacturer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        normalized = ManufacturerNoise().Replace(normalized, " ");
        return Whitespace().Replace(normalized, " ").Trim() is { Length: > 0 } output ? output : null;
    }

    internal static IReadOnlySet<string> Tokens(params string?[] values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => TokenSplit().Split(value!.Normalize(NormalizationForm.FormKC).ToUpperInvariant()))
        .Where(token => token.Length >= 2)
        .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSplit();

    [GeneratedRegex(@"[^\p{L}\p{N}&+./_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex ManufacturerNoise();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

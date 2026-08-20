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

    /// <summary>
    /// The separator characters a buyer and a catalogue routinely disagree about while both
    /// mean the same part. "A2A-50006470", "A2A 50006470" and "A2A50006470" are one number
    /// written three ways; only punctuation separates them.
    /// </summary>
    private static readonly char[] FoldableSeparators = ['-', ' ', '/', '_', '.', '+', '#', ',', ':'];

    /// <summary>
    /// The punctuation-free identity of a catalogue number, for comparing two spellings of the
    /// same part. Upper-cased and compatibility-normalized first, so a full-width digit or a
    /// non-breaking space folds the same way an ASCII one does.
    ///
    /// <para>Note the deliberate difference from <see cref="NormalizePartNumber"/>: that one
    /// PRESERVES meaningful separators, because a part number's punctuation is part of how it is
    /// displayed and stored. This one throws that punctuation away, and is therefore only ever
    /// safe as a fallback comparison AFTER exact equality has been tried — never as the value
    /// written anywhere. <c>DeterministicProductItemResolver</c> makes the same distinction with
    /// its private <c>Compact</c>; the two agree on every input that reaches both.</para>
    /// </summary>
    public static string? FoldIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character) || Array.IndexOf(FoldableSeparators, character) >= 0) continue;
            result.Append(character);
        }
        return result.Length > 0 ? result.ToString() : null;
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

using System.Globalization;
using System.Text;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// The ONE normaliser for buying-organisation names across Nexora.
///
/// Two keys are produced from one raw name:
///   * <see cref="LooseKey"/>  — the stable, stored key. It is what lands in
///     <c>customer_identifiers."NormalizedValue"</c> for the name-shaped identifier types
///     (CustomerName / Alias / Portal / PortalAccount), and what every EXACT comparison
///     uses. <c>RoutingValueNormalizer.Normalize</c> delegates here so the routing store
///     and the resolver can never disagree about what "the same name" means.
///   * <see cref="TightKey"/>  — LooseKey with the separators removed, so
///     <c>AL QURAISHI</c> and <c>ALQURAISHI</c> collapse to one string. Used ONLY by the
///     fuzzy stage; never stored, never used to auto-link.
///
/// Deliberately NOT done: letter substitution (Q↔K, DH↔D, TH↔T …). Gulf transliteration
/// variance is real but unbounded, and a substitution table that is generous enough to
/// catch it is generous enough to merge two different families. The platform resolves
/// transliteration ONCE, by a human, and then LEARNS it as a verified alias — see
/// <c>CustomerAliasLearner</c>. A wrong client on a lead is worse than an unresolved one.
/// </summary>
public static class CustomerNameNormalizer
{
    private const char Tatweel = 'ـ';

    /// <summary>
    /// Legal-form and generic-trade tokens that carry no identity. Stripped only while at
    /// least one token survives, so a customer genuinely called "Trading Company" still
    /// normalises to something.
    /// </summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "LLC", "WLL", "FZE", "FZC", "FZCO", "FZ", "EST", "ESTABLISHMENT",
        "TRADING", "TRDG", "TRAD", "CO", "COMPANY", "CORP", "CORPORATION",
        "INC", "LTD", "LIMITED", "PLC", "JSC", "PJSC", "PSC", "SPC",
        "SAOG", "SAOC", "KSC", "KSCP", "QSC", "QPSC", "BSC", "SARL", "GMBH",
        "SONS", "PARTNERS", "GROUP", "HOLDING", "INTERNATIONAL", "GENERAL"
    };

    private static readonly HashSet<string> LeadingArticles = new(StringComparer.Ordinal) { "AL", "EL", "THE" };

    /// <summary>
    /// Stable exact-match key. Empty string when the input carries no identity at all —
    /// callers MUST treat "" as "no evidence", never as a match key.
    /// </summary>
    public static string LooseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var folded = Fold(value);
        if (folded.Length == 0) return string.Empty;

        var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return string.Empty;

        // A leading standalone article is an artefact of transliteration ("Al Quraishi"
        // vs "Quraishi"), never identity. Only the FIRST token is dropped: "AL" inside
        // the name ("ALI ZAID AL QURAISHI") is part of the name.
        var start = tokens.Length > 1 && LeadingArticles.Contains(tokens[0]) ? 1 : 0;
        var kept = new List<string>(tokens.Length - start);
        for (var i = start; i < tokens.Length; i++)
            if (!NoiseTokens.Contains(tokens[i]))
                kept.Add(tokens[i]);

        // Never normalise a real name away to nothing.
        if (kept.Count == 0)
            for (var i = start; i < tokens.Length; i++)
                kept.Add(tokens[i]);
        if (kept.Count == 0)
            return string.Join(' ', tokens);

        // "&" spells out to AND, which is meaningful BETWEEN two surviving tokens
        // ("A AND B") and pure noise once the token beside it was stripped
        // ("Al-Quraishi & Partners Est." -> "QURAISHI AND").
        while (kept.Count > 1 && kept[^1] == "AND") kept.RemoveAt(kept.Count - 1);
        while (kept.Count > 1 && kept[0] == "AND") kept.RemoveAt(0);

        return string.Join(' ', kept);
    }

    /// <summary>Fuzzy-stage key: <see cref="LooseKey"/> with every separator removed.</summary>
    public static string TightKey(string? value)
    {
        var loose = LooseKey(value);
        if (loose.Length == 0) return string.Empty;
        var builder = new StringBuilder(loose.Length);
        foreach (var c in loose)
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        return builder.ToString();
    }

    /// <summary>
    /// Jaro-Winkler similarity in [0,1]. Used only to SUGGEST (never to auto-link): a
    /// first-time name collision between two Gulf trading houses is common enough that
    /// similarity alone is not evidence of the same legal entity.
    /// </summary>
    public static double JaroWinkler(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return 0d;
        if (string.Equals(left, right, StringComparison.Ordinal)) return 1d;

        var jaro = Jaro(left, right);
        if (jaro <= 0d) return 0d;

        var prefix = 0;
        var maxPrefix = Math.Min(4, Math.Min(left.Length, right.Length));
        while (prefix < maxPrefix && left[prefix] == right[prefix]) prefix++;

        return jaro + (prefix * 0.1d * (1d - jaro));
    }

    private static double Jaro(string s1, string s2)
    {
        var window = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (window < 0) window = 0;

        var s1Matched = new bool[s1.Length];
        var s2Matched = new bool[s2.Length];
        var matches = 0;

        for (var i = 0; i < s1.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(i + window + 1, s2.Length);
            for (var j = from; j < to; j++)
            {
                if (s2Matched[j] || s1[i] != s2[j]) continue;
                s1Matched[i] = true;
                s2Matched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0d;

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matched[i]) continue;
            while (!s2Matched[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        var half = transpositions / 2d;
        return ((matches / (double)s1.Length) + (matches / (double)s2.Length) +
                ((matches - half) / matches)) / 3d;
    }

    /// <summary>
    /// Unicode fold: NFKD, diacritics dropped, Arabic tatweel removed, Arabic-Indic and
    /// extended Arabic-Indic digits mapped to ASCII, "&amp;" spelled out, every other
    /// non-alphanumeric turned into a separator, upper-invariant, whitespace collapsed.
    /// </summary>
    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length + 8);
        var lastWasSpace = true;

        foreach (var raw in decomposed)
        {
            if (raw == Tatweel) continue;
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            var c = raw;
            // Arabic-Indic (U+0660..U+0669) and extended Arabic-Indic (U+06F0..U+06F9).
            if (c is >= '٠' and <= '٩') c = (char)('0' + (c - '٠'));
            else if (c is >= '۰' and <= '۹') c = (char)('0' + (c - '۰'));

            if (c == '&')
            {
                if (!lastWasSpace) builder.Append(' ');
                builder.Append("AND ");
                lastWasSpace = true;
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Services.QuoteTerms;

/// <summary>How long the buyer requires the prices to hold, read from the buyer's own sentence.</summary>
/// <param name="Days">The minimum number of days.</param>
/// <param name="Basis">CLOSING (from the bid closing / due date), SUBMISSION (from the date the
/// quote is submitted) or UNSTATED.</param>
/// <param name="Sentence">The buyer's words the number was read from.</param>
public sealed record BuyerValidityRule(int Days, string Basis, string Sentence)
{
    public const string FromClosing = "CLOSING";
    public const string FromSubmission = "SUBMISSION";
    public const string Unstated = "UNSTATED";
}

/// <summary>
/// Pure readers and defaults for the buyer's commercial terms at quote time (buyer-terms slice 2).
///
/// <para>Every rule is read from the buyer's own wording and nothing is inferred: a document that
/// does not state a validity floor yields none, and the quote keeps the ordinary 30 days.</para>
/// </summary>
public static class BuyerQuoteTermRules
{
    /// <summary>The ordinary validity when the buyer states none.</summary>
    public const int DefaultValidityDays = 30;

    private static readonly Regex ValiditySentence = new(
        @"\bvalid(?:ity)?\b[^.\n]{0,60}?\(?(\d{1,3})\)?\s*(?:calendar\s+|working\s+)?days?\b([^.\n]{0,80})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DaysOnly = new(
        @"\b(\d{1,3})\s*(?:calendar\s+)?days?\b([^.\n]{0,80})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The validity floor in a buyer sentence: "Quotation must be valid for 90 days from bid due
    /// date" → 90 from closing; "validity of at least sixty (60) days from the bid closing date"
    /// → 60 from closing; "valid for at least 60 days from the date of submission" → 60 from
    /// submission; "Quotation Validity (minimum 90 days)" → 90, basis unstated. Null when the text
    /// states no number of days next to the word "valid".
    /// </summary>
    public static BuyerValidityRule? ParseValidity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        BuyerValidityRule? best = null;
        foreach (Match match in ValiditySentence.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                || days is < 1 or > 730) continue;
            var rule = new BuyerValidityRule(days, BasisOf(match.Groups[2].Value), SentenceAround(text, match.Index));
            // A document can say it twice ("4.6 Quotation Validity (minimum 90 days)" and "6. valid
            // for 90 days from bid due date"): keep the longest floor, and prefer the one that says
            // what it counts from.
            if (best is null || rule.Days > best.Days
                || rule.Days == best.Days && best.Basis == BuyerValidityRule.Unstated && rule.Basis != BuyerValidityRule.Unstated)
                best = rule;
        }
        return best;
    }

    /// <summary>
    /// The validity floor from a term the Word reader already found (key "validity"): its sentence
    /// first, then its value ("At least 60 days after closing", "90 days").
    /// </summary>
    public static BuyerValidityRule? ParseValidityTerm(string? value, string? sentence)
    {
        var fromSentence = ParseValidity(sentence);
        if (fromSentence is not null) return fromSentence;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = DaysOnly.Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days is < 1 or > 730) return null;
        return new BuyerValidityRule(days, BasisOf(match.Groups[2].Value), sentence ?? value);
    }

    private static string BasisOf(string tail)
    {
        var t = tail.ToLowerInvariant();
        if (Regex.IsMatch(t, @"clos|due\s+date|opening|deadline|bid\s+date|tender\s+date")) return BuyerValidityRule.FromClosing;
        if (Regex.IsMatch(t, @"submi|date\s+of\s+(?:the\s+)?(?:quotation|quote|offer|bid|proposal)|receipt")) return BuyerValidityRule.FromSubmission;
        return BuyerValidityRule.Unstated;
    }

    private static string SentenceAround(string text, int index)
    {
        var start = index;
        while (start > 0 && text[start - 1] != '\n' && !(text[start - 1] == '.' && start < text.Length && char.IsWhiteSpace(text[start]))
               && index - start < 160) start--;
        var end = text.IndexOfAny(new[] { '\n' }, index);
        if (end < 0) end = text.Length;
        var stop = text.IndexOf(". ", index, StringComparison.Ordinal);
        if (stop >= 0 && stop < end) end = stop + 1;
        var sentence = Regex.Replace(text[start..end], @"\s+", " ").Trim();
        return sentence.Length <= 300 ? sentence : sentence[..300].TrimEnd() + " …";
    }

    private static readonly HashSet<string> KnownCurrencies = new(StringComparer.Ordinal)
    {
        "SAR", "USD", "EUR", "GBP", "AED", "KWD", "QAR", "BHD", "OMR", "JPY", "CNY", "INR", "CHF",
    };

    /// <summary>
    /// The currencies a buyer allows, in the buyer's order: "USD or SAR" → [USD, SAR];
    /// "local vendors MUST bid in SAR only" → [SAR]. Only ISO codes are returned; words such as
    /// "your own currency" add nothing.
    /// </summary>
    public static IReadOnlyList<string> ParseAllowedCurrencies(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return Regex.Matches(text, @"\b[A-Z]{3}\b")
            .Select(m => m.Value)
            .Where(KnownCurrencies.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static readonly Regex CurrencyRule = new(
        @"(?:\b(?:bid|quote|quotation|offer|prices?)\b[^.\n]{0,40}?\bin\s+(?:either\s+)?([A-Z]{3})(?:\s+(?:or|and|/)\s+([A-Z]{3}))?\s+only\b)"
        + @"|(?:\bsubmitted\s+in\s+either\s+([A-Z]{3})\s+or\s+([A-Z]{3}))",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A currency rule stated as a sentence in a document the Word table reader does not see
    /// (a legacy .doc or a PDF): "local vendors MUST bid in SAR only".
    /// </summary>
    public static (IReadOnlyList<string> Codes, string Sentence)? ParseCurrencyRule(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CurrencyRule.Match(text);
        if (!match.Success) return null;
        var codes = Enumerable.Range(1, 4).Select(i => match.Groups[i].Value)
            .Where(v => v.Length == 3 && KnownCurrencies.Contains(v)).Distinct(StringComparer.Ordinal).ToList();
        return codes.Count == 0 ? null : (codes, SentenceAround(text, match.Index));
    }

    /// <summary>
    /// The default "valid until" for a new quote: the later of today + 30 days and the earliest
    /// date the buyer accepts (<see cref="RequiredValidUntil"/>) — for SEC's "valid for 90 days
    /// from bid due date", closing + 90. Returned as a calendar day.
    /// </summary>
    public static DateTime DefaultValidUntil(DateTime today, DateTime? bidClosing, BuyerValidityRule? rule)
    {
        var day = today.Date.AddDays(DefaultValidityDays);
        if (RequiredValidUntil(today, bidClosing, rule) is { } required && required > day) day = required;
        return DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// The earliest "valid until" the buyer accepts, or null when the buyer states no floor. From
    /// closing: closing + days (today + days when no closing date is known). From submission or
    /// unstated: the later of today + days and closing + days, because the quote is submitted
    /// between now and closing.
    /// </summary>
    public static DateTime? RequiredValidUntil(DateTime today, DateTime? bidClosing, BuyerValidityRule? rule)
    {
        if (rule is null) return null;
        if (rule.Basis == BuyerValidityRule.FromClosing)
            return (bidClosing ?? today).Date.AddDays(rule.Days);
        var fromToday = today.Date.AddDays(rule.Days);
        if (bidClosing is { } closing && closing.Date > today.Date)
        {
            var fromClosing = closing.Date.AddDays(rule.Days);
            return fromClosing > fromToday ? fromClosing : fromToday;
        }
        return fromToday;
    }

    /// <summary>
    /// The quote currency the buyer's rule points at, among the tenant's own currencies: the
    /// tenant's base currency when the buyer allows it, else the first allowed one the tenant has.
    /// Null when the buyer states no rule or allows nothing the tenant can quote in — the rep then
    /// chooses, as before.
    /// </summary>
    public static string? DefaultCurrency(IReadOnlyList<string> allowed, IReadOnlyCollection<string> tenantCodes, string? baseCode)
    {
        if (allowed.Count == 0 || tenantCodes.Count == 0) return null;
        var tenant = new HashSet<string>(tenantCodes, StringComparer.OrdinalIgnoreCase);
        if (baseCode is not null && tenant.Contains(baseCode)
            && allowed.Contains(baseCode, StringComparer.OrdinalIgnoreCase)) return baseCode.ToUpperInvariant();
        return allowed.FirstOrDefault(tenant.Contains)?.ToUpperInvariant();
    }
}

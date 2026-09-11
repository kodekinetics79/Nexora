namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// A buyer's currency WORD to the ISO code every downstream table is keyed by.
///
/// <para>A sourcing-portal print says "Currency: US Dollar"; the tenant's currency table, the
/// RFQ line and the quote say "USD". Left as written, the word matched nothing, the line had no
/// usable currency, and the promotion gate asked the rep to pick a currency on every one of
/// 1,500 lines. The mapping is small and closed: only unmistakable names and symbols, never a
/// guess. A word not listed is kept verbatim for the reviewer.</para>
/// </summary>
public static class CurrencyNames
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["usd"] = "USD", ["usdollar"] = "USD", ["usdollars"] = "USD", ["unitedstatesdollar"] = "USD", ["us$"] = "USD", ["$"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD",
        ["sar"] = "SAR", ["saudiriyal"] = "SAR", ["saudiriyals"] = "SAR", ["riyal"] = "SAR", ["riyals"] = "SAR", ["sr"] = "SAR", ["ريال"] = "SAR", ["ريالسعودي"] = "SAR",
        ["eur"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR", ["europeanunioneuro"] = "EUR", ["€"] = "EUR",
        ["gbp"] = "GBP", ["britishpound"] = "GBP", ["poundsterling"] = "GBP", ["britishpounds"] = "GBP", ["£"] = "GBP",
        ["aed"] = "AED", ["uaedirham"] = "AED", ["dirham"] = "AED", ["dirhams"] = "AED",
        ["kwd"] = "KWD", ["kuwaitidinar"] = "KWD",
        ["bhd"] = "BHD", ["bahrainidinar"] = "BHD",
        ["omr"] = "OMR", ["omanirial"] = "OMR",
        ["qar"] = "QAR", ["qataririyal"] = "QAR", ["qataririal"] = "QAR",
        ["jpy"] = "JPY", ["japaneseyen"] = "JPY", ["yen"] = "JPY",
        ["cny"] = "CNY", ["chineseyuan"] = "CNY", ["renminbi"] = "CNY",
        ["inr"] = "INR", ["indianrupee"] = "INR",
        ["chf"] = "CHF", ["swissfranc"] = "CHF",
    };

    /// <summary>
    /// Every ISO 4217 code the runtime knows, plus the ones named above. A three-letter WORD is
    /// not a code: "Lot", "Per", "Set", "Nil" and "TBD" sit in currency columns all the time and
    /// passing them through as codes made "LOT" the inquiry's currency.
    /// </summary>
    private static readonly HashSet<string> IsoCodes = BuildIsoCodes();

    private static HashSet<string> BuildIsoCodes()
    {
        var codes = new HashSet<string>(Codes.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var culture in System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.SpecificCultures))
        {
            try
            {
                var symbol = new System.Globalization.RegionInfo(culture.Name).ISOCurrencySymbol;
                if (symbol.Length == 3) codes.Add(symbol);
            }
            catch (ArgumentException)
            {
                // A culture the runtime lists but cannot describe; nothing to add.
            }
        }
        return codes;
    }

    /// <summary>The ISO code for a currency word or code, or null when the word is not one we know.</summary>
    public static string? ToIsoCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = new string(raw.Trim().Where(c => !char.IsWhiteSpace(c) && c != '.' && c != ',' && c != '-').Select(char.ToLowerInvariant).ToArray());
        if (Codes.TryGetValue(key, out var code)) return code;
        return key.Length == 3 && IsoCodes.Contains(key) ? key.ToUpperInvariant() : null;
    }
}

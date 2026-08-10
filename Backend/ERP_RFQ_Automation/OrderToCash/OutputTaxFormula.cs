namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// The tax treatment the USER stated for one customer quote line. Four values, no inference.
///
/// <para><b>Why this exists</b> (decision R19). A correctly zero-rated export and a Riyadh sale
/// where the rep forgot the 15% used to be byte-identical records: both carried no tax and nothing
/// anywhere said which one it was. Neither the auditor, the ZATCA serializer nor the next person to
/// look at the quote could tell a deliberate treatment from an omission.</para>
///
/// <para>The user chooses; the system does not infer. That is decision R8's own philosophy —
/// "the more complex the harder for them" — applied to the sell side. Nothing here reads a customer
/// address, a delivery term or a country code, because a customer registered in Riyadh can still
/// buy on ex-works terms for export and the record must reflect what was decided, not what an
/// address suggests.</para>
/// </summary>
public static class QuoteLineTaxCategories
{
    /// <summary>The default: output tax is derived at the tenant's rate.</summary>
    public const string Standard = "STANDARD";

    /// <summary>An export taxed at 0%. Zero tax is DERIVED, not absent.</summary>
    public const string ZeroRatedExport = "ZERO_RATED_EXPORT";

    /// <summary>An exempt supply. No output tax, and no input-tax recovery on it either.</summary>
    public const string Exempt = "EXEMPT";

    /// <summary>Outside the scope of the tenant's output tax — typically reverse charge.</summary>
    public const string OutOfScopeRcm = "OUT_OF_SCOPE_RCM";

    /// <summary>Every permitted value, in the order the settings and quote screens present them.</summary>
    public static readonly IReadOnlyList<string> All = [Standard, ZeroRatedExport, Exempt, OutOfScopeRcm];

    /// <summary>
    /// A stored category as the formula should read it. Null and blank mean
    /// <see cref="Standard"/>: the column is nullable so lines written before R19 keep their
    /// row shape, and a standard-rated domestic sale is what those lines were.
    /// </summary>
    public static string Normalize(string? taxCategory) =>
        string.IsNullOrWhiteSpace(taxCategory) ? Standard : taxCategory.Trim().ToUpperInvariant();

    /// <summary>Whether a caller-supplied value is one this platform can act on.</summary>
    public static bool IsKnown(string? taxCategory) => All.Contains(Normalize(taxCategory));

    /// <summary>
    /// Whether the tenant's output tax rate applies to this line. Only a standard-rated supply is
    /// taxed; the other three each derive zero, for three different reasons that the category is
    /// there to record.
    /// </summary>
    public static bool IsTaxable(string? taxCategory) => Normalize(taxCategory) == Standard;

    /// <summary>
    /// Whether the line must carry a stated reason. Departing from the standard rate is the
    /// assertion that needs evidence behind it — an export needs its proof of export, an exempt
    /// supply needs the exemption relied on. A standard-rated line asserts nothing unusual.
    /// </summary>
    public static bool RequiresReason(string? taxCategory) => !IsTaxable(taxCategory);
}

/// <summary>
/// THE definition of output tax on a customer quote line. One formula, one place — the sell-side
/// counterpart to <c>SupplierQuotes.LandedCostFormula</c>.
///
/// Why this file exists
/// --------------------
/// Nothing derived output tax at all (decision R17). <c>QuoteItem.TaxAmount</c> was written null at
/// draft creation, taken verbatim from the client on every update, and validated only against being
/// negative — so null and zero both passed every gate and reached the printed quotation.
///
/// That defect was invisible because it was cancelling another one. Recoverable supplier input VAT
/// was being carried INTO landed cost, and since both legs are 15% on nearly the same base, the
/// overstated cost and the missing output tax offset each other to within rounding. Removing the
/// input-VAT error alone (decision R15) removes the cushion and leaves the sell-side leg missing:
///
///   worked example, one unit, landed cost 120, target margin 20%
///
///                                    price   VAT stated   net to seller   true margin
///     before R15 (both defects)     172.50   none         150.00          20.0%
///     after R15, no output leg      150.00   none         130.43           8.0%
///     after R15 + this formula      150.00   22.50        150.00          20.0%
///
/// The middle row is the danger. Under KSA law a price carrying no separately stated VAT is DEEMED
/// VAT-inclusive, so the seller owes 15/115 ≈ 13.04% out of it — 19.57 on a 150.00 line — and most
/// of a 20% gross margin is gone. R15 is right; shipping it without this is worse than shipping
/// neither.
///
/// What this is NOT
/// ----------------
/// It is not a tax-code engine and decision R8 forbids one. There is no jurisdiction matrix, no
/// place-of-supply logic and no rate table: ONE rate per business unit
/// (<see cref="CommercialMatchingPolicy.OutputTaxRatePercent"/>), applied to the category the user
/// chose on the line. Everything it needs is a number a person set and a category a person picked.
///
/// Scale is 2dp — currency scale, half away from zero — matching the printed document and every
/// other money figure on the quote.
/// </summary>
public static class OutputTaxFormula
{
    /// <summary>Currency scale. Tax appears on a customer document, so it rounds like money.</summary>
    public const int Scale = 2;

    /// <summary>
    /// The amount output tax is charged on: the line's net consideration after discount.
    ///
    /// <para>Rounded before the discount is taken and floored at zero. A discount that exceeds the
    /// line value is a data error, not a negative supply, and deriving negative tax from it would
    /// hand the customer a credit the tenant never agreed to.</para>
    /// </summary>
    public static decimal TaxableBase(decimal grossLineAmount, decimal discountAmount) =>
        Math.Max(0m, decimal.Round(grossLineAmount, Scale, MidpointRounding.AwayFromZero)
            - decimal.Round(discountAmount, Scale, MidpointRounding.AwayFromZero));

    /// <summary>
    /// The rate that actually applies to a line: the tenant's rate on a standard-rated supply, zero
    /// on any other category, and null when a standard-rated line has no rate to apply.
    ///
    /// <para>Recorded on the line as <c>QuoteItem.TaxRatePercentApplied</c>, which is both the
    /// evidence of WHICH rate was used and the marker that derivation happened at all.</para>
    /// </summary>
    public static decimal? EffectiveRatePercent(decimal? outputTaxRatePercent, string? taxCategory) =>
        QuoteLineTaxCategories.IsTaxable(taxCategory) ? outputTaxRatePercent : 0m;

    /// <summary>
    /// The line's output tax, or null when it cannot be derived.
    ///
    /// <para>Null means the tenant has not stated an output tax rate and the line is standard-rated,
    /// so there is no honest answer. It is deliberately NOT zero: quoting zero tax on a standard
    /// rated supply is the exact defect this formula exists to prevent, and a price with no VAT
    /// stated on it is deemed VAT-inclusive. The caller must leave the line underived and the send
    /// gate must refuse it.</para>
    ///
    /// <para>A zero-rated, exempt or out-of-scope line derives 0 whatever the tenant's rate is —
    /// including when no rate is configured at all, so an export-only tenant can quote without ever
    /// stating a standard rate.</para>
    /// </summary>
    public static decimal? Derive(decimal taxableBase, decimal? outputTaxRatePercent, string? taxCategory) =>
        EffectiveRatePercent(outputTaxRatePercent, taxCategory) is { } rate
            ? decimal.Round(taxableBase * rate / 100m, Scale, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// Why a line's tax could not be derived, phrased for the person who has to fix it — or null
    /// when the line is fine. One method so the quote screen, the send gate and the API all refuse
    /// on identical grounds.
    /// </summary>
    /// <param name="lineLabel">How to name the line to the user (its customer reference, or an index).</param>
    public static string? DerivationBlocker(string lineLabel, decimal? outputTaxRatePercent,
        string? taxCategory, string? taxCategoryReason, decimal? taxRatePercentApplied)
    {
        if (!QuoteLineTaxCategories.IsKnown(taxCategory))
            return $"Line {lineLabel} carries an unrecognised tax category '{taxCategory}'.";
        if (QuoteLineTaxCategories.RequiresReason(taxCategory) && string.IsNullOrWhiteSpace(taxCategoryReason))
            return $"Line {lineLabel} is {QuoteLineTaxCategories.Normalize(taxCategory)} and needs a stated reason " +
                   "for departing from the standard rate.";
        if (taxRatePercentApplied is null)
            return EffectiveRatePercent(outputTaxRatePercent, taxCategory) is null
                ? $"Line {lineLabel} is standard-rated but this business unit has no output tax rate configured. " +
                  "Set the output tax rate in Commercial Policy settings."
                : $"Line {lineLabel} has no derived tax. Price the line so its tax is computed.";
        return null;
    }
}

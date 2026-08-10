using System;

namespace ERP_RFQ_Automation.Reporting;

/// <summary>
/// The board-facing gross margin, and everything a reader needs to decide whether to trust it.
///
/// <para><b>Why this shape.</b> The figure it replaces was wrong three ways at once and looked
/// exactly like a figure that was right. It read <c>Product.FinalLandedCost ?? Product.UnitCost</c>
/// — a column set from the last purchase row's bare <c>UnitPrice</c>
/// (<c>Repositories/SupplierPurchaseHistoryRepository.cs</c>), free-typed in the product form and
/// imported from a spreadsheet column, none of which is a landed cost. It averaged per-line
/// PERCENTAGES unweighted, so a 1-unit line at 60% and a 10,000-unit line at 5% reported 32.5%,
/// which is the gross margin of nothing. And it sampled every quote line ever written, drafts and
/// lost bids included.</para>
///
/// <para><b>The contract.</b> Every field here is traceable to a source record.
/// <see cref="MarginPercent"/> is <c>(revenue − cost) / revenue</c> on money totals, never a mean
/// of ratios. Cost is <c>CustomerQuoteSourcingDecision.SupplierLandedUnitCost × Quantity</c> — the
/// immutable quote-time landed cost the customer price was actually built from — and revenue is
/// <c>CustomerUnitPrice × Quantity</c> off the same row, so numerator and denominator come from one
/// record and cannot drift apart. When the figure cannot be computed from evidence, the status is
/// <c>unavailable</c> and the percent is null, following the precedent set at
/// <c>CommercialIntelligence/Growth/GrowthIntelligenceService.cs</c>. A placeholder number is never
/// emitted.</para>
/// </summary>
public sealed class GrossMarginDTO
{
    /// <summary><see cref="GrossMarginStatuses.Available"/> or <see cref="GrossMarginStatuses.Unavailable"/>.</summary>
    public string Status { get; set; } = GrossMarginStatuses.Unavailable;

    /// <summary>
    /// Value-weighted gross margin percent, one decimal place. Null whenever
    /// <see cref="Status"/> is not <c>available</c> — never zero, never a placeholder.
    /// </summary>
    public decimal? MarginPercent { get; set; }

    /// <summary>Σ (CustomerUnitPrice × Quantity), converted to <see cref="CurrencyCode"/>.</summary>
    public decimal? RevenueTotal { get; set; }

    /// <summary>Σ (SupplierLandedUnitCost × Quantity), converted to <see cref="CurrencyCode"/>.</summary>
    public decimal? CostTotal { get; set; }

    /// <summary>ISO code both totals are expressed in — the business unit's base currency.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>Sourcing decisions behind the figure. This is the sample size, and it is a count of rows.</summary>
    public int SampleLines { get; set; }

    /// <summary>Accepted quotes those decisions belong to.</summary>
    public int SampleQuotes { get; set; }

    /// <summary>
    /// Accepted quote lines in the window. The honest denominator for
    /// <see cref="SampleLines"/>: coverage below 100% means the figure describes part of the book.
    /// </summary>
    public int AcceptedQuoteLines { get; set; }

    /// <summary>
    /// Accepted quote lines with NO sourcing decision, so their price cannot be traced to a cost.
    /// These are excluded and counted rather than costed from the product card, which is the
    /// substitution that produced the wrong figure in the first place.
    /// </summary>
    public int LinesWithoutSourcingEvidence { get; set; }

    /// <summary>
    /// Accepted quotes carrying no <c>OutcomeOn</c>, so they belong to no period and are in no
    /// window's sample. Surfaced rather than swept into the current window by a <c>??</c> fallback.
    /// </summary>
    public int QuotesExcludedForMissingAcceptanceDate { get; set; }

    /// <summary>Why the figure is unavailable, in words fit to show a reader. Null when available.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The moment the tenant's input-tax recoverability last changed, read from the tenant
    /// governance ledger (<c>COMMERCIAL_POLICY_UPDATED</c>). Landed costs computed before it were
    /// built on a different basis. Null when the ledger records no such change, in which case the
    /// whole sample shares one basis.
    /// </summary>
    public DateTime? CostBasisChangedOn { get; set; }

    /// <summary>Sample rows priced BEFORE <see cref="CostBasisChangedOn"/>.</summary>
    public int LinesOnPriorCostBasis { get; set; }

    /// <summary>Sample rows priced on or after <see cref="CostBasisChangedOn"/>.</summary>
    public int LinesOnCurrentCostBasis { get; set; }

    /// <summary>
    /// Margin over <see cref="LinesOnCurrentCostBasis"/> alone — the comparable figure when the
    /// window straddles the correction. Null when the sample does not straddle it, or when the
    /// current-basis subset is empty or unconvertible.
    /// </summary>
    public decimal? MarginPercentCurrentBasisOnly { get; set; }

    /// <summary>
    /// Plain-language statement of which cost basis or bases the figure rests on. Always populated
    /// when <see cref="Status"/> is <c>available</c>: an aggregate that blends two bases must say so
    /// on the same screen as the number, not in a footnote nobody reads.
    /// </summary>
    public string? CostBasisNote { get; set; }

    /// <summary>Window start, inclusive. Filtered on <c>Quote.OutcomeOn</c>.</summary>
    public DateTime PeriodFrom { get; set; }

    /// <summary>Window end, exclusive.</summary>
    public DateTime PeriodTo { get; set; }

    /// <summary>Half-open boundary, stated so a reader never has to guess.</summary>
    public string PeriodBoundary => "[from,to)";

    /// <summary>Quote statuses counted as accepted, so the sample is reproducible by hand.</summary>
    public string AcceptedDefinition { get; set; } = string.Empty;

    public DateTime GeneratedAt { get; set; }
}

public static class GrossMarginStatuses
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The same value-weighted margin as <see cref="GrossMarginDTO"/>, narrowed to the priced lines of a
/// single commercial case so a decision about ONE opportunity can depend on it.
///
/// <para><b>Why this exists.</b> The Lead Decision Brief declared a margin field and never assigned
/// it, and its consumer read <c>margin is null</c> as "escalate for margin review" — so every
/// opportunity escalated, permanently. The cost side of a lead was unobtainable only for as long as
/// the brief looked at <c>Product.UnitCost</c>, which carries no currency and is not a landed cost.
/// <c>CustomerQuoteSourcingDecision</c> carries both landed cost and the customer price it produced,
/// on one row, in one stated currency — so the brief now reads the same record the board-facing
/// figure reads, through the same service, and the two cannot disagree.</para>
///
/// <para><b>Sample rule.</b> One decision per <c>RfqItemId</c> — the latest. This differs
/// deliberately from the period report's key (<c>QuoteItemId</c>): the report spans many quotes and
/// must not double-count a re-priced LINE, whereas a single case may carry several quote revisions
/// of the SAME demand line, and summing those would count one line's revenue two or three times.
/// Both rules mean "count each line once"; the identity of "line" differs with the population.</para>
/// </summary>
/// <param name="MarginPercent">Value-weighted <c>(revenue − cost) / revenue</c> percent, one decimal
/// place. Null whenever <paramref name="UnavailableReason"/> is set — never zero.</param>
/// <param name="CostedLines">Demand lines carrying a currency-qualified landed cost. This is a count
/// of quote lines, not of lead lines: it is the number of lines that could be costed at all.</param>
/// <param name="CurrencyCode">ISO code the totals were converted to. Null when unavailable.</param>
/// <param name="EvidenceAsOfUtc">When the newest sampled sourcing decision was recorded.</param>
/// <param name="UnavailableReason">Why there is no figure, in words fit to show a reader.</param>
public sealed record CommercialCaseMarginEvidence(
    decimal? MarginPercent,
    int CostedLines,
    string? CurrencyCode,
    DateTime? EvidenceAsOfUtc,
    string? UnavailableReason)
{
    /// <summary>No figure, and the reason why — never a zero standing in for "not measured".</summary>
    public static CommercialCaseMarginEvidence None(string reason, int costedLines = 0) =>
        new(null, costedLines, null, null, reason);
}

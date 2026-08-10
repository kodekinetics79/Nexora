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

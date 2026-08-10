namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Terminal outcome of the inquiry itself, for the case that ends BEFORE a quotation exists.
///
/// <para>A loss was recordable in two places and not in the third. An RFQ line carries
/// <c>NoQuoteReason</c> under a database check constraint, and a quote carries reason, note and
/// date. But a lead abandoned before any RFQ — "we are not bidding this at all" — had only the
/// free-text <c>BiddingDecision</c>, so it left the pipeline with no reason attached and dropped
/// silently out of win/loss reporting.</para>
///
/// <para>Deliberately the same shape as the quote's outcome, and pointing at the same governed
/// picklist, so one set of reasons serves the whole cycle and reporting does not have to
/// reconcile two vocabularies.</para>
/// </summary>
public partial class Lead
{
    /// <summary>
    /// Why the inquiry ended. Loose FK to <c>SetupMaster.SetupId</c> where SetupType is
    /// "QuoteOutcomeReason" — the same per-tenant list the quote outcome uses, including the
    /// tenant's own added reasons.
    /// </summary>
    public long? OutcomeReasonId { get; set; }

    /// <summary>Optional free-text note captured with the outcome (max 500 characters).</summary>
    public string? OutcomeNote { get; set; }

    /// <summary>When the terminal outcome was recorded.</summary>
    public DateTime? OutcomeOn { get; set; }
}

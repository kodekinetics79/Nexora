namespace ERP_RFQ_Automation.Models;

// Revisions-lite columns (WP-B4). Kept in a partial so the scaffolded Quote.cs
// stays untouched. Property/column configuration lives in
// Models/ErpRfqAutomationContext.Metrics.cs (ConfigureMetricsModel); the lead
// generates the migration from that configuration (see Sla/WAVEB-WIRING.md).
public partial class Quote
{
    /// <summary>
    /// The quote this one revises (loose self-reference to Quotes.Id, no FK/nav
    /// by design). Null for the first version of a quote.
    /// </summary>
    public long? RevisionOfQuoteId { get; set; }

    /// <summary>1 for the original quote, 2 for its first revision, and so on.</summary>
    public int RevisionNo { get; set; } = 1;
}

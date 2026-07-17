using System;

namespace ERP_RFQ_Automation.Models;

// Quote outcome-capture columns (WP-A4). Kept in a partial so the scaffolded
// Quote.cs stays untouched. Property/column configuration lives in
// Models/ErpRfqAutomationContext.Sla.cs (ConfigureSlaModel); the lead generates
// the migration from that configuration.
public partial class Quote
{
    /// <summary>When the quote was actually emailed to the customer (stamped by SendQuoteEmailAsync).</summary>
    public DateTime? SentOn { get; set; }

    /// <summary>When the customer first responded in any form (manual "mark responded" or a terminal outcome).</summary>
    public DateTime? RespondedOn { get; set; }

    /// <summary>
    /// Why the quote ended the way it did. Loose FK to SetupMaster.SetupId
    /// (SetupType "QuoteOutcomeReason"); deliberately configured without a
    /// navigation so the scaffolded SetupMaster entity stays untouched.
    /// </summary>
    public long? OutcomeReasonId { get; set; }

    /// <summary>When the terminal outcome (won/lost/expired) was recorded.</summary>
    public DateTime? OutcomeOn { get; set; }

    /// <summary>Optional free-text note captured with the outcome (max 500 chars).</summary>
    public string? OutcomeNote { get; set; }
}

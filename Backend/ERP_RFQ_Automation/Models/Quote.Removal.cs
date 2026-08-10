using System;

namespace ERP_RFQ_Automation.Models;

// Quote removal columns. Kept in a partial so the scaffolded Quote.cs stays untouched; column
// configuration lives in Models/ErpRfqAutomationContext.QuoteRemoval.cs (ConfigureQuoteRemovalModel)
// and the integration owner generates the migration from it.
//
// Why a quote past DRAFT is withdrawn rather than deleted: once it has been sent, the customer
// holds a document with those numbers on it, and the platform holds the controls that let it be
// sent — the R5 price attestation and the R7 validity extensions. Destroying the row destroyed all
// three at once. See Repositories/QuoteRepository.RemoveAsync.
public partial class Quote
{
    /// <summary>
    /// When this quote was withdrawn. Null for every live quote; non-null rows are excluded from
    /// the quote list and the pipeline statistics but remain fully readable by id, because the
    /// evidence attached to them still describes something that happened.
    /// </summary>
    public DateTime? RemovedOn { get; set; }

    /// <summary>Who withdrew it, from the authenticated caller — never from a request body.</summary>
    public string? RemovedBy { get; set; }

    /// <summary>
    /// Why. Mandatory whenever <see cref="RemovedOn"/> is set, and enforced by a database CHECK
    /// rather than only by the service: a withdrawal with no stated reason is the same audit hole
    /// as a hard delete, one step slower.
    /// </summary>
    public string? RemovalReason { get; set; }
}

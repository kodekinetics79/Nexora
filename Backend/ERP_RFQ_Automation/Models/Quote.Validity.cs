using System;

namespace ERP_RFQ_Automation.Models;

// Decision Register R7 — quote validity is the user's, and changes are reasoned and logged.
// Kept in a partial so the scaffolded Quote.cs stays untouched. Property/column
// configuration lives in Models/ErpRfqAutomationContext.QuoteValidity.cs
// (ConfigureQuoteValidityModel); the integration owner generates the migration from it.
public partial class Quote
{
    /// <summary>
    /// When the validity date was last moved by an explicit, reasoned
    /// <c>POST /api/Quote/{id}/extend-validity</c> command. Null when the validity has only
    /// ever been the one set while the quote was still a draft.
    ///
    /// <para>This is a denormalised marker over <c>QuoteValidityExtensions</c>, not a second
    /// source of truth: the extension rows carry the reason, the actor and the previous date.
    /// It exists because the SLA sweep needs a single-table predicate — see
    /// <c>SlaSweepWorker.ExpiryCandidates</c>, where an explicit extension suppresses the
    /// 90-days-of-silence trigger for as long as the extended date is still in the future.
    /// Without it, a rep who held a tender price open to day 120 at the buyer's request would
    /// still have the quote auto-expired underneath them on day 90.</para>
    /// </summary>
    public DateTime? ValidityExtendedOn { get; set; }
}

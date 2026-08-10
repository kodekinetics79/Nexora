using System;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Decision Register R7 — one recorded, reasoned move of a quote's validity date.
///
/// <para>R7 says the reason is "logged and visible", and its engineering note is explicit:
/// "the change must be an auditable event, not a silent field update. A reason nobody can
/// read later is not a reason." A log line satisfies neither half — it is not queryable by
/// the rep who has to explain the bid six weeks later, and it does not survive log
/// retention. So the reason is a row.</para>
///
/// <para>These rows are append-only. Nothing in the application updates or deletes one; the
/// current validity always equals the <see cref="NewValidUntil"/> of the newest row (or the
/// draft-time value when there are none).</para>
///
/// <para>Tenant-scoped: non-nullable <see cref="BusinessUnitId"/>, the standard fail-closed
/// global query filter, and a composite (BusinessUnitId, QuoteId) foreign key so an
/// extension can never be attached to another tenant's quote by primary key alone.</para>
/// </summary>
public sealed class QuoteValidityExtension
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public long QuoteId { get; set; }

    /// <summary>The validity date this extension replaced. Null when the quote was issued
    /// without one — which is exactly the case the 90-day sweep rule was written for.</summary>
    public DateTime? PreviousValidUntil { get; set; }

    /// <summary>The validity date the customer is now being held to.</summary>
    public DateTime NewValidUntil { get; set; }

    /// <summary>
    /// Why the price is being held longer, in the rep's own words. Mandatory, trimmed,
    /// bounded at 500 characters (the same bound as <see cref="Quote.OutcomeNote"/>).
    /// </summary>
    public string Reason { get; set; } = null!;

    /// <summary>Authenticated user id when the claim carried one; null otherwise.</summary>
    public long? ExtendedByUserId { get; set; }

    /// <summary>Actor identity as resolved by the controller (email, then name).</summary>
    public string ExtendedBy { get; set; } = null!;

    public DateTime ExtendedOn { get; set; }

    /// <summary>
    /// Caller-supplied (or server-generated) key making the command replay-safe. Unique per
    /// tenant: a retried request records one extension, not two.
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;
}

using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// Originates the commercial spine for a quote that predates Nexora.
///
/// A tenant does not begin trading on the day it installs this system. It arrives holding
/// quotes already sent and still open, and those quotes have to be monitored beside the ones
/// Nexora produces or the pipeline is a half-truth from the first day.
///
/// A Quote cannot simply be inserted, and that is deliberate: <c>QuoteController</c> refuses one
/// without an RFQ, and the PostgreSQL trigger
/// <c>nexora_validate_downstream_commercial_identity</c> refuses a serial that does not match a
/// real commercial case. Rather than weaken either — they are the reason lineage can be trusted
/// at all — a back-filled quote is given a real spine: a Lead, then an RFQ, then the quote.
///
/// The lead is not a fiction. The enquiry genuinely happened; it happened before this system
/// existed. Recording it with <see cref="LeadSourceBackfill"/> and its ORIGINAL date states
/// exactly that, and because the serial format interpolates <c>{SOURCE}</c> and <c>{YEAR}</c>
/// from those two values, the resulting Nexora Serial carries the truth on its face.
/// </summary>
public sealed class QuoteBackfillSpine
{
    /// <summary>Lead source recorded on an originated spine. Also appears in the serial.</summary>
    public const string LeadSourceBackfill = "BACKFILL";

    private readonly ErpRfqAutomationContext _db;

    public QuoteBackfillSpine(ErpRfqAutomationContext db) => _db = db;

    /// <summary>
    /// Creates and SAVES a Lead, then an RFQ carrying the lead's commercial identity, and returns
    /// the RFQ ready for a quote to inherit from.
    ///
    /// Two saves, not one, and not by accident: the commercial case and its master reference are
    /// minted by <c>LeadPersistenceRules</c> / <c>TR_Leads_AssignCommercialCase</c> as the lead is
    /// inserted, and <c>Rfq.InheritCommercialIdentity</c> rejects a lead whose
    /// <c>CommercialCaseId</c> is still 0. The lead must therefore hit the database before the RFQ
    /// can be built from it.
    /// </summary>
    public async Task<Rfq> OriginateAsync(
        long businessUnitId,
        long customerId,
        long? contactId,
        DateTime originalQuoteDate,
        string actor,
        string? externalQuoteReference,
        CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An actor is required.", nameof(actor));

        // The date the quote was actually issued, not the date it was typed in. Everything
        // downstream — the serial's {YEAR}, ageing, and any "how long has this been open" report
        // — reads this, so importing a March quote in August must still say March.
        var issuedOn = originalQuoteDate == default ? DateTime.UtcNow : originalQuoteDate;

        var lead = new Lead
        {
            BusinessUnitId = businessUnitId,
            LeadSource = LeadSourceBackfill,
            RecDate = issuedOn,
            CreatedBy = actor,
            CreatedDate = issuedOn,
            HeaderRemarks = externalQuoteReference is { Length: > 0 } r
                ? $"Back-filled from quote {r}, which predates Nexora."
                : "Back-filled from a quote that predates Nexora.",
        };
        // A back-fill IS human resolution: a person is stating whose quote this was, which is
        // precisely what ResolveCommercialIdentity records. The status must satisfy the
        // CustomerID<->status invariant behind CK_Leads_CustomerIdentityStatus, so the
        // contact-unresolved variant is used when no contact is supplied.
        lead.ResolveCommercialIdentity(
            customerId,
            contactId,
            contactId.HasValue
                ? LeadCustomerMatchStatuses.Confirmed
                : LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);   // mints CommercialCase + MasterReference

        var rfqno = $"BF-{lead.CommercialCaseReference}";
        if (await _db.Rfqs.AnyAsync(x => x.Rfqno == rfqno && x.BusinessUnitId == businessUnitId, ct))
            rfqno = $"{rfqno}-{lead.Id}";

        var rfq = new Rfq
        {
            Rfqno = rfqno,
            RecDate = issuedOn,
            LeadId = lead.Id,
            BusinessUnitId = businessUnitId,
            CreatedBy = actor,
            CreatedDate = issuedOn,
            NoOfLineItems = 0,
            HeaderRemarks = lead.HeaderRemarks,
        };
        rfq.InheritCommercialIdentity(lead);

        _db.Rfqs.Add(rfq);
        await _db.SaveChangesAsync(ct);

        // The quote inherits from the RFQ, which needs the lead reachable for SourceLeadRevision.
        rfq.Lead = lead;
        return rfq;
    }
}

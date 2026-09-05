using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.LeadIdentity;

/// <summary>
/// The one definition of "this quote still has an unresolved customer revision".
///
/// <para><see cref="LeadRevisionImpact"/> rows are append-only (trigger
/// <c>trg_lead_revision_impacts_append_only</c>), so a resolution can never flip
/// <see cref="LeadRevisionImpact.Status"/>: it is recorded as a <c>REVISION_IMPACT_RESOLVED</c>
/// audit event whose correlation id names the impact. Until this existed the quote DETAIL joined
/// on that event (and hid the banner) while send-readiness and the send itself read only
/// <c>Status == "OPEN"</c> — so after "Resolve" the screen said resolved and the send said stale,
/// for ever. Every reader goes through here now.</para>
/// </summary>
public static class LeadRevisionImpactQueries
{
    public const string ResolvedEventType = "REVISION_IMPACT_RESOLVED";

    public static string CorrelationIdFor(long impactId) => "quote-impact:" + impactId;

    /// <summary>Impacts on a quote that are open AND have not been resolved by an audit event.</summary>
    public static IQueryable<LeadRevisionImpact> OpenQuoteImpacts(
        ErpRfqAutomationContext context, long businessUnitId, long quoteId)
        => context.Set<LeadRevisionImpact>()
            .Where(impact => impact.BusinessUnitId == businessUnitId
                && impact.AggregateType == "QUOTE"
                && impact.AggregateId == quoteId
                && impact.Status == "OPEN"
                && impact.ResolvedAtUtc == null)
            .Where(impact => !context.Set<LeadIdentityAuditEvent>()
                .Any(audit => audit.BusinessUnitId == businessUnitId
                    && audit.EventType == ResolvedEventType
                    && audit.CorrelationId == "quote-impact:" + impact.Id));
}

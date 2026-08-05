using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.LeadIdentity;

/// <summary>
/// Owner audit requirement (ingestion fairness): a lead that was INGESTED into
/// Nexora after its business due date (RFQ bid-closing / submission deadline)
/// must not count against Nexora's response-time / aging performance metrics —
/// otherwise leads that were already lost before they ever reached the system
/// would be booked as Nexora's failure. This helper is the single definition of
/// (a) the authoritative ingestion timestamp and (b) the "late ingested" rule,
/// shared by the lead read models and the dashboard aggregations so the two can
/// never disagree.
/// </summary>
public static class LeadIngestionAudit
{
    /// <summary>
    /// Extraction sentinel floor — due dates before this year are placeholders
    /// (e.g. DateTime.MinValue), never real deadlines. Mirrors the existing
    /// convention in DashboardRepository / the frontend date-safety helpers.
    /// </summary>
    public const int SentinelYearFloor = 2000;

    /// <summary>
    /// The authoritative ingestion timestamp: the earliest
    /// source_document_occurrences.received_on across the lead's ingestion
    /// occurrences, falling back to the lead's CreatedDate for manual/legacy
    /// leads that have no source-document lineage.
    /// </summary>
    public static DateTimeOffset ResolveIngestionTimestamp(DateTimeOffset? earliestSourceReceivedOn, DateTime createdDate)
        => earliestSourceReceivedOn ?? AsUtc(createdDate);

    /// <summary>
    /// The business due date the ingestion timestamp is judged against:
    /// BidClosingDate when it is a real (non-sentinel) date, otherwise SubDate
    /// (submission deadline). Null when neither exists — a lead without a due
    /// date can never be "late ingested". Both fields exist on the Lead and are
    /// copied verbatim onto the Rfq at conversion, so this rule holds across
    /// the whole lead/RFQ graph.
    /// </summary>
    public static DateTime? EffectiveDueDate(DateTime? bidClosingDate, DateTime? subDate)
        => IsRealDate(bidClosingDate) ? bidClosingDate : IsRealDate(subDate) ? subDate : null;

    /// <summary>
    /// True when the lead entered Nexora strictly AFTER its business due date.
    /// An ingestion exactly at the deadline is not late.
    /// </summary>
    public static bool IsLateIngested(DateTimeOffset ingestionTimestamp, DateTime? bidClosingDate, DateTime? subDate)
    {
        var dueDate = EffectiveDueDate(bidClosingDate, subDate);
        return dueDate.HasValue && ingestionTimestamp > AsUtc(dueDate.Value);
    }

    /// <summary>Convenience overload combining resolution and the late check.</summary>
    public static bool IsLateIngested(
        DateTimeOffset? earliestSourceReceivedOn, DateTime createdDate, DateTime? bidClosingDate, DateTime? subDate)
        => IsLateIngested(ResolveIngestionTimestamp(earliestSourceReceivedOn, createdDate), bidClosingDate, subDate);

    /// <summary>
    /// Batched lookup of the earliest received_on per lead, following the chain
    /// source_document_occurrences → LeadIngestionOccurrences → Lead. Explicitly
    /// tenant-scoped on BOTH sides of the join (the global query filters enforce
    /// the same bound; the explicit predicates mirror repository convention).
    /// Leads without occurrence lineage (manual/legacy) are simply absent from
    /// the result — callers fall back to the lead's CreatedDate via
    /// <see cref="ResolveIngestionTimestamp"/>. The min is taken client-side over
    /// the (small, already page-bounded) row set because the SQLite test provider
    /// cannot aggregate DateTimeOffset server-side.
    /// </summary>
    public static async Task<Dictionary<long, DateTimeOffset>> EarliestSourceReceivedOnAsync(
        Models.ErpRfqAutomationContext context, long businessUnitId, IReadOnlyCollection<long> leadIds)
    {
        if (leadIds.Count == 0) return new Dictionary<long, DateTimeOffset>();

        var rows = await context.Set<LeadIngestionOccurrence>()
            .AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId
                && o.LeadId != null && leadIds.Contains(o.LeadId.Value)
                && o.SourceDocumentOccurrenceId != null)
            .Join(
                context.Set<SourceDocumentOccurrence>().AsNoTracking()
                    .Where(s => s.BusinessUnitId == businessUnitId),
                o => o.SourceDocumentOccurrenceId,
                s => (long?)s.Id,
                (o, s) => new { LeadId = o.LeadId!.Value, s.ReceivedOn })
            .ToListAsync();

        return rows
            .GroupBy(r => r.LeadId)
            .ToDictionary(g => g.Key, g => g.Min(r => r.ReceivedOn));
    }

    private static bool IsRealDate(DateTime? value) => value.HasValue && value.Value.Year >= SentinelYearFloor;

    // Persisted DateTime columns in this codebase are UTC wall-clock values
    // (written from DateTime.UtcNow) whose Kind is lost on materialization;
    // stamp them UTC so comparisons against DateTimeOffset are well-defined.
    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

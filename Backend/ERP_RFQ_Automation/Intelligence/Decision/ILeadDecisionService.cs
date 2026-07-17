namespace ERP_RFQ_Automation.Intelligence.Decision;

/// <summary>
/// Lead Decision Brief intelligence — the read-only signals a sales executive
/// needs to decide Bid / Review / Skip on a lead. Deterministic (no LLM), never
/// throws for missing history; only for a lead that doesn't exist in the tenant.
/// </summary>
public interface ILeadDecisionService
{
    /// <summary>
    /// Full brief for one lead: catalog coverage, estimated value, margin
    /// potential, customer history, deadline feasibility and a transparent
    /// recommendation with plain-language reasons.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Lead not found in this business unit.</exception>
    Task<LeadDecisionBrief> GetBriefAsync(long leadId, long businessUnitId, CancellationToken ct);

    /// <summary>
    /// Cheap batch version for list views (a couple of batched queries total —
    /// no per-lead round trips): exact-code coverage, lead-item-priced value,
    /// deadline band and a coarse recommendation. Unknown / foreign-tenant ids
    /// are silently omitted from the result.
    /// </summary>
    Task<Dictionary<long, LeadDecisionSummary>> GetSummariesAsync(
        IEnumerable<long> leadIds, long businessUnitId, CancellationToken ct);
}

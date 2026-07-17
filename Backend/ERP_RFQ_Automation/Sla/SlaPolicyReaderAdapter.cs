using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Bridges WP-A1's <see cref="ISlaPolicyReader"/> (used by the lead repository's
/// unassigned-aging flag) onto WP-A2's tenant-configurable <see cref="SlaPolicy"/>.
/// Replaces the interim flat-2h <c>DefaultSlaPolicyReader</c> registration so the
/// "Deadlines &amp; Alerts" settings page actually governs the aging threshold.
/// Falls back to the policy default when the tenant has no row.
/// </summary>
public sealed class SlaPolicyReaderAdapter : ISlaPolicyReader
{
    private readonly ErpRfqAutomationContext _db;

    public SlaPolicyReaderAdapter(ErpRfqAutomationContext db) => _db = db;

    public async Task<int> GetUnassignedHoursAsync(long businessUnitId)
    {
        var hours = await _db.Set<SlaPolicy>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.BusinessUnitId == businessUnitId)
            .Select(p => (int?)p.UnassignedHours)
            .FirstOrDefaultAsync();
        return hours ?? SlaPolicy.Default(businessUnitId).UnassignedHours;
    }
}

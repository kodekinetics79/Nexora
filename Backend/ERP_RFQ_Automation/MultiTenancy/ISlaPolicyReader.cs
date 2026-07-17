using System.Threading.Tasks;

namespace ERP_RFQ_Automation.MultiTenancy
{
    /// <summary>
    /// Minimal read-side contract for SLA policy values consumed by the lead
    /// module (WP-A1 unassigned-aging). The full SLA engine (built separately in
    /// Sla/) will provide the real, tenant-configurable implementation and replace
    /// the default registration; consumers must depend only on this interface.
    /// </summary>
    public interface ISlaPolicyReader
    {
        /// <summary>
        /// Hours an ACCEPTED lead may sit unassigned in the given business unit
        /// before it counts as overdue ("Unassigned Xh" badge / isUnassignedOverdue).
        /// </summary>
        Task<int> GetUnassignedHoursAsync(long bu);
    }

    /// <summary>
    /// Interim default: a flat 2-hour unassigned threshold for every business
    /// unit. Swapped out when the SLA engine registers its own reader.
    /// </summary>
    public sealed class DefaultSlaPolicyReader : ISlaPolicyReader
    {
        public const int DefaultUnassignedHours = 2;

        public Task<int> GetUnassignedHoursAsync(long bu) => Task.FromResult(DefaultUnassignedHours);
    }
}

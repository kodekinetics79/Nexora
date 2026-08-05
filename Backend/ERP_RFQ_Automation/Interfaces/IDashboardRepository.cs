using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.Dashboard;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IDashboardRepository
    {
        Task<DashboardDataDTO> GetDashboardDataAsync(long businessUnitId);

        /// <summary>WP-B1: per-rep open/overdue leads + sent/stale quotes, plus an unassigned bucket row.</summary>
        Task<TeamWorkloadDTO> GetTeamWorkloadAsync(long businessUnitId);

        /// <summary>WP-B2: stage funnel, loss reasons, weighted forecast and quoted-vs-floor margin proxy.</summary>
        Task<PipelineAnalyticsDTO> GetPipelineAnalyticsAsync(long businessUnitId);

        /// <summary>Pilot analytics: open enquiries bucketed by days to bid closing date, with line counts.</summary>
        Task<DeadlineBoardDTO> GetDeadlineBoardAsync(
            long businessUnitId, int maxLeads = 200, CancellationToken cancellationToken = default);

        /// <summary>Pilot analytics: documents in, leads out, and the coverage of what came out.</summary>
        Task<DocumentYieldDTO> GetDocumentYieldAsync(
            long businessUnitId, DateTime from, DateTime to, CancellationToken cancellationToken = default);

        /// <summary>
        /// Release-01 dashboard snapshot. All metrics share one tenant, role scope,
        /// reporting window, and generated-at boundary.
        /// </summary>
        Task<DashboardRelease01DTO> GetRelease01Async(
            long businessUnitId,
            long? ownerUserId,
            string roleScope,
            DateTime from,
            DateTime to,
            DateTime generatedAt,
            CancellationToken cancellationToken = default);
    }
}

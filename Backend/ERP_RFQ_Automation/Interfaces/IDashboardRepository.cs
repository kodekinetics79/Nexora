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
    }
}

using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.Dashboard;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IDashboardRepository
    {
        Task<DashboardDataDTO> GetDashboardDataAsync(long businessUnitId);
    }
}

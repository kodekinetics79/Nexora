using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ISetupMasterRepository
    {
        Task<IEnumerable<SetupMaster>> GetAllAsync();
        Task<SetupMaster> GetByIdAsync(long id);
        Task AddAsync(SetupMaster setupMaster);
        Task UpdateAsync(SetupMaster setupMaster);
        Task DeleteAsync(long id);
    }
}
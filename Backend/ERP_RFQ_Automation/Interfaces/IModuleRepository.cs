using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IModuleRepository
    {
        Task<(IEnumerable<Module>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? moduleName, bool? isActive);
        Task<Module> GetByIdAsync(long id);
        Task AddAsync(Module module);
        Task UpdateAsync(Module module);
        Task DeleteAsync(long id);
    }
}
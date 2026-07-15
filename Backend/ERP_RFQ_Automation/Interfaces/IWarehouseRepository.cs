using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IWarehouseRepository
    {
        Task<IEnumerable<Warehouse>> GetAllAsync(long businessUnitId);
        Task<Warehouse> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Warehouse warehouse);
        Task UpdateAsync(Warehouse warehouse);
        Task DeleteAsync(long id, long businessUnitId);
    }
}
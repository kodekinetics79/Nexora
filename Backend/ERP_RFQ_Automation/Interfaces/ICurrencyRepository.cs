using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ICurrencyRepository
    {
        Task<IEnumerable<Currency>> GetAllAsync(long businessUnitId);
        Task<Currency> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Currency currency);
        Task UpdateAsync(Currency currency);
        Task DeleteAsync(long id, long businessUnitId);
    }
}
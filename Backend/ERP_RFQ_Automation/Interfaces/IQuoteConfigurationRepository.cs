using ERP_RFQ_Automation.Models;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IQuoteConfigurationRepository
    {
        Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId);
        Task AddAsync(QuoteConfiguration config);
        Task UpdateAsync(QuoteConfiguration config);
    }
}

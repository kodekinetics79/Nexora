using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IQuoteRepository
    {
        Task<(IEnumerable<QuoteResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber, int pageSize, string? search = null, string? state = null);
        Task<QuoteResponseDTO> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Quote quote);
        Task UpdateAsync(Quote quote);
        Task DeleteAsync(long id, long businessUnitId);
        Task<QuoteStatsDTO> GetQuoteStatsAsync(long businessUnitId);
    }
}

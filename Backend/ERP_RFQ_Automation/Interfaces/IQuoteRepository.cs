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
        /// <summary>
        /// Removes a quotation: a reasoned, audited state change that hard-deletes only a clean
        /// draft. Replaces the unguarded <c>DeleteAsync</c>, which destroyed the quote's R5 price
        /// attestations and R7 validity extensions along with it. Returns null when the quote does
        /// not exist in this tenant.
        /// </summary>
        Task<QuoteRemovalOutcome?> RemoveAsync(long id, long businessUnitId, string reason, string actor);
        Task<QuoteStatsDTO> GetQuoteStatsAsync(long businessUnitId);
    }
}

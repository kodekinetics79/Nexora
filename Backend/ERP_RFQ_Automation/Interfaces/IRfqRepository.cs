using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IRfqRepository
    {
        Task<(IEnumerable<RfqResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null, bool? isActive = null, long? assignedToId = null, string? createdBy = null, long? rfqStatusId = null);
        Task<RfqResponseDTO> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Rfq rfq);
        Task UpdateAsync(Rfq rfq);
        Task<long> ApproveAsync(long id, string approvedBy, long businessUnitId, long? customerId = null);
        Task DeleteAsync(long id, long businessUnitId);

        Task<List<RFQTypeLookupDTO>> GetRFQTypeAsync();
        Task<RfqStatsDTO> GetRfqStatsAsync(long businessUnitId);
    }
}
using System.Collections.Generic;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.SupplierQuotedItem;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ISupplierQuotedItemRepository
    {
        Task<IEnumerable<SupplierQuotedItemResponseDTO>> GetAllAsync(long businessUnitId);
        Task<SupplierQuotedItemResponseDTO?> GetByIdAsync(long id, long businessUnitId);
        Task<SupplierQuotedItem> AddAsync(SupplierQuotedItem entity);
        Task UpdateAsync(SupplierQuotedItem entity, long businessUnitId);
        Task DeleteAsync(long id, long businessUnitId);
        Task<IEnumerable<SupplierQuotedItemResponseDTO>> GetBySupplierIdAsync(long supplierId, long businessUnitId);
    }
}

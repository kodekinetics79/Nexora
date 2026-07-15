using ERP_RFQ_Automation.DTOs.SupplierPurchaseHistory;
using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ISupplierPurchaseHistoryRepository
    {
        Task<IEnumerable<SupplierPurchaseHistoryResponseDTO>> GetAllAsync(long businessUnitId);
        Task<IEnumerable<SupplierPurchaseHistoryResponseDTO>> GetByProductIdAsync(long productId, long businessUnitId);
        Task<SupplierPurchaseHistoryResponseDTO?> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(SupplierPurchaseHistory history);
        Task<string> AddBatchAsync(IEnumerable<SupplierPurchaseHistory> histories);
        Task UpdateAsync(SupplierPurchaseHistory history);
        Task DeleteAsync(long id, long businessUnitId);
        Task DeleteByPoDocIdAsync(string poDocId, long businessUnitId);
    }
}

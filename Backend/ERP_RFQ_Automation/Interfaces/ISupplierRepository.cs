using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ISupplierRepository
    {
        Task<(IEnumerable<SupplierResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? name, string? contactEmail, long? currencyId, bool? isActive, string? docId, long businessUnitId);
        Task<Supplier> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Supplier supplier);
        Task UpdateAsync(Supplier supplier, long businessUnitId);
        Task DeleteAsync(long id, long businessUnitId);
        Task<List<SupplierSearchResultDTO>> SearchSuppliersAsync(string? searchTerm, string? productCategory, long businessUnitId);
        Task<List<SupplierSearchResultDTO>> SearchWebSuppliersAsync(string query);
    }
}

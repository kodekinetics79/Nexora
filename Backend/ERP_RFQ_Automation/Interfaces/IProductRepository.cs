using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IProductRepository
    {
        Task<(IEnumerable<ProductResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null, bool? isActive = null);
        Task<Product> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Product product, List<IFormFile>? attachments);
        Task UpdateAsync(Product product, long businessUnitId, List<IFormFile>? attachments);
        Task DeleteAsync(long id, long businessUnitId);
        Task<List<BusinessUnitLookupDTO>> GetActiveBusinessUnitsAsync();
        Task<List<ProductCategoryLookupDTO>> GetProductCategoriesAsync(long businessUnitId);
        Task<List<LookupItemDTO>> GetItemStatusesAsync();
        Task<List<SupplierLookupDTO>> GetSuppliersAsync(long businessUnitId);
        Task<List<ProductSubCategoryLookupDTO>> GetProductSubCategoriesAsync(long businessUnitId);
        Task<List<WarehouseLookupDTO>> GetWarehousesAsync(long businessUnitId);
        Task<List<LookupItemDTO>> GetUomsAsync(long businessUnitId);
        
        // Product matching methods
        Task<ProductMatchResponseDTO> MatchProductAsync(ProductMatchRequestDTO request);
        Task<StockDetailsDTO> GetStockDetailsAsync(long productId, long businessUnitId);
        Task<PurchaseHistoryDTO> GetPurchaseHistoryAsync(long productId, long businessUnitId);
    }
}
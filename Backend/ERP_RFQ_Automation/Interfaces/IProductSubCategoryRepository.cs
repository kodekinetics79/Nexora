using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IProductSubCategoryRepository
    {
        Task<IEnumerable<ProductSubCategory>> GetAllAsync(long businessUnitId);
        Task<ProductSubCategory> GetByIdAsync(int id, long businessUnitId);
        Task AddAsync(ProductSubCategory subCategory);
        Task UpdateAsync(ProductSubCategory subCategory);
        Task DeleteAsync(int id, long businessUnitId);
    }
}
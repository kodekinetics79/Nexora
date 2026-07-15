using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IProductCategoryRepository
    {
        Task<IEnumerable<ProductCategory>> GetAllAsync(long businessUnitId);
        Task<ProductCategory> GetByIdAsync(long id, long businessUnitId);
        Task<ProductCategory?> GetByIdWithParentAsync(long id, long businessUnitId);
        Task AddAsync(ProductCategory category);
        Task UpdateAsync(ProductCategory category);
        Task DeleteAsync(long id, long businessUnitId);
    }
}
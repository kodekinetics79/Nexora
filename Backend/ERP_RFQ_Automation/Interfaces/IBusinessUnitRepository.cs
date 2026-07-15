using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IBusinessUnitRepository
    {
        Task<(IEnumerable<BusinessUnit>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? businessUnitName);
        Task<BusinessUnit> GetByIdAsync(long id);
        Task AddAsync(BusinessUnit businessUnit);
        Task UpdateAsync(BusinessUnit businessUnit);
        Task DeleteAsync(long id);
    }
}

using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IRolePermissionRepository
    {
        Task<(IEnumerable<RolePermission>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, long? roleId, long? moduleId, long businessUnitId);
        Task<RolePermission> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(RolePermission rolePermission);
        Task UpdateAsync(RolePermission rolePermission);
        Task DeleteAsync(long id, long businessUnitId);
        Task<bool> CheckPermissionAsync(long roleId, string moduleName, string action, long businessUnitId);
    }
}
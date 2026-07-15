using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IUserGroupRepository
    {
        Task<(IEnumerable<UserGroup>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? userGroupsName, long businessUnitId);
        Task<UserGroup> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(UserGroup userGroup);
        Task UpdateAsync(UserGroup userGroup);
        Task DeleteAsync(long id, long businessUnitId);
    }
}
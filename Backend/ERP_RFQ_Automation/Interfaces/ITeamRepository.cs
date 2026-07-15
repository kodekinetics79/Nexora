using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ITeamRepository
    {
        Task<(IEnumerable<Team>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? teamName, long? subTeamId, long businessUnitId);
        Task<Team> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Team team);
        Task UpdateAsync(Team team);
        Task DeleteAsync(long id, long businessUnitId);
    }
}
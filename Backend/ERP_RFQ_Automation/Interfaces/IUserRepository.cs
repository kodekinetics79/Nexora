using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.TeamDTOs;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.DTOs.UserGroup;
using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IUserRepository
    {
        Task<(IEnumerable<UserResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? userName, string? email, long? roleId, string? region, bool? isActive, long businessUnitId);
        Task<User> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(User user);
        Task UpdateAsync(User user);
        Task DeleteAsync(long id, long businessUnitId);
        Task<IEnumerable<RoleResponseDTO>> GetRolesAsync(long businessUnitId);
        Task<IEnumerable<TeamResponseDTO>> GetTeamsAsync(long businessUnitId);
        Task<IEnumerable<BusinessUnitResponseDTO>> GetBusinessUnitsAsync();
        Task<IEnumerable<UserGroupResponseDTO>> GetUserGroupsAsync(long businessUnitId);
        /// <summary>
        /// Sec-A2: whether <paramref name="candidatePassword"/> is the CURRENT password of user
        /// <paramref name="id"/> within <paramref name="businessUnitId"/>. False for an unknown
        /// user, a user outside that business unit, an inactive user, a stored hash that is
        /// missing or unusable, and an empty candidate — every unknown is a refusal, because this
        /// answer is the gate in front of a credential rewrite.
        /// </summary>
        Task<bool> VerifyPasswordAsync(long id, long businessUnitId, string candidatePassword);

        Task ChangePasswordAsync(long id, string newPassword);
    }
}

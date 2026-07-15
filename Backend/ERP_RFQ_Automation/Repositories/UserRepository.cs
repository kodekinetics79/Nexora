using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.TeamDTOs;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.DTOs.UserGroup;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public UserRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<UserResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? userName, string? email, long? roleId, string? region, bool? isActive, long businessUnitId)
        {
            var query = _context.Users
                .AsNoTracking()
                .Where(u => u.Buid == businessUnitId)
                .GroupJoin(_context.SetupMasters.Where(sm => sm.SetupType == "role"),
                    user => user.RoleId,
                    role => role.SetupId,
                    (user, roles) => new { user, roles })
                .SelectMany(
                    x => x.roles.DefaultIfEmpty(),
                    (user, role) => new { user.user, role })
                .GroupJoin(_context.Teams,
                    x => x.user.TeamId,
                    team => team.Id,
                    (x, teams) => new { x.user, x.role, teams })
                .SelectMany(
                    x => x.teams.DefaultIfEmpty(),
                    (x, team) => new { x.user, x.role, team })
                .GroupJoin(_context.BusinessUnits,
                    x => x.user.Buid,
                    bu => bu.Id,
                    (x, bus) => new { x.user, x.role, x.team, bus })
                .SelectMany(
                    x => x.bus.DefaultIfEmpty(),
                    (x, bu) => new { x.user, x.role, x.team, bu })
                
                .GroupJoin(_context.UserGroups,
                    x => x.user.UserGroupId,
                    ug => ug.Id,
                    (x, userGroups) => new { x.user, x.role, x.team, x.bu, userGroups })
                .SelectMany(
                    x => x.userGroups.DefaultIfEmpty(),
                    (x, userGroup) => new
                    {
                        User = x.user,
                        RoleName = x.role != null ? x.role.SetupValue : null,
                        TeamName = x.team != null ? x.team.TeamName : null,
                        BusinessUnitName = x.bu != null ? x.bu.BusinessUnitName : null,
                        UserGroupName = userGroup != null ? userGroup.UserGroupsName : null
                    });

            // Apply filters
            if (id.HasValue)
                query = query.Where(x => x.User.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(userName))
                query = query.Where(x => x.User.FirstName.ToLower().Contains(userName.ToLower()) || x.User.LastName.ToLower().Contains(userName.ToLower()));
            if (!string.IsNullOrWhiteSpace(email))
                query = query.Where(x => x.User.Email.ToLower().Contains(email.ToLower()));
            if (roleId.HasValue)
                query = query.Where(x => x.User.RoleId == roleId.Value);
            if (!string.IsNullOrWhiteSpace(region))
                query = query.Where(x => x.User.Region != null && x.User.Region.ToLower().Contains(region.ToLower()));
            if (isActive.HasValue)
                query = query.Where(x => x.User.IsActive == isActive.Value);

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize);

            // Project to UserResponseDTO
            var users = await query.Select(x => new UserResponseDTO
            {
                Id = x.User.Id,
                FirstName = x.User.FirstName,
                MiddleName = x.User.MiddleName,
                LastName = x.User.LastName,
                Email = x.User.Email,
                ImageUrl = x.User.ImageUrl,
                RoleId = x.User.RoleId,
                RoleName = x.RoleName,
                TeamId = x.User.TeamId,
                TeamName = x.TeamName,
                Timezone = x.User.Timezone,
                LastLogin = x.User.LastLogin,
                Region = x.User.Region,
                ManagerId = x.User.ManagerId,
                Buid = x.User.Buid,
                BusinessUnitName = x.BusinessUnitName,
                UserGroupId = x.User.UserGroupId,
                UserGroupName = x.UserGroupName,
                IsActive = x.User.IsActive,
                CreatedBy = x.User.CreatedBy,
                CreatedOn = x.User.CreatedOn,
                ModifiedBy = x.User.ModifiedBy,
                ModifiedOn = x.User.ModifiedOn
            }).ToListAsync();

            return (users, totalCount);
        }

        public async Task<User> GetByIdAsync(long id, long businessUnitId)
        {
            var user = await _context.Users
                .AsNoTracking()
                .Include(u => u.Role)
                .Include(u => u.Team)
                .Include(u => u.Bu)
                .Include(u => u.UserGroup)
                .FirstOrDefaultAsync(u => u.Id == id && u.Buid == businessUnitId);

            return user ?? throw new KeyNotFoundException($"User with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(User user)
        {
            // Validate unique email globally
            var emailExists = await _context.Users.AnyAsync(u => u.Email == user.Email);
            if (emailExists)
                throw new ArgumentException($"Email {user.Email} is already registered.");

            // Validate foreign keys
            if (user.RoleId.HasValue)
            {
                var roleExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == user.RoleId.Value && sm.SetupType == "role");
                if (!roleExists)
                    throw new ArgumentException($"Role with ID {user.RoleId.Value} does not exist.");
            }
            if (user.TeamId.HasValue)
            {
                var teamExists = await _context.Teams.AnyAsync(t => t.Id == user.TeamId.Value && t.BusinessUnitId == user.Buid);
                if (!teamExists)
                    throw new ArgumentException($"Team with ID {user.TeamId.Value} does not exist in Business Unit {user.Buid}.");
            }
            if (user.Buid.HasValue)
            {
                var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == user.Buid.Value);
                if (!buExists)
                    throw new ArgumentException($"Business Unit with ID {user.Buid.Value} does not exist.");
            }
            
            if (user.UserGroupId.HasValue)
            {
                var userGroupExists = await _context.UserGroups.AnyAsync(ug => ug.Id == user.UserGroupId.Value && ug.BusinessUnitId == user.Buid);
                if (!userGroupExists)
                    throw new ArgumentException($"UserGroup with ID {user.UserGroupId.Value} does not exist in Business Unit {user.Buid}.");
            }
            if (user.ManagerId.HasValue)
            {
                if (user.ManagerId.Value == user.Id)
                    throw new ArgumentException("A user cannot be their own manager.");
                var managerExists = await _context.Users.AnyAsync(u => u.Id == user.ManagerId.Value && u.Buid == user.Buid);
                if (!managerExists)
                    throw new ArgumentException($"Manager with ID {user.ManagerId.Value} does not exist in Business Unit {user.Buid}.");
            }

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(User user)
        {
            var existing = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == user.Id);
            if (existing == null)
                throw new KeyNotFoundException($"User with ID {user.Id} not found.");

            if (existing.Buid != user.Buid)
                throw new ArgumentException("Cannot change the Business Unit of a user.");

            // Validate unique email globally (excluding current user)
            var emailExists = await _context.Users.AnyAsync(u => u.Email == user.Email && u.Id != user.Id);
            if (emailExists)
                throw new ArgumentException($"Email {user.Email} is already registered.");

            // Validate foreign keys
            if (user.RoleId.HasValue)
            {
                var roleExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == user.RoleId.Value && sm.SetupType == "role");
                if (!roleExists)
                    throw new ArgumentException($"Role with ID {user.RoleId.Value} does not exist.");
            }
            if (user.TeamId.HasValue)
            {
                var teamExists = await _context.Teams.AnyAsync(t => t.Id == user.TeamId.Value && t.BusinessUnitId == user.Buid);
                if (!teamExists)
                    throw new ArgumentException($"Team with ID {user.TeamId.Value} does not exist in Business Unit {user.Buid}.");
            }
            if (user.Buid.HasValue)
            {
                var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == user.Buid.Value);
                if (!buExists)
                    throw new ArgumentException($"Business Unit with ID {user.Buid.Value} does not exist.");
            }
           
            if (user.UserGroupId.HasValue)
            {
                var userGroupExists = await _context.UserGroups.AnyAsync(ug => ug.Id == user.UserGroupId.Value && ug.BusinessUnitId == user.Buid);
                if (!userGroupExists)
                    throw new ArgumentException($"UserGroup with ID {user.UserGroupId.Value} does not exist in Business Unit {user.Buid}.");
            }
            if (user.ManagerId.HasValue)
            {
                if (user.ManagerId.Value == user.Id)
                    throw new ArgumentException("A user cannot be their own manager.");
                var managerExists = await _context.Users.AnyAsync(u => u.Id == user.ManagerId.Value && u.Buid == user.Buid);
                if (!managerExists)
                    throw new ArgumentException($"Manager with ID {user.ManagerId.Value} does not exist in Business Unit {user.Buid}.");
            }

            _context.Users.Update(user);
            await _context.SaveChangesAsync();
        }

        public async Task ChangePasswordAsync(long id, string newPassword)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                throw new KeyNotFoundException($"User with ID {id} not found.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.ModifiedOn = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var user = await GetByIdAsync(id, businessUnitId);

            // Check for dependent users (InverseManager)
            var hasManagedUsers = await _context.Users.AnyAsync(u => u.ManagerId == id && u.Buid == businessUnitId);
            if (hasManagedUsers)
                throw new InvalidOperationException($"Cannot delete User with ID {id} because they manage other users.");

            // Check for dependent teams
            var hasManagedTeams = await _context.Teams.AnyAsync(t => t.ManagerId == id && t.BusinessUnitId == businessUnitId);
            if (hasManagedTeams)
                throw new InvalidOperationException($"Cannot delete User with ID {id} because they manage teams.");

           
            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
        }

        public async Task<IEnumerable<RoleResponseDTO>> GetRolesAsync()
        {
            return await _context.SetupMasters
                .AsNoTracking()
                .Where(sm => sm.SetupType == "role")
                .Select(sm => new RoleResponseDTO
                {
                    SetupId = sm.SetupId,
                    SetupName = sm.SetupValue
                })
                .ToListAsync();
        }

        public async Task<IEnumerable<TeamResponseDTO>> GetTeamsAsync(long businessUnitId)
        {
            return await _context.Teams
                .AsNoTracking()
                .Where(t => t.BusinessUnitId == businessUnitId)
                .Select(t => new TeamResponseDTO
                {
                    Id = t.Id,
                    TeamName = t.TeamName
                })
                .ToListAsync();
        }

        public async Task<IEnumerable<BusinessUnitResponseDTO>> GetBusinessUnitsAsync()
        {
            return await _context.BusinessUnits
                .AsNoTracking()
                .Select(bu => new BusinessUnitResponseDTO
                {
                    Id = bu.Id,
                    BusinessUnitName = bu.BusinessUnitName
                })
                .ToListAsync();
        }

    

        public async Task<IEnumerable<UserGroupResponseDTO>> GetUserGroupsAsync(long businessUnitId)
        {
            return await _context.UserGroups
                .AsNoTracking()
                .Where(ug => ug.BusinessUnitId == businessUnitId)
                .Select(ug => new UserGroupResponseDTO
                {
                    Id = ug.Id,
                    UserGroupsName = ug.UserGroupsName
                })
                .ToListAsync();
        }
    }
}
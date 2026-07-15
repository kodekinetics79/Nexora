using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class UserGroupRepository : IUserGroupRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public UserGroupRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<UserGroup>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? userGroupsName, long businessUnitId)
        {
            var query = _context.UserGroups
                .AsNoTracking()
                .Where(ug => ug.BusinessUnitId == businessUnitId)
                .AsQueryable();

            // Apply filters
            if (id.HasValue)
                query = query.Where(ug => ug.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(userGroupsName))
                query = query.Where(ug => ug.UserGroupsName.ToLower().Contains(userGroupsName.ToLower()));

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            var userGroups = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (userGroups, totalCount);
        }

        public async Task<UserGroup> GetByIdAsync(long id, long businessUnitId)
        {
            var userGroup = await _context.UserGroups
                .AsNoTracking()
                .FirstOrDefaultAsync(ug => ug.Id == id && ug.BusinessUnitId == businessUnitId);

            return userGroup ?? throw new KeyNotFoundException($"UserGroup with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(UserGroup userGroup)
        {
            // Validate unique user group name within same BusinessUnit
            var nameExists = await _context.UserGroups.AnyAsync(ug =>
                ug.UserGroupsName == userGroup.UserGroupsName && ug.BusinessUnitId == userGroup.BusinessUnitId);
            if (nameExists)
                throw new ArgumentException($"User group name {userGroup.UserGroupsName} already exists in this Business Unit.");

            // Validate BusinessUnit exists
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == userGroup.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {userGroup.BusinessUnitId} does not exist.");

            _context.UserGroups.Add(userGroup);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(UserGroup userGroup)
        {
            var existing = await _context.UserGroups.AsNoTracking().FirstOrDefaultAsync(ug => ug.Id == userGroup.Id);
            if (existing == null)
                throw new KeyNotFoundException($"UserGroup with ID {userGroup.Id} not found.");

            if (existing.BusinessUnitId != userGroup.BusinessUnitId)
                throw new ArgumentException("Cannot change the Business Unit of a user group.");

            // Validate unique user group name within same BusinessUnit (excluding current user group)
            var nameExists = await _context.UserGroups.AnyAsync(ug =>
                ug.UserGroupsName == userGroup.UserGroupsName && ug.BusinessUnitId == userGroup.BusinessUnitId && ug.Id != userGroup.Id);
            if (nameExists)
                throw new ArgumentException($"User group name {userGroup.UserGroupsName} already exists in this Business Unit.");

            // Validate BusinessUnit exists
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == userGroup.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {userGroup.BusinessUnitId} does not exist.");

            _context.UserGroups.Update(userGroup);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var userGroup = await GetByIdAsync(id, businessUnitId);

            // Check for dependent users
            var hasUsers = await _context.Users.AnyAsync(u => u.UserGroupId == id && u.Buid == businessUnitId);
            if (hasUsers)
                throw new InvalidOperationException($"Cannot delete UserGroup with ID {id} because it has associated users.");

            _context.UserGroups.Remove(userGroup);
            await _context.SaveChangesAsync();
        }
    }
}
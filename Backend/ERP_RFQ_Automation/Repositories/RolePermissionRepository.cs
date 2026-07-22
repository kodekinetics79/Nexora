using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class RolePermissionRepository : IRolePermissionRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public RolePermissionRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<RolePermission>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, long? roleId, long? moduleId, long businessUnitId)
        {
            var query = _context.RolePermissions
                .AsNoTracking()
                .Include(rp => rp.Role)
                .Include(rp => rp.Module)
                .Where(rp => rp.BusinessUnitId == businessUnitId)
                .AsQueryable();

            // Apply filters
            if (id.HasValue)
                query = query.Where(rp => rp.Id == id.Value);
            if (roleId.HasValue)
                query = query.Where(rp => rp.RoleId == roleId.Value);
            if (moduleId.HasValue)
                query = query.Where(rp => rp.ModuleId == moduleId.Value);

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            var rolePermissions = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (rolePermissions, totalCount);
        }

        public async Task<RolePermission> GetByIdAsync(long id, long businessUnitId)
        {
            var rolePermission = await _context.RolePermissions
                .AsNoTracking()
                .Include(rp => rp.Role)
                .Include(rp => rp.Module)
                .FirstOrDefaultAsync(rp => rp.Id == id && rp.BusinessUnitId == businessUnitId);

            return rolePermission ?? throw new KeyNotFoundException($"RolePermission with ID {id} not found.");
        }

        public async Task AddAsync(RolePermission rolePermission)
        {
            // Validate RoleId exists (if provided)
            if (rolePermission.RoleId.HasValue)
            {
                var roleExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == rolePermission.RoleId.Value
                    && sm.BusinessUnitId == rolePermission.BusinessUnitId && sm.SetupType.ToLower() == "role"
                    && sm.IsActive != false);
                if (!roleExists)
                    throw new ArgumentException($"Role with ID {rolePermission.RoleId} does not exist.");
            }

            // Validate ModuleId exists
            var moduleExists = await _context.Modules.AnyAsync(m => m.Id == rolePermission.ModuleId);
            if (!moduleExists)
                throw new ArgumentException($"Module with ID {rolePermission.ModuleId} does not exist.");

            // Validate BusinessUnitId exists
            var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == rolePermission.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {rolePermission.BusinessUnitId} does not exist.");

            // Validate unique RoleId-ModuleId-BusinessUnitId combination
            var permissionExists = await _context.RolePermissions.AnyAsync(rp =>
                rp.RoleId == rolePermission.RoleId && rp.ModuleId == rolePermission.ModuleId
                    && rp.BusinessUnitId == rolePermission.BusinessUnitId);
            if (permissionExists)
                throw new ArgumentException($"Permission for RoleId {rolePermission.RoleId}, ModuleId {rolePermission.ModuleId}, and BusinessUnitId {rolePermission.BusinessUnitId} already exists.");

            _context.RolePermissions.Add(rolePermission);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(RolePermission rolePermission)
        {
            var existing = await _context.RolePermissions.AsNoTracking().FirstOrDefaultAsync(rp =>
                rp.Id == rolePermission.Id && rp.BusinessUnitId == rolePermission.BusinessUnitId);
            if (existing == null)
                throw new KeyNotFoundException($"RolePermission with ID {rolePermission.Id} not found.");

            // BusinessUnitId check removed as per global setup

            // Validate RoleId exists (if provided)
            if (rolePermission.RoleId.HasValue)
            {
                var roleExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == rolePermission.RoleId.Value
                    && sm.BusinessUnitId == rolePermission.BusinessUnitId && sm.SetupType.ToLower() == "role"
                    && sm.IsActive != false);
                if (!roleExists)
                    throw new ArgumentException($"Role with ID {rolePermission.RoleId} does not exist in Business Unit {rolePermission.BusinessUnitId}.");
            }

            // Validate ModuleId exists
            var moduleExists = await _context.Modules.AnyAsync(m => m.Id == rolePermission.ModuleId);
            if (!moduleExists)
                throw new ArgumentException($"Module with ID {rolePermission.ModuleId} does not exist.");

            // Validate BusinessUnitId exists
            var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == rolePermission.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {rolePermission.BusinessUnitId} does not exist.");

            // Validate unique RoleId-ModuleId-BusinessUnitId combination (excluding current permission)
            var permissionExists = await _context.RolePermissions.AnyAsync(rp =>
                rp.RoleId == rolePermission.RoleId && rp.ModuleId == rolePermission.ModuleId
                    && rp.BusinessUnitId == rolePermission.BusinessUnitId && rp.Id != rolePermission.Id);
            if (permissionExists)
                throw new ArgumentException($"Permission for RoleId {rolePermission.RoleId}, ModuleId {rolePermission.ModuleId}, and BusinessUnitId {rolePermission.BusinessUnitId} already exists.");

            _context.RolePermissions.Update(rolePermission);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var rolePermission = await GetByIdAsync(id, businessUnitId);
            _context.RolePermissions.Remove(rolePermission);
            await _context.SaveChangesAsync();
        }

        public async Task<bool> CheckPermissionAsync(long roleId, string moduleName, string action, long businessUnitId)
        {
            var query = _context.RolePermissions
                .AsNoTracking()
                .Where(rp => rp.RoleId == roleId && rp.BusinessUnitId == businessUnitId
                    && rp.Module != null && rp.Module.ModuleName == moduleName);

            switch (action.ToLower())
            {
                case "canview":
                    // Mirrors the frontend PermissionGuard: there is no CanView column —
                    // having ANY RolePermission row for the module grants view.
                    return await query.AnyAsync();
                case "cancreate":
                    return await query.AnyAsync(rp => rp.CanCreate == true);
                case "canedit":
                    return await query.AnyAsync(rp => rp.CanEdit == true);
                case "candelete":
                    return await query.AnyAsync(rp => rp.CanDelete == true);
                default:
                    return false;
            }
        }
    }
}

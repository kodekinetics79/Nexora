using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class ModuleRepository : IModuleRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public ModuleRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<Module>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? moduleName, bool? isActive)
        {
            var query = _context.Modules
                .AsNoTracking()
                .AsQueryable();

            // Apply filters
            if (id.HasValue)
                query = query.Where(m => m.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(moduleName))
                query = query.Where(m => m.ModuleName.ToLower().Contains(moduleName.ToLower()));
            if (isActive.HasValue)
                query = query.Where(m => m.IsActive == isActive.Value);

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            var modules = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (modules, totalCount);
        }

        public async Task<Module> GetByIdAsync(long id)
        {
            var module = await _context.Modules
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);

            return module ?? throw new KeyNotFoundException($"Module with ID {id} already exists.");
        }

        public async Task AddAsync(Module module)
        {
            // Validate unique module name within same BusinessUnit
            var nameExists = await _context.Modules.AnyAsync(m => m.ModuleName == module.ModuleName );
            if (nameExists)
                throw new ArgumentException($"Module name {module.ModuleName} already exists.");

            
            _context.Modules.Add(module);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Module module)
        {
            var existing = await _context.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == module.Id);
            if (existing == null)
                throw new KeyNotFoundException($"Module with ID {module.Id} not found.");

            
            // Validate unique module name within same BusinessUnit (excluding current module)
            var nameExists = await _context.Modules.AnyAsync(m => m.ModuleName == module.ModuleName  && m.Id != module.Id);
            if (nameExists)
                throw new ArgumentException($"Module name {module.ModuleName} already exists.");

            
            _context.Modules.Update(module);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id)
        {
            var module = await GetByIdAsync(id);

            // Check for dependent role permissions
            var hasPermissions = await _context.RolePermissions.AnyAsync(rp => rp.ModuleId == id);
            if (hasPermissions)
                throw new InvalidOperationException($"Cannot delete Module with ID {id} already exists.");

            _context.Modules.Remove(module);
            await _context.SaveChangesAsync();
        }
    }
}
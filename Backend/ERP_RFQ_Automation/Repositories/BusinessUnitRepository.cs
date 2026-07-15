using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class BusinessUnitRepository : IBusinessUnitRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public BusinessUnitRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<BusinessUnit>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? businessUnitName)
        {
            var query = _context.BusinessUnits.AsNoTracking().AsQueryable();

            // Apply filters
            if (id.HasValue)
                query = query.Where(bu => bu.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(businessUnitName))
                query = query.Where(bu => bu.BusinessUnitName.ToLower().Contains(businessUnitName.ToLower()));

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize);

            var businessUnits = await query.ToListAsync();
            return (businessUnits, totalCount);
        }

        public async Task<BusinessUnit> GetByIdAsync(long id)
        {
            var businessUnit = await _context.BusinessUnits
                .AsNoTracking()
                .FirstOrDefaultAsync(bu => bu.Id == id);

            return businessUnit ?? throw new KeyNotFoundException($"BusinessUnit with ID {id} not found.");
        }
        public async Task AddAsync(BusinessUnit businessUnit)
        {
            var codeExists = await _context.BusinessUnits.AnyAsync(bu => bu.BusinessUnitCode == businessUnit.BusinessUnitCode);
            if (codeExists)
                throw new ArgumentException($"Business unit code {businessUnit.BusinessUnitCode} already exists.");

            var nameExists = await _context.BusinessUnits.AnyAsync(bu => bu.BusinessUnitName == businessUnit.BusinessUnitName);
            if (nameExists)
                throw new ArgumentException($"Business unit name {businessUnit.BusinessUnitName} already exists.");

            _context.BusinessUnits.Add(businessUnit);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(BusinessUnit businessUnit)
        {
            var codeExists = await _context.BusinessUnits
                .AnyAsync(bu => bu.BusinessUnitCode == businessUnit.BusinessUnitCode && bu.Id != businessUnit.Id);
            if (codeExists)
                throw new ArgumentException($"Business unit code {businessUnit.BusinessUnitCode} already exists.");

            var nameExists = await _context.BusinessUnits
                .AnyAsync(bu => bu.BusinessUnitName == businessUnit.BusinessUnitName && bu.Id != businessUnit.Id);
            if (nameExists)
                throw new ArgumentException($"Business unit name {businessUnit.BusinessUnitName} already exists.");

            _context.BusinessUnits.Update(businessUnit);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id)
        {
            var businessUnit = await GetByIdAsync(id);

            // Check for dependent users
            var hasUsers = await _context.Users.AnyAsync(u => u.Buid == id); 
            if (hasUsers)
                throw new InvalidOperationException($"Cannot delete BusinessUnit with ID {id} because it has associated users.");

            _context.BusinessUnits.Remove(businessUnit);
            await _context.SaveChangesAsync();
        }
    }
}

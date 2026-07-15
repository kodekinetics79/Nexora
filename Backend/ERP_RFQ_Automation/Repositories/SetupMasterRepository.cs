using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class SetupMasterRepository : ISetupMasterRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public SetupMasterRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<SetupMaster>> GetAllAsync()
        {
            var query = _context.SetupMasters.Include(sm => sm.ParentSetup).AsQueryable();

            return await query.ToListAsync();
        }

        public async Task<SetupMaster> GetByIdAsync(long id)
        {
            var query = _context.SetupMasters.Include(sm => sm.ParentSetup).AsQueryable();

            query = query.Where(sm => sm.SetupId == id);

            var setupMaster = await query.FirstOrDefaultAsync();

            return setupMaster ?? throw new KeyNotFoundException($"SetupMaster with ID {id} not found.");
        }

        public async Task AddAsync(SetupMaster setupMaster)
        {
            // Validate unique SetupCode (if SetupCode is provided)
            if (!string.IsNullOrEmpty(setupMaster.SetupCode))
            {
                var codeExists = await _context.SetupMasters.AnyAsync(sm =>
                    sm.SetupCode == setupMaster.SetupCode);
                if (codeExists)
                    throw new ArgumentException($"Setup code '{setupMaster.SetupCode}' already exists.");
            }

            // Validate unique SetupName
            var nameExists = await _context.SetupMasters.AnyAsync(sm =>
                sm.SetupValue == setupMaster.SetupValue);
            if (nameExists)
                throw new ArgumentException($"Setup name '{setupMaster.SetupValue}' already exists.");

            // Validate ParentSetupId if provided
            if (setupMaster.ParentSetupId.HasValue && setupMaster.ParentSetupId != 0)
            {
                var parentExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == setupMaster.ParentSetupId);
                if (!parentExists)
                    throw new ArgumentException($"ParentSetupId {setupMaster.ParentSetupId} does not exist.");
            }

            _context.SetupMasters.Add(setupMaster);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(SetupMaster setupMaster)
        {
            var existing = await _context.SetupMasters.AsNoTracking().FirstOrDefaultAsync(sm => sm.SetupId == setupMaster.SetupId);
            if (existing == null)
                throw new KeyNotFoundException($"SetupMaster with ID {setupMaster.SetupId} not found.");

            // Validate unique SetupCode (if SetupCode is provided)
            if (!string.IsNullOrEmpty(setupMaster.SetupCode))
            {
                var codeExists = await _context.SetupMasters.AnyAsync(sm =>
                    sm.SetupCode == setupMaster.SetupCode &&
                    sm.SetupId != setupMaster.SetupId);
                if (codeExists)
                    throw new ArgumentException($"Setup code '{setupMaster.SetupCode}' already exists.");
            }

            // Validate unique SetupName
            var nameExists = await _context.SetupMasters.AnyAsync(sm =>
                sm.SetupValue == setupMaster.SetupValue &&
                sm.SetupId != setupMaster.SetupId);
            if (nameExists)
                throw new ArgumentException($"Setup name '{setupMaster.SetupValue}' already exists.");



            // Validate ParentSetupId if provided
            if (setupMaster.ParentSetupId.HasValue && setupMaster.ParentSetupId != 0)
            {
                if (setupMaster.ParentSetupId == setupMaster.SetupId)
                    throw new ArgumentException("A SetupMaster cannot be its own parent.");

                var parentExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == setupMaster.ParentSetupId);
                if (!parentExists)
                    throw new ArgumentException($"ParentSetupId {setupMaster.ParentSetupId} does not exist.");
            }

            _context.SetupMasters.Update(setupMaster);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id)
        {
            var setupMaster = await GetByIdAsync(id);

            // Check for dependent records
            var hasChildren = await _context.SetupMasters.AnyAsync(sm => sm.ParentSetupId == id);
            if (hasChildren)
                throw new InvalidOperationException($"Cannot delete SetupMaster with ID {id} because it has dependent records.");

            _context.SetupMasters.Remove(setupMaster);
            await _context.SaveChangesAsync();
        }
    }
}
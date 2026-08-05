using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    /// <summary>
    /// A role is still referenced by users or permission rows (B4). Distinct from the generic
    /// <see cref="InvalidOperationException"/> the repository throws for other dependency
    /// violations so the controller can answer 409 Conflict — "this is a state problem you can
    /// resolve" — rather than 400 Bad Request.
    /// </summary>
    public sealed class RoleInUseException : InvalidOperationException
    {
        public RoleInUseException(string message) : base(message) { }
    }

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

            // Defence in depth. SetupMaster carries a global query filter, so a request-scoped
            // context already cannot see another tenant's row here — but this method's result is
            // fed straight into UpdateAsync/DeleteAsync, so the tenant predicate is stated
            // explicitly rather than left as an invariant of a file elsewhere. A null scope
            // (background worker / migration path) keeps the previous unfiltered behaviour.
            var tenantId = _context.ScopedTenantId;
            if (tenantId is not null)
                query = query.Where(sm => sm.BusinessUnitId == tenantId);

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
            var storedQuery = _context.SetupMasters.AsNoTracking()
                .Where(sm => sm.SetupId == setupMaster.SetupId);

            var tenantId = _context.ScopedTenantId;
            if (tenantId is not null)
                storedQuery = storedQuery.Where(sm => sm.BusinessUnitId == tenantId);

            var existing = await storedQuery.FirstOrDefaultAsync();
            if (existing == null)
                throw new KeyNotFoundException($"SetupMaster with ID {setupMaster.SetupId} not found.");

            // An update may never move a row between tenants. Re-pin the stored owner regardless of
            // what the caller supplied, so a rewritten BusinessUnitId in the incoming entity cannot
            // reassign the row (the controller used to set it to 1 unconditionally).
            setupMaster.BusinessUnitId = existing.BusinessUnitId;

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

            // B4(5): a role row is referenced by Users.RoleID and RolePermissions.RoleID. The FK on
            // RolePermission is composite (BusinessUnitID, RoleID) and Users.RoleID is nullable, so
            // the database would not necessarily stop this — and orphaning a user's role silently
            // strips their access with no diagnosable cause. Mirrors the actionable-message pattern
            // in UserRepository.DeleteAsync.
            if (ERP_RFQ_Automation.Authorization.SetupTypes.IsRole(setupMaster.SetupType))
            {
                var assignedUsers = await _context.Users.CountAsync(u => u.RoleId == id);
                var grantRows = await _context.RolePermissions.CountAsync(rp => rp.RoleId == id);
                if (assignedUsers > 0 || grantRows > 0)
                    throw new RoleInUseException(
                        $"Cannot delete role '{setupMaster.SetupValue}' (ID {id}): it is still assigned to " +
                        $"{assignedUsers} user(s) and holds {grantRows} permission row(s). Reassign those " +
                        "users and clear the role's permissions first.");
            }

            _context.SetupMasters.Remove(setupMaster);
            await _context.SaveChangesAsync();
        }
    }
}
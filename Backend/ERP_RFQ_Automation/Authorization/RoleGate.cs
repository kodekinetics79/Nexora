using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Shared role-name gate so the "role name contains admin/manager" rule (previously
    /// inlined in LeadRepository.CanManageLeadAssignmentsAsync) lives in ONE place.
    /// Resolves RoleId → the active SetupMaster "role" row and matches on SetupCode /
    /// SetupValue, case-insensitive. Missing/inactive role → false (fail-closed).
    /// </summary>
    public interface IRoleGate
    {
        /// <summary>Role name contains both "super" and "admin" (e.g. "Super Admin", "Super_Administrator").</summary>
        Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId);

        /// <summary>Role name contains "admin" or "manager" (same rule as lead-assignment management).</summary>
        Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId);

        Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId);
    }

    public sealed class RoleGate : IRoleGate
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private readonly ErpRfqAutomationContext _context;
        private readonly IMemoryCache _cache;

        public RoleGate(ErpRfqAutomationContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        public async Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId)
        {
            var role = await ResolveRoleAsync(roleId, businessUnitId);
            return role is not null && (IsSuperAdminName(role.Value.Code) || IsSuperAdminName(role.Value.Value));
        }

        public async Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId)
        {
            var role = await ResolveRoleAsync(roleId, businessUnitId);
            return role is not null && (IsManagerName(role.Value.Code) || IsManagerName(role.Value.Value));
        }

        public async Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId)
        {
            if (!targetRoleId.HasValue) return true;
            if (await IsSuperAdminAsync(callerRoleId, businessUnitId)) return true;
            if (await IsSuperAdminAsync(targetRoleId.Value, businessUnitId)) return false;
            if (await IsManagerOrAdminAsync(targetRoleId.Value, businessUnitId) &&
                !await IsManagerOrAdminAsync(callerRoleId, businessUnitId)) return false;

            var roles = await _context.SetupMasters.AsNoTracking()
                .Where(x => (x.SetupId == callerRoleId || x.SetupId == targetRoleId.Value) &&
                            x.BusinessUnitId == businessUnitId && x.SetupType.ToLower() == "role" &&
                            x.IsActive != false)
                .Select(x => x.SetupId)
                .Distinct()
                .ToListAsync();
            if (!roles.Contains(callerRoleId) || !roles.Contains(targetRoleId.Value)) return false;

            var permissions = await _context.RolePermissions.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.RoleId.HasValue &&
                            (x.RoleId == callerRoleId || x.RoleId == targetRoleId.Value))
                .Select(x => new { RoleId = x.RoleId!.Value, x.ModuleId, x.CanCreate, x.CanEdit, x.CanDelete })
                .ToListAsync();
            var caller = permissions.Where(x => x.RoleId == callerRoleId)
                .GroupBy(x => x.ModuleId)
                .ToDictionary(x => x.Key, x => (
                    CanCreate: x.Any(y => y.CanCreate == true),
                    CanEdit: x.Any(y => y.CanEdit == true),
                    CanDelete: x.Any(y => y.CanDelete == true)));
            foreach (var target in permissions.Where(x => x.RoleId == targetRoleId.Value).GroupBy(x => x.ModuleId))
            {
                if (!caller.TryGetValue(target.Key, out var grant)) return false;
                if (target.Any(x => x.CanCreate == true) && !grant.CanCreate) return false;
                if (target.Any(x => x.CanEdit == true) && !grant.CanEdit) return false;
                if (target.Any(x => x.CanDelete == true) && !grant.CanDelete) return false;
            }
            return true;
        }

        private static bool IsSuperAdminName(string? s) =>
            s != null
            && s.Contains("super", StringComparison.OrdinalIgnoreCase)
            && s.Contains("admin", StringComparison.OrdinalIgnoreCase);

        private static bool IsManagerName(string? s) =>
            s != null && (s.Contains("admin", StringComparison.OrdinalIgnoreCase)
                          || s.Contains("manager", StringComparison.OrdinalIgnoreCase));

        /// <summary>Role names change rarely; cache the resolved (code, value) 60s per roleId.</summary>
        private async Task<(string? Code, string? Value)?> ResolveRoleAsync(long roleId, long businessUnitId)
        {
            return await _cache.GetOrCreateAsync<(string? Code, string? Value)?>(
                $"rbac:role:{businessUnitId}:{roleId}",
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;

                    var role = await _context.SetupMasters
                        .AsNoTracking()
                        .Where(s => s.SetupId == roleId && s.BusinessUnitId == businessUnitId
                                    && s.SetupType.ToLower() == "role"
                                    && (s.IsActive == true || s.IsActive == null))
                        .Select(s => new { s.SetupCode, s.SetupValue })
                        .FirstOrDefaultAsync();

                    return role == null ? null : (role.SetupCode, role.SetupValue);
                });
        }
    }
}

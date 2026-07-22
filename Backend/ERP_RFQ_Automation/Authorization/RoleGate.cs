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

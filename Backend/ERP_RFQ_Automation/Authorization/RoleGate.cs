using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// The single place that answers "how much authority does this role hold?".
    ///
    /// It reads the explicit <c>Setup_Master.RoleRank</c> column (see <see cref="RoleRanks"/>) for
    /// the active role row in the CALLER'S OWN tenant. Missing row, inactive row, or a row owned by
    /// a different business unit → <see cref="RoleRanks.Member"/> (fail-closed).
    ///
    /// It used to resolve privilege by substring-matching the free-text role NAME:
    /// <c>contains "super" &amp;&amp; contains "admin"</c> made a super administrator and
    /// <c>contains "admin" || contains "manager"</c> made a manager. That is a privilege-escalation
    /// class, not a rough edge — "Supervisor Admin", "Superintendent Administrator" and
    /// "Site Supervisor - Admin" are ordinary job titles in industrial and energy procurement, and
    /// every one of them contains "super" + "admin", so every one of them silently held the whole
    /// tenant (PermissionHandler bypassed every module check for a super admin). Those two
    /// name-matching helpers have been DELETED, not deprecated: there is no longer any code path in
    /// which a role name influences authority.
    /// </summary>
    public interface IRoleGate
    {
        /// <summary>Role's <c>RoleRank</c> is at or above <see cref="RoleRanks.Owner"/>.</summary>
        Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId);

        /// <summary>Role's <c>RoleRank</c> is at or above <see cref="RoleRanks.Manager"/>.</summary>
        Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId);

        /// <summary>
        /// The role's stored rank, or <see cref="RoleRanks.Member"/> when the role does not exist,
        /// is inactive, or belongs to another tenant. Callers that must COMPARE two ranks (role
        /// administration) need the number, not the two booleans.
        /// </summary>
        Task<short> GetRoleRankAsync(long roleId, long businessUnitId);

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

        public async Task<short> GetRoleRankAsync(long roleId, long businessUnitId)
            => await ResolveRankAsync(roleId, businessUnitId) ?? RoleRanks.Member;

        public async Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId)
            => await GetRoleRankAsync(roleId, businessUnitId) >= RoleRanks.Owner;

        public async Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId)
            => await GetRoleRankAsync(roleId, businessUnitId) >= RoleRanks.Manager;

        public async Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId)
        {
            if (!targetRoleId.HasValue) return true;

            var callerRank = await GetRoleRankAsync(callerRoleId, businessUnitId);
            var targetRank = await GetRoleRankAsync(targetRoleId.Value, businessUnitId);

            // The owner tier administers the whole tenant plane.
            if (callerRank >= RoleRanks.Owner) return true;

            // You may never administer a role that outranks you. This single comparison replaces
            // the two name-derived special cases it grew out of ("target is a super admin" and
            // "target is a manager while I am not") and additionally closes the tier they could not
            // express — Admin(20) is now genuinely above Manager(10) instead of being the same
            // "contains admin-or-manager" bucket.
            if (targetRank > callerRank) return false;

            var roles = await _context.SetupMasters.AsNoTracking()
                .Where(SetupTypes.IsRoleRow)
                .Where(x => (x.SetupId == callerRoleId || x.SetupId == targetRoleId.Value) &&
                            x.BusinessUnitId == businessUnitId &&
                            x.IsActive != false)
                .Select(x => x.SetupId)
                .Distinct()
                .ToListAsync();
            if (!roles.Contains(callerRoleId) || !roles.Contains(targetRoleId.Value)) return false;

            var permissions = await _context.RolePermissions.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.RoleId.HasValue &&
                            (x.RoleId == callerRoleId || x.RoleId == targetRoleId.Value))
                .Select(x => new { RoleId = x.RoleId!.Value, x.ModuleId, x.CanView, x.CanCreate, x.CanEdit, x.CanDelete })
                .ToListAsync();
            var caller = permissions.Where(x => x.RoleId == callerRoleId)
                .GroupBy(x => x.ModuleId)
                .ToDictionary(x => x.Key, x => (
                    CanView: x.Any(y => y.CanView == true),
                    CanCreate: x.Any(y => y.CanCreate == true),
                    CanEdit: x.Any(y => y.CanEdit == true),
                    CanDelete: x.Any(y => y.CanDelete == true)));
            foreach (var target in permissions.Where(x => x.RoleId == targetRoleId.Value).GroupBy(x => x.ModuleId))
            {
                if (!caller.TryGetValue(target.Key, out var grant)) return false;
                // CanView joins the rank comparison (B3). Before it existed, read access was
                // implied by row existence, so "the target role can see a module I cannot" was not
                // expressible — and therefore not checkable.
                if (target.Any(x => x.CanView == true) && !grant.CanView) return false;
                if (target.Any(x => x.CanCreate == true) && !grant.CanCreate) return false;
                if (target.Any(x => x.CanEdit == true) && !grant.CanEdit) return false;
                if (target.Any(x => x.CanDelete == true) && !grant.CanDelete) return false;
            }
            return true;
        }

        /// <summary>
        /// The IMemoryCache key holding a role's resolved rank. Any write that can change the rank —
        /// or remove the role — MUST evict it: the 60s TTL would otherwise keep serving the previous
        /// rank, and a rank REVOKED by an administrator would keep granting for up to a minute.
        /// <c>SetupMasterController</c> evicts on update and delete.
        /// </summary>
        internal static string RoleCacheKey(long businessUnitId, long roleId)
            => $"rbac:role:{businessUnitId}:{roleId}";

        /// <summary>
        /// Ranks change rarely; cache the resolved rank 60s per (tenant, roleId). A null cached
        /// value means "no such active role in this tenant" and is cached too, so a bogus roleId
        /// cannot be used to hammer the database.
        /// </summary>
        private async Task<short?> ResolveRankAsync(long roleId, long businessUnitId)
        {
            return await _cache.GetOrCreateAsync<short?>(
                RoleCacheKey(businessUnitId, roleId),
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;

                    // BusinessUnitId is part of the predicate, not just the cache key: a role row is
                    // tenant-owned, so a rank of 30 in business unit 2 grants nothing at all to a
                    // caller whose token says business unit 1.
                    var role = await _context.SetupMasters
                        .AsNoTracking()
                        .Where(SetupTypes.IsRoleRow)
                        .Where(s => s.SetupId == roleId && s.BusinessUnitId == businessUnitId
                                    && (s.IsActive == true || s.IsActive == null))
                        .Select(s => (short?)s.RoleRank)
                        .FirstOrDefaultAsync();

                    return role;
                });
        }
    }
}

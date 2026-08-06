using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Authorization
{
    public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        // Role-permission edits are rare; a short TTL keeps the hot path off the DB
        // while a revoked permission still takes effect within a minute.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private readonly IRolePermissionRepository _repository;
        private readonly IRoleGate _roleGate;
        private readonly IMemoryCache _cache;
        private readonly ILogger<PermissionHandler> _logger;

        public PermissionHandler(
            IRolePermissionRepository repository,
            IRoleGate roleGate,
            IMemoryCache cache,
            ILogger<PermissionHandler> logger)
        {
            _repository = repository;
            _roleGate = roleGate;
            _cache = cache;
            _logger = logger;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            var roleIdClaim = context.User.FindFirst("roleId")?.Value;
            var buIdClaim = context.User.FindFirst("businessUnitId")?.Value;

            if (string.IsNullOrEmpty(roleIdClaim) || string.IsNullOrEmpty(buIdClaim))
            {
                return;
            }

            if (!long.TryParse(roleIdClaim, out long roleId) || !long.TryParse(buIdClaim, out long businessUnitId))
            {
                return;
            }

            // Rank rule (replaces the former super-admin BYPASS): a role at RoleRanks.Admin or
            // above satisfies a module permission requirement on its own.
            //
            // This is deliberately a rule, not a bypass. The bypass short-circuited EVERY module
            // check for anything RoleGate called a super admin, and RoleGate decided that by
            // substring-matching the role NAME — so a tenant role called "Supervisor Admin" owned
            // the tenant. Authority now comes from the explicit Setup_Master.RoleRank column, and
            // this check states exactly which tier it grants.
            //
            // The reason the bypass existed is fully satisfied by the rank check: an administrator
            // with missing or partial RolePermissions rows must never be locked out of their own
            // tenant, and rank >= Admin grants that directly, without reference to any row.
            // Everything below Admin is decided by RolePermissions rows and stays fail-closed:
            // no row, no grant.
            var rank = await _roleGate.GetRoleRankAsync(roleId, businessUnitId);
            if (rank >= RoleRanks.Admin)
            {
                _logger.LogDebug(
                    "Module permission {Module}:{Action} satisfied by rank {Rank} for role {RoleId}.",
                    requirement.ModuleName, requirement.Action, rank, roleId);
                context.Succeed(requirement);
                return;
            }

            // check if the user has the permission in the repository (cached 60s per
            // role+module+action so a burst of requests costs one query).
            var hasPermission = await _cache.GetOrCreateAsync(
                $"rbac:perm:{roleId}:{businessUnitId}:{requirement.ModuleName}:{requirement.Action}",
                entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                    return _repository.CheckPermissionAsync(roleId, requirement.ModuleName, requirement.Action, businessUnitId);
                });

            if (hasPermission)
            {
                context.Succeed(requirement);
            }
        }
    }
}

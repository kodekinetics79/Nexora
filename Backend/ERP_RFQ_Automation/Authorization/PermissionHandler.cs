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

            // Super-admin bypass: defense against missing/partial RolePermissions rows.
            // Even if the seed grants a super admin everything, an absent row must never
            // lock the platform owner-role out of its own tenant.
            if (await _roleGate.IsSuperAdminAsync(roleId))
            {
                _logger.LogDebug("Module permission check bypassed for super-admin role {RoleId}.", roleId);
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
